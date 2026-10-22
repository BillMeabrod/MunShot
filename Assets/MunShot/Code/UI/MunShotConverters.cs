using UitkForKsp2.MVVM.Converters;
using UnityEngine.UIElements;

namespace MunShot.UI
{
    /// <summary>
    /// The converters MunShot's UXML binds through, so its views show and hide elements from view model properties.
    /// Registered through UitkForKsp2's <see cref="ConverterGroupRegistry"/>, as UitkForKsp2 registers its own
    /// (Localization, UppercaseLocalization), which also keeps them from being registered twice. MunShot has its own
    /// rather than relying on Redux's BoolToDisplayStyle, which is only registered once the map has been opened.
    /// </summary>
    internal static class MunShotConverters
    {
        /// <summary>True shows the element, false leaves it out.</summary>
        public const string BoolToDisplay = "MunShotBoolToDisplay";

        /// <summary>True leaves the element out, false shows it.</summary>
        public const string InvertedBoolToDisplay = "MunShotInvertedBoolToDisplay";

        public static void Register() =>
            ConverterGroupRegistry.Register(new DisplayGroup(BoolToDisplay, "Bool to display", "True shows the element.", false),
                                            new DisplayGroup(InvertedBoolToDisplay, "Inverted bool to display", "True hides the element.", true));

        private sealed class DisplayGroup : IConverterGroup
        {
            private readonly bool _inverted;

            public DisplayGroup(string id, string displayName, string description, bool inverted)
            {
                Id = id;
                DisplayName = displayName;
                Description = description;
                _inverted = inverted;
            }

            public string Id { get; }
            public string DisplayName { get; }
            public string Description { get; }
            public bool RefreshOnLocalizationChange => false;

            public void RegisterConverters(ConverterGroupBuilder builder) => builder.Add(new DisplayConverter(_inverted));
        }

        private sealed class DisplayConverter : IConverter<bool, StyleEnum<DisplayStyle>>
        {
            private readonly bool _inverted;

            public DisplayConverter(bool inverted) => _inverted = inverted;

            public StyleEnum<DisplayStyle> Convert(bool shown) =>
                new StyleEnum<DisplayStyle>(shown != _inverted ? DisplayStyle.Flex : DisplayStyle.None);
        }
    }
}
