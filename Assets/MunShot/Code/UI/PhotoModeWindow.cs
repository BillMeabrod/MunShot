using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MunShot.Cameras;
using MunShot.Posing;
using SpaceWarp2.API.Input;
using UitkForKsp2.API;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;
using ILogger = SpaceWarp2.API.Logging.ILogger;

namespace MunShot.UI
{
    /// <summary>
    /// Photo mode's overlay, built from PhotoModeWindow.uxml with a <see cref="PhotoModeViewModel"/> as its data
    /// source: the scene fills the screen, and a flight panel on the left edge (Kerbals, Pose, Camera tabs; opened and
    /// closed by its bar toggle), a shutter button and a hint bar sit on its edges. It's built from the KerbalUI
    /// theme's own pieces as Redux's panels are, with localized text and the game's menu sounds on hover and click. It shows while photo mode is open, stays up when photo mode hides the rest of the
    /// game's UI, hides with the Hide UI key to frame the shot, and hides for the moment a photo is taken. Everything it
    /// does goes through <see cref="PhotoMode"/>.
    /// </summary>
    public sealed class PhotoModeWindow : IDisposable
    {
        private enum Tab
        {
            Kerbals,
            Pose,
            Camera
        }

        private const string WindowId = "MunShot_PhotoMode";

        // The chosen one: "selected" for UitkForKsp2's sounds (a chosen item clicks differently) and list rows,
        // "active" for the theme's OabButton (lit up, as the VAB's chosen tool)
        private const string SelectedClass = "selected";
        private const string ActiveClass = "active";

        // How long a note stays up
        private const long ToastMilliseconds = 3500;

        private readonly PhotoMode _photoMode;
        private readonly ILogger _logger;
        private readonly PanelRenderer _renderer;
        private readonly PhotoModeViewModel _model = new PhotoModeViewModel();

        // One pose choice per preset version: "None" first, then e.g. "Wave", "Wave (jetpack)"
        private readonly List<(PosePreset preset, PoseVariant variant)> _poseChoices = new List<(PosePreset, PoseVariant)>();

        // The pose last picked for each double, to show when it's selected again
        private readonly Dictionary<PhotoSession.Entry, int> _poseOf = new Dictionary<PhotoSession.Entry, int>();

        // The game's localizer, told about language changes (kept to stop listening to the same one)
        private ILocalizer _localizer;

        private Tab _tab = Tab.Kerbals;

        // The camera mode the hint bar shows, so it's only rebuilt when that changes
        private PhotoCameraMode? _hintsMode;
        private int _toastId;

        private VisualElement _root;
        private VisualElement _chrome;
        private VisualElement _sidebar;
        private VisualElement _sidePanel;
        private Button _shutter;
        private VisualElement _hints;
        private readonly Dictionary<Tab, (Button tab, VisualElement page)> _tabs = new Dictionary<Tab, (Button, VisualElement)>();
        private Button _bringIn;
        private ScrollView _kerbalList;
        private ScrollView _poseList;
        private ScrollView _orbitList;
        private Toggle _helmet;
        private Toggle _visor;
        private Button _move;
        private Button _rotate;
        private Toggle _angleSnap;
        private Button _free;
        private Button _orbit;
        private Slider _fieldOfView;

        /// <summary>
        /// False once the game has torn down the UI the overlay lived in (it builds its UI afresh for each campaign);
        /// a new one is made then.
        /// </summary>
        public bool IsAlive => _renderer != null;

