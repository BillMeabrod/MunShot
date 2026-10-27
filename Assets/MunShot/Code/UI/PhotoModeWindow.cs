using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MunShot.Cameras;
using MunShot.Lighting;
using MunShot.Posing;
using I2.Loc;
using SpaceWarp2.API.Input;
using UitkForKsp2.API;
using UitkForKsp2.API.Manipulator;
using UitkForKsp2.Controls;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;
using ILogger = SpaceWarp2.API.Logging.ILogger;

namespace MunShot.UI
{
    /// <summary>
    /// Photo mode's overlay, built from PhotoModeWindow.uxml with a <see cref="PhotoModeViewModel"/> as its data
    /// source: the scene fills the screen, and a flight panel on the left edge (Kerbals, Pose, Camera, Color, Light tabs; opened and
    /// closed by its bar toggle), a shutter button and a hint bar sit on its edges. It's built from the KerbalUI
    /// theme's own pieces as Redux's panels are, with localized text and the game's menu sounds on hover and click.
    /// It shows while photo mode is open, stays up when photo mode hides the rest of the game's UI, hides with the
    /// Hide UI key to frame the shot, and hides for the moment a photo is taken. Everything it does goes through
    /// <see cref="PhotoMode"/>.
    /// </summary>
    public sealed class PhotoModeWindow : IDisposable
    {
        private const string WindowId = "MunShot_PhotoMode";

        // The chosen one: UitkForKsp2's "selected" for its sounds (a chosen item clicks differently) and list rows,
        // "active" for the theme's OabButton (lit up, as the VAB's chosen tool)
        private const string SelectedClass = DocumentSoundManipulator.SelectedClass;
        private const string ActiveClass = "active";

        // How long a note stays up
        private const long ToastMilliseconds = 3500;

        private readonly PhotoMode _photoMode;
        private readonly ILogger _logger;
        private readonly PanelRenderer _renderer;
        private readonly PhotoModeViewModel _model;

        // The pose list's choices: "None", then each pose (see ListPoses)
        private readonly List<PosePreset> _poseChoices = new List<PosePreset>();

        private PhotoModeTab _tab = PhotoModeTab.Kerbals;

        // The camera mode the hint bar shows, so it's only rebuilt when that changes
        private (PhotoCameraMode mode, bool dragging)? _hintsFor;
        private int _toastId;

        private VisualElement _root;
        private VisualElement _chrome;
        private VisualElement _sidebar;
        private VisualElement _sidePanel;
        private Button _shutter;
        private VisualElement _hints;
        private VisualElement _selectionBox;
        private readonly Dictionary<PhotoModeTab, (Button tab, VisualElement page)> _tabs =
            new Dictionary<PhotoModeTab, (Button, VisualElement)>();
        private Button _bringIn;
        private ScrollView _kerbalList;
        private ScrollView _poseList;
        private ScrollView _orbitList;
        private Toggle _helmet;
        private Toggle _visor;
        private Toggle _angleSnap;
        private Toggle _jetpack;
        // The handle tool's buttons, on the Pose tab and again on the Light tab
        private Button _move;
        private Button _rotate;
        private Button _lightMove;
        private Button _lightRotate;
        private Button _free;
        private Button _orbit;
        private Slider _fieldOfView;
        private Slider _fisheye;
        private Toggle _depthOfField;
        private VisualElement _focusControls;
        private Slider _focusDistance;
        private Slider _blur;
        private Slider _exposure;
        private Slider _contrast;
        private Slider _saturation;
        private Slider _warmth;
        private ScrollView _lightList;
        private Button _pointLight;
        private Button _spotLight;
        private ColorPicker _lightColor;
        private Slider _lightBrightness;
        private Slider _lightRange;
        private Slider _spotAngle;
        private Toggle _kerbalsOnly;
        // The light whose colour the picker shows, so the picker is only set when that changes (setting it while it's
        // dragged would move its handles)
        private PhotoLight _colorShownFor;

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
            _model = new PhotoModeViewModel(photoMode, ShowTab, OpenKerbalMenu);

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
            // hints) is redrawn then too, told by the game's localization as Redux's own windows are
            LocalizationManager.OnLocalizeEvent += OnLanguageChanged;
        }

