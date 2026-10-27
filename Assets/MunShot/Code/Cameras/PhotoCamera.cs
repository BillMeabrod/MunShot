using System;
using KSP.Game;
using KSP.Sim;
using KSP.Sim.impl;
using Redux.Cameras;
using Redux.Modules;
using UnityEngine;

namespace MunShot.Cameras
{
    public enum PhotoCameraMode
    {
        /// <summary>Fly around in first person.</summary>
        Free,

        /// <summary>Circle a kerbal or vessel, looking at it.</summary>
        Orbit
    }

    /// <summary>
    /// Photo mode's camera. While engaged it is the flight camera's shot, given to the game's camera manager the way
    /// Redux's first-person camera does it (<see cref="UniverseCameraManager.SetCameraShotOverride"/>), with the
    /// game's own camera controls switched off and the first-person and part cameras put away. Releasing it hands the
    /// flight camera back to whatever camera mode it was in.
    ///
    /// Two modes:
    /// <list type="bullet">
    /// <item>Free: fly in first person: look around, fly forward, back and sideways, and straight up and down.</item>
    /// <item>Orbit: circle an <see cref="OrbitTarget"/>, looking at it; up and down bring it closer or further.</item>
    /// </list>
    /// In both, Roll turns the camera about its look and Level rights it against the planet (straight horizon, same
    /// heading); Fast and Slow scale every movement. Flying with Fast held keeps speeding up the longer it's held, for
    /// long distances. The controls come from the keyboard and mouse (<see cref="PhotoCameraIntent"/>).
    /// Every movement is the camera's own, as in space, where there's no up: looking turns about the camera's up and
    /// right, flying goes along its look, right and up, and orbiting circles about its up. So left on the mouse or
    /// keys is always left on the screen, however the camera is turned, upside down included.
    ///
    /// Where the camera is and how it's turned are kept in the simulation's own terms (<see cref="Position"/>,
    /// <see cref="Rotation"/>), so the floating origin moving under it doesn't move it. It runs on unscaled time: photo
    /// mode keeps the game paused.
    /// </summary>
    public sealed class PhotoCamera : ICameraShotProvider, ICameraShot, ICameraNearClipProvider
    {
        // Close enough to put the camera right up to a kerbal's face; what Redux's first-person camera uses
        private const float NearClip = 0.05f;

        // Degrees per second of roll at normal speed
        private const float RollSpeed = 45f;

        // How far one mouse-wheel step moves the free camera up or down: this many seconds of flying at Fly Speed
        private const float WheelStepSeconds = 0.2f;

        // One mouse-wheel step brings the orbit camera this much closer (or further)
        private const float OrbitZoomPerStep = 0.9f;
        private const float MinOrbitDistance = 0.5f;
        private const float MaxOrbitDistance = 20000f;

        private const float FastMultiplier = 5f;
        private const float SlowMultiplier = 0.2f;

        // Holding Fast while flying builds speed, for crossing long distances (pad to runway): it starts at Fast and
        // doubles every this many seconds, up to the top speed. Letting go of Fast, or no longer flying, ends it.
        private const float BoostDoublingSeconds = 1f;
        private const float MaxBoostSpeed = 2000f;

        private readonly KeyboardMouseCameraInput _keyboardMouse;
        private readonly Action<string> _logInfo;
        private readonly Action<string> _logError;

        private UniverseCameraManager _cameraManager;

        // The first-person view photo mode left, to go back to when it gives the camera back
        private FirstPersonCameraTarget _firstPersonTarget;
        private IPhysicsSpaceProvider _physicsSpace;
        private bool _gameInputWasEnabled;

        // Where the camera is and how it's turned
        private Position _position;
        private Rotation _rotation;
        private double _fieldOfView;

        private float _orbitDistance;

        // How long Fast has been held while flying, without a break
        private float _boostSeconds;

        public bool IsEngaged { get; private set; }
        public PhotoCameraMode Mode { get; private set; } = PhotoCameraMode.Free;

        /// <summary>What Orbit circles; null when it has nothing to circle.</summary>
        public OrbitTarget Target { get; private set; }

        /// <summary>Raised when the mode or the target changes, including when a lost target drops it to Free.</summary>
        public event Action Changed;

