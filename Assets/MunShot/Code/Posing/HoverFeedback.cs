using KSP.Audio;
using KSP.Game;
using KSP.Utilities;
using UnityEngine;

namespace MunShot.Posing
{
    /// <summary>
    /// What hovering a photo double looks and sounds like: the same as hovering a part in the VAB. It's outlined with
    /// the game's own outline manager (<c>Game.UI.OutlineManager</c>, the one that outlines parts, in its first colour,
    /// green) and plays the VAB's part-highlight sound once each time the mouse moves onto a double.
    /// </summary>
    public sealed class HoverFeedback
    {
        private const int OutlineColorIndex = 0;

        private PhotoSession.Entry _hovered;

        public PhotoSession.Entry Hovered => _hovered;

        /// <summary>Shows <paramref name="entry"/> as hovered (or nothing, when null). Only changes play the sound.</summary>
        public void SetHovered(PhotoSession.Entry entry, Camera camera)
        {
            if (entry == _hovered) return;
            ObjectOutlinesManager outlines = GameManager.Instance?.Game?.UI?.OutlineManager;
            if (_hovered != null) outlines?.ClearOutlines();
            _hovered = entry;
            if (entry?.Root == null) return;

            outlines?.AddOutlineToObject(entry.Root.gameObject, OutlineColorIndex);
            Vector3 viewport = camera != null ? camera.WorldToViewportPoint(entry.Root.position) : Vector3.zero;
            // No VAB part to ask for its own sound, so the VAB's general part-highlight sound plays
            KSPAudioEventManager.onVABPartHighlighted(null, viewport);
        }

        public void Clear() => SetHovered(null, null);
    }
}
