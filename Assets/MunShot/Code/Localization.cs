using System;
using System.Globalization;
using I2.Loc;

namespace MunShot
{
    /// <summary>
    /// Text shown to the player. MunShot's own strings, English included, live only in
    /// Copied/localizations/munshot_localization.csv, which SpaceWarp loads into the game's localization at startup.
    /// UXML uses the keys directly ("#MunShot/UI/…"). A missing key shows as the key itself, as Redux's own windows do.
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

        // The hint bar: keys, and what they do
        public const string KeyRightMouse = "MunShot/UI/KeyRightMouse";
        public const string KeyWheel = "MunShot/UI/KeyWheel";
        public const string HintFly = "MunShot/UI/HintFly";
        public const string HintLook = "MunShot/UI/HintLook";
        public const string HintUpDown = "MunShot/UI/HintUpDown";
        public const string HintCircle = "MunShot/UI/HintCircle";
        public const string HintCloser = "MunShot/UI/HintCloser";
        public const string HintRoll = "MunShot/UI/HintRoll";
        public const string HintLevel = "MunShot/UI/HintLevel";
        public const string KeyDrag = "MunShot/UI/KeyDrag";
        public const string KeyShift = "MunShot/UI/KeyShift";
        public const string KeyCtrl = "MunShot/UI/KeyCtrl";
        public const string KeyAlt = "MunShot/UI/KeyAlt";
        public const string KeySpace = "MunShot/UI/KeySpace";
        public const string KeyEnter = "MunShot/UI/KeyEnter";
        public const string KeyEsc = "MunShot/UI/KeyEsc";
        public const string KeyNumpad = "MunShot/UI/KeyNumpad";
        public const string HintTurn = "MunShot/UI/HintTurn";
        public const string HintLean = "MunShot/UI/HintLean";
        public const string HintTip = "MunShot/UI/HintTip";
        public const string HintSelectSeveral = "MunShot/UI/HintSelectSeveral";
        public const string HintRemove = "MunShot/UI/HintRemove";
        public const string HintHideUi = "MunShot/UI/HintHideUi";
        public const string HintTakePhoto = "MunShot/UI/HintTakePhoto";
        public const string HintExit = "MunShot/UI/HintExit";

        /// <summary>
        /// Compares names shown in lists (poses, kerbals) alphabetically, ignoring case, by the rules of the game's
        /// language rather than Windows', so lists sort the way a player of that language expects.
        /// </summary>
        public static StringComparer NameOrder =>
            StringComparer.Create(LocalizationManager.CurrentCulture ?? CultureInfo.CurrentCulture, ignoreCase: true);

        /// <summary>Translates one of MunShot's keys with {0}-style placeholders and fills them in.</summary>
        public static string Format(string key, params object[] args) =>
            string.Format(CultureInfo.CurrentCulture, Translate(key), args);

        /// <summary>Translates one of MunShot's keys, showing the key itself if it's missing.</summary>
        public static string Translate(string key) => TranslateOr(key, key);

        /// <summary>
        /// Translates a key that may not exist, such as a pose added after this release, showing
        /// <paramref name="fallback"/> (its own name) if there's no translation.
        /// </summary>
        public static string TranslateOr(string key, string fallback)
        {
            if (string.IsNullOrEmpty(key)) return fallback;
            string text = LocalizationManager.GetTranslation(key);
            return string.IsNullOrEmpty(text) ? fallback : text;
        }
    }
}
