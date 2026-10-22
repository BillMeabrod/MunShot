using System;
using System.Collections.Generic;
using System.Linq;
using KSP.Game;
using KSP.Input;
using KSP.Sim.impl;
using MunShot.Cameras;
using MunShot.Capture;
using MunShot.Posing;
using UnityEngine;
using UnityEngine.InputSystem;
using ILogger = SpaceWarp2.API.Logging.ILogger;

namespace MunShot
{
    /// <summary>
    /// Photo mode itself, apart from any window showing it. Opening it pauses the game, hides the game's UI, locks the
    /// game's controls and hands the camera to the <see cref="PhotoCamera"/>; closing it puts all of that back and
    /// takes every photo double out, so the scene is exactly as it was. In between it holds the photo session (the
    /// doubles, their poses, helmets and handles), the camera and photo capture, and everything the UI offers on them.
    /// The player's window and the developer's pose lab both work through it; neither owns any of this.
    /// </summary>
    public sealed class PhotoMode : IDisposable
    {
        private readonly ILogger _logger;
        private readonly GamePause _pause;
        private readonly UiHider _uiHider;
        private readonly PhotoModeInputLock _inputLock;
        private InputAction _screenshotKey;

        // What the camera can orbit: the active vessel, then the built doubles. Each target is made once and kept, so
        // the camera's target stays the same object while the list is rebuilt.
        private readonly List<OrbitTarget> _orbitTargets = new List<OrbitTarget>();
        private readonly Dictionary<PhotoSession.Entry, OrbitTarget> _doubleTargets = new Dictionary<PhotoSession.Entry, OrbitTarget>();
        private VesselComponent _targetVessel;
        private OrbitTarget _vesselTarget;

        public PhotoSession Session { get; }
        public DoubleManipulator Manipulator { get; }
        public PhotoCamera Camera { get; }
        public PhotoCapture Capture { get; }
        public IReadOnlyList<PosePreset> Presets { get; }

        /// <summary>
        /// Who can be brought in: the kerbals in the scene when photo mode opened (nearest first), then everyone else
        /// in the roster. Listed once on opening: flying the camera away doesn't change who's "in this scene".
        /// </summary>
        public KerbalCandidates.Listing Kerbals { get; private set; } = new KerbalCandidates.Listing();

        public IReadOnlyList<OrbitTarget> OrbitTargets => _orbitTargets;

        public bool IsOpen { get; private set; }

        /// <summary>
        /// Whether photo mode's own UI is hidden to frame the shot: its panels, the handles and the hover outline. The
        /// camera still moves.
        /// </summary>
        public bool UiHidden { get; private set; }

        /// <summary>Raised after photo mode opens or closes.</summary>
        public event Action<bool> OpenChanged;

        /// <summary>Raised when anything the UI shows has changed: doubles, selection, tools, camera.</summary>
        public event Action Changed;

        /// <summary>A message for the player about what just happened.</summary>
        public event Action<string> Status;

        public PhotoMode(ILogger logger, PhotoSession session, PhotoCamera camera, PhotoCapture capture, IReadOnlyList<PosePreset> presets)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            Session = session ?? throw new ArgumentNullException(nameof(session));
            Camera = camera ?? throw new ArgumentNullException(nameof(camera));
            Capture = capture ?? throw new ArgumentNullException(nameof(capture));
            Presets = presets ?? Array.Empty<PosePreset>();
            _pause = new GamePause(Info, Warn);
            _uiHider = new UiHider(Info, Warn);
            _inputLock = new PhotoModeInputLock(Info, Warn);
            Manipulator = new DoubleManipulator(session, Info, message => _logger.LogError(message));

            Session.EntryChanged += OnEntryChanged;
            Manipulator.Selected += OnDoubleClicked;
            Camera.Changed += RaiseChanged;
            Capture.Finished += message =>
            {
                if (message != null) Status?.Invoke(message);
            };
        }

