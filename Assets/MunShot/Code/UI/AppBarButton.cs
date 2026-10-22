using System;
using SpaceWarp2.UI.API.Appbar;
using UnityEngine;
using ReduxAppBar = Redux.UI.AppBar.AppBar;

namespace MunShot.UI
{
    /// <summary>
    /// Photo mode's button in Redux's app tray in flight, which opens and closes photo mode. Registered through
    /// SpaceWarp2's <see cref="Appbar"/>, which hands mods' buttons to Redux's app tray and hides them in campaigns where
    /// MunShot is switched off. Kept pressed while photo mode is open, as Redux's own windows keep theirs in step.
    /// </summary>
    internal static class AppBarButton
    {
        private const string Id = "BTN-MunShot-PhotoMode";

        /// <summary>Adds the button, which appears the next time the flight toolbar is built.</summary>
        public static void Register(string modId, Texture2D icon, Action<bool> onToggled)
        {
            // The text is a localization key, as Redux's own buttons use, so the tooltip is translated
            Appbar.RegisterAppButton(modId, Localization.Title, Id, Appbar.GetAppBarIconFromTexture(icon), onToggled);
        }

        public static void SetPressed(bool pressed) => ReduxAppBar.SetAppBarButtonIndicator(Id, pressed);
    }
}
