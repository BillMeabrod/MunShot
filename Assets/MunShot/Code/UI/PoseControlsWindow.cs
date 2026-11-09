using System;
using System.Collections.Generic;
using System.Globalization;
using MunShot.Posing;
using UitkForKsp2.API;
using UitkForKsp2.Controls;
using UnityEngine;
using UnityEngine.UIElements;

namespace MunShot.UI
{
    /// <summary>
    /// The pose controls window, opened from the Pose tab once Customize pose is on: a kerbal with a dot on each joint;
    /// clicking a dot shows that joint's sliders, which bend the selected kerbal live over its pose. A movable window
    /// like the game's others. It closes when photo mode closes or Customize pose goes off, and hides with the rest of
    /// photo mode's UI and while a photo is taken.
    /// </summary>
    internal sealed class PoseControlsWindow : IDisposable
    {
        private const string WindowId = "MunShot_PoseControls";
        private const string TitleKey = "MunShot/UI/PoseControls";
        private const float FigureWidth = 340f;

        private static readonly Color Text = new Color32(0xC0, 0xC7, 0xD5, 0xFF);
        private static readonly Color DimText = new Color32(0x70, 0x75, 0x80, 0xFF);

        private readonly PhotoMode _photoMode;
        private readonly PanelRenderer _renderer;
        private readonly Texture2D _kerbal;

        private AppShell _shell;
        private KerbalFigure _figure;
        private Label _jointName;
        private VisualElement _controls;
        private Label _hint;
        private Button _resetJoint;
        private Button _resetAll;

        private PoseJoints.Joint _joint;
        private PhotoSession.Entry _shownFor;
        private readonly List<(PoseJoints.Control control, Slider slider, TextField box)> _sliders =
            new List<(PoseJoints.Control, Slider, TextField)>();
        private bool _hiddenForCapture;

        public bool IsOpen { get; private set; }

        public PoseControlsWindow(PhotoMode photoMode, Texture2D kerbal)
        {
            _photoMode = photoMode ?? throw new ArgumentNullException(nameof(photoMode));
            _kerbal = kerbal;
            var options = new WindowOptions
            {
                WindowId = WindowId,
                Parent = null,
                // Photo mode hides the rest of the UI; this hides with photo mode's own (Refresh)
                IsHidingEnabled = false,
                UseStockScale = true,
                DisableGameInputForTextFields = true,
                BringToFrontOnPointerDown = true,
                BlockGameInput = true,
                MoveOptions = new MoveOptions { IsMovingEnabled = true, CheckScreenBounds = true }
            };
            _renderer = Window.Create(options, BuildContent());
            _renderer.EnableUiSounds();
            _renderer.Hide();

            _photoMode.OpenChanged += OnOpenChanged;
            _photoMode.Changed += Refresh;
            _photoMode.Capture.Started += OnCaptureStarted;
            _photoMode.Capture.Finished += OnCaptureFinished;
        }

        public void Dispose()
        {
            _photoMode.OpenChanged -= OnOpenChanged;
            _photoMode.Changed -= Refresh;
            _photoMode.Capture.Started -= OnCaptureStarted;
            _photoMode.Capture.Finished -= OnCaptureFinished;
            if (_renderer != null) UnityEngine.Object.Destroy(_renderer.gameObject);
        }

        /// <summary>Opens the window for the selected kerbal (when Customize pose is on for them).</summary>
        public void Open()
        {
            IsOpen = true;
            Refresh();
        }

        public void Close()
        {
            IsOpen = false;
            Refresh();
        }

        /// <summary>Whether the mouse is over the window, so clicks there aren't for the scene.</summary>
        public bool IsPointerOver(Vector2 screen)
        {
            if (!IsOpen || _shell?.panel == null || _shell.resolvedStyle.display == DisplayStyle.None) return false;
            // Screen positions count up from the bottom, panel positions down from the top
            Vector2 inPanel = RuntimePanelUtils.ScreenToPanel(_shell.panel, new Vector2(screen.x, Screen.height - screen.y));
            return _renderer.gameObject.activeInHierarchy && _shell.worldBound.Contains(inPanel);
        }