        public PhotoCamera(KeyboardMouseCameraInput keyboardMouse, Action<string> logInfo, Action<string> logError)
        {
            _keyboardMouse = keyboardMouse ?? throw new ArgumentNullException(nameof(keyboardMouse));
            _logInfo = logInfo ?? throw new ArgumentNullException(nameof(logInfo));
            _logError = logError ?? throw new ArgumentNullException(nameof(logError));
        }

        // ICameraShotProvider / ICameraShot / ICameraNearClipProvider: what the game's camera manager reads every frame
        public ICameraShot Shot => this;
        public Position CameraPosition => _position;
        public Rotation CameraRotation => _rotation;
        public Vector CameraForward => _physicsSpace.PhysicsToVector(PhysicsRotation() * Vector3.forward);
        public Vector CameraUp => _physicsSpace.PhysicsToVector(PhysicsRotation() * Vector3.up);
        public bool IsOrthographic => false;
        double ICameraShot.FieldOfView => _fieldOfView;

        /// <summary>
        /// The camera's vertical field of view in degrees, between <see cref="MinFieldOfView"/> and
        /// <see cref="MaxFieldOfView"/>: narrow to zoom in, wide for a wide-angle shot. Starts at the game camera's.
        /// </summary>
        public float FieldOfView
        {
            get => (float)_fieldOfView;
            set => _fieldOfView = Mathf.Clamp(value, MinFieldOfView, MaxFieldOfView);
        }

        public const float MinFieldOfView = 10f;
        // Wide enough for a fisheye shot: past 100° a flat picture stretches at the edges, which Fisheye pulls back in
        public const float MaxFieldOfView = 140f;
        public double OrthographicSize => 5.0;
        public float NearClipPlane => NearClip;

        /// <summary>
        /// Takes over the flight camera, starting exactly where the game's camera is now, in Free mode.
        /// </summary>
        /// <returns>False, with the reason logged, when there's no flight camera to take over.</returns>
        public bool Engage()
        {
            if (IsEngaged) return true;
            GameInstance game = GameManager.Instance?.Game;
            UniverseCameraManager cameraManager = game?.CameraManager;
            IPhysicsSpaceProvider physicsSpace = game?.UniverseView?.PhysicsSpace;
            if (cameraManager == null || physicsSpace == null)
            {
                _logError("Photo camera: can't take over the camera, the game's camera manager or physics space isn't there.");
                return false;
            }
            if (cameraManager.PrimaryScreenCameraID != CameraID.Flight)
            {
                _logError($"Photo camera: can't take over the camera, the screen shows the {cameraManager.PrimaryScreenCameraID} camera, not the flight camera.");
                return false;
            }

            // The first-person and part cameras override the flight camera's shot too; only one can
            _firstPersonTarget = cameraManager.IsFirstPersonCameraActive ? cameraManager.ActiveFirstPersonCameraTarget : null;
            if (cameraManager.ExitFirstPersonCamera(selectAutoMode: false))
                _logInfo("Photo camera: left the first-person camera; it's selected again when photo mode closes.");
            PartCameraController.Deactivate();

            ICameraShot current = cameraManager.GetCameraShotIncludeOverride(CameraID.Flight);
            if (current == null)
            {
                _logError("Photo camera: can't take over the camera, the flight camera has no shot to start from.");
                return false;
            }

            _cameraManager = cameraManager;
            _physicsSpace = physicsSpace;
            _position = current.CameraPosition;
            _fieldOfView = current.FieldOfView > 0 ? Mathf.Clamp((float)current.FieldOfView, MinFieldOfView, MaxFieldOfView) : 60.0;
            _rotation = current.CameraRotation;

            _gameInputWasEnabled = cameraManager.InputEnabled;
            cameraManager.DisableInput();
            cameraManager.SetCameraShotOverride(CameraID.Flight, this);
            cameraManager.RefreshCameraShot(CameraID.Flight);
            IsEngaged = true;
            SetMode(PhotoCameraMode.Free);
            _logInfo($"Photo camera: took over the flight camera at field of view {_fieldOfView:0.#}°.");
            return true;
        }

