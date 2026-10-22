using SpaceWarp2.API.Configuration;
using SpaceWarp2.API.Input;
using UnityEngine;

namespace MunShot
{
    /// <summary>
    /// MunShot's settings, on its own page in the game's settings menu. Declared the way Redux declares its own:
    /// entries in a section, each wrapped in a <see cref="ConfigValue{T}"/>. Bound in OnPreInitialized, since the
    /// game only gives a page to mods whose settings exist when the menu is set up.
    ///
    /// All settings are global (the mod's own config file), not per campaign.
    /// </summary>
    public static class MunShotSettings
    {
        private const string PhotoModeSection = "Photo Mode";
        private const string DeveloperSection = "Developer";
        private const string CameraSection = "Photo Camera";

        private static readonly KeyboardShortcut DefaultTogglePhotoMode = new(KeyCode.P, KeyCode.LeftAlt);
        private static readonly KeyboardShortcut DefaultTogglePoseLab = new(KeyCode.L, KeyCode.LeftAlt);
        private const float DefaultFlySpeed = 5f;

        private static ConfigValue<KeyboardShortcut> _togglePhotoMode;
        private static ConfigValue<bool> _poseLabEnabled;
        private static ConfigValue<KeyboardShortcut> _togglePoseLab;
        private static ConfigValue<float> _flySpeed;
        private static ConfigValue<KeyBind> _hideUi;
        private static ConfigValue<KeyBind> _forward, _back, _left, _right, _rollLeft, _rollRight, _level, _fast, _slow;

        /// <summary>Opens and closes photo mode in flight. Left Alt+P by default.</summary>
        public static KeyboardShortcut TogglePhotoMode => _togglePhotoMode?.Value ?? DefaultTogglePhotoMode;

        /// <summary>
        /// Whether the developer pose lab can be opened. Off by default and not in the settings menu: switched on in
        /// MunShot's config file by whoever is making poses.
        /// </summary>
        public static bool PoseLabEnabled => _poseLabEnabled?.Value ?? false;

        /// <summary>Opens and closes the developer pose lab in flight, when it's enabled. Left Alt+L by default.</summary>
        public static KeyboardShortcut TogglePoseLab => _togglePoseLab?.Value ?? DefaultTogglePoseLab;

        /// <summary>Hides and shows photo mode's own UI, to frame the shot. H by default.</summary>
        public static KeyBind HideUi => _hideUi?.Value ?? new KeyBind(KeyCode.H);

        /// <summary>How fast the free camera flies, in metres per second, before Fast or Slow.</summary>
        public static float FlySpeed => _flySpeed?.Value ?? DefaultFlySpeed;

        public static KeyBind Forward => _forward?.Value ?? new KeyBind(KeyCode.W);
        public static KeyBind Back => _back?.Value ?? new KeyBind(KeyCode.S);
        public static KeyBind Left => _left?.Value ?? new KeyBind(KeyCode.A);
        public static KeyBind Right => _right?.Value ?? new KeyBind(KeyCode.D);
        public static KeyBind RollLeft => _rollLeft?.Value ?? new KeyBind(KeyCode.Q);
        public static KeyBind RollRight => _rollRight?.Value ?? new KeyBind(KeyCode.E);
        public static KeyBind LevelHorizon => _level?.Value ?? new KeyBind(KeyCode.R);
        public static KeyBind Fast => _fast?.Value ?? new KeyBind(KeyCode.LeftShift);
        public static KeyBind Slow => _slow?.Value ?? new KeyBind(KeyCode.LeftControl);

        public static void Bind(IConfigFile config)
        {
            if (config == null || _togglePhotoMode != null) return;

            IConfigSection photoMode = config.GetOrCreateSection(PhotoModeSection);
            _togglePhotoMode = new ConfigValue<KeyboardShortcut>(photoMode.BindEntry("Toggle Photo Mode",
                DefaultTogglePhotoMode,
                "Opens and closes photo mode in flight. It's also in the app tray and the pause menu."));
            _hideUi = Key(photoMode, "Hide Photo Mode UI", KeyCode.H, "Hides photo mode's panels and handles to frame the shot; press again to bring them back.");

            // Developer tools: kept out of the settings menu (Redux's Hidden tag, as Redux hides its own internal
            // settings), so players never see them; a section with only hidden entries isn't shown at all
            IConfigSection developer = config.GetOrCreateSection(DeveloperSection);
            string[] hidden = { ConfigTags.Hidden };
            _poseLabEnabled = new ConfigValue<bool>(developer.BindEntry("Enable Pose Lab", false,
                "Lets the developer pose lab be opened in flight, for picking poses from the game's animations and "
                + "inspecting the kerbal rig. Not for players.", null, null, null, hidden));
            _togglePoseLab = new ConfigValue<KeyboardShortcut>(developer.BindEntry("Toggle Pose Lab",
                DefaultTogglePoseLab,
                "Opens and closes the developer pose lab in flight, when Enable Pose Lab is on.", null, null, null, hidden));

            IConfigSection camera = config.GetOrCreateSection(CameraSection);
            _flySpeed = new ConfigValue<float>(camera.BindEntry("Fly Speed", DefaultFlySpeed,
                "How fast the free camera flies, in metres per second. Hold Fast for five times this, doubling every second it stays held (up to 2000 m/s); Slow for a fifth.",
                new RangeConstraint<float>(0.5f, 100f)));
            _forward = Key(camera, "Fly Forward", KeyCode.W, "Free camera: fly forward.");
            _back = Key(camera, "Fly Back", KeyCode.S, "Free camera: fly backward.");
            _left = Key(camera, "Fly Left", KeyCode.A, "Free camera: fly left.");
            _right = Key(camera, "Fly Right", KeyCode.D, "Free camera: fly right.");
            _rollLeft = Key(camera, "Roll Left", KeyCode.Q, "Tilts the photo camera to the left.");
            _rollRight = Key(camera, "Roll Right", KeyCode.E, "Tilts the photo camera to the right.");
            _level = Key(camera, "Level Horizon", KeyCode.R, "Takes the tilt off the photo camera.");
            _fast = Key(camera, "Fast", KeyCode.LeftShift, "Hold to move the photo camera five times faster; while flying, it keeps speeding up the longer it is held.");
            _slow = Key(camera, "Slow", KeyCode.LeftControl, "Hold to move the photo camera five times slower.");
        }

        private static ConfigValue<KeyBind> Key(IConfigSection section, string name, KeyCode key, string description) =>
            new ConfigValue<KeyBind>(section.BindEntry(name, new KeyBind(key), description));
    }
}
