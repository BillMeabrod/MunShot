using UnityEngine.UIElements;

namespace MunShot.UI
{
    /// <summary>
    /// The converters MunShot's UXML binds through, so its views show and hide elements from view model properties the
    /// way Redux's do (Redux's tracking station panel registers BoolToDisplayStyleConverter for its own). MunShot
    /// registers its own rather than relying on Redux's, which are only registered once the map has been opened.
    /// </summary>
    internal static class MunShotConverters
    {
        /// <summary>True shows the element, false leaves it out.</summary>
        public const string BoolToDisplay = "MunShotBoolToDisplay";

        /// <summary>True leaves the element out, false shows it.</summary>
        public const string InvertedBoolToDisplay = "MunShotInvertedBoolToDisplay";

        private static bool _registered;

        public static void Register()
        {
            if (_registered) return;
            _registered = true;

            var display = new ConverterGroup(BoolToDisplay);
            display.AddConverter((ref bool value) => new StyleEnum<DisplayStyle>(value ? DisplayStyle.Flex : DisplayStyle.None));
            ConverterGroups.RegisterConverterGroup(display);

            var inverted = new ConverterGroup(InvertedBoolToDisplay);
            inverted.AddConverter((ref bool value) => new StyleEnum<DisplayStyle>(value ? DisplayStyle.None : DisplayStyle.Flex));
            ConverterGroups.RegisterConverterGroup(inverted);
        }
    }
}
