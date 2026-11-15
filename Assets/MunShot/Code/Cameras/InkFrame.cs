using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace MunShot.Cameras
{
    /// <summary>
    /// What <see cref="InkLines"/> takes from earlier in the frame (Ink's paper all of it, Cel shaded's outlines the
    /// ground's depth and colours; both where the terrain, distant planets, exhaust and smoke are), and
    /// <see cref="CelEffects"/> too, kept in the camera's frame data (URP resets it every frame). Filled by the
    /// <see cref="InkCapture"/> hooks.
    /// </summary>
    internal sealed class InkFrame : ContextItem
    {
        /// <summary>The depth once the deferred pass has drawn what it draws (the ground, buildings, trees, parts).</summary>
        public TextureHandle SceneDepth;

        /// <summary>
        /// The colour of each surface the deferred pass drew, before any light: a copy of URP's G-buffer's, which the
        /// pipeline needn't keep once the lighting's done.
        /// </summary>
        public TextureHandle Albedo;

        /// <summary>
        /// The depth once everything solid is drawn, before the see-through things: what was drawn after the deferred
        /// pass (a kerbal) shows where it differs from <see cref="SceneDepth"/>.
        /// </summary>
        public TextureHandle OpaqueDepth;

        /// <summary>
        /// How much of each pixel is a kerbal's own ink (0 to 1): its pupils, from its eyes' own texture, unlit, and its
        /// hair if dark.
        /// </summary>
        public TextureHandle KerbalInk;

        /// <summary>The surfaces' own colours just before the planet's terrain is drawn among them.</summary>
        public TextureHandle BeforeTerrain;

        /// <summary>
        /// Where the planet's own terrain was drawn (0 to 1), from how much drawing it changed the surfaces' colours, so
        /// its slopes are outlined and not a ship's or a building's.
        /// </summary>
        public TextureHandle Terrain;

        /// <summary>
        /// Where Redux's faraway view shows a distant planet or moon (1, 0 elsewhere), so Cel shaded puts them in flat
        /// tones and both filters outline them.
        /// </summary>
        public TextureHandle Bodies;

        /// <summary>The picture just before Redux draws the engines' exhaust and smoke over it (none while a filter is off).</summary>
        public TextureHandle BeforeEffects;

        /// <summary>
        /// Where the engines' exhaust and smoke and the game's particle effects are drawn as a cartoon cloud (red 1
        /// inside it; green 1 inside the particle smoke's part of it, the only part outlined): where drawing them
        /// changed the picture enough, evened out so its edge is smooth, so both filters can draw them so, the lines
        /// behind it hidden (<see cref="InkLines"/>, <see cref="CelEffects"/>). None while a filter is off.
        /// </summary>
        public TextureHandle Smoke;

        /// <summary>
        /// The game's volume clouds round each pixel, evened out as the game draws them grainy: how much they cover it
        /// and show, their own light, and how that light changes across it (Cel shaded's tones, both filters' lines; for
        /// the paper, the game has cleared its own by then).
        /// </summary>
        public TextureHandle Clouds;

        /// <summary>
        /// How far off the game's volume clouds are at each pixel (metres, 0 where there are none: the game's own cloud
        /// depth), so they fade and tone only what's behind them.
        /// </summary>
        public TextureHandle CloudDistance;

        /// <summary>Whether the deferred pass's depth and colours were taken, for the ground lines.</summary>
        public bool HasGround => SceneDepth.IsValid() && Albedo.IsValid();

        /// <summary>Whether everything was taken, so the paper can use it.</summary>
        public bool IsComplete => SceneDepth.IsValid() && Albedo.IsValid() && OpaqueDepth.IsValid() && KerbalInk.IsValid();

        public override void Reset()
        {
            SceneDepth = TextureHandle.nullHandle;
            Albedo = TextureHandle.nullHandle;
            OpaqueDepth = TextureHandle.nullHandle;
            KerbalInk = TextureHandle.nullHandle;
            BeforeTerrain = TextureHandle.nullHandle;
            Terrain = TextureHandle.nullHandle;
            Bodies = TextureHandle.nullHandle;
            BeforeEffects = TextureHandle.nullHandle;
            Smoke = TextureHandle.nullHandle;
            Clouds = TextureHandle.nullHandle;
            CloudDistance = TextureHandle.nullHandle;
        }
    }
}
