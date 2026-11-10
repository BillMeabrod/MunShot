using Redux.Rendering;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace MunShot.Cameras
{
    /// <summary>
    /// The Cel shaded filter's ink: dark lines of an even width wherever the depth jumps (an object's outline against
    /// what's behind) or the surface turns sharply (a building's corner, a crease), drawn over the picture before the
    /// post-processing (so a fisheye bends them with the rest). Read from the camera's depth, with the game's water
    /// surface laid over it so the seabed gets none, and its normals texture (MunShot's CelShading shader, Outlines
    /// pass). A Redux camera render hook; <see cref="PhotoLook"/> registers it with <see cref="CelShading"/>.
    /// </summary>
    internal sealed class CelOutlines : ICameraRenderHook
    {
        // Sizes are in pixels of a picture 1080 high, and as big for the picture's size at others
        private const float ReferenceHeight = 1080f;

        private static readonly int PixelId = Shader.PropertyToID("_MunShotPixel");
        private static readonly int DepthId = Shader.PropertyToID("_CameraDepthTexture");
        private static readonly int NormalsId = Shader.PropertyToID("_CameraNormalsTexture");
        private static readonly int BlitScaleBiasId = Shader.PropertyToID("_BlitScaleBias");

        private const int OutlinesPass = 1;

        private sealed class PassData
        {
            public TextureHandle Depth;
            public TextureHandle Normals;
            public Material Material;
            public Vector4 Pixel;
        }

        private readonly Material _material;

        public CelOutlines(Shader shader)
        {
            _material = CoreUtils.CreateEngineMaterial(shader);
        }

        /// <summary>The camera drawn over, and whether the filter is on; nothing is drawn otherwise.</summary>
        public Camera Camera { get; set; }
        public bool IsOn { get; set; }

        public RenderPassEvent Event => RenderPassEvent.BeforeRenderingPostProcessing;
        public ScriptableRenderPassInput Inputs => ScriptableRenderPassInput.Depth | ScriptableRenderPassInput.Normal;

        public bool ShouldRender(Camera camera) => IsOn && camera != null && camera == Camera && _material != null;

        public void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            var resources = frameData.Get<UniversalResourceData>();
            TextureHandle color = resources.activeColorTexture;
            TextureHandle depth = resources.cameraDepthTexture;
            TextureHandle normals = resources.cameraNormalsTexture;
            if (!color.IsValid() || !depth.IsValid() || !normals.IsValid()) return;

            RenderTargetInfo size = renderGraph.GetRenderTargetInfo(color);
            float pixel = Mathf.Max(1f, size.height / ReferenceHeight);
            using (IRasterRenderGraphBuilder draw = renderGraph.AddRasterRenderPass("MunShot Cel Shading Outlines", out PassData passData))
            {
                passData.Depth = depth;
                passData.Normals = normals;
                passData.Material = _material;
                passData.Pixel = new Vector4(pixel / Mathf.Max(1, size.width), pixel / Mathf.Max(1, size.height), 0f, 0f);
                draw.UseTexture(depth);
                draw.UseTexture(normals);
                // For the game's water depth, which it sets as a global texture
                draw.UseAllGlobalTextures(true);
                // Ink is laid over the picture (blended by its coverage), so the picture isn't read
                draw.SetRenderAttachment(color, 0, AccessFlags.ReadWrite);
                draw.SetRenderFunc((PassData data, RasterGraphContext context) =>
                {
                    MaterialPropertyBlock properties = context.renderGraphPool.GetTempMaterialPropertyBlock();
                    properties.SetTexture(DepthId, data.Depth);
                    properties.SetTexture(NormalsId, data.Normals);
                    properties.SetVector(PixelId, data.Pixel);
                    properties.SetVector(BlitScaleBiasId, new Vector4(1f, 1f, 0f, 0f));
                    context.cmd.DrawProcedural(Matrix4x4.identity, data.Material, OutlinesPass, MeshTopology.Triangles, 3, 1, properties);
                });
            }
        }

        /// <summary>Lets go of the material.</summary>
        public void Destroy() => CoreUtils.Destroy(_material);
    }
}
