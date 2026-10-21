using System;
using System.Collections.Generic;
using System.IO;
using KSP.Animation;
using KSP.Game;
using KSP.Messages;
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
        private PosePickerWindow _posePicker;
        private IReadOnlyList<PosePreset> _presets = Array.Empty<PosePreset>();

        public override void OnPreInitialized()
        {
            // The game only gives a mod its settings page if its settings exist when the menu is set up
            MunShotSettings.Bind(SWConfiguration);
        }

        public override void OnInitialized()
        {
            // After a freeze, the kerbal's Animator gets the speed the game gives it for the current time warp
            _posePlayer = new PosePlayer(message => SWLogger.LogError(message),
                _ => AnimationManager.GetAnimSpeedForTimeWarpRate(Game?.ViewController?.TimeWarp?.CurrentRate ?? 1f));
            Messages?.PersistentSubscribe<GameStateChangedMessage>(OnGameStateChanged);
            _presets = LoadPresets();
        }

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

        // Lets go of the campaign before the game tears it down: no kerbal stays posed, and the picker closes
        public override void OnCampaignUnloading()
        {
            _posePlayer?.Release();
            _posePicker?.SetOpen(false);
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

            _posePlayer.Tick();
            if (_posePicker != null && _posePicker.IsAlive) _posePicker.Tick();
        }

        private PosePickerWindow GetOrCreatePosePicker()
        {
            if (_posePicker == null || !_posePicker.IsAlive)
                _posePicker = new PosePickerWindow(SWLogger, _posePlayer, _presets);
            return _posePicker;
        }

        private void OnGameStateChanged(MessageCenterMessage message)
        {
            // Posing is for flight; leaving it (map view, KSC, VAB) gives the kerbal back and closes the picker
            if (message is GameStateChangedMessage { CurrentState: not GameState.FlightView })
            {
                _posePlayer?.Release();
                if (_posePicker != null && _posePicker.IsAlive) _posePicker.SetOpen(false);
            }
        }
    }
}