        public PhotoModeWindow(PhotoMode photoMode, VisualTreeAsset uxml, ILogger logger)
        {
            _photoMode = photoMode ?? throw new ArgumentNullException(nameof(photoMode));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            if (uxml == null) throw new ArgumentNullException(nameof(uxml));

            _poseChoices.Add((null, PoseVariant.Ground));
            foreach (PosePreset preset in photoMode.Presets)
            {
                if (preset.Ground.HasValue) _poseChoices.Add((preset, PoseVariant.Ground));
                if (preset.Jetpack.HasValue) _poseChoices.Add((preset, PoseVariant.Jetpack));
            }

            var options = new WindowOptions
            {
                WindowId = WindowId,
                Parent = null,
                // Photo mode's own overlay: it stays up when photo mode hides the rest of the UI
                IsHidingEnabled = false,
                UseStockScale = true,
                DisableGameInputForTextFields = true,
                BringToFrontOnPointerDown = false,
                // It covers the screen; only its panels are UI, the rest is the scene
                BlockGameInput = false,
                MoveOptions = new MoveOptions { IsMovingEnabled = false, CheckScreenBounds = false }
            };
            _renderer = Window.Create(options, uxml);
            _renderer.OnWindowRoot(CoverScreen);
            _renderer.OnPanelRoot(Bind);
            _renderer.EnableLocalization();
            // The game's menu sounds: a blip on hovering buttons, toggles and sliders, a click on pressing them
            _renderer.EnableUiSounds();
            _renderer.Hide();

            _photoMode.OpenChanged += OnOpenChanged;
            _photoMode.Changed += Refresh;
            _photoMode.Status += ShowToast;
            _photoMode.Capture.Started += OnCaptureStarted;
            _photoMode.Capture.Finished += OnCaptureFinished;

            // Text from UXML is translated again by UitkForKsp2 when the language changes; what's built here (lists,
            // hints) is redrawn then too
            _localizer = ILocalizer.Instance;
            if (_localizer != null) _localizer.OnLocalize += OnLanguageChanged;
            else _logger.LogWarning("Photo mode overlay: the game's localizer isn't there; a language change shows after reopening photo mode.");
        }

        public void Dispose()
        {
            _photoMode.OpenChanged -= OnOpenChanged;
            _photoMode.Changed -= Refresh;
            _photoMode.Status -= ShowToast;
            _photoMode.Capture.Started -= OnCaptureStarted;
            _photoMode.Capture.Finished -= OnCaptureFinished;
            if (_localizer != null) _localizer.OnLocalize -= OnLanguageChanged;
            if (_renderer != null) UnityEngine.Object.Destroy(_renderer.gameObject);
        }

        /// <summary>Whether the mouse is over one of the overlay's panels, so clicks there aren't for the scene.</summary>
        public bool IsPointerOver(Vector2 screen)
        {
            if (!_photoMode.IsOpen || _photoMode.UiHidden || _root?.panel == null) return false;
            // Screen positions count up from the bottom, panel positions down from the top
            Vector2 inPanel = RuntimePanelUtils.ScreenToPanel(_root.panel, new Vector2(screen.x, Screen.height - screen.y));
            return Contains(_sidebar, inPanel) || Contains(_shutter, inPanel) || Contains(_hints, inPanel);
        }

        /// <summary>Whether a text or number field in the overlay has the keyboard.</summary>
        public bool IsTyping
        {
            get
            {
                var focused = _root?.panel?.focusController?.focusedElement as VisualElement;
                return focused != null && (focused.GetFirstOfType<TextField>() != null || focused.GetFirstOfType<FloatField>() != null);
            }
        }

        private static bool Contains(VisualElement element, Vector2 point) =>
            element != null && element.resolvedStyle.display != DisplayStyle.None && element.worldBound.Contains(point);

        // ---- Setting up ----

        // The window's container covers the screen, as Redux's full-screen panels do, and lets clicks through to the
        // scene: only the overlay's panels take them
        private static void CoverScreen(VisualElement windowRoot)
        {
            foreach (VisualElement element in new[] { windowRoot, windowRoot?.parent })
            {
                if (element == null) continue;
                element.pickingMode = PickingMode.Ignore;
                element.style.position = Position.Absolute;
                element.style.left = 0;
                element.style.top = 0;
                element.style.width = new StyleLength(Length.Percent(100f));
                element.style.height = new StyleLength(Length.Percent(100f));
            }
        }

