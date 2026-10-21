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
    /// The developer settings are global (the mod's own config file), not per campaign.
    /// </summary>
    public static class MunShotSettings
    {
        private const string DeveloperSection = "Developer";

        private static ConfigValue<KeyboardShortcut> _togglePosePicker;

        private static readonly KeyboardShortcut DefaultTogglePosePicker = new(KeyCode.P, KeyCode.LeftAlt);

        /// <summary>Opens and closes the pose picker in flight. Left Alt+P by default.</summary>
        public static KeyboardShortcut TogglePosePicker => _togglePosePicker?.Value ?? DefaultTogglePosePicker;

        public static void Bind(IConfigFile config)
        {
            if (config == null || _togglePosePicker != null) return;

            IConfigSection developer = config.GetOrCreateSection(DeveloperSection);
            _togglePosePicker = new ConfigValue<KeyboardShortcut>(developer.BindEntry("Toggle Pose Picker",
                DefaultTogglePosePicker,
                "Opens and closes the developer pose picker in flight, for picking poses from the game's animations."));
        }
    }
}
