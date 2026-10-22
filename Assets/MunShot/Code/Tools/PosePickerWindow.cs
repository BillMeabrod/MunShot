using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using KSP.Game;
using KSP.Sim.impl;
using MunShot.Cameras;
using MunShot.Capture;
using MunShot.Posing;
using UitkForKsp2.API;
using UitkForKsp2.Controls;
using UnityEngine;
using UnityEngine.UIElements;
using ILogger = SpaceWarp2.API.Logging.ILogger;

namespace MunShot.Tools
{
    /// <summary>
    /// Developer window for photo mode's posing. The photo session part brings loaded kerbals in front of the camera
    /// as photo doubles and picks which one to pose; closing the window (or the session) puts everything back. The
    /// posing part lists the target's animation clips, scrubs through the selected one and holds the kerbal at that
    /// frame, shown as a <see cref="ClipPose"/> to copy into a preset; the Preset dropdown holds any version of the
    /// loaded presets. The target is the selected double, or the active EVA kerbal when no double is in. The camera
    /// part switches the <see cref="PhotoCamera"/> between flying free and orbiting the active vessel or a double
    /// (clicking a double makes it the orbit target). It also hosts the rig diagnostics. Built in code, so it needs no
    /// assets.
    /// </summary>
    public sealed class PosePickerWindow : IDisposable
    {
        private const string WindowId = "MunShot_PosePicker";
        private const float SliderStep = 1f / 60f;

        private readonly ILogger _logger;
        private readonly PosePlayer _evaPlayer;
        private readonly PhotoSession _session;
        private readonly DoubleManipulator _manipulator;
        private readonly PhotoCamera _camera;
        private readonly PhotoCapture _capture;
        private readonly PanelRenderer _renderer;

        // One entry per preset version, e.g. "Wave (jetpack)"
        private readonly List<(string label, ClipPose pose)> _presetChoices = new List<(string, ClipPose)>();

        private readonly List<AnimationClip> _allClips = new List<AnimationClip>();
        private readonly List<AnimationClip> _shownClips = new List<AnimationClip>();
        // Who can be brought in: the kerbals in the scene when photo mode opened (nearest first), then everyone else in
        // the roster. Listed once on opening: flying the camera away doesn't change who's "in this scene".
        private KerbalCandidates.Listing _listing = new KerbalCandidates.Listing();
        private KerbalCandidate _picked;

        // What the camera can orbit, as listed: the active vessel, then the built doubles. Each target is made once
        // and kept, so the camera's target stays the same object while the list is rebuilt.
        private readonly List<OrbitTarget> _orbitTargets = new List<OrbitTarget>();
        private readonly Dictionary<PhotoSession.Entry, OrbitTarget> _doubleTargets = new Dictionary<PhotoSession.Entry, OrbitTarget>();
        private VesselComponent _targetVessel;
        private OrbitTarget _vesselTarget;

        private AppShell _shell;
        private Label _status;
        private Button _moveTool;
        private Button _rotateTool;
        private Button _angleSnap;
        private Button _freeCamera;
        private Button _orbitCamera;
        private DropdownField _orbitPicker;
        private Button _candidatePicker;
        private DropdownField _posingPicker;
        private Label _target;
        private TextField _search;
        private ListView _clipList;
        private Label _clipInfo;
        private Slider _timeSlider;
        private Toggle _freeze;
        private Label _readout;

        private Animator _animator;
        private string _targetName;
        private AnimationClip _selectedClip;
        private RigDiagnostics.RigSnapshot _snapshot;

        public bool IsOpen { get; private set; }

        /// <summary>Raised after the window opens or closes; the window being open is photo mode being open.</summary>
        public event Action<bool> OpenChanged;

        /// <summary>
        /// False once the game has torn down the UI the window lived in (it builds its UI afresh for each campaign);
        /// the plugin then makes a new window.
        /// </summary>
        public bool IsAlive => _renderer != null;

        // The selected double's player, or the one for the active EVA kerbal when no double is in
        private PosePlayer Player => _session.Selected?.Player ?? _evaPlayer;

