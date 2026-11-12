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
    /// Solid ink lines: heavier where the depth jumps (an outline, a step) or the water meets the land, lighter where the
    /// surface turns at a corner (only near enough), read from the camera's depth, with the game's water surface laid
    /// over it so the seabed gets none, and its normals texture (MunShot's CelShading shader, InkLines.hlsl). A Redux camera render hook, which <see cref="PhotoLook"/> registers twice:
    /// - Over the picture, for Cel shaded: drawn before the post-processing, so a fisheye bends them with the rest; with
    ///   what <see cref="InkCapture"/> took of the ground, lines where its own colour changes sharply (a path's kerb).
    /// - On paper, for the Ink filter: the lines alone on white, drawn after the post-processing so the game's
    ///   exposure, bloom and tonemapping leave the white white; bent by <see cref="Lens"/> as the fisheye bends the
    ///   picture; among a tree's leaves, only the outlines. With what <see cref="InkCapture"/> took earlier in the
    ///   frame, also ground lines (road markings, kerbs) and the kerbals' pupils and dark hair; and solid black wherever
    ///   the finished picture (copied just before) was near black.
    /// </summary>
    internal sealed class InkLines : ICameraRenderHook
    {
        // Sizes are in pixels of a picture 1080 high, and as big for the picture's size at others
        private const float ReferenceHeight = 1080f;

        private static readonly int PixelId = Shader.PropertyToID("_MunShotPixel");
        private static readonly int LensId = Shader.PropertyToID("_MunShotLens");
        private static readonly int UpId = Shader.PropertyToID("_MunShotUp");
        private static readonly int TakenId = Shader.PropertyToID("_MunShotTaken");
        private static readonly int SceneDepthId = Shader.PropertyToID("_MunShotSceneDepth");
        private static readonly int AlbedoId = Shader.PropertyToID("_MunShotAlbedo");
        private static readonly int OpaqueDepthId = Shader.PropertyToID("_MunShotOpaqueDepth");
        private static readonly int KerbalInkId = Shader.PropertyToID("_MunShotKerbalInk");
        private static readonly int LeavesId = Shader.PropertyToID("_MunShotLeaves");
        private static readonly int InkColorId = Shader.PropertyToID("_MunShotInkColor");
        private static readonly int PaperColorId = Shader.PropertyToID("_MunShotPaperColor");
        private static readonly int PlanetId = Shader.PropertyToID("_MunShotPlanet");
        private static readonly int EyeId = Shader.PropertyToID("_MunShotEye");
        private static readonly int FarLookId = Shader.PropertyToID("_MunShotFarLook");
        private static readonly int FarEyeId = Shader.PropertyToID("_MunShotFarEye");
        private static readonly int FarPlanetId = Shader.PropertyToID("_MunShotFarPlanet");
        private static readonly int FarMetresId = Shader.PropertyToID("_MunShotFarMetres");
        private static readonly int FarSurfaceId = Shader.PropertyToID("_MunShotFarSurface");
        private static readonly int FarSeaId = Shader.PropertyToID("_MunShotFarSea");
        private static readonly int FarViewProjectionId = Shader.PropertyToID("_MunShotFarViewProjection");
        private static readonly int PackedMapId = Shader.PropertyToID("_PackedMap");
        private static readonly int PictureId = Shader.PropertyToID("_MunShotPicture");
        private static readonly int DepthId = Shader.PropertyToID("_CameraDepthTexture");
        private static readonly int NormalsId = Shader.PropertyToID("_CameraNormalsTexture");
        private static readonly int BlitScaleBiasId = Shader.PropertyToID("_BlitScaleBias");

        private const int OutlinesPass = 1;
        private const int PaperPass = 2;
        private const int LeavesPass = 5;
        private const int FarSurfacePass = 6;

        private sealed class PassData
        {
            public TextureHandle Depth;
            public TextureHandle Normals;
            public TextureHandle SceneDepth;
            public TextureHandle Albedo;
            public TextureHandle OpaqueDepth;
            public TextureHandle KerbalInk;
            public TextureHandle Leaves;
            public TextureHandle Picture;
            public bool Taken;
            public Material Material;
            public int Pass;
            public Vector4 Pixel;
            public Vector4 Lens;
            public Vector4 Up;
            public Vector4 InkColor;
            public Vector4 PaperColor;
            public Vector4 Planet;
            public Vector4 Eye;
            public Matrix4x4 FarLook;
            public Vector4 FarEye;
            public Vector4 FarPlanet;
            public float FarMetres;
            public TextureHandle FarSurface;
        }

        private readonly Material _material;
        // The faraway planet's surface is drawn with this, the planet's own packed map set on it
        private readonly Material _farMaterial;
        private readonly bool _onPaper;

        /// <param name="onPaper">The lines alone on white (the Ink filter), rather than over the picture.</param>
        public InkLines(Shader shader, bool onPaper)
        {
            _material = CoreUtils.CreateEngineMaterial(shader);
            _farMaterial = CoreUtils.CreateEngineMaterial(shader);
            _onPaper = onPaper;
        }

        /// <summary>The camera drawn over, and whether the filter is on; nothing is drawn otherwise.</summary>
        public Camera Camera { get; set; }
        public bool IsOn { get; set; }

        /// <summary>
        /// On paper, the fisheye as URP's lens distortion draws it: x its angle, y its sigma, z how far it zooms in; x
        /// is 0 with no fisheye.
        /// </summary>
        public Vector4 Lens { get; set; }

        /// <summary>Up from the ground where the camera is, so the ground can be told from what stands on it.</summary>
        public Vector3 Up { get; set; } = Vector3.up;

        /// <summary>On paper, the ink's and the paper's colours (linear).</summary>
        public Color InkColor { get; set; } = Color.black;
        public Color PaperColor { get; set; } = Color.white;

        /// <summary>
        /// The planet the camera is near, in physics space: xyz its centre, w its radius (0 when there's none), so its
        /// coasts can be faded from high above it.
        /// </summary>
        public Vector4 Planet { get; set; }

        /// <summary>
        /// The planet as Redux's faraway view draws it (a scaled-down copy, which writes no depth), so its edge is
        /// drawn where the picture shows it: that view's camera (from the picture back to its world, the inverse of
        /// its view and projection) and where it is, the copy's centre and radius there, to the top of its highest
        /// cloud layer (w 0 when there's none), and how many metres one of that world's units is.
        /// </summary>
        public Matrix4x4 FarLook { get; set; } = Matrix4x4.identity;
        public Vector3 FarEye { get; set; }
        public Vector4 FarPlanet { get; set; }
        public float FarMetres { get; set; }

        /// <summary>
        /// The faraway planet itself (its renderer in that view; none for a planet with no sea) and that view's
        /// camera's view and projection, as drawn into a texture: where its surface is seen and how smooth it is there
        /// is drawn, for its coasts.
        /// </summary>
        public Renderer FarBody { get; set; }
        public Matrix4x4 FarViewProjection { get; set; } = Matrix4x4.identity;

        public RenderPassEvent Event => _onPaper ? RenderPassEvent.AfterRenderingPostProcessing : RenderPassEvent.BeforeRenderingPostProcessing;
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
            var inputs = new PassData
            {
                Depth = depth,
                Normals = normals,
                Material = _material,
                Pixel = new Vector4(pixel / Mathf.Max(1, size.width), pixel / Mathf.Max(1, size.height), 0f, 0f),
                Lens = Lens,
                Up = Up,
                InkColor = InkColor,
                PaperColor = PaperColor,
                Planet = Planet,
                Eye = Camera.transform.position,
                FarLook = FarLook,
                FarEye = FarEye,
                FarPlanet = FarPlanet,
                FarMetres = FarMetres
            };

            if (FarBody != null && FarPlanet.w > 0f) inputs.FarSurface = DrawFarSurface(renderGraph, color);

            InkFrame ink = frameData.Contains<InkFrame>() ? frameData.Get<InkFrame>() : null;
            if (!_onPaper)
            {
                // The ground lines, with the depth now as the depth once everything solid is drawn
                inputs.Taken = ink != null && ink.HasGround;
                if (inputs.Taken)
                {
                    inputs.SceneDepth = ink.SceneDepth;
                    inputs.Albedo = ink.Albedo;
                    inputs.OpaqueDepth = depth;
                }
                Draw(renderGraph, "MunShot Cel Shading Outlines", OutlinesPass, color, inputs);
                return;
            }

            // On paper, the finished picture is copied first, as it's then drawn over; with what was taken earlier in the
            // frame (without all of which there are just the lines and the black), and where lines crowd, worked out first
            inputs.Picture = CopyOf(renderGraph, color);

            inputs.Taken = ink != null && ink.IsComplete;
            if (inputs.Taken)
            {
                inputs.SceneDepth = ink.SceneDepth;
                inputs.Albedo = ink.Albedo;
                inputs.OpaqueDepth = ink.OpaqueDepth;
                inputs.KerbalInk = ink.KerbalInk;
            }

            inputs.Leaves = Mask(renderGraph, color, "MunShot Ink Leaves");
            Draw(renderGraph, "MunShot Ink Leaves", LeavesPass, inputs.Leaves, inputs);
            Draw(renderGraph, "MunShot Ink", PaperPass, color, inputs);
        }

        // A copy of the picture as it is now
        private static TextureHandle CopyOf(RenderGraph renderGraph, TextureHandle color)
        {
            TextureDesc pictureDesc = renderGraph.GetTextureDesc(color);
            pictureDesc.name = "MunShot Ink Picture";
            pictureDesc.clearBuffer = false;
            pictureDesc.depthBufferBits = DepthBits.None;
            TextureHandle picture = renderGraph.CreateTexture(pictureDesc);
            renderGraph.AddBlitPass(color, picture, Vector2.one, Vector2.zero, passName: "MunShot Ink Picture");
            return picture;
        }

        private sealed class FarSurfaceData
        {
            public Renderer Body;
            public Material Material;
        }

        // Where the faraway planet's surface is seen and how smooth it is there, drawn from that view's camera with the
        // planet's own mesh
        private TextureHandle DrawFarSurface(RenderGraph renderGraph, TextureHandle color)
        {
            Material surface = FarBody.sharedMaterial;
            if (surface == null || !surface.HasTexture(PackedMapId)) return TextureHandle.nullHandle;
            _farMaterial.SetTexture(PackedMapId, surface.GetTexture(PackedMapId));
            _farMaterial.SetMatrix(FarViewProjectionId, FarViewProjection);
            _farMaterial.SetVector(FarEyeId, FarEye);

            TextureDesc desc = renderGraph.GetTextureDesc(color);
            desc.name = "MunShot Far Surface";
            desc.depthBufferBits = DepthBits.None;
            desc.format = GraphicsFormat.R8G8_UNorm;
            desc.clearBuffer = true;
            desc.clearColor = Color.clear;
            desc.msaaSamples = MSAASamples.None;
            TextureHandle mask = renderGraph.CreateTexture(desc);
            using (IRasterRenderGraphBuilder draw = renderGraph.AddRasterRenderPass("MunShot Far Surface", out FarSurfaceData passData))
            {
                passData.Body = FarBody;
                passData.Material = _farMaterial;
                draw.SetRenderAttachment(mask, 0, AccessFlags.Write);
                draw.SetRenderFunc((FarSurfaceData data, RasterGraphContext context) =>
                {
                    context.cmd.DrawRenderer(data.Body, data.Material, 0, FarSurfacePass);
                });
            }
            return mask;
        }

        // A one-channel texture the size of the picture, for a mask
        private static TextureHandle Mask(RenderGraph renderGraph, TextureHandle color, string name)
        {
            TextureDesc desc = renderGraph.GetTextureDesc(color);
            desc.name = name;
            desc.depthBufferBits = DepthBits.None;
            desc.format = GraphicsFormat.R8_UNorm;
            desc.clearBuffer = false;
            desc.msaaSamples = MSAASamples.None;
            return renderGraph.CreateTexture(desc);
        }

        // One of the shader's passes over the whole of target, with everything in inputs that's there
        private static void Draw(RenderGraph renderGraph, string name, int pass, TextureHandle target, PassData inputs)
        {
            using (IRasterRenderGraphBuilder draw = renderGraph.AddRasterRenderPass(name, out PassData passData))
            {
                passData.Depth = inputs.Depth;
                passData.Normals = inputs.Normals;
                passData.SceneDepth = inputs.SceneDepth;
                passData.Albedo = inputs.Albedo;
                passData.OpaqueDepth = inputs.OpaqueDepth;
                passData.KerbalInk = inputs.KerbalInk;
                passData.Leaves = inputs.Leaves;
                passData.Picture = inputs.Picture;
                passData.Taken = inputs.Taken;
                passData.Material = inputs.Material;
                passData.Pass = pass;
                passData.Pixel = inputs.Pixel;
                passData.Lens = inputs.Lens;
                passData.Up = inputs.Up;
                passData.InkColor = inputs.InkColor;
                passData.PaperColor = inputs.PaperColor;
                passData.Planet = inputs.Planet;
                passData.Eye = inputs.Eye;
                passData.FarLook = inputs.FarLook;
                passData.FarEye = inputs.FarEye;
                passData.FarPlanet = inputs.FarPlanet;
                passData.FarMetres = inputs.FarMetres;
                passData.FarSurface = inputs.FarSurface;
                foreach (TextureHandle input in new[]
                         {
                             inputs.Depth, inputs.Normals, inputs.SceneDepth, inputs.Albedo, inputs.OpaqueDepth,
                             inputs.KerbalInk, inputs.Leaves, inputs.Picture, inputs.FarSurface
                         })
                    if (input.IsValid()) draw.UseTexture(input);
                // For the game's water depth, which it sets as a global texture
                draw.UseAllGlobalTextures(true);
                // The outlines are laid over the picture (blended by their coverage); every other pass covers all of
                // its target (the paper reads the picture's copy, not the picture)
                draw.SetRenderAttachment(target, 0, pass == OutlinesPass ? AccessFlags.ReadWrite : AccessFlags.Write);
                draw.SetRenderFunc((PassData data, RasterGraphContext context) =>
                {
                    MaterialPropertyBlock properties = context.renderGraphPool.GetTempMaterialPropertyBlock();
                    properties.SetTexture(DepthId, data.Depth);
                    properties.SetTexture(NormalsId, data.Normals);
                    properties.SetVector(PixelId, data.Pixel);
                    properties.SetVector(LensId, data.Lens);
                    properties.SetVector(UpId, data.Up);
                    properties.SetVector(InkColorId, data.InkColor);
                    properties.SetVector(PaperColorId, data.PaperColor);
                    properties.SetVector(PlanetId, data.Planet);
                    properties.SetVector(EyeId, data.Eye);
                    properties.SetMatrix(FarLookId, data.FarLook);
                    properties.SetVector(FarEyeId, data.FarEye);
                    properties.SetVector(FarPlanetId, data.FarPlanet);
                    properties.SetFloat(FarMetresId, data.FarMetres);
                    SetIfThere(properties, FarSurfaceId, data.FarSurface);
                    properties.SetFloat(FarSeaId, data.FarSurface.IsValid() ? 1f : 0f);
                    properties.SetFloat(TakenId, data.Taken ? 1f : 0f);
                    SetIfThere(properties, SceneDepthId, data.SceneDepth);
                    SetIfThere(properties, AlbedoId, data.Albedo);
                    SetIfThere(properties, OpaqueDepthId, data.OpaqueDepth);
                    SetIfThere(properties, KerbalInkId, data.KerbalInk);
                    SetIfThere(properties, LeavesId, data.Leaves);
                    SetIfThere(properties, PictureId, data.Picture);
                    properties.SetVector(BlitScaleBiasId, new Vector4(1f, 1f, 0f, 0f));
                    context.cmd.DrawProcedural(Matrix4x4.identity, data.Material, data.Pass, MeshTopology.Triangles, 3, 1, properties);
                });
            }
        }

        private static void SetIfThere(MaterialPropertyBlock properties, int id, TextureHandle texture)
        {
            if (texture.IsValid()) properties.SetTexture(id, texture);
        }

        /// <summary>Lets go of the material.</summary>
        public void Destroy()
        {
            CoreUtils.Destroy(_material);
            CoreUtils.Destroy(_farMaterial);
        }
    }
}