        /// <summary>Gives the flight camera back to the game. Safe to call when not engaged, and while the game is torn down.</summary>
        public void Release()
        {
            if (!IsEngaged) return;
            IsEngaged = false;
            _boostSeconds = 0f;
            _keyboardMouse.Reset();
            if (_cameraManager != null)
            {
                if (_cameraManager.GetCameraShotIncludeOverride(CameraID.Flight) == (ICameraShot)this)
                {
                    _cameraManager.ClearCameraShotOverride(CameraID.Flight);
                    _cameraManager.RefreshCameraShot(CameraID.Flight);
                }
                // Back to first person if that's where photo mode found the player (Redux re-finds the target, and says
                // no if it's gone, e.g. the kerbal boarded or the vessel is out of range)
                if (_firstPersonTarget != null && !_cameraManager.SelectFirstPersonCameraTarget(_firstPersonTarget))
                    _logInfo($"Photo camera: couldn't go back to {_firstPersonTarget.KerbalName}'s first-person view; it's no longer available.");
                if (_gameInputWasEnabled) _cameraManager.EnableInput();
            }
            _cameraManager = null;
            _physicsSpace = null;
            _firstPersonTarget = null;
            Target = null;
            Mode = PhotoCameraMode.Free;
            _logInfo("Photo camera: gave the flight camera back.");
        }

        /// <summary>
        /// Sets what Orbit circles. Changing it while orbiting swings around the new target from where the camera is.
        /// Null leaves nothing to orbit, which drops the camera to Free.
        /// </summary>
        public void SetTarget(OrbitTarget target)
        {
            if (Target == target) return;
            Target = target;
            if (Mode == PhotoCameraMode.Orbit)
            {
                if (target == null || !BeginOrbit()) SetMode(PhotoCameraMode.Free);
                else Changed?.Invoke();
            }
            else Changed?.Invoke();
        }

        /// <summary>Switches between flying and circling. Orbit needs a target that's still there.</summary>
        /// <returns>False, with <paramref name="message"/> saying why, when Orbit can't start.</returns>
        public bool SetMode(PhotoCameraMode mode, out string message)
        {
            if (!IsEngaged)
            {
                message = Localization.Translate("MunShot/Status/CameraOff");
                return false;
            }
            if (mode == PhotoCameraMode.Orbit)
            {
                if (Target == null)
                {
                    message = Localization.Translate("MunShot/Status/PickOrbitTarget");
                    return false;
                }
                if (!BeginOrbit())
                {
                    message = Localization.Format("MunShot/Status/OrbitTargetGone", Target.Name);
                    return false;
                }
            }
            SetMode(mode);
            message = mode == PhotoCameraMode.Orbit
                ? Localization.Format("MunShot/Status/Orbiting", Target.Name)
                : Localization.Translate("MunShot/Status/FlyingFree");
            return true;
        }

        /// <summary>
        /// Moves the camera from this frame's input. <paramref name="mouseTaken"/>: the mouse is over photo mode's own
        /// UI or dragging something, so its right button and wheel aren't the camera's. <paramref name="keyboardTaken"/>:
        /// a text field has the keyboard, so typing doesn't fly the camera.
        /// </summary>
        public void Tick(bool mouseTaken, bool keyboardTaken)
        {
            if (!IsEngaged) return;
            if (_cameraManager == null || _physicsSpace == null || GameManager.Instance?.Game?.CameraManager != _cameraManager)
            {
                _logError("Photo camera: the game's camera manager went away while photo mode was open; letting go.");
                Release();
                return;
            }

            float deltaTime = Time.unscaledDeltaTime;
            PhotoCameraIntent intent = _keyboardMouse.Read(mouseTaken, keyboardTaken);
            float speed = intent.Fast ? FastMultiplier : intent.Slow ? SlowMultiplier : 1f;

            // Look (Free) or circle (Orbit): both turn the camera, about its own up (mouse left and right), right (up and
            // down) and look (roll)
            Quaternion rotation = PhysicsRotation();
            float roll = intent.Roll * RollSpeed * speed * deltaTime;
            rotation = Quaternion.AngleAxis(intent.Look.x, rotation * Vector3.up)
                       * Quaternion.AngleAxis(-intent.Look.y, rotation * Vector3.right)
                       * Quaternion.AngleAxis(roll, rotation * Vector3.forward)
                       * rotation;
            if (intent.Level) rotation = Levelled(rotation);
            _rotation = _physicsSpace.PhysicsToRotation(rotation);

            if (Mode == PhotoCameraMode.Orbit) TickOrbit(rotation * Vector3.forward, intent, speed);
            else TickFree(rotation, intent, speed, deltaTime);
        }

