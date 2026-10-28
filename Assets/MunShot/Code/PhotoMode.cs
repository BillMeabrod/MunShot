using System;
using System.Collections.Generic;
using System.Linq;
using KSP.Game;
using KSP.Input;
using KSP.Sim.impl;
using MunShot.Cameras;
using MunShot.Capture;
using MunShot.Lighting;
using MunShot.Posing;
using UnityEngine;
using UnityEngine.InputSystem;
using ILogger = SpaceWarp2.API.Logging.ILogger;

namespace MunShot
{
    /// <summary>
    /// Photo mode itself, apart from any window showing it. Opening it pauses the game, hides the game's UI, locks the
    /// game's controls, hands the camera to the <see cref="PhotoCamera"/> and lays photo mode's own look over the game's
    /// (<see cref="PhotoLook"/>); closing it puts all of that back and takes every photo double and light out, so the
    /// scene is exactly as it was. In between it holds the photo session (the doubles, their poses, helmets and
    /// handles), the camera, the look, photo mode's own lights and photo capture, and everything the UI offers on them.
    /// The player's window and the developer's pose lab both work through it; neither owns any of this.
    /// </summary>
    public sealed class PhotoMode
    {
        private readonly ILogger _logger;
        private readonly Func<IReadOnlyList<PosePreset>> _loadPresets;
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
        public PlaceableManipulator Manipulator { get; }
        public PhotoCamera Camera { get; }
        public PhotoLook Look { get; }
        public PhotoLights Lights { get; }
        public PhotoCapture Capture { get; }
        /// <summary>The pose presets, read from the mod's pose files again each time photo mode opens.</summary>
        public IReadOnlyList<PosePreset> Presets { get; private set; } = Array.Empty<PosePreset>();

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

        /// <param name="loadPresets">
        /// Reads the pose files; called each time photo mode opens, so poses added or changed while the game runs show
        /// the next time it's opened.
        /// </param>
        public PhotoMode(ILogger logger, PhotoSession session, PhotoCamera camera, PhotoCapture capture,
                         Func<IReadOnlyList<PosePreset>> loadPresets)
        {
            _loadPresets = loadPresets ?? throw new ArgumentNullException(nameof(loadPresets));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            Session = session ?? throw new ArgumentNullException(nameof(session));
            Camera = camera ?? throw new ArgumentNullException(nameof(camera));
            Capture = capture ?? throw new ArgumentNullException(nameof(capture));
            _pause = new GamePause(Info, Warn);
            _uiHider = new UiHider(Info, Warn);
            _inputLock = new PhotoModeInputLock(Info, Warn);
            Look = new PhotoLook(Info, Warn);
            Lights = new PhotoLights(() => session.KerbalLayer, Info, Warn);
            Manipulator = new PlaceableManipulator(session, Lights, Look, Info, message => _logger.LogError(message));

            Session.EntryChanged += OnEntryChanged;
            Manipulator.Selected += OnClicked;
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
                Status?.Invoke(Localization.Translate("MunShot/Status/OnlyInFlight"));
                _logger.LogInfo($"Photo mode: not opened; the game is in {game?.GlobalGameState?.GetState().ToString() ?? "no state yet"}, not flight.");
                return false;
            }

            // The camera first: without it photo mode is no use, so if it can't be taken over (the reason is logged),
            // nothing else is touched
            if (!Camera.Engage())
            {
                Status?.Invoke(Localization.Translate("MunShot/Status/CameraUnavailable"));
                return false;
            }
            Presets = _loadPresets() ?? Array.Empty<PosePreset>();

