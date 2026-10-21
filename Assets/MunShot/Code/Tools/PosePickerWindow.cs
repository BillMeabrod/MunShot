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
    /// Developer window for picking poses out of the game's animation clips: lists the active EVA kerbal's clips,
    /// scrubs through the selected one and holds the kerbal at that frame, showing the result as a
    /// <see cref="ClipPose"/> to copy into a preset. The Preset dropdown holds any version of the loaded presets, to
    /// check them on a kerbal. It also hosts the rig diagnostics. Built in code, so it needs no assets.
    /// </summary>
    public sealed class PosePickerWindow : IDisposable
    {
        private const string WindowId = "MunShot_PosePicker";
        private const float SliderStep = 1f / 60f;

        private readonly ILogger _logger;
        private readonly PosePlayer _player;
        private readonly PanelRenderer _renderer;

        // One entry per preset version, e.g. "Wave (jetpack)"
        private readonly List<(string label, ClipPose pose)> _presetChoices = new List<(string, ClipPose)>();

        private readonly List<AnimationClip> _allClips = new List<AnimationClip>();
        private readonly List<AnimationClip> _shownClips = new List<AnimationClip>();

        private Label _status;
        private TextField _search;
        private ListView _clipList;
        private Label _clipInfo;
        private Slider _timeSlider;
        private Label _readout;

        private Animator _animator;
        private string _kerbalName;
        private AnimationClip _selectedClip;
        private RigDiagnostics.RigSnapshot _snapshot;

        public bool IsOpen { get; private set; }

        /// <summary>
        /// False once the game has torn down the UI the window lived in (it builds its UI afresh for each campaign);
        /// the plugin then makes a new window.
        /// </summary>
        public bool IsAlive => _renderer != null;

        public PosePickerWindow(ILogger logger, PosePlayer player, IReadOnlyList<PosePreset> presets)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _player = player ?? throw new ArgumentNullException(nameof(player));
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
                // F2 hides it with the rest of the UI
                IsHidingEnabled = true,
                UseStockScale = true,
                DisableGameInputForTextFields = true,
                BringToFrontOnPointerDown = true,
                BlockGameInput = true,
                MoveOptions = new MoveOptions { IsMovingEnabled = true, CheckScreenBounds = true }
            };
            _renderer = Window.Create(options, BuildContent());
            _renderer.Hide();
        }

        /// <summary>Opens or closes the window. Opening reads the clips of the kerbal on EVA at that moment.</summary>
        public void SetOpen(bool open)
        {
            if (open == IsOpen) return;
            IsOpen = open;
            if (open)
            {
                _renderer.Show();
                RefreshKerbal();
            }
            else
            {
                _renderer.Hide();
            }
        }

        /// <summary>
        /// Called every frame by the plugin: notices when the kerbal being posed is gone (EVA ended, vessel switched)
        /// and lets the pose go.
        /// </summary>
        public void Tick()
        {
            if (_player.Current.HasValue && !_player.IsHolding)
            {
                _logger.LogInfo($"Pose picker: the posed kerbal '{_kerbalName}' is gone; releasing the pose.");
                _player.Release();
                _animator = null;
                ShowStatus("The posed kerbal is gone. Press Refresh on another EVA kerbal.");
                UpdateReadout();
            }
        }

        public void Dispose()
        {
            _player.Release();
            if (_renderer != null) UnityEngine.Object.Destroy(_renderer.gameObject);
        }

        private VisualElement BuildContent()
        {
            var shell = new AppShell { Title = "MunShot: Pose Picker" };
            shell.CloseClicked += () => SetOpen(false);
            shell.style.width = 380;

            _status = new Label { style = { whiteSpace = WhiteSpace.Normal, marginBottom = 4 } };
            shell.Add(_status);
            shell.Add(new Button(RefreshKerbal) { text = "Refresh" });

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
                style = { height = 260, marginTop = 4, marginBottom = 4 }
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
            var freeze = new Toggle("Freeze game animation") { value = _player.FreezeBaseAnimation };
            freeze.RegisterValueChangedCallback(evt => _player.SetFreezeBaseAnimation(evt.newValue));
            shell.Add(freeze);

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

        private void RefreshKerbal()
        {
            if (!EvaKerbalLocator.TryFind(GameManager.Instance?.Game, out EvaKerbalLocator.Result found, out string reason))
            {
                _animator = null;
                _allClips.Clear();
                ApplyFilter();
                ShowStatus(reason);
                _logger.LogInfo($"Pose picker: no kerbal to pose. {reason}");
                return;
            }

            if (_player.IsHolding && found.Animator != _animator) _player.Release();
            _animator = found.Animator;
            _kerbalName = found.KerbalName;

            _allClips.Clear();
            _allClips.AddRange(PosePlayer.GetClips(_animator));
            ApplyFilter();

            if (_allClips.Count == 0)
            {
                RuntimeAnimatorController controller = _animator.runtimeAnimatorController;
                string message = $"'{_kerbalName}' has no animation clips (controller "
                                 + $"'{(controller != null ? controller.name : "<none>")}').";
                ShowStatus(message);
                _logger.LogWarning($"Pose picker: {message}");
                return;
            }

            ShowStatus($"Posing {_kerbalName}: {_allClips.Count} clips.");
            UpdateReadout();
        }

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
            if (_player.IsHolding) HoldSelected();
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
            if (_player.IsHolding && _selectedClip != null) HoldSelected();
            else UpdateReadout();
        }

        private void HoldSelected()
        {
            if (_selectedClip == null)
            {
                ShowStatus("Pick a clip first.");
                return;
            }
            if (!EnsureKerbal()) return;

            var pose = new ClipPose(_selectedClip.name, Mathf.Max(0f, _timeSlider.value));
            if (!_player.Hold(_animator, pose))
                ShowStatus($"Could not hold {pose}; see the log.");
            UpdateReadout();
        }

        // Shows the preset's clip and time in the list and slider, then holds it
        private void HoldPreset(int index)
        {
            if (index < 0 || index >= _presetChoices.Count) return;
            (string label, ClipPose pose) = _presetChoices[index];
            if (!EnsureKerbal()) return;

            AnimationClip clip = _allClips.FirstOrDefault(c => c.name == pose.ClipName);
            if (clip == null)
            {
                string message = $"Preset '{label}' uses clip '{pose.ClipName}', which '{_kerbalName}' doesn't have.";
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
            _player.Release();
            UpdateReadout();
        }

        private void CopyReadout()
        {
            ClipPose? pose = CurrentPose();
            if (!pose.HasValue) return;
            GUIUtility.systemCopyBuffer = pose.Value.ToString();
            _logger.LogInfo($"Pose picker: copied {pose.Value}");
        }

        private void DumpRig()
        {
            if (!EnsureKerbal()) return;
            try
            {
                string file = RigDiagnostics.DumpRig(_animator, _kerbalName, DescribeHeld());
                _logger.LogInfo($"Pose picker: rig of '{_kerbalName}' dumped to {file}");
                ShowStatus($"Rig dumped to {file}");
            }
            catch (Exception e)
            {
                _logger.LogError($"Pose picker: could not dump the rig of '{_kerbalName}': {e}");
                ShowStatus("Could not dump the rig; see the log.");
            }
        }

        // Samples the selected clip within this frame, then puts the kerbal back as it was (held or released)
        private void WhatMoves()
        {
            if (_selectedClip == null)
            {
                ShowStatus("Pick a clip first.");
                return;
            }
            if (!EnsureKerbal()) return;

            ClipPose? previous = _player.IsHolding ? _player.Current : null;

            // The kerbal's own pose, to tell which bones the clip pins: let go, and have the game's animation
            // pose it again now without moving time on
            _player.Release();
            _animator.Update(0f);
            RigDiagnostics.RigSnapshot live = RigDiagnostics.Snapshot(_animator, "own pose");

            string result;
            try
            {
                if (!_player.Hold(_animator, new ClipPose(_selectedClip.name, 0f)))
                {
                    ShowStatus($"Could not hold '{_selectedClip.name}'; see the log.");
                    return;
                }
                result = RigDiagnostics.WhatMoves(_animator, _selectedClip.name, _selectedClip.length, _player.ApplyNow, live);
            }
            finally
            {
                if (previous.HasValue) _player.Hold(_animator, previous.Value);
                else _player.Release();
            }

            _logger.LogInfo($"Pose picker: {result}");
            int firstLineEnd = result.IndexOf('\n');
            ShowStatus((firstLineEnd >= 0 ? result.Substring(0, firstLineEnd) : result) + ". Details are in the log.");
            UpdateReadout();
        }

        private void TakeSnapshot()
        {
            if (!EnsureKerbal()) return;
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

        private bool EnsureKerbal()
        {
            if (_animator == null) RefreshKerbal();
            return _animator != null;
        }

        private ClipPose? CurrentPose()
        {
            if (_player.Current.HasValue) return _player.Current;
            if (_selectedClip != null) return new ClipPose(_selectedClip.name, Mathf.Max(0f, _timeSlider.value));
            return null;
        }

        private string DescribeHeld() => _player.Current?.ToString() ?? "live animation";

        private void UpdateReadout()
        {
            if (_readout == null) return;
            ClipPose? pose = CurrentPose();
            string state = _player.IsHolding ? "Holding" : "Not holding";
            _readout.text = pose.HasValue ? $"{state}: {pose.Value}" : state;
        }

        private void ShowStatus(string text) => _status.text = text;

        private static string Format(float seconds) => seconds.ToString("0.000", CultureInfo.InvariantCulture);
    }
}