        public void Dispose()
        {
            _photoMode.OpenChanged -= OnOpenChanged;
            _photoMode.Changed -= Refresh;
            _photoMode.Status -= ShowToast;
            _photoMode.Capture.Started -= OnCaptureStarted;
            _photoMode.Capture.Finished -= OnCaptureFinished;
            LocalizationManager.OnLocalizeEvent -= OnLanguageChanged;
            if (_renderer != null) UnityEngine.Object.Destroy(_renderer.gameObject);
        }

        /// <summary>Called every frame: draws the selection box while one is being dragged.</summary>
        public void Tick()
        {
            // Dragging kerbals shows what else the drag can do; letting go brings the usual hints back
            if (_photoMode.IsOpen) ShowHints(_photoMode.Camera.Mode, _photoMode.Manipulator.IsDraggingDoubles);

            if (_selectionBox == null || _root?.panel == null) return;
            Rect? box = _photoMode.IsOpen ? _photoMode.Manipulator.SelectionBox : null;
            if (!box.HasValue)
            {
                SetShown(_selectionBox, false);
                return;
            }

            // Screen positions count up from the bottom, panel positions down from the top
            Rect screen = box.Value;
            Vector2 topLeft = RuntimePanelUtils.ScreenToPanel(_root.panel, new Vector2(screen.xMin, Screen.height - screen.yMax));
            Vector2 bottomRight = RuntimePanelUtils.ScreenToPanel(_root.panel, new Vector2(screen.xMax, Screen.height - screen.yMin));
            SetShown(_selectionBox, true);
            _selectionBox.style.left = topLeft.x;
            _selectionBox.style.top = topLeft.y;
            _selectionBox.style.width = bottomRight.x - topLeft.x;
            _selectionBox.style.height = bottomRight.y - topLeft.y;
        }

        /// <summary>
        /// Whether the mouse is over UI that takes clicks (the overlay's panels, the Bring in menu, any window on the
        /// same panel), so clicks there aren't for the scene. The overlay's full-screen parts let clicks through
        /// (picking-mode Ignore), so they aren't picked.
        /// </summary>
        public bool IsPointerOver(Vector2 screen)
        {
            if (!_photoMode.IsOpen || _photoMode.UiHidden || _root?.panel == null) return false;
            // Screen positions count up from the bottom, panel positions down from the top
            Vector2 inPanel = RuntimePanelUtils.ScreenToPanel(_root.panel, new Vector2(screen.x, Screen.height - screen.y));
            VisualElement picked = _root.panel.Pick(inPanel);
            // The window's own containers (and the panel's) cover the screen too
            return picked != null && !Encloses(picked, _root);
        }

