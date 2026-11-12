using System.Collections.Generic;
using Redux.Rendering;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.RenderGraphModule.Util;
using UnityEngine.Rendering.Universal;

namespace MunShot.Cameras
{
    /// <summary>
    /// What <see cref="InkLines"/> takes from earlier in the frame (Ink's paper all of it, Cel shaded's outlines the
    /// ground's depth and colours), kept in the camera's frame data (URP resets it every frame).
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
        }
    }

    /// <summary>
    /// Takes what the Ink filter needs from the frame as it's drawn, into <see cref="InkFrame"/> (MunShot's CelShading
    /// shader). A Redux camera render hook, which <see cref="PhotoLook"/> registers once for each <see cref="Stage"/>
    /// (Scene for Cel shaded too):
    /// - <see cref="Stage.Scene"/>, right after the deferred lighting: the depth so far and the surfaces' own colours,
    ///   so the paper can tell what the deferred pass drew (the ground, buildings, trees, parts) from what's drawn after.
    /// - <see cref="Stage.Opaque"/>, before the see-through things: the depth so far, and the kerbals' own ink
    ///   (<see cref="KerbalParts"/>): their pupils, drawn from their eyes' own texture, and dark hair, so neither light
    ///   nor the visor in front changes them.
    /// </summary>
    internal sealed class InkCapture : ICameraRenderHook
    {
        public enum Stage
        {
            Scene,
            Opaque
        }

        private static readonly int DepthId = Shader.PropertyToID("_MunShotDepth");
        private static readonly int BlitScaleBiasId = Shader.PropertyToID("_BlitScaleBias");
        private static readonly int MainTexId = Shader.PropertyToID("_MainTex");
        private static readonly int SolidId = Shader.PropertyToID("_MunShotSolid");
        private static readonly int OpaqueDepthId = Shader.PropertyToID("_MunShotOpaqueDepth");

        // URP's G-buffer slot for the surface's colour
        private const int AlbedoSlot = 0;

        private const int KeepDepthPass = 3;
        private const int KerbalInkPass = 4;

        private sealed class DepthData
        {
            public TextureHandle Depth;
            public Material Material;
        }

        private sealed class KerbalInkData
        {
            public TextureHandle OpaqueDepth;
            public List<(Renderer part, Material material)> Parts;
        }

        private readonly Shader _shader;
        private readonly Material _material;
        private readonly Stage _stage;

        // The pupils' material for each eye texture (kerbals share a few), the dark hair's, and the parts drawn this
        // frame
        private readonly Dictionary<Texture, Material> _pupilMaterials = new Dictionary<Texture, Material>();
        private Material _hairMaterial;
        private readonly List<(Renderer, Material)> _drawn = new List<(Renderer, Material)>();

        public InkCapture(Shader shader, Stage stage)
        {
            _shader = shader;
            _material = CoreUtils.CreateEngineMaterial(shader);
            _stage = stage;
        }

        /// <summary>The camera drawn from, and whether a filter that needs this is on; nothing is taken otherwise.</summary>
        public Camera Camera { get; set; }
        public bool IsOn { get; set; }

        /// <summary>
        /// The kerbals' eyeballs, whose pupils are drawn, and dark hair, drawn whole (<see cref="Stage.Opaque"/> only).
        /// </summary>
        public IReadOnlyList<(Renderer part, bool hair)> KerbalParts { get; set; }

        public RenderPassEvent Event => _stage == Stage.Scene
            ? RenderPassEvent.AfterRenderingDeferredLights
            : RenderPassEvent.BeforeRenderingTransparents;

        public ScriptableRenderPassInput Inputs => ScriptableRenderPassInput.None;

        public bool ShouldRender(Camera camera) => IsOn && camera != null && camera == Camera && _material != null;

        public void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            var resources = frameData.Get<UniversalResourceData>();
            TextureHandle color = resources.activeColorTexture;
            TextureHandle depth = resources.activeDepthTexture;
            if (!color.IsValid() || !depth.IsValid()) return;
            InkFrame ink = frameData.GetOrCreate<InkFrame>();

            if (_stage == Stage.Scene)
            {
                TextureHandle[] gBuffer = resources.gBuffer;
                if (gBuffer == null || gBuffer.Length <= AlbedoSlot || !gBuffer[AlbedoSlot].IsValid()) return;
                TextureDesc albedoDesc = renderGraph.GetTextureDesc(gBuffer[AlbedoSlot]);
                albedoDesc.name = "MunShot Ink Albedo";
                albedoDesc.clearBuffer = false;
                ink.Albedo = renderGraph.CreateTexture(albedoDesc);
                renderGraph.AddBlitPass(gBuffer[AlbedoSlot], ink.Albedo, Vector2.one, Vector2.zero, passName: "MunShot Ink Albedo");
                ink.SceneDepth = KeepDepth(renderGraph, color, depth, "MunShot Ink Scene Depth");
                return;
            }

            ink.OpaqueDepth = KeepDepth(renderGraph, color, depth, "MunShot Ink Opaque Depth");
            ink.KerbalInk = DrawKerbalInk(renderGraph, color, ink.OpaqueDepth);
        }

        // A copy of the depth as it is now, in a texture of its own
        private TextureHandle KeepDepth(RenderGraph renderGraph, TextureHandle color, TextureHandle depth, string name)
        {
            TextureHandle kept = renderGraph.CreateTexture(Like(renderGraph, color, name, GraphicsFormat.R32_SFloat));
            using (IRasterRenderGraphBuilder draw = renderGraph.AddRasterRenderPass(name, out DepthData passData))
            {
                passData.Depth = depth;
                passData.Material = _material;
                draw.UseTexture(depth);
                draw.SetRenderAttachment(kept, 0, AccessFlags.Write);
                draw.SetRenderFunc((DepthData data, RasterGraphContext context) =>
                {
                    MaterialPropertyBlock properties = context.renderGraphPool.GetTempMaterialPropertyBlock();
                    properties.SetTexture(DepthId, data.Depth);
                    properties.SetVector(BlitScaleBiasId, new Vector4(1f, 1f, 0f, 0f));
                    context.cmd.DrawProcedural(Matrix4x4.identity, data.Material, KeepDepthPass, MeshTopology.Triangles, 3, 1, properties);
                });
            }
            return kept;
        }

        // The pupils and dark hair, where they're seen (not behind an eyelid, a hand or the helmet), and nothing elsewhere
        private TextureHandle DrawKerbalInk(RenderGraph renderGraph, TextureHandle color, TextureHandle opaqueDepth)
        {
            TextureDesc inkDesc = Like(renderGraph, color, "MunShot Ink Kerbals", GraphicsFormat.R8_UNorm);
            inkDesc.clearBuffer = true;
            inkDesc.clearColor = Color.clear;
            TextureHandle kerbalInk = renderGraph.CreateTexture(inkDesc);

            _drawn.Clear();
            if (KerbalParts != null)
                foreach ((Renderer part, bool hair) in KerbalParts)
                    if (part != null && part.enabled && !part.forceRenderingOff && part.gameObject.activeInHierarchy)
                    {
                        Material material = hair ? HairMaterial() : PupilMaterial(part.sharedMaterial);
                        if (material != null) _drawn.Add((part, material));
                    }

            using (IRasterRenderGraphBuilder draw = renderGraph.AddRasterRenderPass("MunShot Ink Kerbals", out KerbalInkData passData))
            {
                passData.OpaqueDepth = opaqueDepth;
                passData.Parts = _drawn;
                draw.UseTexture(opaqueDepth);
                draw.SetRenderAttachment(kerbalInk, 0, AccessFlags.Write);
                draw.SetRenderFunc((KerbalInkData data, RasterGraphContext context) =>
                {
                    foreach ((Renderer part, Material material) in data.Parts)
                    {
                        material.SetTexture(OpaqueDepthId, data.OpaqueDepth);
                        context.cmd.DrawRenderer(part, material, 0, KerbalInkPass);
                    }
                });
            }
            return kerbalInk;
        }

        // The pupils' material for an eye's: MunShot's shader, with the eye's texture
        private Material PupilMaterial(Material eyeMaterial)
        {
            if (eyeMaterial == null || !eyeMaterial.HasTexture(MainTexId)) return null;
            Texture texture = eyeMaterial.GetTexture(MainTexId);
            if (texture == null) return null;
            if (_pupilMaterials.TryGetValue(texture, out Material material) && material != null) return material;
            material = CoreUtils.CreateEngineMaterial(_shader);
            material.SetTexture(MainTexId, texture);
            material.SetTextureScale(MainTexId, eyeMaterial.GetTextureScale(MainTexId));
            material.SetTextureOffset(MainTexId, eyeMaterial.GetTextureOffset(MainTexId));
            _pupilMaterials[texture] = material;
            return material;
        }

        // Dark hair's material: MunShot's shader, all ink
        private Material HairMaterial()
        {
            if (_hairMaterial != null) return _hairMaterial;
            _hairMaterial = CoreUtils.CreateEngineMaterial(_shader);
            _hairMaterial.SetFloat(SolidId, 1f);
            return _hairMaterial;
        }

        // A texture the size of the picture, in the given format
        private static TextureDesc Like(RenderGraph renderGraph, TextureHandle color, string name, GraphicsFormat format)
        {
            TextureDesc desc = renderGraph.GetTextureDesc(color);
            desc.name = name;
            desc.depthBufferBits = DepthBits.None;
            desc.format = format;
            desc.clearBuffer = false;
            desc.msaaSamples = MSAASamples.None;
            return desc;
        }

        /// <summary>Lets go of the materials.</summary>
        public void Destroy()
        {
            CoreUtils.Destroy(_material);
            foreach (Material material in _pupilMaterials.Values) CoreUtils.Destroy(material);
            _pupilMaterials.Clear();
            CoreUtils.Destroy(_hairMaterial);
            _hairMaterial = null;
        }
    }
}
