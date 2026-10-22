using HarmonyLib;
using KSP.Game;

namespace MunShot.Capture
{
    /// <summary>
    /// Switches off the game's flight hover on vessel parts while photo mode is open. In flight the game finds the part
    /// under the mouse every frame (<c>UIManager.LateUpdateMouseOverLogic</c>) and, each time it changes, clears every
    /// outline and outlines that part: that would fight photo mode's own outlines on the kerbals (flickering as the
    /// mouse crosses a ship) and could leave a part outlined in the photo. Nothing else needs the part under the mouse
    /// in photo mode: the clicks that use it (focus, parts manager) are locked.
    ///
    /// The game has no switch for it, so it's a Harmony patch, applied through Redux's
    /// <c>KerbalMod.CreateHarmonyAndPatchAll</c>.
    /// </summary>
    [HarmonyPatch(typeof(UIManager), "LateUpdateMouseOverLogic")]
    internal static class PartHoverOutlines
    {
        /// <summary>Whether the game's part hover is off (photo mode is open).</summary>
        public static bool Suppressed { get; set; }

        // Skips the game's part hover while suppressed
        private static bool Prefix() => !Suppressed;
    }
}
