using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace MunShot.Cameras
{
    /// <summary>
    /// After URP's G-buffer pass and before Redux draws the planet's terrain into it (at 221): the surfaces' own colours
    /// so far (<see cref="InkFrame.BeforeTerrain"/>), so <see cref="SceneCapture"/> can tell where the terrain went.
    /// </summary>
    internal sealed class TerrainCapture : InkCapture
    {
        public TerrainCapture(Shader shader) : base(shader) { }

        public override RenderPassEvent Event => RenderPassEvent.AfterRenderingGbuffer;

        protected override void Capture(RenderGraph renderGraph, InkFrame ink, UniversalResourceData resources,
                                        TextureHandle color, TextureHandle depth)
        {
            TextureHandle albedo = AlbedoOf(resources);
            if (albedo.IsValid()) ink.BeforeTerrain = FilterPasses.CopyOf(renderGraph, albedo, "MunShot Ink Before Terrain");
        }
    }

    /// <summary>
    /// Right after the deferred lighting: the depth so far and the surfaces' own colours (<see cref="InkFrame.SceneDepth"/>,
    /// <see cref="InkFrame.Albedo"/>), so the paper can tell what the deferred pass drew (the ground, buildings, trees,
    /// parts) from what's drawn after; where the terrain went (<see cref="InkFrame.Terrain"/>, against
    /// <see cref="TerrainCapture"/>'s); and where the faraway view shows distant planets and moons
    /// (<see cref="InkFrame.Bodies"/>, from <see cref="FarBodies"/>).
    /// </summary>
    internal sealed class SceneCapture : InkCapture
    {
        private static readonly int FarViewProjectionId = Shader.PropertyToID("_MunShotFarViewProjection");
        private static readonly int BodyShownId = Shader.PropertyToID("_MunShotBodyShown");

        private sealed class BodiesData
        {
            public IReadOnlyList<(Renderer body, bool orbited)> Bodies;
            public Material Shown;
            public Material Orbited;
            public int Pass;
        }

        private readonly int _keepDepthPass;
        private readonly int _changePass;
        private readonly int _bodiesPass;

        // The distant planets and moons are drawn with the first, the body the vessel orbits (hiding what's behind it)
        // with the second
        private Material _bodyMaterial;
        private Material _orbitedMaterial;

        public SceneCapture(Shader shader) : base(shader)
        {
            _keepDepthPass = FilterPasses.Find(Material, "KeepDepth");
            _changePass = FilterPasses.Find(Material, "Effects");
            _bodiesPass = FilterPasses.Find(Material, "Bodies");
        }

        /// <summary>
        /// The planets and moons Redux's faraway view draws, and whether each is the body the vessel orbits, with that
        /// view's camera's view and projection (as drawn into a texture); none while it's empty.
        /// </summary>
        public IReadOnlyList<(Renderer body, bool orbited)> FarBodies { get; set; }
        public Matrix4x4 FarViewProjection { get; set; } = Matrix4x4.identity;

        public override RenderPassEvent Event => RenderPassEvent.AfterRenderingDeferredLights;

        protected override void Capture(RenderGraph renderGraph, InkFrame ink, UniversalResourceData resources,
                                        TextureHandle color, TextureHandle depth)
        {
            TextureHandle albedo = AlbedoOf(resources);
            if (!albedo.IsValid()) return;
            ink.Albedo = FilterPasses.CopyOf(renderGraph, albedo, "MunShot Ink Albedo");
            ink.SceneDepth = KeepDepth(renderGraph, color, depth, _keepDepthPass, "MunShot Ink Scene Depth");
            if (FarBodies != null && FarBodies.Count > 0) ink.Bodies = DrawBodies(renderGraph, color);
            if (ink.BeforeTerrain.IsValid())
                ink.Terrain = DrawChange(renderGraph, color, ink.BeforeTerrain, ink.Albedo, _changePass, "MunShot Ink Terrain",
                                         GraphicsFormat.R8_UNorm, 0f);
        }

        // Where the faraway view shows a distant planet or moon, drawn from that view's camera with their own meshes,
        // nearest first (the body the vessel orbits hides what's behind it but isn't marked)
        private TextureHandle DrawBodies(RenderGraph renderGraph, TextureHandle color)
        {
            if (_bodyMaterial == null)
            {
                _bodyMaterial = CoreUtils.CreateEngineMaterial(Shader);
                _bodyMaterial.SetFloat(BodyShownId, 1f);
                _orbitedMaterial = CoreUtils.CreateEngineMaterial(Shader);
                _orbitedMaterial.SetFloat(BodyShownId, 0f);
            }
            _bodyMaterial.SetMatrix(FarViewProjectionId, FarViewProjection);
            _orbitedMaterial.SetMatrix(FarViewProjectionId, FarViewProjection);

            TextureHandle mask = ClearedMask(renderGraph, color, "MunShot Ink Bodies");
            TextureDesc depthDesc = Like(renderGraph, color, "MunShot Ink Bodies Depth", GraphicsFormat.None);
            depthDesc.depthBufferBits = DepthBits.Depth32;
            depthDesc.clearBuffer = true;
            TextureHandle depth = renderGraph.CreateTexture(depthDesc);
            using (IRasterRenderGraphBuilder draw = renderGraph.AddRasterRenderPass("MunShot Ink Bodies", out BodiesData passData))
            {
                passData.Bodies = FarBodies;
                passData.Shown = _bodyMaterial;
                passData.Orbited = _orbitedMaterial;
                passData.Pass = _bodiesPass;
                draw.SetRenderAttachment(mask, 0, AccessFlags.Write);
                draw.SetRenderAttachmentDepth(depth, AccessFlags.Write);
                draw.SetRenderFunc((BodiesData data, RasterGraphContext context) =>
                {
                    foreach ((Renderer body, bool orbited) in data.Bodies)
                        if (body != null && body.enabled && body.gameObject.activeInHierarchy)
                            context.cmd.DrawRenderer(body, orbited ? data.Orbited : data.Shown, 0, data.Pass);
                });
            }
            return mask;
        }

        public override void Destroy()
        {
            base.Destroy();
            CoreUtils.Destroy(_bodyMaterial);
            CoreUtils.Destroy(_orbitedMaterial);
            _bodyMaterial = null;
            _orbitedMaterial = null;
        }
    }
}
