using System;
using Redux.Rendering;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace MunShot.Cameras
{
    /// <summary>
    /// The Cel shaded filter's flat pass: right after the game's deferred lighting, everything it lit (the ground, and
    /// parts and buildings drawn deferred) is drawn again as a cartoon from the G-buffer, URP's per-pixel store of each
    /// surface's own colour and facing: the colour evened out over the surface, lit by the sun in two tones with a
    /// hard edge between (MunShot's CelShading shader). What's drawn after (kerbals, glass, water, the atmosphere's
    /// haze) goes over it as usual. A Redux camera render hook; <see cref="PhotoLook"/> registers it while photo mode
    /// is open and says each frame which camera and whether it's on.
    /// </summary>
    internal sealed class CelShading : ICameraRenderHook
    {
        // URP's G-buffer slots: the surface's colour, and its facing
        private const int AlbedoSlot = 0;
        private const int NormalSlot = 2;

        // Sizes are in pixels of a picture 1080 high, and as big for the picture's size at others
        private const float ReferenceHeight = 1080f;

        private static readonly int AlbedoId = Shader.PropertyToID("_MunShotAlbedo");
        private static readonly int NormalsId = Shader.PropertyToID("_MunShotNormals");
        private static readonly int DepthId = Shader.PropertyToID("_MunShotDepth");
        private static readonly int PixelId = Shader.PropertyToID("_MunShotPixel");
        private static readonly int UpId = Shader.PropertyToID("_MunShotUp");
        private static readonly int BlitScaleBiasId = Shader.PropertyToID("_BlitScaleBias");

        private const int FlatPass = 0;

        private sealed class PassData
        {
            public TextureHandle Albedo;
            public TextureHandle Normals;
            public TextureHandle Depth;
            public Material Material;
            public Vector4 Pixel;
            public Vector4 Up;
        }

        private readonly Material _material;
        private readonly Action<string> _logInfo;

        // Whether the G-buffer was there last frame, so a change is logged once
        private bool? _hadGBuffer;

        /// <param name="logInfo">Where it's reported whether the camera has a G-buffer to draw from, when that changes.</param>
        public CelShading(Shader shader, Action<string> logInfo)
        {
            _logInfo = logInfo ?? throw new ArgumentNullException(nameof(logInfo));
            _material = CoreUtils.CreateEngineMaterial(shader);
        }

        /// <summary>The camera drawn over, and whether the filter is on; nothing is drawn otherwise.</summary>
        public Camera Camera { get; set; }
        public bool IsOn { get; set; }

        /// <summary>Up from the ground where the camera is, so the ground can be told from what stands on it.</summary>
        public Vector3 Up { get; set; } = Vector3.up;

        public RenderPassEvent Event => RenderPassEvent.AfterRenderingDeferredLights;
        public ScriptableRenderPassInput Inputs => ScriptableRenderPassInput.None;

        public bool ShouldRender(Camera camera) => IsOn && camera != null && camera == Camera && _material != null;

        public void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            var resources = frameData.Get<UniversalResourceData>();
            TextureHandle color = resources.activeColorTexture;
            TextureHandle depth = resources.activeDepthTexture;
            TextureHandle[] gBuffer = resources.gBuffer;
            bool hasGBuffer = gBuffer != null && gBuffer.Length > NormalSlot
                              && gBuffer[AlbedoSlot].IsValid() && gBuffer[NormalSlot].IsValid();
            if (_hadGBuffer != hasGBuffer)
            {
                _hadGBuffer = hasGBuffer;
                _logInfo(hasGBuffer
                    ? "Photo mode: Cel shaded draws from the camera's G-buffer."
                    : "Photo mode: Cel shaded can't draw: the camera has no G-buffer (it isn't rendering deferred).");
            }
            if (!hasGBuffer || !color.IsValid() || !depth.IsValid()) return;

            RenderTargetInfo size = renderGraph.GetRenderTargetInfo(color);
            float pixel = Mathf.Max(1f, size.height / ReferenceHeight);
            using (IRasterRenderGraphBuilder draw = renderGraph.AddRasterRenderPass("MunShot Cel Shading Flat", out PassData passData))
            {
                passData.Albedo = gBuffer[AlbedoSlot];
                passData.Normals = gBuffer[NormalSlot];
                passData.Depth = depth;
                passData.Material = _material;
                passData.Pixel = new Vector4(pixel / Mathf.Max(1, size.width), pixel / Mathf.Max(1, size.height), 0f, 0f);
                passData.Up = Up;
                draw.UseTexture(passData.Albedo);
                draw.UseTexture(passData.Normals);
                draw.UseTexture(depth);
                // The sun's shadow map, the cloud shadows and the sky's light, which the game sets as globals
                draw.UseAllGlobalTextures(true);
                draw.SetRenderAttachment(color, 0, AccessFlags.Write);
                draw.SetRenderFunc((PassData data, RasterGraphContext context) =>
                {
                    MaterialPropertyBlock properties = context.renderGraphPool.GetTempMaterialPropertyBlock();
                    properties.SetTexture(AlbedoId, data.Albedo);
                    properties.SetTexture(NormalsId, data.Normals);
                    properties.SetTexture(DepthId, data.Depth);
                    properties.SetVector(PixelId, data.Pixel);
                    properties.SetVector(UpId, data.Up);
                    properties.SetVector(BlitScaleBiasId, new Vector4(1f, 1f, 0f, 0f));
                    context.cmd.DrawProcedural(Matrix4x4.identity, data.Material, FlatPass, MeshTopology.Triangles, 3, 1, properties);
                });
            }
        }

        /// <summary>Lets go of the material.</summary>
        public void Destroy() => CoreUtils.Destroy(_material);
    }
}
