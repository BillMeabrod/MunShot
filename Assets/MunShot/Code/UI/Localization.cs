using I2.Loc;

namespace MunShot.UI
{
    /// <summary>
    /// Text shown to the player. MunShot's own strings live in Copied/localizations/munshot_localization.csv, which
    /// SpaceWarp loads into the game's localization at startup. UXML uses the keys directly ("#MunShot/UI/…").
    /// </summary>
    public static class Localization
    {
        public const string Title = "MunShot/UI/Title";

        /// <summary>The pause menu button's key, without the "Menu/Escape/" the pause menu puts in front of it.</summary>
        public const string PauseMenuButton = "MunShotPhotoMode";

        public const string InThisScene = "MunShot/UI/InThisScene";
        public const string Elsewhere = "MunShot/UI/Elsewhere";
        public const string NoKerbals = "MunShot/UI/NoKerbals";
        public const string EveryoneIsIn = "MunShot/UI/EveryoneIsIn";
        public const string NobodyIn = "MunShot/UI/NobodyIn";
        public const string NoPose = "MunShot/UI/NoPose";
        public const string JetpackPose = "MunShot/UI/JetpackPose";

        // The hint bar: keys, and what they do
        public const string KeyRightMouse = "MunShot/UI/KeyRightMouse";
        public const string KeyWheel = "MunShot/UI/KeyWheel";
        public const string HintFly = "MunShot/UI/HintFly";
        public const string HintLook = "MunShot/UI/HintLook";
        public const string HintUpDown = "MunShot/UI/HintUpDown";
        public const string HintCircle = "MunShot/UI/HintCircle";
        public const string HintCloser = "MunShot/UI/HintCloser";
        public const string HintRoll = "MunShot/UI/HintRoll";
        public const string HintHideUi = "MunShot/UI/HintHideUi";
        public const string HintTakePhoto = "MunShot/UI/HintTakePhoto";
        public const string HintExit = "MunShot/UI/HintExit";

        /// <summary>Translates one of MunShot's keys, showing the key itself if it's missing (as the game does).</summary>
        public static string Translate(string key) => Translate(key, key);

        public static string Translate(string key, string fallback)
        {
            if (string.IsNullOrEmpty(key)) return fallback;
            string text = LocalizationManager.GetTranslation(key);
            return string.IsNullOrEmpty(text) ? fallback : text;
        }
    }
}