        public PosePickerWindow(ILogger logger, PosePlayer evaPlayer, PhotoSession session, PhotoCamera camera,
                                PhotoCapture capture, IReadOnlyList<PosePreset> presets)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _evaPlayer = evaPlayer ?? throw new ArgumentNullException(nameof(evaPlayer));
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _camera = camera ?? throw new ArgumentNullException(nameof(camera));
            _capture = capture ?? throw new ArgumentNullException(nameof(capture));
            _manipulator = new DoubleManipulator(session, message => logger.LogInfo(message), message => logger.LogError(message));
            _manipulator.Selected += OnDoubleClicked;
            foreach (PosePreset preset in presets ?? Array.Empty<PosePreset>())
                foreach (PoseVariant variant in new[] { PoseVariant.Ground, PoseVariant.Jetpack })
                {
                    ClipPose? pose = preset.Get(variant);
                    if (pose.HasValue)
                        _presetChoices.Add(($"{preset.Name} ({(variant == PoseVariant.Jetpack ? "jetpack" : "ground")})", pose.Value));
                }

            var options = new WindowOptions
            {
                WindowId = WindowId,
                Parent = null,
                // Photo mode's own window: it stays up when photo mode hides the rest of the UI
                IsHidingEnabled = false,
                UseStockScale = true,
                DisableGameInputForTextFields = true,
                BringToFrontOnPointerDown = true,
                BlockGameInput = true,
                MoveOptions = new MoveOptions { IsMovingEnabled = true, CheckScreenBounds = true }
            };
            _renderer = Window.Create(options, BuildContent());
            _renderer.Hide();
            ShowToolState();

            _session.EntryChanged += OnSessionEntryChanged;
            _camera.Changed += ShowCameraState;
            _capture.Started += OnCaptureStarted;
            _capture.Finished += OnCaptureFinished;
        }

        /// <summary>
        /// Opens or closes the window. Opening lists every kerbal loaded in the scene right then, and finds the pose
        /// target; closing ends the photo session, so every double goes and every original comes back.
        /// </summary>
        public void SetOpen(bool open)
        {
            if (open == IsOpen) return;
            IsOpen = open;
            if (open)
            {
                _renderer.Show();
                Camera camera = CurrentCamera();
                if (camera == null) _logger.LogWarning("Pose picker: no camera; kerbals are listed by distance from the scene's origin instead.");
                _listing = KerbalCandidates.FindAll(camera != null ? camera.transform.position : Vector3.zero,
                                                    message => _logger.LogInfo(message), message => _logger.LogWarning(message));
                _picked = null;
                RefreshCandidates();
                RefreshTarget();
                RefreshOrbitTargets();
            }
            else
            {
                CloseSession();
                _listing = new KerbalCandidates.Listing();
                _picked = null;
                _evaPlayer.Release();
                _renderer.Hide();
            }
            OpenChanged?.Invoke(open);
        }

        /// <summary>
        /// Called every frame by the plugin: drives the manipulator and the camera from the mouse and keyboard, and
        /// notices when the EVA kerbal being posed is gone (EVA ended, vessel switched) and lets the pose go.
        /// </summary>
        public void Tick()
        {
            // While a photo is taken the window is hidden and the camera holds still
            if (IsOpen && _capture.IsCapturing) _manipulator.HideForCapture();
            else if (IsOpen)
            {
                TickManipulator();
                _camera.Tick(IsOverWindow(global::Mouse.Position), IsTyping());
            }
            if (_session.Selected == null && _evaPlayer.Current.HasValue && !_evaPlayer.IsHolding)
            {
                _logger.LogInfo($"Pose picker: the posed kerbal '{_targetName}' is gone; releasing the pose.");
                _evaPlayer.Release();
                _animator = null;
                ShowStatus("The posed kerbal is gone. Press Refresh on another EVA kerbal.");
                UpdateReadout();
            }
        }

        public void Dispose()
        {
            _session.EntryChanged -= OnSessionEntryChanged;
            _manipulator.Selected -= OnDoubleClicked;
            _camera.Changed -= ShowCameraState;
            _capture.Started -= OnCaptureStarted;
            _capture.Finished -= OnCaptureFinished;
            CloseSession();
            _evaPlayer.Release();
            if (_renderer != null) UnityEngine.Object.Destroy(_renderer.gameObject);
        }

