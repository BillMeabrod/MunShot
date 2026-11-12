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
    /// ground's depth and colours; both where the terrain, exhaust and smoke are), and <see cref="CelEffects"/> too, kept in the
    /// camera's frame data (URP resets it every frame).
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

        /// <summary>The picture just before Redux draws the engines' exhaust and smoke over it.</summary>
        public TextureHandle BeforeEffects;


        /// <summary>
        /// How much the engines' exhaust and smoke cover each pixel (0 to 1), from how much drawing them changed it, so
        /// both filters can draw them as a cartoon cloud, the lines behind it hidden (<see cref="InkLines"/>, <see cref="CelEffects"/>).
        /// </summary>
        public TextureHandle Effects;

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
            BeforeEffects = TextureHandle.nullHandle;
            Effects = TextureHandle.nullHandle;
        }
    }

    /// <summary>
    /// Takes what the Ink filter needs from the frame as it's drawn, into <see cref="InkFrame"/> (MunShot's CelShading
    /// shader). A Redux camera render hook, which <see cref="PhotoLook"/> registers once for each <see cref="Stage"/>
    /// (all but Opaque for Cel shaded too):
    /// - <see cref="Stage.BeforeTerrain"/>, after URP's G-buffer pass and before Redux draws the planet's terrain into
    ///   it (at 221): the surfaces' own colours so far, so <see cref="Stage.Scene"/> can tell where the terrain went.
    /// - <see cref="Stage.Scene"/>, right after the deferred lighting: the depth so far and the surfaces' own colours,
    ///   so the paper can tell what the deferred pass drew (the ground, buildings, trees, parts) from what's drawn after.
    /// - <see cref="Stage.Opaque"/>, before the see-through things: the depth so far, and the kerbals' own ink
    ///   (<see cref="KerbalParts"/>): their pupils, drawn from their eyes' own texture, and dark hair, so neither light
    ///   nor the visor in front changes them.
    /// - <see cref="Stage.BeforeEffects"/> and <see cref="Stage.Effects"/>, just before and just after Redux draws the
    ///   engines' exhaust and smoke (its volumetric plumes, their smoke trails and the ground flow, all in one pass at
    ///   AfterRenderingTransparents): a copy of the picture, then how much drawing them changed each pixel.
    /// </summary>
    internal sealed class InkCapture : ICameraRenderHook
    {
        public enum Stage
        {
            BeforeTerrain,
            Scene,
            Opaque,
            BeforeEffects,
            Effects
        }

        private static readonly int DepthId = Shader.PropertyToID("_MunShotDepth");
        private static readonly int BlitScaleBiasId = Shader.PropertyToID("_BlitScaleBias");
        private static readonly int MainTexId = Shader.PropertyToID("_MainTex");
        private static readonly int SolidId = Shader.PropertyToID("_MunShotSolid");
        private static readonly int OpaqueDepthId = Shader.PropertyToID("_MunShotOpaqueDepth");
        private static readonly int BeforeEffectsId = Shader.PropertyToID("_MunShotBeforeEffects");
        private static readonly int AfterEffectsId = Shader.PropertyToID("_MunShotAfterEffects");

        // URP's G-buffer slot for the surface's colour
        private const int AlbedoSlot = 0;

        private const int KeepDepthPass = 3;
        private const int KerbalInkPass = 4;
        private const int EffectsPass = 7;

        private sealed class DepthData
        {
            public TextureHandle Depth;
            public Material Material;
        }

        private sealed class EffectsData
        {
            public TextureHandle Before;
            public TextureHandle After;
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

        public RenderPassEvent Event
        {
            get
            {
                switch (_stage)
                {
                    case Stage.BeforeTerrain: return RenderPassEvent.AfterRenderingGbuffer;
                    case Stage.Scene: return RenderPassEvent.AfterRenderingDeferredLights;
                    case Stage.Opaque: return RenderPassEvent.BeforeRenderingTransparents;
                    // Either side of Redux's exhaust and smoke, at AfterRenderingTransparents. Before it at the same time
                    // (any earlier is before URP's own see-through things, the atmosphere and the sea among them):
                    // passes at one time go in the order registered, so PhotoLook moves Redux's after this. After it,
                    // one later, as Redux registers its pass again whenever an engine starts. Its clouds' last pass is
                    // at 503.
                    case Stage.BeforeEffects: return RenderPassEvent.AfterRenderingTransparents;
                    default: return RenderPassEvent.AfterRenderingTransparents + 1;
                }
            }
        }

        public ScriptableRenderPassInput Inputs => ScriptableRenderPassInput.None;

        public bool ShouldRender(Camera camera) => IsOn && camera != null && camera == Camera && _material != null;

        public void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            var resources = frameData.Get<UniversalResourceData>();
            TextureHandle color = resources.activeColorTexture;
            TextureHandle depth = resources.activeDepthTexture;
            if (!color.IsValid() || !depth.IsValid()) return;
            InkFrame ink = frameData.GetOrCreate<InkFrame>();

            if (_stage == Stage.BeforeTerrain || _stage == Stage.Scene)
            {
                TextureHandle[] gBuffer = resources.gBuffer;
                if (gBuffer == null || gBuffer.Length <= AlbedoSlot || !gBuffer[AlbedoSlot].IsValid()) return;
                if (_stage == Stage.BeforeTerrain)
                {
                    ink.BeforeTerrain = CopyOf(renderGraph, gBuffer[AlbedoSlot], "MunShot Ink Before Terrain");
                    return;
                }
                ink.Albedo = CopyOf(renderGraph, gBuffer[AlbedoSlot], "MunShot Ink Albedo");
                ink.SceneDepth = KeepDepth(renderGraph, color, depth, "MunShot Ink Scene Depth");
                if (ink.BeforeTerrain.IsValid())
                    ink.Terrain = DrawChange(renderGraph, color, ink.BeforeTerrain, ink.Albedo, "MunShot Ink Terrain");
                return;
            }

            if (_stage == Stage.BeforeEffects)
            {
                ink.BeforeEffects = InkLines.CopyOf(renderGraph, color, "MunShot Ink Before Effects");
                return;
            }

            if (_stage == Stage.Effects)
            {
                if (ink.BeforeEffects.IsValid()) ink.Effects = DrawChange(renderGraph, color, ink.BeforeEffects, color, "MunShot Ink Effects");
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

        // A copy of a texture as it is now
        private static TextureHandle CopyOf(RenderGraph renderGraph, TextureHandle source, string name)
        {
            TextureDesc desc = renderGraph.GetTextureDesc(source);
            desc.name = name;
            desc.clearBuffer = false;
            TextureHandle copy = renderGraph.CreateTexture(desc);
            renderGraph.AddBlitPass(source, copy, Vector2.one, Vector2.zero, passName: name);
            return copy;
        }

        // How much what was drawn between before and after (the exhaust and smoke on the picture, or the terrain among
        // the surfaces' colours) changed each pixel, the size of the picture
        private TextureHandle DrawChange(RenderGraph renderGraph, TextureHandle color, TextureHandle before, TextureHandle after, string name)
        {
            TextureHandle effects = renderGraph.CreateTexture(Like(renderGraph, color, name, GraphicsFormat.R8_UNorm));
            using (IRasterRenderGraphBuilder draw = renderGraph.AddRasterRenderPass(name, out EffectsData passData))
            {
                passData.Before = before;
                passData.After = after;
                passData.Material = _material;
                draw.UseTexture(before);
                draw.UseTexture(after);
                draw.SetRenderAttachment(effects, 0, AccessFlags.Write);
                draw.SetRenderFunc((EffectsData data, RasterGraphContext context) =>
                {
                    MaterialPropertyBlock properties = context.renderGraphPool.GetTempMaterialPropertyBlock();
                    properties.SetTexture(BeforeEffectsId, data.Before);
                    properties.SetTexture(AfterEffectsId, data.After);
                    properties.SetVector(BlitScaleBiasId, new Vector4(1f, 1f, 0f, 0f));
                    context.cmd.DrawProcedural(Matrix4x4.identity, data.Material, EffectsPass, MeshTopology.Triangles, 3, 1, properties);
                });
            }
            return effects;
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
