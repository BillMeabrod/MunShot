using Redux.Rendering;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace MunShot.Cameras
{
    /// <summary>
    /// The Cel shaded filter's exhaust and smoke: right after Redux draws them, they're drawn again as a cartoon cloud,
    /// in flat tones inside its hard edge, with the picture as it was before them outside; and the planet's clouds in
    /// flat tones (the game's own cloud texture, as much as <see cref="CloudsShown"/>), distant planets and moons in flat
    /// tones (<see cref="InkFrame.Bodies"/>), and the clear sky (where nothing has depth: the atmosphere's glow, the
    /// planet from far off) as much as <see cref="SkyBands"/> says (MunShot's CelShading shader).
    /// The cloud's outline is drawn with the other outlines (<see cref="InkLines"/>). From what <see cref="InkCapture"/>
    /// took either side of them (<see cref="InkFrame.BeforeEffects"/>, <see cref="InkFrame.Effects"/>), so it's
    /// registered after InkCapture's <see cref="InkCapture.Stage.Effects"/>, at the same time. A Redux camera render
    /// hook; <see cref="PhotoLook"/> registers it while photo mode is open and says each frame which camera and whether
    /// it's on.
    /// </summary>
    internal sealed class CelEffects : IFilterHook
    {
        private static readonly int PixelId = Shader.PropertyToID("_MunShotPixel");
        private static readonly int BeforeEffectsId = Shader.PropertyToID("_MunShotBeforeEffects");
        private static readonly int PictureId = Shader.PropertyToID("_MunShotPicture");
        private static readonly int EffectsId = Shader.PropertyToID("_MunShotEffects");
        private static readonly int HasEffectsId = Shader.PropertyToID("_MunShotHasEffects");
        private static readonly int CloudsShownId = Shader.PropertyToID("_MunShotCloudsShown");
        private static readonly int BodiesId = Shader.PropertyToID("_MunShotBodies");
        private static readonly int HasBodiesId = Shader.PropertyToID("_MunShotHasBodies");
        private static readonly int SkyBandsId = Shader.PropertyToID("_MunShotSkyBands");
        private static readonly int DepthId = Shader.PropertyToID("_CameraDepthTexture");


        private sealed class PassData
        {
            public TextureHandle Before;
            public TextureHandle Picture;
            public TextureHandle Effects;
            public float CloudsShown;
            public TextureHandle Depth;
            public TextureHandle Bodies;
            public float SkyBands;
            public Material Material;
            public int Pass;
            public Vector4 Pixel;
        }

        private readonly Material _material;
        private readonly int _pass;

        public CelEffects(Shader shader)
        {
            _material = CoreUtils.CreateEngineMaterial(shader);
            _pass = FilterPasses.Find(_material, "CelEffects");
        }

        /// <summary>The camera drawn over, and whether the filter is on; nothing is drawn otherwise.</summary>
        public Camera Camera { get; set; }
        public bool IsOn { get; set; }

        /// <summary>How much the game's volume clouds show (0 to 1), so they're put in flat tones.</summary>
        public float CloudsShown { get; set; }

        /// <summary>How much of the flat tones the clear sky gets (0 to 1): none near the ground, all of them from orbit.</summary>
        public float SkyBands { get; set; }

        /// <summary>When Redux draws the engines' exhaust and smoke for the camera (<see cref="InkCapture.EffectsEvent"/>).</summary>
        public RenderPassEvent EffectsEvent { get; set; } = RenderPassEvent.AfterRenderingTransparents;

        public RenderPassEvent Event => EffectsEvent + 1;
        public ScriptableRenderPassInput Inputs => ScriptableRenderPassInput.Depth;

        public bool ShouldRender(Camera camera) => IsOn && camera != null && camera == Camera && _material != null;

        public void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            var resources = frameData.Get<UniversalResourceData>();
            TextureHandle color = resources.activeColorTexture;
            TextureHandle depth = resources.cameraDepthTexture;
            if (!color.IsValid() || !depth.IsValid() || !frameData.Contains<InkFrame>()) return;
            InkFrame ink = frameData.Get<InkFrame>();
            if (!ink.BeforeEffects.IsValid() || !ink.Effects.IsValid()) return;

            // The picture is copied first, as it's then drawn over
            TextureHandle picture = FilterPasses.CopyOf(renderGraph, color, "MunShot Cel Effects Picture");
            using (IRasterRenderGraphBuilder draw = renderGraph.AddRasterRenderPass("MunShot Cel Effects", out PassData passData))
            {
                passData.Before = ink.BeforeEffects;
                passData.Picture = picture;
                passData.Effects = ink.Effects;
                passData.CloudsShown = CloudsShown;
                passData.Depth = depth;
                passData.Bodies = ink.Bodies;
                passData.SkyBands = SkyBands;
                passData.Material = _material;
                passData.Pixel = FilterPasses.PixelOf(renderGraph, color);
                passData.Pass = _pass;
                draw.UseTexture(passData.Before);
                draw.UseTexture(passData.Picture);
                draw.UseTexture(passData.Effects);
                draw.UseTexture(depth);
                if (ink.Bodies.IsValid()) draw.UseTexture(ink.Bodies);
                // The game's clouds, which it sets as a global texture
                draw.UseAllGlobalTextures(true);
                // Every pixel is drawn, so what's there needn't be loaded
                draw.SetRenderAttachment(color, 0, AccessFlags.WriteAll);
                draw.SetRenderFunc((PassData data, RasterGraphContext context) =>
                {
                    MaterialPropertyBlock properties = context.renderGraphPool.GetTempMaterialPropertyBlock();
                    properties.SetTexture(BeforeEffectsId, data.Before);
                    properties.SetTexture(PictureId, data.Picture);
                    properties.SetTexture(EffectsId, data.Effects);
                    properties.SetTexture(DepthId, data.Depth);
                    if (data.Bodies.IsValid()) properties.SetTexture(BodiesId, data.Bodies);
                    properties.SetFloat(HasBodiesId, data.Bodies.IsValid() ? 1f : 0f);
                    properties.SetFloat(CloudsShownId, data.CloudsShown);
                    properties.SetFloat(SkyBandsId, data.SkyBands);
                    properties.SetFloat(HasEffectsId, 1f);
                    properties.SetVector(PixelId, data.Pixel);
                    properties.SetVector(FilterPasses.BlitScaleBiasId, FilterPasses.WholePicture);
                    context.cmd.DrawProcedural(Matrix4x4.identity, data.Material, data.Pass, MeshTopology.Triangles, 3, 1, properties);
                });
            }
        }

        /// <summary>Lets go of the material.</summary>
        public void Destroy() => CoreUtils.Destroy(_material);
    }
}