        private void Bind(VisualElement root)
        {
            _root = root;
            root.dataSource = _model;
            _chrome = Required<VisualElement>(root, "chrome");
            _sidebar = Required<VisualElement>(root, "sidebar");
            _sidePanel = Required<VisualElement>(root, "side-panel");
            _shutter = Required<Button>(root, "shutter");
            _hints = Required<VisualElement>(root, "hints");
            if (_chrome == null || _sidePanel == null) return;

            // The bar toggle opens and closes the panel, as on Redux's orbital info panel
            Required<Toggle>(root, "panel-toggle")?.RegisterValueChangedCallback(change =>
                _sidePanel.style.display = change.newValue ? DisplayStyle.Flex : DisplayStyle.None);

            _tabs[Tab.Kerbals] = (Required<Button>(root, "tab-kerbals"), Required<VisualElement>(root, "page-kerbals"));
            _tabs[Tab.Pose] = (Required<Button>(root, "tab-pose"), Required<VisualElement>(root, "page-pose"));
            _tabs[Tab.Camera] = (Required<Button>(root, "tab-camera"), Required<VisualElement>(root, "page-camera"));
            foreach (KeyValuePair<Tab, (Button tab, VisualElement page)> tab in _tabs)
            {
                Tab which = tab.Key;
                OnClick(tab.Value.tab, () => ShowTab(which));
            }

            _bringIn = Required<Button>(root, "bring-in");
            _kerbalList = Required<ScrollView>(root, "kerbal-list");
            _poseList = Required<ScrollView>(root, "pose-list");
            _orbitList = Required<ScrollView>(root, "orbit-list");
            _helmet = Required<Toggle>(root, "helmet");
            _visor = Required<Toggle>(root, "visor");
            _move = Required<Button>(root, "tool-move");
            _rotate = Required<Button>(root, "tool-rotate");
            _angleSnap = Required<Toggle>(root, "angle-snap");
            _free = Required<Button>(root, "camera-free");
            _orbit = Required<Button>(root, "camera-orbit");
            _fieldOfView = Required<Slider>(root, "field-of-view");

            OnClick(_bringIn, OpenKerbalMenu);
            OnClick(_move, () => _photoMode.SetTool(HandleMode.Move));
            OnClick(_rotate, () => _photoMode.SetTool(HandleMode.Rotate));
            OnClick(Required<Button>(root, "snap-to-surface"), _photoMode.SnapToSurface);
            OnClick(Required<Button>(root, "stand-upright"), _photoMode.StandUpright);
            OnClick(_free, () => _photoMode.SetCameraMode(PhotoCameraMode.Free));
            OnClick(_orbit, () => _photoMode.SetCameraMode(PhotoCameraMode.Orbit));
            OnClick(_shutter, _photoMode.TakePhoto);

            _helmet?.RegisterValueChangedCallback(change => _photoMode.SetHelmet(change.newValue));
            _visor?.RegisterValueChangedCallback(change => _photoMode.SetVisor(change.newValue));
            _angleSnap?.RegisterValueChangedCallback(change => _photoMode.SetAngleSnap(change.newValue));
            if (_fieldOfView != null)
            {
                _fieldOfView.lowValue = PhotoCamera.MinFieldOfView;
                _fieldOfView.highValue = PhotoCamera.MaxFieldOfView;
                _fieldOfView.RegisterValueChangedCallback(change =>
                {
                    _photoMode.Camera.FieldOfView = change.newValue;
                    ShowFieldOfView();
                });
            }

            ShowTab(_tab);
            Refresh();
        }

        // A named element the UXML must have; a missing one is logged (the rest of the overlay still works)
        private T Required<T>(VisualElement root, string name) where T : VisualElement
        {
            T element = root.Q<T>(name);
            if (element == null) _logger.LogError($"Photo mode overlay: PhotoModeWindow.uxml has no {typeof(T).Name} named '{name}'.");
            return element;
        }