        /// <summary>Whether one of the number boxes has the keyboard, so typing isn't photo mode's keys.</summary>
        public bool IsTyping
        {
            get
            {
                var focused = _shell?.panel?.focusController?.focusedElement as VisualElement;
                return IsOpen && focused != null && focused.GetFirstOfType<TextField>() != null;
            }
        }

        /// <summary>Text built here, in the current language.</summary>
        public void Relocalize()
        {
            if (_shell == null) return;
            _shell.Title = Localization.Translate(TitleKey);
            _figure.Relocalize();
            _resetJoint.text = Localization.Translate("MunShot/UI/ResetJoint");
            _resetAll.text = Localization.Translate("MunShot/UI/ResetAll");
            _hint.text = Localization.Translate("MunShot/UI/PickAJoint");
            ShowJoint(_joint);
        }

        private void OnOpenChanged(bool open) => Refresh();

        private void OnCaptureStarted()
        {
            _hiddenForCapture = true;
            Refresh();
        }

        private void OnCaptureFinished(string message)
        {
            _hiddenForCapture = false;
            Refresh();
        }

        // Shown while open, photo mode is open with its UI showing, and the selected kerbal is customized
        private void Refresh()
        {
            PhotoSession.Entry selected = _photoMode.Session.Selected;
            bool customizable = _photoMode.IsOpen && selected != null && selected.IsReady && selected.Customized;
            // Closed for good when photo mode closes or Customize pose goes off; a kerbal still being built keeps it
            bool building = _photoMode.IsOpen && selected != null && !selected.IsReady;
            if (IsOpen && !customizable && !building) IsOpen = false;
            bool shown = IsOpen && customizable && !_photoMode.UiHidden && !_hiddenForCapture;
            if (shown) _renderer.Show();
            else _renderer.Hide();
            if (!shown) return;

            // Another kerbal: their own settings on the same joint
            if (selected != _shownFor)
            {
                _shownFor = selected;
                ShowJoint(_joint);
            }
            else
            {
                foreach ((PoseJoints.Control control, Slider slider, TextField box) in _sliders)
                    ShowValue(slider, box, selected.Edits.Get(control.Id));
            }
        }

        // ---- Building ----

        private VisualElement BuildContent()
        {
            _shell = new AppShell { Title = Localization.Translate(TitleKey) };
            _shell.CloseClicked += Close;
            _shell.style.width = 900;

            var body = new VisualElement { style = { flexDirection = FlexDirection.Row } };
            _figure = new KerbalFigure(FigureWidth, _kerbal);
            _figure.style.marginRight = 20;
            _figure.JointClicked += ShowJoint;
            body.Add(_figure);

            var side = new VisualElement { style = { flexGrow = 1, flexShrink = 1 } };
            _jointName = new Label { style = { color = Color.white, marginBottom = 4 } };
            _jointName.AddToClassList("uppercase");
            side.Add(_jointName);
            _hint = new Label(Localization.Translate("MunShot/UI/PickAJoint"))
            {
                style = { color = DimText, whiteSpace = WhiteSpace.Normal }
            };
            side.Add(_hint);
            _controls = new ScrollView(ScrollViewMode.Vertical) { style = { maxHeight = 460, flexGrow = 1 } };
            side.Add(_controls);
            _resetJoint = new OabButton { text = Localization.Translate("MunShot/UI/ResetJoint") };
            _resetJoint.clicked += ResetJoint;
            _resetJoint.style.marginTop = 8;
            _resetJoint.style.alignSelf = Align.FlexStart;
            _resetJoint.style.minWidth = 200;
            side.Add(_resetJoint);
            body.Add(side);
            _shell.Add(body);

            var bar = new VisualElement
            {
                style =
                {
                    flexDirection = FlexDirection.Row, marginTop = 8, paddingTop = 8, borderTopWidth = 1,
                    borderTopColor = (Color)new Color32(0x2A, 0x32, 0x3C, 0xFF)
                }
            };
            _resetAll = new OabButton { text = Localization.Translate("MunShot/UI/ResetAll") };
            _resetAll.clicked += ResetAll;
            _resetAll.style.minWidth = 200;
            bar.Add(_resetAll);
            _shell.Add(bar);

            ShowJoint(null);
            return _shell;
        }