        /// <summary>
        /// Opens photo mode, in flight only. Lists everyone who can be brought in, as of now.
        /// </summary>
        /// <returns>False, with a status saying why, when it can't open here.</returns>
        public bool Open()
        {
            if (IsOpen) return true;
            GameInstance game = GameManager.Instance?.Game;
            if (game?.GlobalGameState == null || game.GlobalGameState.GetState() != GameState.FlightView)
            {
                Status?.Invoke(Localization.Translate("MunShot/Status/OnlyInFlight", "Photo mode is only available in flight."));
                _logger.LogInfo($"Photo mode: not opened; the game is in {game?.GlobalGameState?.GetState().ToString() ?? "no state yet"}, not flight.");
                return false;
            }

            IsOpen = true;
            UiHidden = false;
            _pause.Engage();
            _inputLock.Engage();
            _uiHider.Engage();
            Camera.Engage();
            ListenToScreenshotKey(true);

            UnityEngine.Camera view = CurrentCamera();
            if (view == null) _logger.LogWarning("Photo mode: no camera; kerbals are listed by distance from the scene's origin instead.");
            Kerbals = KerbalCandidates.FindAll(view != null ? view.transform.position : Vector3.zero, Info, Warn);
            RefreshOrbitTargets();

            OpenChanged?.Invoke(true);
            RaiseChanged();
            return true;
        }

        /// <summary>
        /// Closes photo mode: every double goes and every original comes back, and the game's camera, controls, UI and
        /// time are as they were. Safe to call when not open, and while the game is being torn down.
        /// </summary>
        public void Close()
        {
            if (!IsOpen) return;
            IsOpen = false;
            UiHidden = false;
            bool hadDoubles = Session.Entries.Count > 0;
            Session.Close();
            Manipulator.Dispose();
            _doubleTargets.Clear();
            _orbitTargets.Clear();
            _targetVessel = null;
            _vesselTarget = null;
            ListenToScreenshotKey(false);
            Capture.Dispose();
            Camera.Release();
            _uiHider.Release();
            _inputLock.Release();
            _pause.Release();
            Kerbals = new KerbalCandidates.Listing();
            if (hadDoubles) _logger.LogInfo("Photo mode: closed; doubles removed and originals shown again.");

            OpenChanged?.Invoke(false);
            RaiseChanged();
        }

        public void Dispose()
        {
            Close();
            Session.EntryChanged -= OnEntryChanged;
            Manipulator.Selected -= OnDoubleClicked;
            Camera.Changed -= RaiseChanged;
        }

        /// <summary>
        /// Called every frame. <paramref name="pointerOverUi"/>: the mouse is over one of photo mode's windows, so
        /// clicks there aren't for the scene. <paramref name="typing"/>: a text field has the keyboard.
        /// </summary>
        public void Tick(bool pointerOverUi, bool typing)
        {
            _pause.Tick();
            _uiHider.Tick();
            Capture.Tick();
            Session.Tick();
            if (!IsOpen) return;

            // While a photo is taken the handles are hidden and the camera holds still; while the UI is hidden to
            // frame the shot, the handles are too but the camera moves
            if (Capture.IsCapturing)
            {
                Manipulator.HideAll();
                return;
            }
            if (UiHidden)
            {
                Manipulator.HideAll();
                Camera.Tick(false, typing);
                return;
            }

            // The game's own cursor. Fast (Shift) is the fine snap modifier, as Shift is in the VAB
            global::Mouse.MouseButton left = global::Mouse.Left;
            if (left != null)
                Manipulator.Tick(CurrentCamera(), global::Mouse.Position, left.WasPressedThisFrame(), left.IsPressed(),
                                 left.WasReleasedThisFrame(), pointerOverUi, MunShotSettings.Fast.Held);
            Camera.Tick(pointerOverUi, typing);
        }

        // ---- Kerbals ----

        /// <summary>Those of <paramref name="candidates"/> who aren't in the session yet.</summary>
        public IEnumerable<KerbalCandidate> NotYetIn(IEnumerable<KerbalCandidate> candidates) =>
            candidates.Where(c => !Session.Contains(c.Info));