        private static void OnClick(Button button, Action action)
        {
            if (button != null) button.clicked += action;
        }

        // ---- Showing photo mode ----

        private void OnOpenChanged(bool open)
        {
            if (open)
            {
                _poseOf.Clear();
                _hintsMode = null;
                _model.ToastVisible = false;
                ShowTab(Tab.Kerbals);
                _renderer.Show();
            }
            else
            {
                _renderer.Hide();
            }
            Refresh();
        }

        private void OnLanguageChanged()
        {
            _hintsMode = null;
            Refresh();
        }

        private void OnCaptureStarted()
        {
            if (_photoMode.IsOpen) _renderer.Hide();
        }

        private void OnCaptureFinished(string message)
        {
            if (_photoMode.IsOpen) _renderer.Show();
        }

        // A note at the top that goes away by itself; a newer note keeps its full time
        private void ShowToast(string message)
        {
            if (string.IsNullOrEmpty(message) || _root == null) return;
            int id = ++_toastId;
            _model.Toast = message;
            _model.ToastVisible = true;
            _root.schedule.Execute(() =>
            {
                if (id == _toastId) _model.ToastVisible = false;
            }).ExecuteLater(ToastMilliseconds);
        }

        private void ShowTab(Tab tab)
        {
            _tab = tab;
            foreach (KeyValuePair<Tab, (Button tab, VisualElement page)> entry in _tabs)
            {
                bool shown = entry.Key == tab;
                MarkActive(entry.Value.tab, shown);
                if (entry.Value.page != null) entry.Value.page.style.display = shown ? DisplayStyle.Flex : DisplayStyle.None;
            }
        }

        // Brings every control in line with photo mode
        private void Refresh()
        {
            if (_chrome == null) return;
            _chrome.style.display = _photoMode.UiHidden ? DisplayStyle.None : DisplayStyle.Flex;

            PhotoSession session = _photoMode.Session;
            PhotoSession.Entry selected = session.Selected;
            bool ready = selected != null && selected.IsReady;
            _model.HasKerbals = session.Entries.Count > 0;
            _model.SelectedName = selected != null ? selected.Double.Source.Name : Localization.Translate(Localization.NobodyIn);

            RefreshKerbals(session, selected);
            RefreshPoses(selected, ready);

            _helmet?.SetEnabled(ready);
            _helmet?.SetValueWithoutNotify(!ready || selected.Double.HelmetOn);
            _visor?.SetEnabled(ready && selected.Double.HelmetOn);
            _visor?.SetValueWithoutNotify(!ready || selected.Double.VisorOn);

            MarkActive(_move, _photoMode.Manipulator.Mode == HandleMode.Move);
            MarkActive(_rotate, _photoMode.Manipulator.Mode == HandleMode.Rotate);
            _angleSnap?.SetValueWithoutNotify(_photoMode.Manipulator.AngleSnap);

            PhotoCamera camera = _photoMode.Camera;
            MarkActive(_free, camera.Mode == PhotoCameraMode.Free);
            MarkActive(_orbit, camera.Mode == PhotoCameraMode.Orbit);
            _model.IsOrbiting = camera.Mode == PhotoCameraMode.Orbit;
            RefreshOrbitTargets(camera);
            _fieldOfView?.SetValueWithoutNotify(camera.FieldOfView);
            ShowFieldOfView();
            ShowHints(camera.Mode);
        }

        // Who's in the shot: click a name to select them, x to take them out
        private void RefreshKerbals(PhotoSession session, PhotoSession.Entry selected)
        {
            if (_kerbalList == null) return;
            _kerbalList.Clear();
            foreach (PhotoSession.Entry entry in session.Entries)
            {
                PhotoSession.Entry chosen = entry;
                var row = new VisualElement();
                row.AddToClassList("pm-item-row");
                Button name = Item(entry.IsReady ? entry.Double.Source.Name : $"{entry.Double.Source.Name}…", entry == selected,
                                   () => _photoMode.Select(chosen));
                var remove = new Button(() => _photoMode.Remove(chosen)) { text = "×" };
                remove.AddToClassList("pm-remove");
                row.Add(name);
                row.Add(remove);
                _kerbalList.Add(row);
            }
            _bringIn?.SetEnabled(_photoMode.NotYetIn(_photoMode.Kerbals.InScene).Any() || _photoMode.NotYetIn(_photoMode.Kerbals.Elsewhere).Any());
        }