        private VisualElement BuildContent()
        {
            var shell = _shell = new AppShell { Title = "MunShot: Pose Picker" };
            shell.CloseClicked += () => SetOpen(false);
            shell.style.width = 400;

            _status = new Label { style = { whiteSpace = WhiteSpace.Normal, marginBottom = 4 } };
            shell.Add(_status);

            // Photo session: bring any kerbal in as a double, choose which one to pose, put everything back. The kerbal
            // list has sections ("In this scene", "Elsewhere"), which a DropdownField can't show, so it's a button
            // opening the UI's generic dropdown menu
            shell.Add(new Label("Photo session") { style = { unityFontStyleAndWeight = FontStyle.Bold, marginTop = 4 } });
            var bringRow = new VisualElement { style = { flexDirection = FlexDirection.Row } };
            bringRow.Add(new Label("Kerbal") { style = { unityTextAlign = TextAnchor.MiddleLeft, minWidth = 50 } });
            _candidatePicker = new Button(OpenCandidateMenu) { style = { flexGrow = 1, unityTextAlign = TextAnchor.MiddleLeft } };
            bringRow.Add(_candidatePicker);
            bringRow.Add(new Button(BringCandidate) { text = "Bring in" });
            shell.Add(bringRow);

            var posingRow = new VisualElement { style = { flexDirection = FlexDirection.Row } };
            _posingPicker = new DropdownField("Posing", new List<string>(), -1) { style = { flexGrow = 1 } };
            _posingPicker.RegisterValueChangedCallback(_ => OnPosingPicked());
            posingRow.Add(_posingPicker);
            posingRow.Add(new Button(CloseSession) { text = "Close session" });
            shell.Add(posingRow);

            // Tools for the selected double: click a double to select it, then drag it or its handles
            var toolRow = new VisualElement { style = { flexDirection = FlexDirection.Row } };
            _moveTool = new Button(() => SetTool(HandleMode.Move)) { text = "Move" };
            _rotateTool = new Button(() => SetTool(HandleMode.Rotate)) { text = "Rotate" };
            _angleSnap = new Button(ToggleAngleSnap);
            toolRow.Add(_moveTool);
            toolRow.Add(_rotateTool);
            toolRow.Add(_angleSnap);
            shell.Add(toolRow);
            var placeRow = new VisualElement { style = { flexDirection = FlexDirection.Row } };
            placeRow.Add(new Button(DropSelectedToSurface) { text = "Snap to surface" });
            placeRow.Add(new Button(StandSelectedUpright) { text = "Stand upright" });
            shell.Add(placeRow);

            // Take the photo: the game's screenshot (also on its own key), with this window and the handles hidden
            shell.Add(new Button(_capture.Take) { text = "Take photo", style = { marginTop = 6 } });

            // Camera: fly free, or circle the active vessel or a double. Hold right mouse to look; keys are in the
            // mod's settings.
            shell.Add(new Label("Camera") { style = { unityFontStyleAndWeight = FontStyle.Bold, marginTop = 4 } });
            var cameraRow = new VisualElement { style = { flexDirection = FlexDirection.Row } };
            _freeCamera = new Button(() => SetCameraMode(PhotoCameraMode.Free)) { text = "Free" };
            _orbitCamera = new Button(() => SetCameraMode(PhotoCameraMode.Orbit)) { text = "Orbit" };
            cameraRow.Add(_freeCamera);
            cameraRow.Add(_orbitCamera);
            _orbitPicker = new DropdownField(new List<string>(), -1) { style = { flexGrow = 1 } };
            _orbitPicker.RegisterValueChangedCallback(_ => OnOrbitTargetPicked());
            cameraRow.Add(_orbitPicker);
            shell.Add(cameraRow);

            // Posing
            _target = new Label { style = { marginTop = 6, unityFontStyleAndWeight = FontStyle.Bold } };
            shell.Add(_target);
            shell.Add(new Button(RefreshTarget) { text = "Refresh" });

            // Picking a preset holds it straight away
            var presetPicker = new DropdownField("Preset", _presetChoices.Select(c => c.label).ToList(), -1);
            presetPicker.RegisterValueChangedCallback(_ => HoldPreset(presetPicker.index));
            presetPicker.SetEnabled(_presetChoices.Count > 0);
            shell.Add(presetPicker);

            _search = new TextField("Search");
            _search.RegisterValueChangedCallback(_ => ApplyFilter());
            shell.Add(_search);

            _clipList = new ListView
            {
                itemsSource = _shownClips,
                fixedItemHeight = 20,
                selectionType = SelectionType.Single,
                makeItem = () => new Label(),
                bindItem = (element, index) => ((Label)element).text = _shownClips[index].name,
                style = { height = 220, marginTop = 4, marginBottom = 4 }
            };
            _clipList.selectionChanged += OnClipSelected;
            shell.Add(_clipList);

            _clipInfo = new Label();
            shell.Add(_clipInfo);

            _timeSlider = new Slider("Time", 0f, 1f) { showInputField = true };
            _timeSlider.RegisterValueChangedCallback(evt => OnTimeChanged(evt.newValue));
            _timeSlider.SetEnabled(false);
            shell.Add(_timeSlider);

            var buttons = new VisualElement { style = { flexDirection = FlexDirection.Row, marginTop = 4 } };
            buttons.Add(new Button(HoldSelected) { text = "Hold" });
            buttons.Add(new Button(ReleasePose) { text = "Release" });
            buttons.Add(new Button(CopyReadout) { text = "Copy" });
            shell.Add(buttons);

            // Off: bones the held clip doesn't animate keep playing the game's animation. On: everything stops.
            _freeze = new Toggle("Freeze game animation");
            _freeze.RegisterValueChangedCallback(evt => Player.SetFreezeBaseAnimation(evt.newValue));
            shell.Add(_freeze);

            // Diagnostics: results go to the game log (and the dump to a file next to it)
            var diagnostics = new VisualElement { style = { flexDirection = FlexDirection.Row, marginTop = 4 } };
            diagnostics.Add(new Button(DumpRig) { text = "Dump rig" });
            diagnostics.Add(new Button(WhatMoves) { text = "What moves" });
            diagnostics.Add(new Button(TakeSnapshot) { text = "Snapshot" });
            diagnostics.Add(new Button(CompareToSnapshot) { text = "Compare" });
            shell.Add(diagnostics);

            _readout = new Label { style = { marginTop = 4, whiteSpace = WhiteSpace.Normal } };
            shell.Add(_readout);

            UpdateReadout();
            return shell;
        }