        // Whether outer is element itself or one of its ancestors
        private static bool Encloses(VisualElement outer, VisualElement element)
        {
            for (VisualElement e = element; e != null; e = e.parent)
                if (e == outer) return true;
            return false;
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
            _selectionBox = Required<VisualElement>(root, "selection-box");
            if (_chrome == null || _sidePanel == null) return;

            // The bar toggle opens and closes the panel, as on Redux's orbital info panel
            Required<Toggle>(root, "panel-toggle")?.RegisterValueChangedCallback(change =>
                SetShown(_sidePanel, change.newValue));

            // The buttons run the view model's commands, bound in the UXML; these are looked up to show which is chosen
            _tabs[PhotoModeTab.Kerbals] = (Required<Button>(root, "tab-kerbals"), Required<VisualElement>(root, "page-kerbals"));
            _tabs[PhotoModeTab.Pose] = (Required<Button>(root, "tab-pose"), Required<VisualElement>(root, "page-pose"));
            _tabs[PhotoModeTab.Camera] = (Required<Button>(root, "tab-camera"), Required<VisualElement>(root, "page-camera"));
            _tabs[PhotoModeTab.Color] = (Required<Button>(root, "tab-color"), Required<VisualElement>(root, "page-color"));
            _tabs[PhotoModeTab.Light] = (Required<Button>(root, "tab-light"), Required<VisualElement>(root, "page-light"));

            _bringIn = Required<Button>(root, "bring-in");
            _kerbalList = Required<ScrollView>(root, "kerbal-list");
            _poseList = Required<ScrollView>(root, "pose-list");
            _orbitList = Required<ScrollView>(root, "orbit-list");
            _helmet = Required<Toggle>(root, "helmet");
            _visor = Required<Toggle>(root, "visor");
            _move = Required<Button>(root, "tool-move");
            _rotate = Required<Button>(root, "tool-rotate");
            _lightMove = Required<Button>(root, "light-tool-move");
            _lightRotate = Required<Button>(root, "light-tool-rotate");
            _angleSnap = Required<Toggle>(root, "angle-snap");
            _jetpack = Required<Toggle>(root, "jetpack");
            _free = Required<Button>(root, "camera-free");
            _orbit = Required<Button>(root, "camera-orbit");
            _fieldOfView = Required<Slider>(root, "field-of-view");
            _fisheye = Required<Slider>(root, "fisheye");
            _depthOfField = Required<Toggle>(root, "depth-of-field");
            _focusControls = Required<VisualElement>(root, "focus-controls");
            _focusDistance = Required<Slider>(root, "focus-distance");
            _blur = Required<Slider>(root, "blur");
            _exposure = ColorSlider(root, "exposure", PhotoLook.MaxExposure, _photoMode.SetExposure);
            _contrast = ColorSlider(root, "contrast", PhotoLook.MaxColorAdjustment, _photoMode.SetContrast);
            _saturation = ColorSlider(root, "saturation", PhotoLook.MaxColorAdjustment, _photoMode.SetSaturation);
            _warmth = ColorSlider(root, "warmth", PhotoLook.MaxColorAdjustment, _photoMode.SetWarmth);
            BindLights(root);

            _helmet?.RegisterValueChangedCallback(change => _photoMode.SetHelmet(change.newValue));
            _visor?.RegisterValueChangedCallback(change => _photoMode.SetVisor(change.newValue));
            _angleSnap?.RegisterValueChangedCallback(change => _photoMode.SetAngleSnap(change.newValue));
            _jetpack?.RegisterValueChangedCallback(change => _photoMode.SetJetpack(change.newValue));
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
            _fisheye?.RegisterValueChangedCallback(change => _photoMode.SetFisheye(change.newValue));
            _depthOfField?.RegisterValueChangedCallback(change => _photoMode.SetDepthOfField(change.newValue));
            _focusDistance?.RegisterValueChangedCallback(change => _photoMode.SetFocusDistance(FocusDistanceAt(change.newValue)));
            _blur?.RegisterValueChangedCallback(change => _photoMode.SetAperture(ApertureAt(change.newValue)));

            ShowTab(_tab);
            Refresh();
        }

        private void BindLights(VisualElement root)
        {
            _lightList = Required<ScrollView>(root, "light-list");
            _pointLight = Required<Button>(root, "light-point");
            _spotLight = Required<Button>(root, "light-spot");
            _lightColor = Required<ColorPicker>(root, "light-color");
            _lightBrightness = Required<Slider>(root, "light-brightness");
            _lightRange = Required<Slider>(root, "light-range");
            _spotAngle = Required<Slider>(root, "spot-angle");
            _kerbalsOnly = Required<Toggle>(root, "light-kerbals-only");

            _lightColor?.RegisterValueChangedCallback(change => _photoMode.ChangeLight(light => light.Color = change.newValue));
            _lightBrightness?.RegisterValueChangedCallback(change =>
                _photoMode.ChangeLight(light => light.Brightness = ValueAt(change.newValue, PhotoLight.MinBrightness, PhotoLight.MaxBrightness)));
            _lightRange?.RegisterValueChangedCallback(change =>
                _photoMode.ChangeLight(light => light.Range = ValueAt(change.newValue, PhotoLight.MinRange, PhotoLight.MaxRange)));
            if (_spotAngle != null)
            {
                _spotAngle.lowValue = PhotoLight.MinSpotAngle;
                _spotAngle.highValue = PhotoLight.MaxSpotAngle;
                _spotAngle.RegisterValueChangedCallback(change => _photoMode.ChangeLight(light => light.SpotAngle = change.newValue));
            }
            _kerbalsOnly?.RegisterValueChangedCallback(change => _photoMode.ChangeLight(light => light.KerbalsOnly = change.newValue));
        }

        // A colour setting's slider: from -max to max, 0 (the game's own picture) in the middle
        private Slider ColorSlider(VisualElement root, string name, float max, Action<float> set)
        {
            Slider slider = Required<Slider>(root, name);
            if (slider == null) return null;
            slider.lowValue = -max;
            slider.highValue = max;
            slider.RegisterValueChangedCallback(change => set(change.newValue));
            return slider;
        }

        // A named element the UXML must have; a missing one is logged (the rest of the overlay still works)
        private T Required<T>(VisualElement root, string name) where T : VisualElement
        {
            T element = root.Q<T>(name);
            if (element == null) _logger.LogError($"Photo mode overlay: PhotoModeWindow.uxml has no {typeof(T).Name} named '{name}'.");
            return element;
        }

