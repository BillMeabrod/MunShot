using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using KSP.Game;
using MunShot.Posing;
using UitkForKsp2.API;
using UitkForKsp2.Controls;
using UnityEngine;
using UnityEngine.UIElements;
using ILogger = SpaceWarp2.API.Logging.ILogger;

namespace MunShot.Tools
{
    /// <summary>
    /// Developer window for making poses: lists the target's animation clips, scrubs through the selected one and holds
    /// the kerbal at that frame, shown as a <see cref="ClipPose"/> to copy into a preset. The target is photo mode's
    /// selected double, or the active EVA kerbal when there's none. It also hosts the rig diagnostics. It's opened with
    /// its own shortcut, apart from photo mode, and built in code, so it needs no assets.
    /// </summary>
    public sealed class PoseLabWindow : IDisposable
    {
        private const string WindowId = "MunShot_PoseLab";
        private const float SliderStep = 1f / 60f;

        private readonly ILogger _logger;
        private readonly PosePlayer _evaPlayer;
        private readonly PhotoMode _photoMode;
        private readonly PanelRenderer _renderer;

        private readonly List<AnimationClip> _allClips = new List<AnimationClip>();
        private readonly List<AnimationClip> _shownClips = new List<AnimationClip>();

        private AppShell _shell;
        private Label _status;
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

        /// <summary>
        /// False once the game has torn down the UI the window lived in (it builds its UI afresh for each campaign);
        /// the plugin then makes a new window.
        /// </summary>
        public bool IsAlive => _renderer != null;

        // The selected double's player, or the one for the active EVA kerbal when no double is selected
        private PosePlayer Player => _photoMode.Session.Selected?.Player ?? _evaPlayer;

        public PoseLabWindow(ILogger logger, PosePlayer evaPlayer, PhotoMode photoMode)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _evaPlayer = evaPlayer ?? throw new ArgumentNullException(nameof(evaPlayer));
            _photoMode = photoMode ?? throw new ArgumentNullException(nameof(photoMode));

            var options = new WindowOptions
            {
                WindowId = WindowId,
                Parent = null,
                // Usable in photo mode, which hides the rest of the UI
                IsHidingEnabled = false,
                UseStockScale = true,
                DisableGameInputForTextFields = true,
                BringToFrontOnPointerDown = true,
                BlockGameInput = true,
                MoveOptions = new MoveOptions { IsMovingEnabled = true, CheckScreenBounds = true }
            };
            _renderer = Window.Create(options, BuildContent());
            _renderer.Hide();
            _photoMode.Changed += OnPhotoModeChanged;
            _photoMode.Capture.Started += OnCaptureStarted;
            _photoMode.Capture.Finished += OnCaptureFinished;
        }

        public void SetOpen(bool open)
        {
            if (open == IsOpen) return;
            IsOpen = open;
            if (open)
            {
                _renderer.Show();
                RefreshTarget();
            }
            else
            {
                _evaPlayer.Release();
                _renderer.Hide();
            }
        }

        /// <summary>
        /// Called every frame by the plugin: notices when the EVA kerbal being posed is gone (EVA ended, vessel
        /// switched) and lets the pose go.
        /// </summary>
        public void Tick()
        {
            if (_photoMode.Session.Selected == null && _evaPlayer.Current.HasValue && !_evaPlayer.IsHolding)
            {
                _logger.LogInfo($"Pose lab: the posed kerbal '{_targetName}' is gone; releasing the pose.");
                _evaPlayer.Release();
                _animator = null;
                ShowStatus("The posed kerbal is gone. Press Refresh on another EVA kerbal.");
                UpdateReadout();
            }
        }

        /// <summary>Whether the mouse is over the window, so clicks there aren't for the scene.</summary>
        public bool IsPointerOver(Vector2 screen)
        {
            if (!IsOpen || _shell?.panel == null) return false;
            // Screen positions count up from the bottom, panel positions down from the top
            Vector2 inPanel = RuntimePanelUtils.ScreenToPanel(_shell.panel, new Vector2(screen.x, Screen.height - screen.y));
            return _shell.worldBound.Contains(inPanel);
        }

        /// <summary>Whether the clip search has the keyboard.</summary>
        public bool IsTyping
        {
            get
            {
                var focused = _shell?.panel?.focusController?.focusedElement as VisualElement;
                return IsOpen && focused != null && (focused.GetFirstOfType<TextField>() != null || focused.GetFirstOfType<FloatField>() != null);
            }
        }

        public void Dispose()
        {
            _photoMode.Changed -= OnPhotoModeChanged;
            _photoMode.Capture.Started -= OnCaptureStarted;
            _photoMode.Capture.Finished -= OnCaptureFinished;
            _evaPlayer.Release();
            if (_renderer != null) UnityEngine.Object.Destroy(_renderer.gameObject);
        }

        // The target follows photo mode's selection
        private void OnPhotoModeChanged()
        {
            if (!IsOpen) return;
            PhotoSession.Entry selected = _photoMode.Session.Selected;
            Animator wanted = selected != null && selected.IsReady ? selected.Double.Animator : null;
            if (wanted != _animator) RefreshTarget();
        }

        private void OnCaptureStarted()
        {
            if (IsOpen) _renderer.Hide();
        }

        private void OnCaptureFinished(string message)
        {
            if (IsOpen) _renderer.Show();
        }

        private VisualElement BuildContent()
        {
            var shell = _shell = new AppShell { Title = "MunShot: Pose Lab" };
            shell.CloseClicked += () => SetOpen(false);
            shell.style.width = 400;

            _status = new Label { style = { whiteSpace = WhiteSpace.Normal, marginBottom = 4 } };
            shell.Add(_status);

            _target = new Label { style = { marginTop = 6, unityFontStyleAndWeight = FontStyle.Bold } };
            shell.Add(_target);
            shell.Add(new Button(RefreshTarget) { text = "Refresh" });

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

        // ---- Pose target ----

        // The selected double when there is one, else the active EVA kerbal
        private void RefreshTarget()
        {
            PhotoSession.Entry selected = _photoMode.Session.Selected;
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
                _logger.LogInfo($"Pose lab: no kerbal to pose. {reason}");
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
                _logger.LogWarning($"Pose lab: {message}");
            }
            UpdateReadout();
        }

        private bool EnsureTarget()
        {
            if (_animator == null) RefreshTarget();
            if (_animator == null) ShowStatus(_photoMode.Session.Selected != null ? "The double is still being built." : "There's nobody to pose.");
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
            _logger.LogInfo($"Pose lab: copied {pose.Value}");
        }

        // ---- Diagnostics ----

        private void DumpRig()
        {
            if (!EnsureTarget()) return;
            try
            {
                string file = RigDiagnostics.DumpRig(_animator, _targetName, DescribeHeld());
                _logger.LogInfo($"Pose lab: rig of '{_targetName}' dumped to {file}");
                ShowStatus($"Rig dumped to {file}");
            }
            catch (Exception e)
            {
                _logger.LogError($"Pose lab: could not dump the rig of '{_targetName}': {e}");
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

            _logger.LogInfo($"Pose lab: {result}");
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
            _logger.LogInfo($"Pose lab: {result}");
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