        // ---- Photo session ----

        private IEnumerable<KerbalCandidate> NotYetIn(IEnumerable<KerbalCandidate> candidates) =>
            candidates.Where(c => !_session.Contains(c.Info));

        // Keeps the picked kerbal while they can still be brought in; otherwise picks the first who can (the nearest in
        // the scene, else the first elsewhere)
        private void RefreshCandidates()
        {
            if (_picked == null || _session.Contains(_picked.Info))
                _picked = NotYetIn(_listing.InScene).FirstOrDefault() ?? NotYetIn(_listing.Elsewhere).FirstOrDefault();
            _candidatePicker.text = _picked != null ? _picked.ToString()
                                    : _listing.Count == 0 ? "(no kerbals found)" : "(everyone is in)";
        }

        // "In this scene" then "Elsewhere", each with a greyed-out heading, as in the mock-up
        private void OpenCandidateMenu()
        {
            var menu = new GenericDropdownMenu();
            bool any = AddSection(menu, "In this scene", NotYetIn(_listing.InScene).ToList(), false);
            any |= AddSection(menu, "Elsewhere", NotYetIn(_listing.Elsewhere).ToList(), any);
            if (!any) menu.AddDisabledItem(_listing.Count == 0 ? "No kerbals found" : "Everyone is in", false);
            menu.DropDown(_candidatePicker.worldBound, _candidatePicker, true);
        }

        private bool AddSection(GenericDropdownMenu menu, string heading, List<KerbalCandidate> candidates, bool separate)
        {
            if (candidates.Count == 0) return false;
            if (separate) menu.AddSeparator(string.Empty);
            menu.AddDisabledItem(heading, false);
            foreach (KerbalCandidate candidate in candidates)
                menu.AddItem(candidate.ToString(), candidate == _picked, () =>
                {
                    _picked = candidate;
                    RefreshCandidates();
                });
            return true;
        }

        private void BringCandidate()
        {
            if (_picked == null || _session.Contains(_picked.Info))
            {
                ShowStatus(_listing.Count == 0 ? "No kerbals were found when photo mode opened; see the log." : "Pick a kerbal to bring in.");
                RefreshCandidates();
                return;
            }

            KerbalCandidate candidate = _picked;
            _session.Bring(candidate, CurrentCamera(), message =>
            {
                ShowStatus(message);
                RefreshPosingChoices();
                RefreshCandidates();
                RefreshOrbitTargets();
            });
        }

