using System.Collections.Generic;
using KSP;
using KSP.Audio;
using KSP.Game;
using KSP.Utilities;
using UnityEngine;

namespace MunShot.Posing
{
    /// <summary>
    /// What's outlined (photo doubles and lights, <see cref="IPlaceable"/>), and the hover sound: the same as parts in
    /// the VAB. What's selected for moving stays outlined, and the hovered one is outlined too, with the game's own outline manager
    /// (<c>Game.UI.OutlineManager</c>, the one that outlines parts, in its first colour, green). The VAB's
    /// part-highlight sound plays once each time the mouse moves onto one. The outline manager can only clear
    /// every outline at once, so the outlines are redrawn when the lit set changes, and also whenever the game has
    /// dropped them: it clears every outline itself at times (starting mouse look, its own highlights), and refuses to
    /// draw any while the right mouse button is held (<c>CameraMouseLook.GetMouseLook</c>). The game's own hover on
    /// vessel parts, which would clear them constantly, is off in photo mode (<see cref="Capture.PartHoverOutlines"/>).
    /// </summary>
    public sealed class HoverFeedback
    {
        private const int OutlineColorIndex = 0;

        private readonly HashSet<IPlaceable> _lit = new HashSet<IPlaceable>();
        private readonly HashSet<IPlaceable> _wanted = new HashSet<IPlaceable>();
        private IPlaceable _hovered;

        /// <summary>
        /// Outlines <paramref name="selected"/> and <paramref name="hovered"/> (either may be empty or null), unless
        /// <paramref name="outlined"/> is false: then none are, but the sound still plays. Moving onto a new one plays
        /// the sound.
        /// </summary>
        public void Show(IReadOnlyCollection<IPlaceable> selected, IPlaceable hovered, Camera camera, bool outlined = true)
        {
            if (hovered != _hovered && hovered?.Root != null)
            {
                Vector3 viewport = camera != null ? camera.WorldToViewportPoint(hovered.Root.position) : Vector3.zero;
                // No VAB part to ask for its own sound, so the VAB's general part-highlight sound plays
                KSPAudioEventManager.onVABPartHighlighted(null, viewport);
            }
            _hovered = hovered;

            _wanted.Clear();
            if (outlined)
            {
                if (selected != null)
                    foreach (IPlaceable placeable in selected)
                        if (placeable?.Root != null) _wanted.Add(placeable);
                if (hovered?.Root != null) _wanted.Add(hovered);
            }
            if (_wanted.SetEquals(_lit) && StillOutlined()) return;
            // The game draws no outlines while the right mouse button is held; try again once it's let go
            if (_wanted.Count > 0 && CameraMouseLook.GetMouseLook()) return;

            ObjectOutlinesManager outlines = GameManager.Instance?.Game?.UI?.OutlineManager;
            if (_lit.Count > 0) outlines?.ClearOutlines();
            _lit.Clear();
            foreach (IPlaceable placeable in _wanted)
            {
                outlines?.AddOutlineToObject(placeable.Root.gameObject, OutlineColorIndex);
                _lit.Add(placeable);
            }
        }

        public void Clear() => Show(null, null, null);

        // Whether everything meant to be lit still has the game's outline on it
        private bool StillOutlined()
        {
            foreach (IPlaceable placeable in _lit)
                if (placeable.Root != null && placeable.Root.GetComponentInChildren<Outline>(true) == null)
                    return false;
            return true;
        }
    }
}
