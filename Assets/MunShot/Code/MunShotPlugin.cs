using System;
using System.Collections.Generic;
using System.IO;
using KSP.Animation;
using KSP.Game;
using KSP.Input;
using KSP.Messages;
using MunShot.Cameras;
using MunShot.Capture;
using MunShot.Posing;
using MunShot.Tools;
using Redux.ExtraModTypes;
using UnityEngine.InputSystem;

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
        private PhotoModeInputLock _inputLock;
        private PhotoCamera _camera;
        private PhotoCapture _capture;
        private InputAction _screenshotKey;
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
            _inputLock = new PhotoModeInputLock(message => SWLogger.LogInfo(message), message => SWLogger.LogWarning(message));
            _camera = new PhotoCamera(new KeyboardMouseCameraInput(),
                new GamepadCameraInput(message => SWLogger.LogInfo(message), message => SWLogger.LogWarning(message)),
                message => SWLogger.LogInfo(message), message => SWLogger.LogError(message));
            _capture = new PhotoCapture(message => SWLogger.LogInfo(message), message => SWLogger.LogError(message));
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
            ListenToScreenshotKey(false);
            _capture?.Dispose();
            _camera?.Release();
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
            _capture.Tick();
            _posePlayer.Tick();
            _photoSession.Tick();
            if (_posePicker != null && _posePicker.IsAlive) _posePicker.Tick();
        }

        private PosePickerWindow GetOrCreatePosePicker()
        {
            if (_posePicker == null || !_posePicker.IsAlive)
            {
                _posePicker = new PosePickerWindow(SWLogger, _posePlayer, _photoSession, _camera, _capture, _presets);
                // Photo mode is open while the window is: the game stays paused, its UI hidden and its camera ours for
                // all of it
                _posePicker.OpenChanged += open =>
                {
                    if (open)
                    {
                        _pause.Engage();
                        _inputLock.Engage();
                        _uiHider.Engage();
                        _camera.Engage();
                        ListenToScreenshotKey(true);
                    }
                    else
                    {
                        ListenToScreenshotKey(false);
                        _capture.Dispose();
                        _camera.Release();
                        _uiHider.Release();
                        _inputLock.Release();
                        _pause.Release();
                    }
                };
            }
            return _posePicker;
        }

        // In photo mode the game's screenshot key takes the photo without photo mode's UI. Redux takes the screenshot
        // for the key itself; PhotoCapture joins that capture and hides the UI for it
        private void ListenToScreenshotKey(bool listen)
        {
            if (_screenshotKey != null)
            {
                _screenshotKey.performed -= OnScreenshotKey;
                _screenshotKey = null;
            }
            if (!listen) return;
            GameInput input = Game?.Input;
            _screenshotKey = input != null ? input.Global.TakeScreenshot : null;
            if (_screenshotKey != null) _screenshotKey.performed += OnScreenshotKey;
            else SWLogger.LogWarning("Photo mode: the game's screenshot key isn't available; use Take photo in the picker.");
        }

        private void OnScreenshotKey(InputAction.CallbackContext context) => _capture.Take();

        private void OnGameStateChanged(MessageCenterMessage message)
        {
            // Photo mode is for flight; leaving it (map view, KSC, VAB) puts everything back
            if (message is GameStateChangedMessage { CurrentState: not GameState.FlightView })
                EndPhotoMode();
        }
    }
}