        private void OnSessionEntryChanged(PhotoSession.Entry entry)
        {
            RefreshPosingChoices();
            RefreshOrbitTargets();
            if (entry.IsReady)
            {
                ShowStatus($"{entry.Double.Source.Name} is in front of the camera.");
                if (_session.Selected == entry) RefreshTarget();
            }
            else
            {
                ShowStatus(entry.Double.Failure + " See the log.");
                RefreshTarget();
            }
        }

        private void OnPosingPicked()
        {
            int index = _posingPicker.index;
            if (index < 0 || index >= _session.Entries.Count) return;
            _session.Select(_session.Entries[index]);
            _manipulator.ShowHandles();
            RefreshTarget();
        }

        private void RefreshPosingChoices()
        {
            _posingPicker.choices = _session.Entries
                .Select(e => e.Double.Source.Name + (e.IsReady ? string.Empty : " (building…)"))
                .ToList();
            int selected = _session.Selected != null ? _session.Entries.ToList().IndexOf(_session.Selected) : -1;
            _posingPicker.SetValueWithoutNotify(selected >= 0 ? _posingPicker.choices[selected] : string.Empty);
        }

        private void CloseSession()
        {
            bool hadDoubles = _session.Entries.Count > 0;
            _session.Close();
            // The handles go with the doubles; they're made again when the next double is in
            _manipulator.Dispose();
            if (_posingPicker == null) return;
            RefreshPosingChoices();
            RefreshOrbitTargets();
            if (hadDoubles)
            {
                _logger.LogInfo("Photo mode: session closed; doubles removed and originals shown again.");
                ShowStatus("Session closed: everyone is back where they were.");
                RefreshCandidates();
                RefreshTarget();
            }
        }

        // ---- Moving and rotating doubles ----

        private void SetTool(HandleMode mode)
        {
            _manipulator.SetMode(mode);
            ShowToolState();
        }

        // The active tool's button is marked, as the VAB marks its active tool
        private void ShowToolState()
        {
            _moveTool.text = _manipulator.Mode == HandleMode.Move ? "[Move]" : "Move";
            _rotateTool.text = _manipulator.Mode == HandleMode.Rotate ? "[Rotate]" : "Rotate";
            _angleSnap.text = _manipulator.AngleSnap ? "[Angle snap]" : "Angle snap";
        }

        // As the VAB's angle snap: rings turn in 15° steps (5° with Shift) while on
        private void ToggleAngleSnap()
        {
            _manipulator.AngleSnap = !_manipulator.AngleSnap;
            ShowToolState();
        }

        private void StandSelectedUpright()
        {
            _session.StandUpright(_session.Selected, out string message);
            ShowStatus(message);
        }

        private void DropSelectedToSurface()
        {
            _session.DropToSurface(_session.Selected, out string message);
            ShowStatus(message);
        }

        // Feeds the mouse to the manipulator through the game's own cursor (which is the virtual cursor when playing
        // with a gamepad); presses over this window are the window's, not the world's. Fast (Shift) is the fine snap
        // modifier, as Shift is in the VAB
        private void TickManipulator()
        {
            global::Mouse.MouseButton left = global::Mouse.Left;
            if (left == null) return;
            Vector2 screen = global::Mouse.Position;
            _manipulator.Tick(CurrentCamera(), screen, left.WasPressedThisFrame(), left.IsPressed(), left.WasReleasedThisFrame(),
                              IsOverWindow(screen), MunShotSettings.Fast.Held);
        }

        // A double clicked: posing and the orbit target follow it
        private void OnDoubleClicked(PhotoSession.Entry entry)
        {
            RefreshPosingChoices();
            RefreshTarget();
            _camera.SetTarget(OrbitTargetFor(entry));
            ShowStatus($"Selected {entry.Double.Source.Name}.");
        }

        private bool IsOverWindow(Vector2 screen)
        {
            IPanel panel = _shell?.panel;
            if (panel == null) return false;
            // Screen positions count up from the bottom, panel positions down from the top
            Vector2 inPanel = RuntimePanelUtils.ScreenToPanel(panel, new Vector2(screen.x, Screen.height - screen.y));
            return _shell.worldBound.Contains(inPanel);
        }