        private void RefreshPoses(PhotoSession.Entry selected, bool ready)
        {
            if (_poseList == null) return;
            _poseList.Clear();
            int current = ready && _poseOf.TryGetValue(selected, out int chosen) ? chosen : 0;
            for (int i = 0; i < _poseChoices.Count; i++)
            {
                int index = i;
                Button item = Item(PoseLabel(_poseChoices[i]), ready && i == current, () => PickPose(index));
                item.SetEnabled(ready);
                _poseList.Add(item);
            }
        }

        private void RefreshOrbitTargets(PhotoCamera camera)
        {
            if (_orbitList == null) return;
            _orbitList.Clear();
            foreach (OrbitTarget target in _photoMode.OrbitTargets)
            {
                OrbitTarget chosen = target;
                _orbitList.Add(Item(target.Name, camera.Target == target, () => _photoMode.SetOrbitTarget(chosen)));
            }
        }

        // A list item: a button, so it gets the game's hover and click sounds; the chosen one is marked selected
        private static Button Item(string text, bool selected, Action clicked)
        {
            var item = new Button(clicked) { text = text };
            item.AddToClassList("pm-item");
            item.EnableInClassList(SelectedClass, selected);
            return item;
        }

        private static void MarkActive(VisualElement element, bool active)
        {
            element?.EnableInClassList(ActiveClass, active);
            element?.EnableInClassList(SelectedClass, active);
        }

        private void ShowFieldOfView() =>
            _model.FieldOfViewText = _photoMode.Camera.FieldOfView.ToString("0", CultureInfo.CurrentCulture) + "°";

        // ---- Controls ----

        // "In this scene" then "Elsewhere", each under a greyed-out heading; picking someone brings them in
        private void OpenKerbalMenu()
        {
            var menu = new GenericDropdownMenu();
            bool any = AddSection(menu, Localization.InThisScene, _photoMode.NotYetIn(_photoMode.Kerbals.InScene).ToList(), false);
            any |= AddSection(menu, Localization.Elsewhere, _photoMode.NotYetIn(_photoMode.Kerbals.Elsewhere).ToList(), any);
            if (!any)
                menu.AddDisabledItem(Localization.Translate(_photoMode.Kerbals.Count == 0 ? Localization.NoKerbals : Localization.EveryoneIsIn), false);
            menu.DropDown(_bringIn.worldBound, _bringIn, DropdownMenuSizeMode.Auto);
        }

        private bool AddSection(GenericDropdownMenu menu, string headingKey, List<KerbalCandidate> candidates, bool separate)
        {
            if (candidates.Count == 0) return false;
            if (separate) menu.AddSeparator(string.Empty);
            menu.AddDisabledItem(Localization.Translate(headingKey), false);
            foreach (KerbalCandidate candidate in candidates)
            {
                KerbalCandidate chosen = candidate;
                menu.AddItem(candidate.Name, false, () => _photoMode.Bring(chosen));
            }
            return true;
        }

        private void PickPose(int index)
        {
            PhotoSession.Entry selected = _photoMode.Session.Selected;
            if (selected == null || index < 0 || index >= _poseChoices.Count) return;
            _poseOf[selected] = index;
            (PosePreset preset, PoseVariant variant) = _poseChoices[index];
            if (preset == null) _photoMode.ReleasePose();
            else _photoMode.HoldPreset(preset, variant);
        }