        // ---- Showing photo mode ----

        private void OnOpenChanged(bool open)
        {
            if (open)
            {
                _hintsFor = null;
                ListPoses();
                _model.ToastVisible = false;
                ShowTab(PhotoModeTab.Kerbals);
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
            _hintsFor = null;
            ListPoses();   // sorted by their names in the new language
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

        private void ShowTab(PhotoModeTab tab)
        {
            _tab = tab;
            foreach (KeyValuePair<PhotoModeTab, (Button tab, VisualElement page)> entry in _tabs)
            {
                bool shown = entry.Key == tab;
                MarkActive(entry.Value.tab, shown);
                if (entry.Value.page != null) SetShown(entry.Value.page, shown);
            }
        }

        // Shows or hides through UitkForKsp2 (hiding also lets go of focus and text input inside), only on a change: some
        // of this runs every frame, and hiding tells UitkForKsp2's listeners each time. Whether it shows now is its own
        // display if set, or else the stylesheet's (the selection box starts hidden there)
        private static void SetShown(VisualElement element, bool shown)
        {
            if (element == null) return;
            StyleEnum<DisplayStyle> own = element.style.display;
            bool showing = own.keyword == StyleKeyword.Undefined
                ? own.value != DisplayStyle.None
                : element.resolvedStyle.display != DisplayStyle.None;
            if (showing == shown) return;
            if (shown) element.Show();
            else element.Hide();
        }

        // Brings every control in line with photo mode
        private void Refresh()
        {
            if (_chrome == null) return;
            SetShown(_chrome, !_photoMode.UiHidden);

            PhotoSession session = _photoMode.Session;
            PhotoSession.Entry selected = session.Selected;
            bool ready = selected != null && selected.IsReady;
            _model.HasKerbals = session.Entries.Count > 0;
            // In capitals like the headings beside it: "nobody in" is a key the binding's UppercaseLocalization converter
            // translates (again on a language change), which leaves a name as it is
            _model.SelectedName = selected != null ? selected.Double.Source.Name.ToUpperInvariant() : "#" + Localization.NobodyIn;

            RefreshKerbals(session, selected);
            RefreshPoses(selected, ready);

            _helmet?.SetEnabled(ready);
            _helmet?.SetValueWithoutNotify(!ready || selected.Double.HelmetOn);
            _visor?.SetEnabled(ready && selected.Double.HelmetOn);
            _visor?.SetValueWithoutNotify(!ready || selected.Double.VisorOn);

            MarkActive(_move, _photoMode.Manipulator.Mode == HandleMode.Move);
            MarkActive(_rotate, _photoMode.Manipulator.Mode == HandleMode.Rotate);
            MarkActive(_lightMove, _photoMode.Manipulator.Mode == HandleMode.Move);
            MarkActive(_lightRotate, _photoMode.Manipulator.Mode == HandleMode.Rotate);
            _angleSnap?.SetValueWithoutNotify(_photoMode.Manipulator.AngleSnap);

            PhotoCamera camera = _photoMode.Camera;
            MarkActive(_free, camera.Mode == PhotoCameraMode.Free);
            MarkActive(_orbit, camera.Mode == PhotoCameraMode.Orbit);
            _model.IsOrbiting = camera.Mode == PhotoCameraMode.Orbit;
            RefreshOrbitTargets(camera);
            _fieldOfView?.SetValueWithoutNotify(camera.FieldOfView);
            ShowFieldOfView();
            _fisheye?.SetValueWithoutNotify(_photoMode.Look.Fisheye);
            _model.FisheyeText = _photoMode.Look.Fisheye.ToString("P0", CultureInfo.CurrentCulture);
            RefreshFocus(_photoMode.Look);
            RefreshColor(_photoMode.Look);
            RefreshLights(_photoMode.Lights);
            ShowHints(camera.Mode, _photoMode.Manipulator.IsDraggingDoubles);
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
            _model.RefreshCommands();
        }

        private void RefreshPoses(PhotoSession.Entry selected, bool ready)
        {
            _jetpack?.SetEnabled(ready);
            _jetpack?.SetValueWithoutNotify(ready && selected.Jetpack);
            if (_poseList == null) return;
            _poseList.Clear();
            PosePreset current = ready ? selected.Preset : null;
            foreach (PosePreset preset in _poseChoices)
            {
                PosePreset choice = preset;
                Button item = Item(PoseLabel(choice), ready && choice == current, () => _photoMode.HoldPreset(choice));
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

        // The lights: click a name to select it, x to take it away; the selected one's settings below
        private void RefreshLights(PhotoLights lights)
        {
            _model.HasLights = lights.All.Count > 0;
            if (_lightList != null)
            {
                _lightList.Clear();
                foreach (PhotoLight light in lights.All)
                {
                    PhotoLight chosen = light;
                    var row = new VisualElement();
                    row.AddToClassList("pm-item-row");
                    var remove = new Button(() => _photoMode.RemoveLight(chosen)) { text = "×" };
                    remove.AddToClassList("pm-remove");
                    row.Add(Item(PhotoMode.LightName(light), light == lights.Selected, () => _photoMode.SelectLight(chosen)));
                    row.Add(remove);
                    _lightList.Add(row);
                }
            }

            PhotoLight selected = lights.Selected;
            _model.HasSelectedLight = selected != null;
            _model.IsSpotLight = selected?.Kind == PhotoLightKind.Spot;
            if (selected == null)
            {
                _colorShownFor = null;
                return;
            }
            _model.SelectedLightName = PhotoMode.LightName(selected).ToUpper(CultureInfo.CurrentCulture);
            MarkActive(_pointLight, selected.Kind == PhotoLightKind.Point);
            MarkActive(_spotLight, selected.Kind == PhotoLightKind.Spot);
            if (_lightColor != null && _colorShownFor != selected)
            {
                // Set as a change: the SDK's picker only moves its handles on a change event. The change comes back
                // as the light's own colour, so setting it changes nothing
                _colorShownFor = selected;
                _lightColor.value = selected.Color;
            }
            _lightBrightness?.SetValueWithoutNotify(SliderAt(selected.Brightness, PhotoLight.MinBrightness, PhotoLight.MaxBrightness));
            _lightRange?.SetValueWithoutNotify(SliderAt(selected.Range, PhotoLight.MinRange, PhotoLight.MaxRange));
            _spotAngle?.SetValueWithoutNotify(selected.SpotAngle);
            _kerbalsOnly?.SetValueWithoutNotify(selected.KerbalsOnly);
            _model.LightBrightnessText = selected.Brightness.ToString(selected.Brightness < 10f ? "0.0" : "0", CultureInfo.CurrentCulture);
            _model.LightRangeText = selected.Range.ToString("0", CultureInfo.CurrentCulture) + " " + KSP.Units.SymbolMeters;
            _model.SpotAngleText = selected.SpotAngle.ToString("0", CultureInfo.CurrentCulture) + KSP.Units.SymbolDegree;
        }

        private void RefreshColor(PhotoLook look)
        {
            _exposure?.SetValueWithoutNotify(look.Exposure);
            _contrast?.SetValueWithoutNotify(look.Contrast);
            _saturation?.SetValueWithoutNotify(look.Saturation);
            _warmth?.SetValueWithoutNotify(look.Warmth);
            _model.ExposureText = Signed(look.Exposure, "0.0");
            _model.ContrastText = Signed(look.Contrast, "0");
            _model.SaturationText = Signed(look.Saturation, "0");
            _model.WarmthText = Signed(look.Warmth, "0");
            _model.RefreshCommands();
        }

        // "+1.5", "-20", or "0" in the middle
        private static string Signed(float value, string format)
        {
            string shown = value.ToString(format, CultureInfo.CurrentCulture);
            return value > 0f && shown != 0f.ToString(format, CultureInfo.CurrentCulture) ? "+" + shown : shown;
        }

        private void RefreshFocus(PhotoLook look)
        {
            _depthOfField?.SetValueWithoutNotify(look.DepthOfField);
            _focusControls?.SetEnabled(look.DepthOfField);
            _focusDistance?.SetValueWithoutNotify(SliderAt(look.FocusDistance, PhotoLook.MinFocusDistance, PhotoLook.MaxFocusDistance));
            _blur?.SetValueWithoutNotify(SliderAt(look.Aperture, PhotoLook.MaxAperture, PhotoLook.MinAperture));
            _model.FocusDistanceText = FocusDistanceText(look.FocusDistance);
            _model.ApertureText = "f/" + look.Aperture.ToString("0.#", CultureInfo.CurrentCulture);
        }

        // Infinity as a lens's ring marks it
        private static string FocusDistanceText(float metres) =>
            float.IsPositiveInfinity(metres)
                ? "∞"
                : metres.ToString(metres < 10f ? "0.0" : "0", CultureInfo.CurrentCulture) + " " + KSP.Units.SymbolMeters;

        // The focus and blur sliders run from 0 to 1 along a curve, as a lens's rings do: each step is the same share
        // nearer or further (a few centimetres up close, hundreds of metres far off), and each step of blur the same
        // fraction of an f-stop. Blur runs from the narrowest aperture (least blur) to the widest. The focus slider's far
        // end is infinity, as on a lens. A light's brightness and reach run along the same kind of curve
        private static float FocusDistanceAt(float slider) =>
            slider >= 1f ? float.PositiveInfinity : ValueAt(slider, PhotoLook.MinFocusDistance, PhotoLook.MaxFocusDistance);
        private static float ApertureAt(float slider) => ValueAt(slider, PhotoLook.MaxAperture, PhotoLook.MinAperture);

        private static float ValueAt(float slider, float from, float to) => from * Mathf.Pow(to / from, Mathf.Clamp01(slider));
        private static float SliderAt(float value, float from, float to) =>
            float.IsPositiveInfinity(value) ? 1f : Mathf.Clamp01(Mathf.Log(value / from) / Mathf.Log(to / from));

        private void ShowFieldOfView() =>
            _model.FieldOfViewText = _photoMode.Camera.FieldOfView.ToString("0", CultureInfo.CurrentCulture) + KSP.Units.SymbolDegree;

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

        // "None" first, then each pose once (the Jetpack switch picks its version), in alphabetical order of the names
        // shown, in the game's language. Listed again each time photo mode opens (it reads the pose files again then)
        // and when the language changes
        private void ListPoses()
        {
            _poseChoices.Clear();
            _poseChoices.Add(null);
            _poseChoices.AddRange(_photoMode.Presets.OrderBy(preset => preset.DisplayName, Localization.NameOrder));
        }

        private static string PoseLabel(PosePreset preset) =>
            preset == null ? Localization.Translate(Localization.NoPose) : preset.DisplayName;

        // ---- Hints ----

        // The keys as they're bound, as keycaps, and what they do in the camera's current mode. Only the essentials:
        // the rest (Fast, clicking a kerbal) shows itself
        private void ShowHints(PhotoCameraMode mode, bool dragging)
        {
            if (_hints == null || _hintsFor == (mode, dragging)) return;
            _hintsFor = (mode, dragging);
            _hints.Clear();
            if (dragging)
            {
                AddHint(Localization.HintTurn, Localization.Translate(Localization.KeyRightMouse));
                AddHint(Localization.HintLean, Key(MunShotSettings.TiltWhileDragging));
                AddHint(Localization.HintTip, Key(MunShotSettings.TiltWhileDragging), Key(MunShotSettings.TipWhileDragging));
                return;
            }
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
            AddHint(Localization.HintLevel, Key(MunShotSettings.LevelHorizon));
            AddHint(Localization.HintSelectSeveral, Localization.Translate(Localization.KeyDrag));
            AddHint(Localization.HintRemove, Key(MunShotSettings.RemoveKerbal));
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

        // Unity's key names, the way a keyboard in the game's language labels them: no "Left", short modifier names,
        // digits as digits. Letter and function keys read the same everywhere, so they stay Unity's
        private static string KeyName(KeyCode key)
        {
            switch (key)
            {
                case KeyCode.LeftShift:
                case KeyCode.RightShift:
                    return Localization.Translate(Localization.KeyShift);
                case KeyCode.LeftControl:
                case KeyCode.RightControl:
                    return Localization.Translate(Localization.KeyCtrl);
                case KeyCode.LeftAlt:
                case KeyCode.RightAlt:
                    return Localization.Translate(Localization.KeyAlt);
                case KeyCode.Space:
                    return Localization.Translate(Localization.KeySpace);
                case KeyCode.Return:
                    return Localization.Translate(Localization.KeyEnter);
                case KeyCode.Escape:
                    return Localization.Translate(Localization.KeyEsc);
            }
            string name = key.ToString();
            if (name.StartsWith("Alpha", StringComparison.Ordinal) && name.Length == 6) return name.Substring(5);
            if (name.StartsWith("Keypad", StringComparison.Ordinal)) return Localization.Format(Localization.KeyNumpad, name.Substring(6));
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