        // Whether a text or number field has the keyboard, so typing in it doesn't fly the camera
        private bool IsTyping()
        {
            var focused = _shell?.panel?.focusController?.focusedElement as VisualElement;
            return focused != null && (focused.GetFirstOfType<TextField>() != null || focused.GetFirstOfType<FloatField>() != null);
        }

        private static Camera CurrentCamera() => GameManager.Instance?.Game?.GraphicsManager?.GetCurrentUnityCamera();

        // ---- Taking the photo ----

        private void OnCaptureStarted()
        {
            if (IsOpen) _renderer.Hide();
        }

        private void OnCaptureFinished(string message)
        {
            if (!IsOpen) return;
            _renderer.Show();
            if (message != null) ShowStatus(message);
        }

        // ---- Camera ----

        private void SetCameraMode(PhotoCameraMode mode)
        {
            if (mode == PhotoCameraMode.Orbit && _camera.Target == null) _camera.SetTarget(PickedOrbitTarget());
            if (!_camera.SetMode(mode, out string message)) _logger.LogInfo($"Photo camera: {message}");
            ShowStatus(message);
        }

        private void RefreshOrbitTargets()
        {
            if (_orbitPicker == null) return;
            _orbitTargets.Clear();
            OrbitTarget vessel = ActiveVesselTarget();
            if (vessel != null) _orbitTargets.Add(vessel);
            foreach (PhotoSession.Entry entry in _session.Entries)
                if (entry.IsReady)
                    _orbitTargets.Add(OrbitTargetFor(entry));
            _orbitPicker.choices = _orbitTargets.Select(t => t.Name).ToList();

            // A target that's left the list (its double went) is no longer orbited
            if (_camera.Target != null && !_orbitTargets.Contains(_camera.Target)) _camera.SetTarget(null);
            ShowCameraState();
        }

        private void OnOrbitTargetPicked()
        {
            OrbitTarget picked = PickedOrbitTarget();
            if (picked != null) _camera.SetTarget(picked);
        }

        private OrbitTarget PickedOrbitTarget()
        {
            int index = _orbitPicker.index;
            return index >= 0 && index < _orbitTargets.Count ? _orbitTargets[index] : _orbitTargets.FirstOrDefault();
        }

        // Made again only when the active vessel changes
        private OrbitTarget ActiveVesselTarget()
        {
            VesselComponent vessel = GameManager.Instance?.Game?.ViewController?.GetActiveSimVessel();
            if (vessel != _targetVessel)
            {
                _targetVessel = vessel;
                _vesselTarget = OrbitTargets.Vessel(vessel);
            }
            return _vesselTarget;
        }

        // A double is circled around the middle of its body, wherever it's been moved to
        private OrbitTarget OrbitTargetFor(PhotoSession.Entry entry)
        {
            foreach (PhotoSession.Entry gone in _doubleTargets.Keys.Where(e => !_session.Entries.Contains(e)).ToList())
                _doubleTargets.Remove(gone);
            if (!_doubleTargets.TryGetValue(entry, out OrbitTarget target))
            {
                target = new OrbitTarget(entry.Double.Source.Name, () =>
                    _session.Entries.Contains(entry) && PhotoSession.BoundsOf(entry, out Bounds bounds) ? bounds.center : (Vector3?)null);
                _doubleTargets[entry] = target;
            }
            return target;
        }

        private void ShowCameraState()
        {
            if (_freeCamera == null) return;
            _freeCamera.text = _camera.Mode == PhotoCameraMode.Free ? "[Free]" : "Free";
            _orbitCamera.text = _camera.Mode == PhotoCameraMode.Orbit ? "[Orbit]" : "Orbit";
            OrbitTarget shown = _camera.Target ?? PickedOrbitTarget();
            _orbitPicker.SetValueWithoutNotify(shown != null ? shown.Name : "(nothing to orbit)");
        }

        // ---- Pose target ----