            IsOpen = true;
            UiHidden = false;
            PartHoverOutlines.Suppressed = true;
            // A part the mouse was over as photo mode opened is still outlined; the game won't take it away now
            game.UI?.OutlineManager?.ClearOutlines();
            _pause.Engage();
            _inputLock.Engage();
            _uiHider.Engage();
            Look.Engage();
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
            Manipulator.Reset();
            _doubleTargets.Clear();
            _orbitTargets.Clear();
            _targetVessel = null;
            _vesselTarget = null;
            ListenToScreenshotKey(false);
            Capture.Cancel();
            Camera.Release();
            Look.Release();
            Lights.Clear();
            _uiHider.Release();
            _inputLock.Release();
            _pause.Release();
            PartHoverOutlines.Suppressed = false;
            Kerbals = new KerbalCandidates.Listing();
            if (hadDoubles) _logger.LogInfo("Photo mode: closed; doubles removed and originals shown again.");

            OpenChanged?.Invoke(false);
            RaiseChanged();
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
            Look.Tick(CurrentCamera());
            // The lights' markers are for placing them: not in the photo, nor while the UI is hidden to frame it
            Lights.ShowMarkers(!UiHidden && !Capture.IsCapturing);

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

            // The game's own cursor. Fast (Shift) also adds to the selection on a click or box, and is the fine snap
            // modifier while turning a ring, as Shift is in the VAB. The right button held too turns dragged doubles; the
            // tilt key leans them, or with the tip key tips them
            global::Mouse.MouseButton left = global::Mouse.Left;
            bool rightHeld = global::Mouse.Right != null && global::Mouse.Right.IsPressed();
            if (left != null)
                Manipulator.Tick(CurrentCamera(), global::Mouse.Position, left.WasPressedThisFrame(), left.IsPressed(),
                                 left.WasReleasedThisFrame(), pointerOverUi, MunShotSettings.Fast.Held, MunShotSettings.Fast.Held,
                                 rightHeld, MunShotSettings.TiltWhileDragging.Held, MunShotSettings.TipWhileDragging.Held);
            // While something's dragged, the right button and wheel are the drag's, not the camera's
            Camera.Tick(pointerOverUi || Manipulator.IsDragging, typing);
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
                Status?.Invoke(Localization.Format("MunShot/Status/AlreadyIn", candidate.Name));
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
            RemoveAll(new[] { entry });
        }

        /// <summary>
        /// Takes out everything selected for moving (the outlined ones): doubles, whose originals come back where
        /// they're in the scene, and lights.
        /// </summary>
        public void RemoveMoving()
        {
            if (!IsOpen || Manipulator.Moving.Count == 0) return;
            List<PhotoSession.Entry> doubles = Manipulator.Moving.OfType<PhotoSession.Entry>().ToList();
            List<PhotoLight> lights = Manipulator.Moving.OfType<PhotoLight>().ToList();
            foreach (PhotoLight light in lights) Lights.Remove(light);
            if (doubles.Count > 0) RemoveAll(doubles, announce: lights.Count == 0);
            // Only lights taken out: the Light tab's selection, if any are left, gets the handles
            else if (Lights.Selected != null) Manipulator.Select(Lights.Selected);

            if (doubles.Count > 0 && lights.Count > 0)
                Status?.Invoke(Localization.Format("MunShot/Status/KerbalsAndLightsRemoved", doubles.Count, lights.Count));
            else if (lights.Count > 0)
                Status?.Invoke(LightsRemovedNote(lights));
            RaiseChanged();
        }

        // announce: say who's back (left out when more was taken out, which the caller says instead)
        private void RemoveAll(IReadOnlyList<PhotoSession.Entry> entries, bool announce = true)
        {
            foreach (PhotoSession.Entry entry in entries)
            {
                _logger.LogInfo($"Photo mode: {entry.Double.Source.Name}'s double removed.");
                Session.Remove(entry);
            }
            RefreshOrbitTargets();
            if (announce)
                Status?.Invoke(entries.Count == 1
                    ? Localization.Format("MunShot/Status/BackWhereTheyWere", entries[0].Double.Source.Name)
                    : Localization.Format("MunShot/Status/AllBack", entries.Count));
            RaiseChanged();
        }

