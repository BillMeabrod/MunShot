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
    /// flat tones (<see cref="InkFrame.Clouds"/>, the game's own evened out), distant planets and moons in flat
    /// tones (<see cref="InkFrame.Bodies"/>), and the clear sky (where nothing has depth: the atmosphere's glow, the
    /// planet from far off) as much as <see cref="SkyBands"/> says (MunShot's CelShading shader).
    /// The cloud's outline is drawn with the other outlines (<see cref="InkLines"/>). From what
    /// <see cref="BeforeEffectsCapture"/> and <see cref="EffectsCapture"/> took either side of them
    /// (<see cref="InkFrame.BeforeEffects"/>, <see cref="InkFrame.Smoke"/>; with no smoke, just the clouds, planets and
    /// sky), so it's registered after EffectsCapture, at the same time. A Redux camera render hook;
    /// <see cref="CelAndInk"/> registers it while photo mode is open and says each frame which camera and whether it's on.
    /// </summary>
    internal sealed class CelEffects : IFilterHook
    {
        private static readonly int PixelId = Shader.PropertyToID("_MunShotPixel");
        private static readonly int BeforeEffectsId = Shader.PropertyToID("_MunShotBeforeEffects");
        private static readonly int PictureId = Shader.PropertyToID("_MunShotPicture");
        private static readonly int SmokeId = Shader.PropertyToID("_MunShotSmoke");
        private static readonly int HasSmokeId = Shader.PropertyToID("_MunShotHasSmoke");
        private static readonly int CloudsId = Shader.PropertyToID("_MunShotClouds");
        private static readonly int CloudDistanceId = Shader.PropertyToID("_MunShotCloudDistance");
        private static readonly int HasCloudsId = Shader.PropertyToID("_MunShotHasClouds");
        private static readonly int BodiesId = Shader.PropertyToID("_MunShotBodies");
        private static readonly int HasBodiesId = Shader.PropertyToID("_MunShotHasBodies");
        private static readonly int SkyBandsId = Shader.PropertyToID("_MunShotSkyBands");
        private static readonly int DepthId = Shader.PropertyToID("_CameraDepthTexture");

        private sealed class PassData
        {
            public TextureHandle Before;
            public TextureHandle Picture;
            public TextureHandle Smoke;
            public TextureHandle Clouds;
            public TextureHandle CloudDistance;
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

        /// <summary>How much of the flat tones the clear sky gets (0 to 1): none near the ground, all of them from orbit.</summary>
        public float SkyBands { get; set; }

        /// <summary>When Redux draws the engines' exhaust and smoke for the camera (<see cref="EffectsCapture.EffectsEvent"/>).</summary>
        public RenderPassEvent EffectsEvent { get; set; } = RenderPassEvent.AfterRenderingTransparents;

        public RenderPassEvent Event => EffectsEvent + 1;
        public ScriptableRenderPassInput Inputs => ScriptableRenderPassInput.Depth;

        public bool ShouldRender(Camera camera) => IsOn && camera != null && camera == Camera && _material != null;

        public void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            var resources = frameData.Get<UniversalResourceData>();
            TextureHandle color = resources.activeColorTexture;
            TextureHandle depth = resources.cameraDepthTexture;
            if (!color.IsValid() || !depth.IsValid()) return;
            InkFrame ink = frameData.Contains<InkFrame>() ? frameData.Get<InkFrame>() : null;
            TextureHandle smoke = ink?.Smoke ?? TextureHandle.nullHandle;
            TextureHandle clouds = ink?.Clouds ?? TextureHandle.nullHandle;
            TextureHandle cloudDistance = ink?.CloudDistance ?? TextureHandle.nullHandle;
            TextureHandle bodies = ink?.Bodies ?? TextureHandle.nullHandle;

            // The picture is copied first, as it's then drawn over. With no smoke taken, what's outside the
            // smoke "as it was before it" is the picture itself
            TextureHandle picture = FilterPasses.CopyOf(renderGraph, color, "MunShot Cel Effects Picture");
            bool hasSmoke = smoke.IsValid() && ink.BeforeEffects.IsValid();
            using (IRasterRenderGraphBuilder draw = renderGraph.AddRasterRenderPass("MunShot Cel Effects", out PassData passData))
            {
                passData.Before = hasSmoke ? ink.BeforeEffects : picture;
                passData.Picture = picture;
                passData.Smoke = hasSmoke ? smoke : TextureHandle.nullHandle;
                passData.Clouds = clouds;
                passData.CloudDistance = cloudDistance;
                passData.Depth = depth;
                passData.Bodies = bodies;
                passData.SkyBands = SkyBands;
                passData.Material = _material;
                passData.Pixel = FilterPasses.PixelOf(renderGraph, color);
                passData.Pass = _pass;
                draw.UseTexture(picture);
                if (hasSmoke)
                {
                    draw.UseTexture(passData.Before);
                    draw.UseTexture(passData.Smoke);
                }
                if (clouds.IsValid()) draw.UseTexture(clouds);
                if (cloudDistance.IsValid()) draw.UseTexture(cloudDistance);
                draw.UseTexture(depth);
                if (bodies.IsValid()) draw.UseTexture(bodies);
                // Every pixel is drawn, so what's there needn't be loaded
                draw.SetRenderAttachment(color, 0, AccessFlags.WriteAll);
                draw.SetRenderFunc((PassData data, RasterGraphContext context) =>
                {
                    MaterialPropertyBlock properties = context.renderGraphPool.GetTempMaterialPropertyBlock();
                    properties.SetTexture(BeforeEffectsId, data.Before);
                    properties.SetTexture(PictureId, data.Picture);
                    if (data.Smoke.IsValid()) properties.SetTexture(SmokeId, data.Smoke);
                    if (data.Clouds.IsValid()) properties.SetTexture(CloudsId, data.Clouds);
                    if (data.CloudDistance.IsValid()) properties.SetTexture(CloudDistanceId, data.CloudDistance);
                    properties.SetFloat(HasCloudsId, data.Clouds.IsValid() && data.CloudDistance.IsValid() ? 1f : 0f);
                    properties.SetTexture(DepthId, data.Depth);
                    if (data.Bodies.IsValid()) properties.SetTexture(BodiesId, data.Bodies);
                    properties.SetFloat(HasBodiesId, data.Bodies.IsValid() ? 1f : 0f);
                    properties.SetFloat(SkyBandsId, data.SkyBands);
                    properties.SetFloat(HasSmokeId, data.Smoke.IsValid() ? 1f : 0f);
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