        // The selected double when there is one, else the active EVA kerbal
        private void RefreshTarget()
        {
            PhotoSession.Entry selected = _session.Selected;
            if (selected != null)
            {
                if (!selected.IsReady)
                {
                    SetTarget(null, selected.Double.Source.Name, $"Posing: {selected.Double.Source.Name}'s double (building…)");
                    return;
                }
                SetTarget(selected.Double.Animator, selected.Double.Source.Name, $"Posing: {selected.Double.Source.Name}'s double");
                return;
            }

            if (!EvaKerbalLocator.TryFind(GameManager.Instance?.Game, out EvaKerbalLocator.Result found, out string reason))
            {
                SetTarget(null, null, "Posing: nobody");
                ShowStatus(reason);
                _logger.LogInfo($"Pose picker: no kerbal to pose. {reason}");
                return;
            }
            if (_evaPlayer.IsHolding && found.Animator != _animator) _evaPlayer.Release();
            SetTarget(found.Animator, found.KerbalName, $"Posing: {found.KerbalName} (the active EVA kerbal)");
        }

        private void SetTarget(Animator animator, string name, string label)
        {
            _animator = animator;
            _targetName = name;
            _target.text = label;
            _freeze.SetValueWithoutNotify(Player.FreezeBaseAnimation);

            _allClips.Clear();
            if (animator != null) _allClips.AddRange(PosePlayer.GetClips(animator));
            ApplyFilter();

            if (animator != null && _allClips.Count == 0)
            {
                RuntimeAnimatorController controller = animator.runtimeAnimatorController;
                string message = $"'{name}' has no animation clips (controller '{(controller != null ? controller.name : "<none>")}').";
                ShowStatus(message);
                _logger.LogWarning($"Pose picker: {message}");
            }
            UpdateReadout();
        }

        private bool EnsureTarget()
        {
            if (_animator == null) RefreshTarget();
            if (_animator == null) ShowStatus(_session.Selected != null ? "The double is still being built." : "There's nobody to pose.");
            return _animator != null;
        }

        // ---- Posing ----