        // ---- The selected double ----

        /// <summary>
        /// Holds a pose on the selected double, null for its own animation, with its jetpack as it's set
        /// (<see cref="SetJetpack"/>); false (with a status note) if it couldn't.
        /// </summary>
        public bool HoldPreset(PosePreset preset)
        {
            PhotoSession.Entry selected = ReadySelected();
            if (selected == null) return false;
            bool held = Hold(selected, preset);
            RaiseChanged();
            return held;
        }

        /// <summary>Deploys or folds the selected double's jetpack, keeping its pose.</summary>
        public void SetJetpack(bool deployed)
        {
            PhotoSession.Entry selected = ReadySelected();
            if (selected == null) return;
            selected.Jetpack = deployed;
            Hold(selected, selected.Preset);
            RaiseChanged();
        }

        // A pose's own clip for the jetpack's setting has the jetpack right; otherwise (the other version's clip, a pose
        // of bones only, or the double's own animation) the jetpack's bones are laid under the pose's own
        private bool Hold(PhotoSession.Entry entry, PosePreset preset)
        {
            Animator animator = entry.Double.Animator;
            ClipPose? clip = null;
            bool exact = false;
            if (preset != null) clip = preset.ClipFor(entry.Jetpack, out exact);

            if (preset == null && !entry.Jetpack)
            {
                // The double's own animation has the jetpack folded
                entry.Player.Release();
                entry.Preset = null;
                return true;
            }

            BonePose jetpack = null;
            if (!exact)
            {
                jetpack = JetpackBones.For(animator, entry.Jetpack, out string problem);
                if (jetpack == null)
                    _logger.LogWarning($"Photo mode: {entry.Double.Source.Name}'s jetpack can't be {(entry.Jetpack ? "deployed" : "folded")} for "
                                       + $"'{preset?.DisplayName ?? "their own animation"}': {problem}.");
            }

            bool held = entry.Player.Hold(animator, clip, BonePose.Over(jetpack, preset?.Bones), preset?.Eyes, preset?.Lashes ?? false);
            if (held) entry.Preset = preset;
            else Status?.Invoke(Localization.Format("MunShot/Status/CouldNotHold", preset?.DisplayName ?? Localization.Translate(Localization.NoPose)));
            return held;
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

        /// <summary>Stands every double selected for moving (or the posed one, when none are) on the surface below.</summary>
        public void SnapToSurface() =>
            ForEachToPlace(Session.DropToSurface, "MunShot/Status/AllOnSurface");

        /// <summary>
        /// Turns every double selected for moving (or the posed one, when none are) upright on screen: along the
        /// camera's up, as in space, where there's no up. Snap to surface stands them on the ground instead.
        /// </summary>
        public void StandUpright()
        {
            UnityEngine.Camera camera = CurrentCamera();
            if (camera == null)
            {
                Status?.Invoke(Localization.Translate("MunShot/Status/NoCamera"));
                return;
            }
            Vector3 up = camera.transform.up;
            ForEachToPlace((PhotoSession.Entry entry, out string message) => Session.StandUpright(entry, up, out message),
                           "MunShot/Status/AllUpright");
        }

        // Runs a placing action on the moving selection, or the posed double when nothing's selected for moving. One
        // double says what happened to it; several say how many it worked for (allKey, with {0} for the count), or the
        // first that it didn't
        private delegate bool PlaceAction(PhotoSession.Entry entry, out string message);

        private void ForEachToPlace(PlaceAction place, string allKey)
        {
            var targets = Manipulator.Moving.OfType<PhotoSession.Entry>().ToList();
            if (targets.Count == 0) targets.Add(Session.Selected);
            string first = null, failure = null;
            int placed = 0;
            foreach (PhotoSession.Entry entry in targets)
            {
                if (place(entry, out string message)) placed++;
                else failure ??= message;
                first ??= message;
            }
            Status?.Invoke(targets.Count == 1 ? first : failure ?? Localization.Format(allKey, placed));
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

        // ---- Look ----

        public void SetDepthOfField(bool on)
        {
            Look.DepthOfField = on;
            RaiseChanged();
        }

        /// <summary>
        /// Sets the focus, in metres; <see cref="float.PositiveInfinity"/> focuses on everything far off. Turns depth of
        /// field on, as moving the focus is asking for it.
        /// </summary>
        public void SetFocusDistance(float metres)
        {
            Look.DepthOfField = true;
            Look.FocusDistance = float.IsPositiveInfinity(metres)
                ? metres
                : Mathf.Clamp(metres, PhotoLook.MinFocusDistance, PhotoLook.MaxFocusDistance);
            RaiseChanged();
        }

        // ---- Lights ----

        /// <summary>Adds a light a little in front of the camera, shining where it looks, and selects it.</summary>
        public void AddLight()
        {
            if (!IsOpen) return;
            if (!Lights.CanAdd)
            {
                Status?.Invoke(Localization.Format("MunShot/Status/LightsFull", PhotoLights.MaxLights));
                return;
            }
            UnityEngine.Camera camera = CurrentCamera();
            if (camera == null)
            {
                Status?.Invoke(Localization.Translate("MunShot/Status/NoCamera"));
                return;
            }
            PhotoLight light = Lights.Add(camera, PhotoSession.ActivePhysicsSpace());
            if (light == null) return;
            Manipulator.Select(light);
            Status?.Invoke(Localization.Format("MunShot/Status/LightAdded", LightName(light)));
            RaiseChanged();
        }

        /// <summary>Takes a light away; if it had the handles, the Light tab's next selection gets them.</summary>
        public void RemoveLight(PhotoLight light)
        {
            if (light == null) return;
            bool hadHandles = Manipulator.Moving.Contains(light);
            Lights.Remove(light);
            if (hadHandles && Lights.Selected != null) Manipulator.Select(Lights.Selected);
            Status?.Invoke(LightsRemovedNote(new[] { light }));
            RaiseChanged();
        }

        private static string LightsRemovedNote(IReadOnlyList<PhotoLight> lights) =>
            lights.Count == 1
                ? Localization.Format("MunShot/Status/LightRemoved", LightName(lights[0]))
                : Localization.Format("MunShot/Status/LightsRemoved", lights.Count);

        /// <summary>Selects a light, for the Light tab to set and the handles to move.</summary>
        public void SelectLight(PhotoLight light)
        {
            Lights.Select(light);
            Manipulator.Select(light);
            RaiseChanged();
        }

        /// <summary>Changes the selected light, if there is one, and tells the UI.</summary>
        public void ChangeLight(Action<PhotoLight> change)
        {
            if (Lights.Selected == null) return;
            change(Lights.Selected);
            RaiseChanged();
        }

        /// <summary>A light's name, as lists and notes show it ("Light 1").</summary>
        public static string LightName(PhotoLight light) => Localization.Format("MunShot/UI/LightName", light.Number);

        // ---- Color ----

        public void SetExposure(float stops)
        {
            Look.Exposure = Mathf.Clamp(stops, -PhotoLook.MaxExposure, PhotoLook.MaxExposure);
            RaiseChanged();
        }

        public void SetContrast(float amount)
        {
            Look.Contrast = Mathf.Clamp(amount, -PhotoLook.MaxColorAdjustment, PhotoLook.MaxColorAdjustment);
            RaiseChanged();
        }

        public void SetSaturation(float amount)
        {
            Look.Saturation = Mathf.Clamp(amount, -PhotoLook.MaxColorAdjustment, PhotoLook.MaxColorAdjustment);
            RaiseChanged();
        }

        public void SetWarmth(float amount)
        {
            Look.Warmth = Mathf.Clamp(amount, -PhotoLook.MaxColorAdjustment, PhotoLook.MaxColorAdjustment);
            RaiseChanged();
        }

        /// <summary>Puts the colour settings back to the game's own picture.</summary>
        public void ResetColor()
        {
            Look.ResetColor();
            RaiseChanged();
        }

        /// <summary>How much the picture bulges, as through a fisheye lens: 0 (flat) to 1.</summary>
        public void SetFisheye(float amount)
        {
            Look.Fisheye = Mathf.Clamp01(amount);
            RaiseChanged();
        }

        /// <summary>The shape photos are cropped to (the guides shade what's cut off).</summary>
        public void SetFrameShape(FrameShape shape)
        {
            Capture.Crop = shape;
            RaiseChanged();
        }

        /// <summary>How much the picture darkens towards its edges: 0 (none) to 1.</summary>
        public void SetVignette(float amount)
        {
            Look.Vignette = Mathf.Clamp01(amount);
            RaiseChanged();
        }

        /// <summary>Sets how much is blurred (a lower f-number blurs more). Turns depth of field on, as SetFocusDistance does.</summary>
        public void SetAperture(float fNumber)
        {
            Look.DepthOfField = true;
            Look.Aperture = Mathf.Clamp(fNumber, PhotoLook.MinAperture, PhotoLook.MaxAperture);
            RaiseChanged();
        }

        /// <summary>
        /// Focuses on the selected kerbal: how far in front of the camera their middle is (depth of field measures
        /// along the camera's look, not straight to them). Turns depth of field on, as SetFocusDistance does.
        /// </summary>
        public void FocusOnSelected()
        {
            PhotoSession.Entry entry = ReadySelected();
            if (entry == null) return;
            UnityEngine.Camera camera = CurrentCamera();
            if (camera == null)
            {
                Status?.Invoke(Localization.Translate("MunShot/Status/NoCamera"));
                return;
            }
            string name = entry.Double.Source.Name;
            if (!PhotoSession.BoundsOf(entry, out Bounds bounds))
            {
                _logger.LogWarning($"Photo mode: can't focus on {name}; their double has no bounds.");
                return;
            }
            float depth = Vector3.Dot(bounds.center - camera.transform.position, camera.transform.forward);
            if (depth < PhotoLook.MinFocusDistance)
            {
                Status?.Invoke(Localization.Format("MunShot/Status/FocusBehind", name));
                return;
            }
            SetFocusDistance(depth);
            Status?.Invoke(Localization.Format("MunShot/Status/FocusedOn", name));
        }

        // ---- Inside ----

        private PhotoSession.Entry ReadySelected()
        {
            PhotoSession.Entry selected = Session.Selected;
            if (selected != null && selected.IsReady) return selected;
            Status?.Invoke(selected == null
                ? Localization.Translate("MunShot/Status/BringKerbalFirst")
                : Localization.Translate("MunShot/Status/StillBuilding"));
            return null;
        }

        private void OnEntryChanged(PhotoSession.Entry entry)
        {
            RefreshOrbitTargets();
            if (entry.IsReady)
                Status?.Invoke(Localization.Format("MunShot/Status/InFrontOfCamera", entry.Double.Source.Name));
            else
                Status?.Invoke(Localization.Format("MunShot/Status/BuildFailed", entry.Double.Source.Name));
            RaiseChanged();
        }

        // A double clicked: posing and the orbit target follow it. A light clicked: the Light tab sets it
        private void OnClicked(IPlaceable placeable)
        {
            if (placeable is PhotoSession.Entry entry)
            {
                Camera.SetTarget(OrbitTargetFor(entry));
                Status?.Invoke(Localization.Format("MunShot/Status/Selected", entry.Double.Source.Name));
            }
            else if (placeable is PhotoLight light)
            {
                Status?.Invoke(Localization.Format("MunShot/Status/Selected", LightName(light)));
            }
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
