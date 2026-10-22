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
        private const string DeveloperSection = "Developer";
        private const string CameraSection = "Photo Camera";

        private static readonly KeyboardShortcut DefaultTogglePosePicker = new(KeyCode.P, KeyCode.LeftAlt);
        private const float DefaultFlySpeed = 5f;

        private static ConfigValue<KeyboardShortcut> _togglePosePicker;
        private static ConfigValue<float> _flySpeed;
        private static ConfigValue<KeyBind> _forward, _back, _left, _right, _rollLeft, _rollRight, _level, _fast, _slow;

        /// <summary>Opens and closes the pose picker in flight. Left Alt+P by default.</summary>
        public static KeyboardShortcut TogglePosePicker => _togglePosePicker?.Value ?? DefaultTogglePosePicker;

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
            if (config == null || _togglePosePicker != null) return;

            IConfigSection developer = config.GetOrCreateSection(DeveloperSection);
            _togglePosePicker = new ConfigValue<KeyboardShortcut>(developer.BindEntry("Toggle Pose Picker",
                DefaultTogglePosePicker,
                "Opens and closes the developer pose picker in flight, for picking poses from the game's animations."));

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