        private void ApplyFilter()
        {
            string filter = _search?.value?.Trim() ?? string.Empty;
            _shownClips.Clear();
            foreach (AnimationClip clip in _allClips)
                if (filter.Length == 0 || clip.name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                    _shownClips.Add(clip);
            _clipList?.RefreshItems();
        }

        private void OnClipSelected(IEnumerable<object> selection)
        {
            AnimationClip clip = selection.OfType<AnimationClip>().FirstOrDefault();
            if (clip == null) return;

            ShowClip(clip);
            _timeSlider.SetValueWithoutNotify(0f);

            // Switching clips while holding shows the new clip straight away
            if (Player.IsHolding) HoldSelected();
            else UpdateReadout();
        }

        private void ShowClip(AnimationClip clip)
        {
            _selectedClip = clip;
            _clipInfo.text = $"{clip.name}: {Format(clip.length)}s, "
                             + $"{clip.frameRate.ToString("0", CultureInfo.InvariantCulture)} fps, "
                             + $"{(clip.isLooping ? "looping" : "not looping")}";
            _timeSlider.highValue = Mathf.Max(clip.length, SliderStep);
            _timeSlider.SetEnabled(true);
        }

        private void OnTimeChanged(float time)
        {
            if (Player.IsHolding && _selectedClip != null) HoldSelected();
            else UpdateReadout();
        }

        private void HoldSelected()
        {
            if (_selectedClip == null)
            {
                ShowStatus("Pick a clip first.");
                return;
            }
            if (!EnsureTarget()) return;

            var pose = new ClipPose(_selectedClip.name, Mathf.Max(0f, _timeSlider.value));
            if (!Player.Hold(_animator, pose))
                ShowStatus($"Could not hold {pose}; see the log.");
            UpdateReadout();
        }

        // Shows the preset's clip and time in the list and slider, then holds it
        private void HoldPreset(int index)
        {
            if (index < 0 || index >= _presetChoices.Count) return;
            (string label, ClipPose pose) = _presetChoices[index];
            if (!EnsureTarget()) return;

            AnimationClip clip = _allClips.FirstOrDefault(c => c.name == pose.ClipName);
            if (clip == null)
            {
                string message = $"Preset '{label}' uses clip '{pose.ClipName}', which '{_targetName}' doesn't have.";
                ShowStatus(message);
                _logger.LogWarning($"Pose picker: {message}");
                return;
            }

            _search.SetValueWithoutNotify(string.Empty);
            ApplyFilter();
            int row = _shownClips.IndexOf(clip);
            _clipList.SetSelectionWithoutNotify(new[] { row });
            _clipList.ScrollToItem(row);
            ShowClip(clip);
            _timeSlider.SetValueWithoutNotify(Mathf.Min(pose.Time, clip.length));
            HoldSelected();
            ShowStatus($"Holding preset '{label}'.");
        }

        private void ReleasePose()
        {
            Player.Release();
            UpdateReadout();
        }

        private void CopyReadout()
        {
            ClipPose? pose = CurrentPose();
            if (!pose.HasValue) return;
            GUIUtility.systemCopyBuffer = pose.Value.ToString();
            _logger.LogInfo($"Pose picker: copied {pose.Value}");
        }

        // ---- Diagnostics ----

        private void DumpRig()
        {
            if (!EnsureTarget()) return;
            try
            {
                string file = RigDiagnostics.DumpRig(_animator, _targetName, DescribeHeld());
                _logger.LogInfo($"Pose picker: rig of '{_targetName}' dumped to {file}");
                ShowStatus($"Rig dumped to {file}");
            }
            catch (Exception e)
            {
                _logger.LogError($"Pose picker: could not dump the rig of '{_targetName}': {e}");
                ShowStatus("Could not dump the rig; see the log.");
            }
        }

        // Samples the selected clip within this frame, then puts the target back as it was (held or released)
        private void WhatMoves()
        {
            if (_selectedClip == null)
            {
                ShowStatus("Pick a clip first.");
                return;
            }
            if (!EnsureTarget()) return;

            PosePlayer player = Player;
            ClipPose? previous = player.IsHolding ? player.Current : null;

            // The target's own pose, to tell which bones the clip pins: let go, and have the game's animation
            // pose it again now without moving time on
            player.Release();
            _animator.Update(0f);
            RigDiagnostics.RigSnapshot live = RigDiagnostics.Snapshot(_animator, "own pose");

            string result;
            try
            {
                if (!player.Hold(_animator, new ClipPose(_selectedClip.name, 0f)))
                {
                    ShowStatus($"Could not hold '{_selectedClip.name}'; see the log.");
                    return;
                }
                result = RigDiagnostics.WhatMoves(_animator, _selectedClip.name, _selectedClip.length, player.ApplyNow, live);
            }
            finally
            {
                if (previous.HasValue) player.Hold(_animator, previous.Value);
                else player.Release();
            }

            _logger.LogInfo($"Pose picker: {result}");
            int firstLineEnd = result.IndexOf('\n');
            ShowStatus((firstLineEnd >= 0 ? result.Substring(0, firstLineEnd) : result) + ". Details are in the log.");
            UpdateReadout();
        }

        private void TakeSnapshot()
        {
            if (!EnsureTarget()) return;
            _snapshot = RigDiagnostics.Snapshot(_animator, DescribeHeld());
            ShowStatus($"Snapshot taken at {_snapshot.Label}. Change the frame or clip, then Compare.");
        }

        private void CompareToSnapshot()
        {
            if (_snapshot == null)
            {
                ShowStatus("Take a Snapshot first.");
                return;
            }

            string result = RigDiagnostics.Compare(_snapshot, DescribeHeld());
            _logger.LogInfo($"Pose picker: {result}");
            int firstLineEnd = result.IndexOf('\n');
            ShowStatus((firstLineEnd >= 0 ? result.Substring(0, firstLineEnd) : result) + " Details are in the log.");
        }

        // ---- Readout ----

        private ClipPose? CurrentPose()
        {
            if (Player.Current.HasValue) return Player.Current;
            if (_selectedClip != null) return new ClipPose(_selectedClip.name, Mathf.Max(0f, _timeSlider.value));
            return null;
        }

        private string DescribeHeld() => Player.Current?.ToString() ?? "live animation";

        private void UpdateReadout()
        {
            if (_readout == null) return;
            ClipPose? pose = CurrentPose();
            string state = Player.IsHolding ? "Holding" : "Not holding";
            _readout.text = pose.HasValue ? $"{state}: {pose.Value}" : state;
        }

        private void ShowStatus(string text) => _status.text = text;

        private static string Format(float seconds) => seconds.ToString("0.000", CultureInfo.InvariantCulture);
    }
}
