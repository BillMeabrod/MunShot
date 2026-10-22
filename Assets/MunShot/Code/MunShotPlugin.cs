using System;
using System.Collections.Generic;
using System.IO;
using KSP.Animation;
using KSP.Game;
using KSP.Messages;
using MunShot.Cameras;
using MunShot.Capture;
using MunShot.Posing;
using MunShot.Tools;
using MunShot.UI;
using Redux.ExtraModTypes;
using SpaceWarp2.API.Loading;
using UnityEngine;
using UnityEngine.UIElements;

namespace MunShot
{
    public class MunShotPlugin : KerbalMod
    {
        // The game's own poses shipped with MunShot, in the mod folder's Poses folder
        private const string StockPresetsFile = "stock.json";
        private const string PresetsFolder = "Poses";

        // Addressable keys, set on the assets in the MunShot Addressables group
        private const string WindowAddress = "MunShot/PhotoModeWindow.uxml";
        // App tray icon: 48px like the stock toolbar icons (drawn by Tools/Icons/make_toolbar_icon.py)
        private const string ToolbarIconAddress = "MunShot/toolbar.png";

        private const string LoadAssetsStep = "MunShot: Loading photo mode";
        // A stock campaign-load step to load after, as K.E.R.N.E.L does: early enough that the app tray button is
        // registered before the flight app tray is built
        private const string LoadAssetsAfter = "Loading Celestial Body Data";

        private PhotoMode _photoMode;
        private PosePlayer _evaPlayer;
        private PhotoModeWindow _window;
        private PoseLabWindow _poseLab;
        private PauseMenuButton _pauseMenuButton;
        private VisualTreeAsset _windowUxml;
        private bool _assetsRequested;

        public override void OnPreInitialized()
        {
            // The game only gives a mod its settings page if its settings exist when the menu is set up
            MunShotSettings.Bind(SWConfiguration);

            // Redux only lets a mod load its own assets in a campaign that has it switched on. Loading them in a step of
            // the campaign load, rather than OnCampaignLoaded, registers the app tray button before the flight app
            // tray is built
            SaveLoad.AddStepAfter(SWMetadata.Guid, LoadAssetsStep, StockFlow.CampaignLoad, LoadAssetsAfter,
                (resolve, reject) => LoadAssets(resolve));
        }

        public override void OnInitialized()
        {
            // The window binds show and hide through these, so they're registered before it loads
            MunShotConverters.Register();

            var session = new PhotoSession(new PhotoDoubleFactory(message => SWLogger.LogError(message)), NewPosePlayer,
                message => SWLogger.LogInfo(message), message => SWLogger.LogError(message));
            var camera = new PhotoCamera(new KeyboardMouseCameraInput(),
                new GamepadCameraInput(message => SWLogger.LogInfo(message), message => SWLogger.LogWarning(message)),
                message => SWLogger.LogInfo(message), message => SWLogger.LogError(message));
            var capture = new PhotoCapture(message => SWLogger.LogInfo(message), message => SWLogger.LogError(message));
            _photoMode = new PhotoMode(SWLogger, session, camera, capture, LoadPresets());
            _photoMode.OpenChanged += AppBarButton.SetPressed;
            _evaPlayer = NewPosePlayer();
            _pauseMenuButton = new PauseMenuButton(OpenPhotoMode, SWLogger);

            Messages?.PersistentSubscribe<GameStateChangedMessage>(OnGameStateChanged);
            Messages?.PersistentSubscribe<EscapeMenuOpenedMessage>(_ => _pauseMenuButton.OnPauseMenuOpened());
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

        private void LoadAssets(Action done)
        {
            if (_assetsRequested)
            {
                done();
                return;
            }

            _assetsRequested = true;
            int pending = 2;
            void Loaded()
            {
                if (--pending == 0) done();
            }

            Assets.Load<VisualTreeAsset>(WindowAddress, uxml =>
            {
                _windowUxml = uxml;
                if (uxml == null) SWLogger.LogError($"Could not load the photo mode window '{WindowAddress}'; photo mode will open without it.");
                Loaded();
            });
            Assets.Load<Texture2D>(ToolbarIconAddress, texture =>
            {
                if (texture == null)
                {
                    SWLogger.LogError($"Could not load the app tray icon '{ToolbarIconAddress}'; there's no photo mode button in the app tray. "
                                      + "Use the pause menu or the keyboard shortcut.");
                }
                else
                {
                    Sprite icon = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), new Vector2(0.5f, 0.5f));
                    AppBarButton.Register(SWMetadata.Guid, icon, open =>
                    {
                        if (open) OpenPhotoMode();
                        else _photoMode.Close();
                    });
                }
                Loaded();
            });
        }