        private void TickFree(Quaternion rotation, PhotoCameraIntent intent, float speed, float deltaTime)
        {
            Vector3 flying = rotation * new Vector3(intent.Move.x, 0f, intent.Move.y);

            float flySpeed = MunShotSettings.FlySpeed * speed;
            bool boosting = intent.Fast && flying.sqrMagnitude > 0.01f;
            _boostSeconds = boosting ? _boostSeconds + deltaTime : 0f;
            float heldSpeed = boosting
                ? Mathf.Min(flySpeed * Mathf.Pow(2f, _boostSeconds / BoostDoublingSeconds), Mathf.Max(MaxBoostSpeed, flySpeed))
                : flySpeed;
            Vector3 up = rotation * Vector3.up;
            Vector3 offset = Vector3.ClampMagnitude(flying, 1f) * (heldSpeed * deltaTime) + up * (intent.Steps * flySpeed * WheelStepSeconds);

            _position = _physicsSpace.PhysicsToPosition(PhysicsPosition() + offset);
        }

        private void TickOrbit(Vector3 forward, PhotoCameraIntent intent, float speed)
        {
            if (!Target.TryGetCentre(out Vector3 centre))
            {
                _logInfo($"Photo camera: {Target.Name} is gone; flying free.");
                Target = null;
                SetMode(PhotoCameraMode.Free);
                return;
            }
            float closer = Mathf.Pow(OrbitZoomPerStep, intent.Steps * speed);
            _orbitDistance = Mathf.Clamp(_orbitDistance * closer, MinOrbitDistance, MaxOrbitDistance);
            _position = _physicsSpace.PhysicsToPosition(centre - forward * _orbitDistance);
        }

        // Orbit starts from where the camera is: turned the least it can be to face the target, at the distance it's
        // at, so nothing jumps
        private bool BeginOrbit()
        {
            if (Target == null || !Target.TryGetCentre(out Vector3 centre)) return false;
            Quaternion rotation = PhysicsRotation();
            Vector3 toTarget = centre - PhysicsPosition();
            float distance = toTarget.magnitude;
            if (distance > 1e-3f)
                rotation = Quaternion.FromToRotation(rotation * Vector3.forward, toTarget / distance) * rotation;
            _rotation = _physicsSpace.PhysicsToRotation(rotation);
            _orbitDistance = Mathf.Clamp(distance, MinOrbitDistance, MaxOrbitDistance);
            _position = _physicsSpace.PhysicsToPosition(centre - rotation * Vector3.forward * _orbitDistance);
            return true;
        }

        private void SetMode(PhotoCameraMode mode)
        {
            bool changed = Mode != mode;
            Mode = mode;
            _boostSeconds = 0f;
            if (changed) _logInfo($"Photo camera: {(mode == PhotoCameraMode.Orbit ? $"orbiting {Target?.Name}" : "flying free")}.");
            Changed?.Invoke();
        }

        // The camera turned upright against the planet, still looking the same way: a straight horizon. Looking straight
        // up or down, it keeps the way the top of the picture points, levelled
        private Quaternion Levelled(Quaternion rotation)
        {
            Vector3 up = PlanetUp();
            Vector3 forward = rotation * Vector3.forward;
            if (Vector3.Cross(forward, up).sqrMagnitude > 1e-6f) return Quaternion.LookRotation(forward, up);
            Vector3 heading = Vector3.ProjectOnPlane(rotation * Vector3.up, up);
            return heading.sqrMagnitude > 1e-6f ? Quaternion.LookRotation(forward, heading) : rotation;
        }

        private Quaternion PhysicsRotation() => _physicsSpace.RotationToPhysics(_rotation);

        private Vector3 PhysicsPosition() => _physicsSpace.PositionToPhysics(_position);

        // Away from the centre of the body the active vessel orbits, at the camera: the same "up" the game gives
        // kerbals on EVA (KerbalBehavior's horizon up). The physics space's own up when there's no body to go by.
        private Vector3 PlanetUp() => Planet.UpAt(PhysicsPosition(), _physicsSpace.transform.up);

    }
}