        // Shows only this joint's sliders (and their headings), set as the selected kerbal has them
        private void ShowJoint(PoseJoints.Joint joint)
        {
            _joint = joint;
            _figure.Select(joint);
            _controls.Clear();
            _sliders.Clear();
            bool any = joint != null;
            _hint.style.display = any ? DisplayStyle.None : DisplayStyle.Flex;
            _resetJoint.style.display = any ? DisplayStyle.Flex : DisplayStyle.None;
            _jointName.text = any ? Localization.Translate(joint.NameKey) : "";
            if (!any) return;

            PhotoSession.Entry selected = _photoMode.Session.Selected;
            foreach (PoseJoints.Group group in joint.Groups)
            {
                if (group.HeadingKey != null)
                {
                    var heading = new Label(Localization.Translate(group.HeadingKey))
                    {
                        style = { color = DimText, marginTop = 8, marginBottom = 2 }
                    };
                    heading.AddToClassList("uppercase");
                    _controls.Add(heading);
                }
                foreach (PoseJoints.Control control in group.Controls)
                    _controls.Add(SliderRow(control, selected?.Edits.Get(control.Id) ?? 0f));
            }
        }

        // A slider with a box beside it for typing an exact number: dragging moves in whole steps, the box takes
        // tenths; a number past either end goes to that end, anything else puts the box back as it was
        private VisualElement SliderRow(PoseJoints.Control control, float setting)
        {
            var row = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, marginTop = 6 } };
            row.Add(new Label(Localization.Translate(control.NameKey)) { style = { width = 190, color = Text } });
            var slider = new Slider(control.Min, control.Max) { style = { flexGrow = 1, flexShrink = 1 } };
            // Only taken when Enter is pressed or the box is left, not on every key
            var box = new TextField { isDelayed = true, style = { width = 70, marginLeft = 8 } };
            var unit = new Label(control.Unit == PoseJoints.Unit.Percent ? "%" : "°") { style = { width = 18, color = Text } };
            ShowValue(slider, box, setting);
            slider.RegisterValueChangedCallback(change =>
            {
                float whole = Mathf.Round(change.newValue);
                _photoMode.SetPoseControl(control, whole);
                box.SetValueWithoutNotify(Number(whole));
            });
            box.RegisterValueChangedCallback(change =>
            {
                if (!TryParse(change.newValue, out float typed))
                {
                    box.SetValueWithoutNotify(change.previousValue);
                    return;
                }
                float value = Mathf.Round(Mathf.Clamp(typed, control.Min, control.Max) * 10f) / 10f;
                _photoMode.SetPoseControl(control, value);
                ShowValue(slider, box, value);
            });
            row.Add(slider);
            row.Add(box);
            row.Add(unit);
            _sliders.Add((control, slider, box));
            return row;
        }

        private static void ShowValue(Slider slider, TextField box, float setting)
        {
            slider.SetValueWithoutNotify(setting);
            box.SetValueWithoutNotify(Number(setting));
        }

        private static string Number(float value) => value.ToString("0.#", CultureInfo.CurrentCulture);

        // In the player's own way of writing numbers, or with a point
        private static bool TryParse(string text, out float value)
        {
            text = text?.Trim().TrimEnd('°', '%').Trim();
            return float.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value)
                   || float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        private void ResetJoint()
        {
            if (_joint != null) _photoMode.ResetJoint(_joint);
        }

        private void ResetAll() => _photoMode.ResetPoseEdits();
    }
}