        /// <summary>Builds a double of <paramref name="candidate"/> in front of the camera, and selects it.</summary>
        public void Bring(KerbalCandidate candidate)
        {
            if (!IsOpen || candidate == null) return;
            if (Session.Contains(candidate.Info))
            {
                Status?.Invoke(Localization.Format("MunShot/Status/AlreadyIn", "{0} is already in.", candidate.Name));
                return;
            }
            Session.Bring(candidate, CurrentCamera(), message =>
            {
                Status?.Invoke(message);
                RaiseChanged();
            });
        }

        /// <summary>Selects a double (for posing, the handles, and as what Orbit circles).</summary>
        public void Select(PhotoSession.Entry entry)
        {
            if (entry == null) return;
            Session.Select(entry);
            Manipulator.ShowHandles();
            Camera.SetTarget(OrbitTargetFor(entry));
            RaiseChanged();
        }

        /// <summary>Hides or shows photo mode's own UI, to frame the shot.</summary>
        public void ToggleUiHidden()
        {
            if (!IsOpen) return;
            UiHidden = !UiHidden;
            RaiseChanged();
        }

        /// <summary>Takes a double out; their original comes back.</summary>
        public void Remove(PhotoSession.Entry entry)
        {
            if (entry == null) return;
            string name = entry.Double.Source.Name;
            Session.Remove(entry);
            RefreshOrbitTargets();
            Status?.Invoke(Localization.Format("MunShot/Status/BackWhereTheyWere", "{0} is back where they were.", name));
            _logger.LogInfo($"Photo mode: {name}'s double removed.");
            RaiseChanged();
        }

        // ---- The selected double ----

        public void HoldPreset(PosePreset preset, PoseVariant variant)
        {
            PhotoSession.Entry selected = ReadySelected();
            if (selected == null || preset == null) return;
            ClipPose? pose = preset.Get(variant);
            if (!pose.HasValue)
            {
                Status?.Invoke(variant == PoseVariant.Jetpack
                    ? Localization.Format("MunShot/Status/NoJetpackVersion", "{0} has no jetpack version.", preset.Name)
                    : Localization.Format("MunShot/Status/NoGroundVersion", "{0} has no ground version.", preset.Name));
                return;
            }
            if (!selected.Player.Hold(selected.Double.Animator, pose.Value))
                Status?.Invoke(Localization.Format("MunShot/Status/CouldNotHold", "Couldn't hold {0}; see the log.", preset.Name));
            RaiseChanged();
        }

        public void ReleasePose()
        {
            ReadySelected()?.Player.Release();
            RaiseChanged();
        }

        public void SetHelmet(bool on)
        {
            PhotoSession.Entry selected = ReadySelected();
            if (selected == null) return;
            selected.Double.SetHelmet(on);
            RaiseChanged();
        }

        public void SetVisor(bool on)
        {
            PhotoSession.Entry selected = ReadySelected();
            if (selected == null) return;
            selected.Double.SetVisor(on);
            if (selected.Double.VisorWarning != null) _logger.LogWarning(selected.Double.VisorWarning);
            RaiseChanged();
        }

        public void SetTool(HandleMode mode)
        {
            Manipulator.SetMode(mode);
            RaiseChanged();
        }

        public void SetAngleSnap(bool on)
        {
            Manipulator.AngleSnap = on;
            RaiseChanged();
        }

        public void SnapToSurface()
        {
            Session.DropToSurface(Session.Selected, out string message);
            Status?.Invoke(message);
        }

        public void StandUpright()
        {
            Session.StandUpright(Session.Selected, out string message);
            Status?.Invoke(message);
        }

        // ---- Camera and photo ----

        public void SetCameraMode(PhotoCameraMode mode)
        {
            if (mode == PhotoCameraMode.Orbit && Camera.Target == null) Camera.SetTarget(_orbitTargets.FirstOrDefault());
            if (!Camera.SetMode(mode, out string message)) _logger.LogInfo($"Photo camera: {message}");
            Status?.Invoke(message);
        }

        public void SetOrbitTarget(OrbitTarget target)
        {
            if (target != null) Camera.SetTarget(target);
        }

        public void TakePhoto() => Capture.Take();

        // ---- Inside ----

