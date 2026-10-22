using System;
using KSP.UI.Binding;
using SpaceWarp2.UI.API.Appbar;
using UnityEngine;

namespace MunShot.UI
{
    /// <summary>
    /// Photo mode's button in Redux's app tray in flight, which opens and closes photo mode. Registered through Redux's
    /// <see cref="Appbar"/>, which puts mods' buttons in the app tray and hides them in campaigns where MunShot is
    /// switched off. Kept pressed while photo mode is open, as Redux's own windows keep theirs in step.
    /// </summary>
    internal static class AppBarButton
    {
        private const string Id = "BTN-MunShot-PhotoMode";

        /// <summary>Adds the button, which appears the next time the flight toolbar is built.</summary>
        public static void Register(string modId, Sprite icon, Action<bool> onToggled)
        {
            // The text is a localization key, as Redux's own buttons use, so the tooltip is translated
            Appbar.RegisterAppButton(modId, Localization.Title, Id, icon, onToggled);
        }

        public static void SetPressed(bool pressed) =>
            GameObject.Find(Id)?.GetComponent<UIValue_WriteBool_Toggle>()?.SetValue(pressed);
    }
}