        // Lets go of the campaign before the game tears it down: photo mode closes (doubles go, originals come back)
        // and no kerbal stays posed
        public override void OnCampaignUnloading() => EndPhotoMode();

        private void EndPhotoMode()
        {
            _photoMode?.Close();
            if (_poseLab != null && _poseLab.IsAlive) _poseLab.SetOpen(false);
            _evaPlayer?.Release();
        }

        private void OpenPhotoMode()
        {
            EnsureWindow();
            // Opening can be refused (not in flight); the app tray button then pops back up
            if (!_photoMode.Open()) AppBarButton.SetPressed(false);
        }

        // The window is made when photo mode first opens in a campaign, and again if the game has torn down the UI it
        // lived in
        private void EnsureWindow()
        {
            if (_window != null && _window.IsAlive) return;
            _window?.Dispose();
            _window = null;
            if (_windowUxml == null)
            {
                SWLogger.LogError($"Photo mode: no window to show; '{WindowAddress}' isn't loaded. Close photo mode with the keyboard shortcut.");
                return;
            }
            _window = new PhotoModeWindow(_photoMode, _windowUxml, SWLogger);
        }

        private void Update()
        {
            if (_photoMode == null) return;

            bool inFlight = Enabled && Game?.GlobalGameState != null && Game.GlobalGameState.GetState() == GameState.FlightView;
            if (inFlight && MunShotSettings.TogglePhotoMode.Down)
            {
                if (_photoMode.IsOpen) _photoMode.Close();
                else OpenPhotoMode();
            }
            if (_photoMode.IsOpen && MunShotSettings.HideUi.Down && !(_window != null && _window.IsAlive && _window.IsTyping))
                _photoMode.ToggleUiHidden();
            if (inFlight && MunShotSettings.TogglePoseLab.Down)
            {
                PoseLabWindow lab = GetOrCreatePoseLab();
                lab.SetOpen(!lab.IsOpen);
            }

            // Clicks and typing in photo mode's windows aren't for the scene or the camera
            Vector2 mouse = global::Mouse.Position;
            bool labAlive = _poseLab != null && _poseLab.IsAlive;
            bool windowAlive = _window != null && _window.IsAlive;
            bool pointerOverUi = (windowAlive && _window.IsPointerOver(mouse)) || (labAlive && _poseLab.IsPointerOver(mouse));
            bool typing = (windowAlive && _window.IsTyping) || (labAlive && _poseLab.IsTyping);
            _photoMode.Tick(pointerOverUi, typing);

            _evaPlayer.Tick();
            if (labAlive) _poseLab.Tick();
        }

        private PoseLabWindow GetOrCreatePoseLab()
        {
            if (_poseLab == null || !_poseLab.IsAlive)
            {
                _poseLab?.Dispose();
                _poseLab = new PoseLabWindow(SWLogger, _evaPlayer, _photoMode);
            }
            return _poseLab;
        }

        private void OnGameStateChanged(MessageCenterMessage message)
        {
            // Photo mode is for flight; leaving it (map view, KSC, VAB) puts everything back
            if (message is GameStateChangedMessage { CurrentState: not GameState.FlightView })
                EndPhotoMode();
        }
    }
}
