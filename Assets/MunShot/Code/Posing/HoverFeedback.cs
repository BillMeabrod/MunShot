using System.Collections.Generic;
using KSP;
using KSP.Audio;
using KSP.Game;
using KSP.Utilities;
using UnityEngine;

namespace MunShot.Posing
{
    /// <summary>
    /// Which photo doubles are outlined, and the hover sound: the same as parts in the VAB. Doubles selected for moving
    /// stay outlined, and the hovered one is outlined too, with the game's own outline manager
    /// (<c>Game.UI.OutlineManager</c>, the one that outlines parts, in its first colour, green). The VAB's
    /// part-highlight sound plays once each time the mouse moves onto a double. The outline manager can only clear
    /// every outline at once, so the outlines are redrawn when the lit set changes, and also whenever the game has
    /// dropped them: it clears every outline itself at times (starting mouse look, its own highlights), and refuses to
    /// draw any while the right mouse button is held (<c>CameraMouseLook.GetMouseLook</c>). The game's own hover on
    /// vessel parts, which would clear them constantly, is off in photo mode (<see cref="Capture.PartHoverOutlines"/>).
    /// </summary>
    public sealed class HoverFeedback
    {
        private const int OutlineColorIndex = 0;

        private readonly HashSet<PhotoSession.Entry> _lit = new HashSet<PhotoSession.Entry>();
        private readonly HashSet<PhotoSession.Entry> _wanted = new HashSet<PhotoSession.Entry>();
        private PhotoSession.Entry _hovered;

        /// <summary>
        /// Outlines <paramref name="selected"/> and <paramref name="hovered"/> (either may be empty or null). Moving
        /// onto a new double plays the sound.
        /// </summary>
        public void Show(IReadOnlyCollection<PhotoSession.Entry> selected, PhotoSession.Entry hovered, Camera camera)
        {
            if (hovered != _hovered && hovered?.Root != null)
            {
                Vector3 viewport = camera != null ? camera.WorldToViewportPoint(hovered.Root.position) : Vector3.zero;
                // No VAB part to ask for its own sound, so the VAB's general part-highlight sound plays
                KSPAudioEventManager.onVABPartHighlighted(null, viewport);
            }
            _hovered = hovered;

            _wanted.Clear();
            if (selected != null)
                foreach (PhotoSession.Entry entry in selected)
                    if (entry?.Root != null) _wanted.Add(entry);
            if (hovered?.Root != null) _wanted.Add(hovered);
            if (_wanted.SetEquals(_lit) && StillOutlined()) return;
            // The game draws no outlines while the right mouse button is held; try again once it's let go
            if (_wanted.Count > 0 && CameraMouseLook.GetMouseLook()) return;

            ObjectOutlinesManager outlines = GameManager.Instance?.Game?.UI?.OutlineManager;
            if (_lit.Count > 0) outlines?.ClearOutlines();
            _lit.Clear();
            foreach (PhotoSession.Entry entry in _wanted)
            {
                outlines?.AddOutlineToObject(entry.Root.gameObject, OutlineColorIndex);
                _lit.Add(entry);
            }
        }

        public void Clear() => Show(null, null, null);

        // Whether every double meant to be lit still has the game's outline on it
        private bool StillOutlined()
        {
            foreach (PhotoSession.Entry entry in _lit)
                if (entry.Root != null && entry.Root.GetComponentInChildren<Outline>(true) == null)
                    return false;
            return true;
        }
    }
}