        private PhotoSession.Entry ReadySelected()
        {
            PhotoSession.Entry selected = Session.Selected;
            if (selected != null && selected.IsReady) return selected;
            Status?.Invoke(selected == null
                ? Localization.Translate("MunShot/Status/BringKerbalFirst", "Bring a kerbal in first.")
                : Localization.Translate("MunShot/Status/StillBuilding", "The double is still being built."));
            return null;
        }

        private void OnEntryChanged(PhotoSession.Entry entry)
        {
            RefreshOrbitTargets();
            if (entry.IsReady)
                Status?.Invoke(Localization.Format("MunShot/Status/InFrontOfCamera", "{0} is in front of the camera.", entry.Double.Source.Name));
            else
                Status?.Invoke(Localization.Format("MunShot/Status/BuildFailed", "Couldn't build {0}'s double; see the log.", entry.Double.Source.Name));
            RaiseChanged();
        }

        // A double clicked: posing and the orbit target follow it
        private void OnDoubleClicked(PhotoSession.Entry entry)
        {
            Camera.SetTarget(OrbitTargetFor(entry));
            Status?.Invoke(Localization.Format("MunShot/Status/Selected", "Selected {0}.", entry.Double.Source.Name));
            RaiseChanged();
        }

        private void RaiseChanged() => Changed?.Invoke();

        private void Info(string message) => _logger.LogInfo(message);
        private void Warn(string message) => _logger.LogWarning(message);

        private void RefreshOrbitTargets()
        {
            _orbitTargets.Clear();
            OrbitTarget vessel = ActiveVesselTarget();
            if (vessel != null) _orbitTargets.Add(vessel);
            foreach (PhotoSession.Entry entry in Session.Entries)
                if (entry.IsReady)
                    _orbitTargets.Add(OrbitTargetFor(entry));

            // A target that's left the list (its double went) is no longer orbited
            if (Camera.Target != null && !_orbitTargets.Contains(Camera.Target)) Camera.SetTarget(null);
        }

        // Made again only when the active vessel changes
        private OrbitTarget ActiveVesselTarget()
        {
            VesselComponent vessel = GameManager.Instance?.Game?.ViewController?.GetActiveSimVessel();
            if (vessel != _targetVessel)
            {
                _targetVessel = vessel;
                _vesselTarget = Cameras.OrbitTargets.Vessel(vessel);
            }
            return _vesselTarget;
        }

        // A double is circled around the middle of its body, wherever it's been moved to
        private OrbitTarget OrbitTargetFor(PhotoSession.Entry entry)
        {
            foreach (PhotoSession.Entry gone in _doubleTargets.Keys.Where(e => !Session.Entries.Contains(e)).ToList())
                _doubleTargets.Remove(gone);
            if (!_doubleTargets.TryGetValue(entry, out OrbitTarget target))
            {
                target = new OrbitTarget(entry.Double.Source.Name, () =>
                    Session.Entries.Contains(entry) && PhotoSession.BoundsOf(entry, out Bounds bounds) ? bounds.center : (Vector3?)null);
                _doubleTargets[entry] = target;
            }
            return target;
        }

        // In photo mode the game's screenshot key takes the photo without photo mode's UI. Redux takes the screenshot
        // for the key itself; PhotoCapture joins that capture and hides the UI for it
        private void ListenToScreenshotKey(bool listen)
        {
            if (_screenshotKey != null)
            {
                _screenshotKey.performed -= OnScreenshotKey;
                _screenshotKey = null;
            }
            if (!listen) return;
            GameInput input = GameManager.Instance?.Game?.Input;
            _screenshotKey = input != null ? input.Global.TakeScreenshot : null;
            if (_screenshotKey != null) _screenshotKey.performed += OnScreenshotKey;
            else _logger.LogWarning("Photo mode: the game's screenshot key isn't available; use Take photo instead.");
        }

        private void OnScreenshotKey(InputAction.CallbackContext context) => Capture.Take();

        private static UnityEngine.Camera CurrentCamera() => GameManager.Instance?.Game?.GraphicsManager?.GetCurrentUnityCamera();
    }
}