        private static string PoseLabel((PosePreset preset, PoseVariant variant) choice)
        {
            if (choice.preset == null) return Localization.Translate(Localization.NoPose, "None");
            if (choice.variant == PoseVariant.Ground) return choice.preset.Name;
            return string.Format(Localization.Translate(Localization.JetpackPose, "{0} (jetpack)"), choice.preset.Name);
        }

        // ---- Hints ----

        // The keys as they're bound, as keycaps, and what they do in the camera's current mode. Only the essentials:
        // the rest (Fast, clicking a kerbal) shows itself
        private void ShowHints(PhotoCameraMode mode)
        {
            if (_hints == null || _hintsMode == mode) return;
            _hintsMode = mode;
            _hints.Clear();
            if (mode == PhotoCameraMode.Free)
            {
                AddHint(Localization.HintFly, Key(MunShotSettings.Forward), Key(MunShotSettings.Left), Key(MunShotSettings.Back), Key(MunShotSettings.Right));
                AddHint(Localization.HintLook, Localization.Translate(Localization.KeyRightMouse));
                AddHint(Localization.HintUpDown, Localization.Translate(Localization.KeyWheel));
            }
            else
            {
                AddHint(Localization.HintCircle, Localization.Translate(Localization.KeyRightMouse));
                AddHint(Localization.HintCloser, Localization.Translate(Localization.KeyWheel));
            }
            AddHint(Localization.HintRoll, Key(MunShotSettings.RollLeft), Key(MunShotSettings.RollRight));
            AddHint(Localization.HintHideUi, Key(MunShotSettings.HideUi));
            AddHint(Localization.HintTakePhoto, ScreenshotKey());
            AddHint(Localization.HintExit, Shortcut(MunShotSettings.TogglePhotoMode));
        }

        private void AddHint(string actionKey, params string[] keys)
        {
            var hint = new VisualElement();
            hint.AddToClassList("pm-hint");
            foreach (string key in keys)
            {
                var keycap = new Label(key);
                keycap.AddToClassList("pm-key");
                hint.Add(keycap);
            }
            var action = new Label(Localization.Translate(actionKey));
            action.AddToClassList("pm-action");
            hint.Add(action);
            _hints.Add(hint);
        }

        private static string Key(KeyBind key) => KeyName(key.Code);

        private static string Shortcut(KeyboardShortcut shortcut)
        {
            var parts = new List<string>();
            foreach (KeyCode modifier in shortcut.Modifiers ?? Array.Empty<KeyCode>()) parts.Add(KeyName(modifier));
            parts.Add(KeyName(shortcut.Main));
            return string.Join(" + ", parts);
        }

        // Unity's key names, the way a keyboard labels them: no "Left", short modifier names, digits as digits
        private static string KeyName(KeyCode key)
        {
            switch (key)
            {
                case KeyCode.LeftShift:
                case KeyCode.RightShift:
                    return "Shift";
                case KeyCode.LeftControl:
                case KeyCode.RightControl:
                    return "Ctrl";
                case KeyCode.LeftAlt:
                case KeyCode.RightAlt:
                    return "Alt";
                case KeyCode.Space:
                    return "Space";
                case KeyCode.Return:
                    return "Enter";
                case KeyCode.Escape:
                    return "Esc";
            }
            string name = key.ToString();
            if (name.StartsWith("Alpha", StringComparison.Ordinal) && name.Length == 6) return name.Substring(5);
            if (name.StartsWith("Keypad", StringComparison.Ordinal)) return "Num " + name.Substring(6);
            return name;
        }

        // The game's screenshot key as the player has it bound (F1 by default), without "Press"
        private static string ScreenshotKey()
        {
            KSP.Input.GameInput input = KSP.Game.GameManager.Instance?.Game?.Input;
            InputAction action = input != null ? input.Global.TakeScreenshot : null;
            string bound = action?.GetBindingDisplayString(InputBinding.DisplayStringOptions.DontIncludeInteractions);
            return string.IsNullOrEmpty(bound) ? "F1" : bound;
        }
    }
}
