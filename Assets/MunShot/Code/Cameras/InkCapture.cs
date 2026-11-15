using Redux.Rendering;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace MunShot.Cameras
{
    /// <summary>
    /// A Redux camera render hook that takes something the Cel shaded and Ink filters need from the frame as it's drawn,
    /// into <see cref="InkFrame"/> (MunShot's CelShading shader). <see cref="CelAndInk"/> registers one of each, in the
    /// frame's order:
    /// - <see cref="TerrainCapture"/> and <see cref="SceneCapture"/>, either side of Redux drawing the planet's terrain
    ///   and the deferred lighting: the surfaces' own colours, the depth so far, where the terrain went, and where the
    ///   faraway view shows distant planets and moons.
    /// - <see cref="KerbalCapture"/>, before the see-through things (Ink only): the depth so far, and the kerbals'
    ///   pupils and dark hair.
    /// - <see cref="BeforeEffectsCapture"/> and <see cref="EffectsCapture"/>, either side of Redux's exhaust and smoke:
    ///   where they changed the picture, with the game's own particle effects added (the cartoon smoke cloud), and the
    ///   game's volume clouds evened out.
    /// </summary>
    internal abstract class InkCapture : IFilterHook
    {
        private static readonly int DepthId = Shader.PropertyToID("_MunShotDepth");
        private static readonly int BeforeEffectsId = Shader.PropertyToID("_MunShotBeforeEffects");
        private static readonly int AfterEffectsId = Shader.PropertyToID("_MunShotAfterEffects");
        private static readonly int MinChangeId = Shader.PropertyToID("_MunShotMinChange");

        // URP's G-buffer slot for the surface's colour
        private const int AlbedoSlot = 0;

        private sealed class DepthData
        {
            public TextureHandle Depth;
            public Material Material;
            public int Pass;
        }

        private sealed class ChangeData
        {
            public TextureHandle Before;
            public TextureHandle After;
            public float MinChange;
            public Material Material;
            public int Pass;
        }

        /// <summary>The shader's material this hook draws with; further ones are made from <see cref="Shader"/>.</summary>
        protected readonly Material Material;
        protected readonly Shader Shader;

        protected InkCapture(Shader shader)
        {
            Shader = shader;
            Material = CoreUtils.CreateEngineMaterial(shader);
        }

        /// <summary>The camera drawn from, and whether a filter that needs this is on; nothing is taken otherwise.</summary>
        public Camera Camera { get; set; }
        public bool IsOn { get; set; }

        public abstract RenderPassEvent Event { get; }
        public ScriptableRenderPassInput Inputs => ScriptableRenderPassInput.None;

        public bool ShouldRender(Camera camera) => IsOn && camera != null && camera == Camera && Material != null;

        public void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            var resources = frameData.Get<UniversalResourceData>();
            TextureHandle color = resources.activeColorTexture;
            TextureHandle depth = resources.activeDepthTexture;
            if (!color.IsValid() || !depth.IsValid()) return;
            Capture(renderGraph, frameData.GetOrCreate<InkFrame>(), resources, color, depth);
        }

        /// <summary>Takes this stage's part of the frame into <paramref name="ink"/>.</summary>
        protected abstract void Capture(RenderGraph renderGraph, InkFrame ink, UniversalResourceData resources,
                                        TextureHandle color, TextureHandle depth);

        /// <summary>Lets go of the materials.</summary>
        public virtual void Destroy() => CoreUtils.Destroy(Material);

        /// <summary>URP's G-buffer's surface colours, or the null handle when the camera has none (not deferred).</summary>
        protected static TextureHandle AlbedoOf(UniversalResourceData resources)
        {
            TextureHandle[] gBuffer = resources.gBuffer;
            return gBuffer != null && gBuffer.Length > AlbedoSlot && gBuffer[AlbedoSlot].IsValid()
                ? gBuffer[AlbedoSlot]
                : TextureHandle.nullHandle;
        }

        /// <summary>A copy of the depth as it is now, in a texture of its own.</summary>
        protected TextureHandle KeepDepth(RenderGraph renderGraph, TextureHandle color, TextureHandle depth, int pass, string name)
        {
            TextureHandle kept = renderGraph.CreateTexture(Like(renderGraph, color, name, GraphicsFormat.R32_SFloat));
            using (IRasterRenderGraphBuilder draw = renderGraph.AddRasterRenderPass(name, out DepthData passData))
            {
                passData.Depth = depth;
                passData.Material = Material;
                passData.Pass = pass;
                draw.UseTexture(depth);
                draw.SetRenderAttachment(kept, 0, AccessFlags.WriteAll);
                draw.SetRenderFunc((DepthData data, RasterGraphContext context) =>
                {
                    MaterialPropertyBlock properties = context.renderGraphPool.GetTempMaterialPropertyBlock();
                    properties.SetTexture(DepthId, data.Depth);
                    properties.SetVector(FilterPasses.BlitScaleBiasId, FilterPasses.WholePicture);
                    context.cmd.DrawProcedural(Matrix4x4.identity, data.Material, data.Pass, MeshTopology.Triangles, 3, 1, properties);
                });
            }
            return kept;
        }

        /// <summary>
        /// How much what was drawn between before and after (the exhaust on the picture, or the terrain among the
        /// surfaces' colours) changed each pixel, the size of the picture in the given format (the shader's Effects
        /// pass); a change under minChange isn't counted.
        /// </summary>
        protected TextureHandle DrawChange(RenderGraph renderGraph, TextureHandle color, TextureHandle before, TextureHandle after,
                                           int pass, string name, GraphicsFormat format, float minChange)
        {
            TextureHandle change = renderGraph.CreateTexture(Like(renderGraph, color, name, format));
            using (IRasterRenderGraphBuilder draw = renderGraph.AddRasterRenderPass(name, out ChangeData passData))
            {
                passData.Before = before;
                passData.After = after;
                passData.MinChange = minChange;
                passData.Material = Material;
                passData.Pass = pass;
                draw.UseTexture(before);
                draw.UseTexture(after);
                draw.SetRenderAttachment(change, 0, AccessFlags.WriteAll);
                draw.SetRenderFunc((ChangeData data, RasterGraphContext context) =>
                {
                    MaterialPropertyBlock properties = context.renderGraphPool.GetTempMaterialPropertyBlock();
                    properties.SetTexture(BeforeEffectsId, data.Before);
                    properties.SetTexture(AfterEffectsId, data.After);
                    properties.SetFloat(MinChangeId, data.MinChange);
                    properties.SetVector(FilterPasses.BlitScaleBiasId, FilterPasses.WholePicture);
                    context.cmd.DrawProcedural(Matrix4x4.identity, data.Material, data.Pass, MeshTopology.Triangles, 3, 1, properties);
                });
            }
            return change;
        }

        /// <summary>A texture the size of the picture, in the given format.</summary>
        protected static TextureDesc Like(RenderGraph renderGraph, TextureHandle color, string name, GraphicsFormat format)
        {
            TextureDesc desc = renderGraph.GetTextureDesc(color);
            desc.name = name;
            desc.depthBufferBits = DepthBits.None;
            desc.format = format;
            desc.clearBuffer = false;
            desc.msaaSamples = MSAASamples.None;
            return desc;
        }

        /// <summary>A one-channel mask the size of the picture, cleared to 0 before it's drawn into.</summary>
        protected static TextureHandle ClearedMask(RenderGraph renderGraph, TextureHandle color, string name)
        {
            TextureDesc desc = Like(renderGraph, color, name, GraphicsFormat.R8_UNorm);
            desc.clearBuffer = true;
            desc.clearColor = Color.clear;
            return renderGraph.CreateTexture(desc);
        }
    }
}
