using System;
using System.Collections.Generic;
using System.IO;
using KSP.Animation;
using KSP.Game;
using KSP.Messages;
using MunShot.Capture;
using MunShot.Posing;
using MunShot.Tools;
using Redux.ExtraModTypes;

namespace MunShot
{
    public class MunShotPlugin : KerbalMod
    {
        // The game's own poses shipped with MunShot, in the mod folder's Poses folder
        private const string StockPresetsFile = "stock.json";
        private const string PresetsFolder = "Poses";

        private PosePlayer _posePlayer;
        private PhotoSession _photoSession;
        private GamePause _pause;
        private UiHider _uiHider;
        private FlightInputLock _inputLock;
        private PosePickerWindow _posePicker;
        private IReadOnlyList<PosePreset> _presets = Array.Empty<PosePreset>();

        public override void OnPreInitialized()
        {
            // The game only gives a mod its settings page if its settings exist when the menu is set up
            MunShotSettings.Bind(SWConfiguration);
        }

        public override void OnInitialized()
        {
            _posePlayer = NewPosePlayer();
            _photoSession = new PhotoSession(new PhotoDoubleFactory(message => SWLogger.LogError(message)), NewPosePlayer,
                message => SWLogger.LogInfo(message), message => SWLogger.LogError(message));
            _pause = new GamePause(message => SWLogger.LogInfo(message), message => SWLogger.LogWarning(message));
            _uiHider = new UiHider(message => SWLogger.LogInfo(message), message => SWLogger.LogWarning(message));
            _inputLock = new FlightInputLock(message => SWLogger.LogInfo(message), message => SWLogger.LogWarning(message));
            Messages?.PersistentSubscribe<GameStateChangedMessage>(OnGameStateChanged);
            _presets = LoadPresets();
        }

        // After a freeze, a kerbal's Animator gets the speed the game gives it for the current time warp
        private PosePlayer NewPosePlayer() => new PosePlayer(message => SWLogger.LogError(message),
            _ => AnimationManager.GetAnimSpeedForTimeWarpRate(Game?.ViewController?.TimeWarp?.CurrentRate ?? 1f));

        private IReadOnlyList<PosePreset> LoadPresets()
        {
            string file = Path.Combine(SWMetadata.Folder.FullName, PresetsFolder, StockPresetsFile);
            if (!File.Exists(file))
            {
                SWLogger.LogError($"Stock pose presets not found at '{file}'; the mod's install is incomplete.");
                return Array.Empty<PosePreset>();
            }

            IReadOnlyList<PosePreset> presets = PosePresetLibrary.Load(file, message => SWLogger.LogWarning(message));
            SWLogger.LogInfo($"Loaded {presets.Count} pose presets from '{file}'.");
            return presets;
        }

        // Lets go of the campaign before the game tears it down: doubles go, originals come back, no kerbal stays
        // posed, and the picker closes
        public override void OnCampaignUnloading() => EndPhotoMode();

        private void EndPhotoMode()
        {
            _photoSession?.Close();
            _posePlayer?.Release();
            if (_posePicker != null && _posePicker.IsAlive) _posePicker.SetOpen(false);
            // Also when the window is already gone with the game's UI, which wouldn't report closing
            _uiHider?.Release();
            _inputLock?.Release();
            _pause?.Release();
        }

        private void Update()
        {
            if (_posePlayer == null) return;

            bool inFlight = Enabled && Game?.GlobalGameState != null && Game.GlobalGameState.GetState() == GameState.FlightView;
            if (inFlight && MunShotSettings.TogglePosePicker.Down)
            {
                PosePickerWindow picker = GetOrCreatePosePicker();
                picker.SetOpen(!picker.IsOpen);
            }

            _pause.Tick();
            _uiHider.Tick();
            _posePlayer.Tick();
            _photoSession.Tick();
            if (_posePicker != null && _posePicker.IsAlive) _posePicker.Tick();
        }

        private PosePickerWindow GetOrCreatePosePicker()
        {
            if (_posePicker == null || !_posePicker.IsAlive)
            {
                _posePicker = new PosePickerWindow(SWLogger, _posePlayer, _photoSession, _presets);
                // Photo mode is open while the window is: the game stays paused and its UI hidden for all of it
                _posePicker.OpenChanged += open =>
                {
                    if (open)
                    {
                        _pause.Engage();
                        _inputLock.Engage();
                        _uiHider.Engage();
                    }
                    else
                    {
                        _uiHider.Release();
                        _inputLock.Release();
                        _pause.Release();
                    }
                };
            }
            return _posePicker;
        }

        private void OnGameStateChanged(MessageCenterMessage message)
        {
            // Photo mode is for flight; leaving it (map view, KSC, VAB) puts everything back
            if (message is GameStateChangedMessage { CurrentState: not GameState.FlightView })
                EndPhotoMode();
        }
    }
}
