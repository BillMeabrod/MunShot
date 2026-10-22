using System;
using KSP.Game;
using KSP.Sim;
using KSP.Sim.impl;
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
    /// In both, Roll tilts the camera and Level takes the tilt off; Fast and Slow scale every movement. Flying with
    /// Fast held keeps speeding up the longer it's held, for long distances. The controls come from the keyboard and
    /// mouse and the gamepad together (<see cref="PhotoCameraIntent"/>), so both behave alike.
    /// Looking turns around the planet's up, like a tripod head, so the horizon stays put while you look around and
    /// tilt is a separate angle on top.
    ///
    /// Where the camera is and which way it looks are kept in the simulation's own terms (<see cref="Position"/>,
    /// <see cref="Vector"/>), so the floating origin moving under it doesn't move it. It runs on unscaled time: photo
    /// mode keeps the game paused.
    /// </summary>
    public sealed class PhotoCamera : ICameraShotProvider, ICameraShot, ICameraNearClipProvider
    {
        // Close enough to put the camera right up to a kerbal's face; what Redux's first-person camera uses
        private const float NearClip = 0.05f;

        // Looking never goes quite straight up or down, where "turn left" would have no meaning
        private const float MinAngleFromVertical = 1f;

        // Degrees per second of roll at normal speed
        private const float RollSpeed = 45f;

        // How far one mouse-wheel step moves the free camera up or down: this many seconds of flying at Fly Speed
        private const float WheelStepSeconds = 0.2f;

        // One mouse-wheel step brings the orbit camera this much closer (or further); holding up or down (d-pad)
        // brings it this many times closer per second
        private const float OrbitZoomPerStep = 0.9f;
        private const float OrbitZoomPerSecond = 2f;
        private const float MinOrbitDistance = 0.5f;
        private const float MaxOrbitDistance = 20000f;

        private const float FastMultiplier = 5f;
        private const float SlowMultiplier = 0.2f;

        // Holding Fast while flying builds speed, for crossing long distances (pad to runway): it starts at Fast and
        // doubles every this many seconds, up to the top speed. Letting go of Fast, or no longer flying, ends it.
        private const float BoostDoublingSeconds = 1f;
        private const float MaxBoostSpeed = 2000f;

        private readonly KeyboardMouseCameraInput _keyboardMouse;
        private readonly GamepadCameraInput _gamepad;
        private readonly Action<string> _logInfo;
        private readonly Action<string> _logError;

        private UniverseCameraManager _cameraManager;
        private IPhysicsSpaceProvider _physicsSpace;
        private bool _gameInputWasEnabled;

        // Where the camera is, which way it looks (always level: roll is kept apart) and its tilt
        private Position _position;
        private Vector _forward;
        private float _roll;
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

        public PhotoCamera(KeyboardMouseCameraInput keyboardMouse, GamepadCameraInput gamepad, Action<string> logInfo, Action<string> logError)
        {
            _keyboardMouse = keyboardMouse ?? throw new ArgumentNullException(nameof(keyboardMouse));
            _gamepad = gamepad ?? throw new ArgumentNullException(nameof(gamepad));
            _logInfo = logInfo ?? throw new ArgumentNullException(nameof(logInfo));
            _logError = logError ?? throw new ArgumentNullException(nameof(logError));
        }

        // ICameraShotProvider / ICameraShot / ICameraNearClipProvider: what the game's camera manager reads every frame
        public ICameraShot Shot => this;
        public Position CameraPosition => _position;
        public Rotation CameraRotation => _physicsSpace.PhysicsToRotation(PhysicsRotation());
        public Vector CameraForward => ToVector(PhysicsRotation() * Vector3.forward);
        public Vector CameraUp => ToVector(PhysicsRotation() * Vector3.up);
        public bool IsOrthographic => false;
        public double FieldOfView => _fieldOfView;
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
            if (cameraManager.ExitFirstPersonCamera(selectAutoMode: false))
                _logInfo("Photo camera: left the first-person camera; the flight camera goes back to its previous mode afterwards.");
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
            _fieldOfView = current.FieldOfView > 0 ? current.FieldOfView : 60.0;
            StartFrom(physicsSpace.RotationToPhysics(current.CameraRotation));

            _gameInputWasEnabled = cameraManager.InputEnabled;
            cameraManager.DisableInput();
            cameraManager.SetCameraShotOverride(CameraID.Flight, this);
            cameraManager.RefreshCameraShot(CameraID.Flight);
            IsEngaged = true;
            _gamepad.Enable();
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
            _gamepad.Disable();
            if (_cameraManager != null)
            {
                if (_cameraManager.GetCameraShotIncludeOverride(CameraID.Flight) == (ICameraShot)this)
                {
                    _cameraManager.ClearCameraShotOverride(CameraID.Flight);
                    _cameraManager.RefreshCameraShot(CameraID.Flight);
                }
                if (_gameInputWasEnabled) _cameraManager.EnableInput();
            }
            _cameraManager = null;
            _physicsSpace = null;
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
                message = "The photo camera isn't on.";
                return false;
            }
            if (mode == PhotoCameraMode.Orbit)
            {
                if (Target == null)
                {
                    message = "Pick something to orbit first.";
                    return false;
                }
                if (!BeginOrbit())
                {
                    message = $"{Target.Name} isn't there to orbit any more.";
                    return false;
                }
            }
            SetMode(mode);
            message = mode == PhotoCameraMode.Orbit ? $"Orbiting {Target.Name}." : "Flying free.";
            return true;
        }

        /// <summary>
        /// Moves the camera from this frame's input. <paramref name="pointerOverUi"/>: the mouse is over photo mode's
        /// own UI, so a right-click or wheel there is the UI's. <paramref name="keyboardTaken"/>: a text field has the
        /// keyboard, so typing doesn't fly the camera.
        /// </summary>
        public void Tick(bool pointerOverUi, bool keyboardTaken)
        {
            if (!IsEngaged) return;
            if (_cameraManager == null || _physicsSpace == null || GameManager.Instance?.Game?.CameraManager != _cameraManager)
            {
                _logError("Photo camera: the game's camera manager went away while photo mode was open; letting go.");
                Release();
                return;
            }

            float deltaTime = Time.unscaledDeltaTime;
            PhotoCameraIntent intent = _keyboardMouse.Read(pointerOverUi, keyboardTaken);
            intent.Add(_gamepad.Read(deltaTime));
            float speed = intent.Fast ? FastMultiplier : intent.Slow ? SlowMultiplier : 1f;

            // Look (Free) or circle (Orbit): both turn which way the camera faces
            Vector3 up = PlanetUp();
            Vector3 forward = Turn(PhysicsForward(up), up, intent.Look.x, -intent.Look.y);

            _roll = Mathf.Repeat(_roll + intent.Roll * RollSpeed * speed * deltaTime + 180f, 360f) - 180f;
            if (intent.Level) _roll = 0f;

            if (Mode == PhotoCameraMode.Orbit) TickOrbit(forward, intent, speed, deltaTime);
            else TickFree(forward, up, intent, speed, deltaTime);
        }

        private void TickFree(Vector3 forward, Vector3 up, PhotoCameraIntent intent, float speed, float deltaTime)
        {
            Vector3 right = Vector3.Cross(up, forward).normalized;
            Vector3 flying = forward * intent.Move.y + right * intent.Move.x + up * intent.Vertical;

            float flySpeed = MunShotSettings.FlySpeed * speed;
            bool boosting = intent.Fast && flying.sqrMagnitude > 0.01f;
            _boostSeconds = boosting ? _boostSeconds + deltaTime : 0f;
            float heldSpeed = boosting
                ? Mathf.Min(flySpeed * Mathf.Pow(2f, _boostSeconds / BoostDoublingSeconds), Mathf.Max(MaxBoostSpeed, flySpeed))
                : flySpeed;
            Vector3 offset = Vector3.ClampMagnitude(flying, 1f) * (heldSpeed * deltaTime) + up * (intent.Steps * flySpeed * WheelStepSeconds);

            SetPose(PhysicsPosition() + offset, forward);
        }

        private void TickOrbit(Vector3 forward, PhotoCameraIntent intent, float speed, float deltaTime)
        {
            if (!Target.TryGetCentre(out Vector3 centre))
            {
                _logInfo($"Photo camera: {Target.Name} is gone; flying free.");
                Target = null;
                SetMode(PhotoCameraMode.Free);
                return;
            }
            float closer = Mathf.Pow(OrbitZoomPerStep, intent.Steps * speed)
                           * Mathf.Pow(OrbitZoomPerSecond, -intent.Vertical * speed * deltaTime);
            _orbitDistance = Mathf.Clamp(_orbitDistance * closer, MinOrbitDistance, MaxOrbitDistance);
            SetPose(centre - forward * _orbitDistance, forward);
        }

        // Orbit starts from where the camera is: facing the target, at the distance it's at, so nothing jumps
        private bool BeginOrbit()
        {
            if (Target == null || !Target.TryGetCentre(out Vector3 centre)) return false;
            Vector3 toTarget = centre - PhysicsPosition();
            float distance = toTarget.magnitude;
            if (distance < 1e-3f) toTarget = PhysicsForward(PlanetUp());
            Vector3 up = PlanetUp();
            Vector3 forward = KeepOffVertical(toTarget.normalized, up);
            _orbitDistance = Mathf.Clamp(distance, MinOrbitDistance, MaxOrbitDistance);
            SetPose(centre - forward * _orbitDistance, forward);
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

        // The camera's starting look and tilt, read off a rotation: the look is where it points, the tilt how far its
        // up leans away from the planet's up around that look
        private void StartFrom(Quaternion rotation)
        {
            Vector3 up = PlanetUp();
            Vector3 forward = KeepOffVertical(rotation * Vector3.forward, up);
            Quaternion level = Quaternion.LookRotation(forward, up);
            _roll = Vector3.SignedAngle(level * Vector3.up, rotation * Vector3.up, forward);
            _forward = ToVector(forward);
        }

        private void SetPose(Vector3 physicsPosition, Vector3 forward)
        {
            _position = _physicsSpace.PhysicsToPosition(physicsPosition);
            _forward = ToVector(forward);
        }

        // Yaw turns around the planet's up; pitch around the camera's level right, never past straight up or down
        private static Vector3 Turn(Vector3 forward, Vector3 up, float yawDegrees, float pitchDegrees)
        {
            forward = Quaternion.AngleAxis(yawDegrees, up) * forward;
            Vector3 right = Vector3.Cross(up, forward).normalized;
            float fromUp = Vector3.Angle(up, forward);
            float pitch = Mathf.Clamp(pitchDegrees, MinAngleFromVertical - fromUp, 180f - MinAngleFromVertical - fromUp);
            return (Quaternion.AngleAxis(pitch, right) * forward).normalized;
        }

        private static Vector3 KeepOffVertical(Vector3 forward, Vector3 up)
        {
            float fromUp = Vector3.Angle(up, forward);
            if (fromUp >= MinAngleFromVertical && fromUp <= 180f - MinAngleFromVertical) return forward.normalized;
            // Straight up or down: lean towards any horizontal direction
            Vector3 horizontal = Vector3.Cross(up, Mathf.Abs(Vector3.Dot(up, Vector3.right)) < 0.9f ? Vector3.right : Vector3.forward).normalized;
            return Turn(horizontal, up, 0f, fromUp < 90f ? -(90f - MinAngleFromVertical) : 90f - MinAngleFromVertical);
        }

        private Quaternion PhysicsRotation()
        {
            Vector3 up = PlanetUp();
            Vector3 forward = PhysicsForward(up);
            return Quaternion.AngleAxis(_roll, forward) * Quaternion.LookRotation(forward, up);
        }

        private Vector3 PhysicsForward(Vector3 up) => KeepOffVertical(_physicsSpace.VectorToPhysics(_forward), up);

        private Vector3 PhysicsPosition() => _physicsSpace.PositionToPhysics(_position);

        // Away from the centre of the body the active vessel orbits, at the camera: the same "up" the game gives
        // kerbals on EVA (KerbalBehavior's horizon up). The physics space's own up when there's no body to go by.
        private Vector3 PlanetUp()
        {
            CelestialBodyComponent body = GameManager.Instance?.Game?.ViewController?.GetActiveSimVessel()?.mainBody;
            if (body == null) return _physicsSpace.transform.up;
            // Named as the game names them: Delta's parameters are declared end first
            Vector away = MathDP.Normalize(MathDP.Delta(start: new Position(body.transform.celestialFrame, Vector3d.zero), end: _position));
            Vector3 up = _physicsSpace.VectorToPhysics(away);
            return up.sqrMagnitude > 0.5f ? up.normalized : _physicsSpace.transform.up;
        }

        private Vector ToVector(Vector3 v) => _physicsSpace.PhysicsToVector(v);
    }
}
