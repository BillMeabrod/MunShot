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
    /// The pose controls window (PoseControlsWindow.uxml), opened from the Pose tab once Customize pose is on: a kerbal
    /// with a dot on each joint; clicking a dot shows that joint's sliders, which bend the selected kerbal live over its
    /// pose. A movable window like the game's others. It closes when photo mode closes or Customize pose goes off, and
    /// hides with the rest of photo mode's UI and while a photo is taken. The UXML's own texts are translated by
    /// UitkForKsp2; the joint's name and sliders, built here, by <see cref="Relocalize"/>.
    /// </summary>
    internal sealed class PoseControlsWindow : IDisposable
    {
        private const string WindowId = "MunShot_PoseControls";

        private readonly PhotoMode _photoMode;
        private readonly PanelRenderer _renderer;

        private AppShell _shell;
        private KerbalFigure _figure;
        private Label _jointName;
        private ScrollView _controls;
        private Label _hint;
        private Button _resetJoint;

        private PoseJoints.Joint _joint;
        private PhotoSession.Entry _shownFor;
        private readonly List<(PoseJoints.Control control, Slider slider, TextField box)> _sliders =
            new List<(PoseJoints.Control, Slider, TextField)>();
        private bool _hiddenForCapture;

        public bool IsOpen { get; private set; }

        public PoseControlsWindow(PhotoMode photoMode, VisualTreeAsset uxml)
        {
            _photoMode = photoMode ?? throw new ArgumentNullException(nameof(photoMode));
            if (uxml == null) throw new ArgumentNullException(nameof(uxml));
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
            _renderer = Window.Create(options, uxml);
            _renderer.OnPanelRoot(Bind);
            _renderer.EnableLocalization();
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

        /// <summary>Whether the window is open and on screen (not hidden with photo mode's UI or for a photo).</summary>
        public bool IsShown => IsOpen && _shell?.panel != null && _shell.resolvedStyle.display != DisplayStyle.None
                               && _renderer.gameObject.activeInHierarchy;

        /// <summary>Whether the mouse is over the window, so clicks there aren't for the scene.</summary>
        public bool IsPointerOver(Vector2 screen)
        {
            if (!IsShown) return false;
            // Screen positions count up from the bottom, panel positions down from the top
            Vector2 inPanel = RuntimePanelUtils.ScreenToPanel(_shell.panel, new Vector2(screen.x, Screen.height - screen.y));
            return _shell.worldBound.Contains(inPanel);
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

        /// <summary>Text built here (the joint's name and sliders, the dots' names), in the current language.</summary>
        public void Relocalize()
        {
            if (_shell == null) return;
            _figure.Relocalize();
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
            bool shown = IsOpen && customizable && !_photoMode.UiHidden && !_hiddenForCapture && _shell != null;
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

        // The window's elements from its UXML, with the kerbal drawing put in its spot
        private void Bind(VisualElement root)
        {
            _shell = Required<AppShell>(root, "pose-window");
            _jointName = Required<Label>(root, "joint-name");
            _hint = Required<Label>(root, "hint");
            _controls = Required<ScrollView>(root, "controls");
            _resetJoint = Required<Button>(root, "reset-joint");
            Button resetAll = Required<Button>(root, "reset-all");
            VisualElement figureSpot = Required<VisualElement>(root, "figure-spot");

            _shell.CloseClicked += Close;
            _resetJoint.clicked += ResetJoint;
            resetAll.clicked += ResetAll;
            _figure = new KerbalFigure();
            _figure.JointClicked += ShowJoint;
            figureSpot.parent.Insert(figureSpot.parent.IndexOf(figureSpot), _figure);
            figureSpot.RemoveFromHierarchy();
            ShowJoint(_joint);
        }

        // An element the UXML must have; a mismatch between the two is a bug, so it throws
        private static T Required<T>(VisualElement root, string name) where T : VisualElement =>
            root.Q<T>(name) ?? throw new InvalidOperationException($"PoseControlsWindow.uxml has no {typeof(T).Name} named '{name}'.");

        // Shows only this joint's sliders (and their headings), set as the selected kerbal has them
        private void ShowJoint(PoseJoints.Joint joint)
        {
            _joint = joint;
            if (_shell == null) return;
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
                    _controls.Add(Styled(new Label(Localization.Translate(group.HeadingKey)), "pose-heading", "pm-dim", "uppercase"));
                }
                foreach (PoseJoints.Control control in group.Controls)
                    _controls.Add(SliderRow(control, selected?.Edits.Get(control.Id) ?? 0f));
            }
        }

        // An element with these style classes added
        private static T Styled<T>(T element, params string[] classes) where T : VisualElement
        {
            foreach (string name in classes) element.AddToClassList(name);
            return element;
        }

        // A slider with a box beside it for typing an exact number: dragging moves in whole steps, the box takes
        // tenths; a number past either end goes to that end, anything else puts the box back as it was
        private VisualElement SliderRow(PoseJoints.Control control, float setting)
        {
            var row = Styled(new VisualElement(), "pm-row");
            row.Add(Styled(new Label(Localization.Translate(control.NameKey)), "pose-control-name"));
            var slider = Styled(new Slider(control.Min, control.Max), "pose-slider");
            // Only taken when Enter is pressed or the box is left, not on every key
            var box = Styled(new TextField { isDelayed = true }, "pose-number");
            var unit = Styled(new Label(control.Unit == PoseJoints.Unit.Percent ? "%" : "°"), "pose-unit");
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
