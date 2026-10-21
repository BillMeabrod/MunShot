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
        private PosePlayer _posePlayer;
        private PosePickerWindow _posePicker;

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
                _posePicker = new PosePickerWindow(SWLogger, _posePlayer);
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
