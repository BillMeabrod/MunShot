using Redux.Rendering;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
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
    /// Both draw the engines' exhaust and smoke as a cartoon cloud (<see cref="InkFrame.Smoke"/>): no lines inside,
    /// an outline round it; over the picture, <see cref="CelEffects"/> has drawn it in flat tones; on paper, it's
    /// washed lightly with the ink. Both fade the lines behind the planet's clouds (the game's own cloud texture, as
    /// <see cref="InkFrame.Clouds"/>, evened out) and draw their own lines. Where lines crowd (a tree's leaves) is
    /// worked out first, from where the edges are.
    /// </summary>
    internal sealed class InkLines : IFilterHook
    {
        private static readonly int PixelId = Shader.PropertyToID("_MunShotPixel");
        private static readonly int LensId = Shader.PropertyToID("_MunShotLens");
        private static readonly int UpId = Shader.PropertyToID("_MunShotUp");
        private static readonly int TakenId = Shader.PropertyToID("_MunShotTaken");
        private static readonly int SceneDepthId = Shader.PropertyToID("_MunShotSceneDepth");
        private static readonly int AlbedoId = Shader.PropertyToID("_MunShotAlbedo");
        private static readonly int OpaqueDepthId = Shader.PropertyToID("_MunShotOpaqueDepth");
        private static readonly int KerbalInkId = Shader.PropertyToID("_MunShotKerbalInk");
        private static readonly int EdgesId = Shader.PropertyToID("_MunShotEdges");
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
        private static readonly int HasFarSurfaceId = Shader.PropertyToID("_MunShotHasFarSurface");
        private static readonly int PackedMapId = Shader.PropertyToID("_PackedMap");
        private static readonly int NormalMapId = Shader.PropertyToID("_NormalMap");
        private static readonly int PictureId = Shader.PropertyToID("_MunShotPicture");
        private static readonly int SmokeId = Shader.PropertyToID("_MunShotSmoke");
        private static readonly int HasSmokeId = Shader.PropertyToID("_MunShotHasSmoke");
        private static readonly int TerrainId = Shader.PropertyToID("_MunShotTerrain");
        private static readonly int HasTerrainId = Shader.PropertyToID("_MunShotHasTerrain");
        private static readonly int BodiesId = Shader.PropertyToID("_MunShotBodies");
        private static readonly int HasBodiesId = Shader.PropertyToID("_MunShotHasBodies");
        private static readonly int CloudsId = Shader.PropertyToID("_MunShotClouds");
        private static readonly int CloudDistanceId = Shader.PropertyToID("_MunShotCloudDistance");
        private static readonly int HasCloudsId = Shader.PropertyToID("_MunShotHasClouds");
        private static readonly int LineDetailId = Shader.PropertyToID("_MunShotLineDetail");
        private static readonly int DepthId = Shader.PropertyToID("_CameraDepthTexture");
        private static readonly int NormalsId = Shader.PropertyToID("_CameraNormalsTexture");

        private sealed class PassData
        {
            public TextureHandle Depth;
            public TextureHandle Normals;
            public TextureHandle SceneDepth;
            public TextureHandle Albedo;
            public TextureHandle OpaqueDepth;
            public TextureHandle KerbalInk;
            public TextureHandle Edges;
            public TextureHandle Leaves;
            public TextureHandle Picture;
            public TextureHandle Smoke;
            public TextureHandle Terrain;
            public TextureHandle Bodies;
            public TextureHandle Clouds;
            public TextureHandle CloudDistance;
            public float LineDetail;
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
            public bool FarSea;
        }

        private readonly Material _material;
        // The faraway planet's surface is drawn with this, the planet's own packed map set on it
        private readonly Material _farMaterial;
        private readonly bool _onPaper;
        private readonly int _outlinesPass;
        private readonly int _paperPass;
        private readonly int _edgesPass;
        private readonly int _leavesPass;
        private readonly int _farSurfacePass;

        /// <param name="onPaper">The lines alone on white (the Ink filter), rather than over the picture.</param>
        public InkLines(Shader shader, bool onPaper)
        {
            _material = CoreUtils.CreateEngineMaterial(shader);
            _farMaterial = CoreUtils.CreateEngineMaterial(shader);
            _onPaper = onPaper;
            _outlinesPass = FilterPasses.Find(_material, "Outlines");
            _paperPass = FilterPasses.Find(_material, "Paper");
            _edgesPass = FilterPasses.Find(_material, "Edges");
            _leavesPass = FilterPasses.Find(_material, "Leaves");
            _farSurfacePass = FilterPasses.Find(_material, "FarSurface");
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
        /// The faraway planet itself (its renderer in that view), whether it has a sea, and that view's camera's view and
        /// projection, as drawn into a texture: where its surface is seen, how smooth it is there and which way it leans
        /// is drawn, for its coasts (with a sea) and its slopes (crater walls, ridges).
        /// </summary>
        public Renderer FarBody { get; set; }
        public bool FarSea { get; set; }
        public Matrix4x4 FarViewProjection { get; set; } = Matrix4x4.identity;

        /// <summary>How much detail the lines show: 1 all of it, 0 only outlines, coasts and the planet's edge.</summary>
        public float LineDetail { get; set; } = 1f;

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

            var inputs = new PassData
            {
                Depth = depth,
                Normals = normals,
                Material = _material,
                Pixel = FilterPasses.PixelOf(renderGraph, color),
                Lens = Lens,
                Up = Up,
                InkColor = InkColor,
                PaperColor = PaperColor,
                Planet = Planet,
                Eye = Camera.transform.position,
                FarLook = FarLook,
                FarEye = FarEye,
                FarPlanet = FarPlanet,
                FarMetres = FarMetres,
                FarSea = FarSea,
                LineDetail = LineDetail
            };

            if (FarBody != null && FarPlanet.w > 0f) inputs.FarSurface = DrawFarSurface(renderGraph, color);

            InkFrame ink = frameData.Contains<InkFrame>() ? frameData.Get<InkFrame>() : null;
            if (ink != null)
            {
                inputs.Smoke = ink.Smoke;
                inputs.Clouds = ink.Clouds;
                inputs.CloudDistance = ink.CloudDistance;
                inputs.Terrain = ink.Terrain;
                inputs.Bodies = ink.Bodies;
            }

            // Where lines crowd (a tree's leaves): where the edges are, then where most round a pixel are
            // (each drawn before it's an input, so no pass reads what it draws)
            TextureHandle edges = Mask(renderGraph, color, "MunShot Ink Edges");
            Draw(renderGraph, "MunShot Ink Edges", _edgesPass, edges, inputs);
            inputs.Edges = edges;
            TextureHandle leaves = Mask(renderGraph, color, "MunShot Ink Leaves");
            Draw(renderGraph, "MunShot Ink Leaves", _leavesPass, leaves, inputs);
            inputs.Leaves = leaves;

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
                // The picture copied first, for the smoke's tone lines, as the lines are then drawn over it
                inputs.Picture = FilterPasses.CopyOf(renderGraph, color, "MunShot Cel Shading Picture");
                Draw(renderGraph, "MunShot Cel Shading Outlines", _outlinesPass, color, inputs);
                return;
            }

            // On paper, the finished picture is copied first, as it's then drawn over; with what was taken earlier in the
            // frame (without all of which there are just the lines and the black)
            inputs.Picture = FilterPasses.CopyOf(renderGraph, color, "MunShot Ink Picture");

            inputs.Taken = ink != null && ink.IsComplete;
            if (inputs.Taken)
            {
                inputs.SceneDepth = ink.SceneDepth;
                inputs.Albedo = ink.Albedo;
                inputs.OpaqueDepth = ink.OpaqueDepth;
                inputs.KerbalInk = ink.KerbalInk;
            }

            Draw(renderGraph, "MunShot Ink", _paperPass, color, inputs);
        }

        private sealed class FarSurfaceData
        {
            public Renderer Body;
            public Material Material;
            public int Pass;
        }

        // Where the faraway planet's surface is seen, how smooth it is there and which way it leans from the smooth sphere,
        // drawn from that view's camera with the planet's own mesh
        private TextureHandle DrawFarSurface(RenderGraph renderGraph, TextureHandle color)
        {
            Material surface = FarBody.sharedMaterial;
            if (surface == null) return TextureHandle.nullHandle;
            // Its packed map (only a planet with a sea has one) and its normal map, none standing for a smooth surface
            Texture packed = surface.HasTexture(PackedMapId) ? surface.GetTexture(PackedMapId) : null;
            _farMaterial.SetTexture(PackedMapId, packed != null ? packed : Texture2D.blackTexture);
            Texture normals = surface.HasTexture(NormalMapId) ? surface.GetTexture(NormalMapId) : null;
            _farMaterial.SetTexture(NormalMapId, normals != null ? normals : Texture2D.normalTexture);
            _farMaterial.SetTextureScale(NormalMapId, normals != null ? surface.GetTextureScale(NormalMapId) : Vector2.one);
            _farMaterial.SetTextureOffset(NormalMapId, normals != null ? surface.GetTextureOffset(NormalMapId) : Vector2.zero);
            _farMaterial.SetMatrix(FarViewProjectionId, FarViewProjection);
            _farMaterial.SetVector(FarEyeId, FarEye);

            TextureDesc desc = renderGraph.GetTextureDesc(color);
            desc.name = "MunShot Far Surface";
            desc.depthBufferBits = DepthBits.None;
            desc.format = GraphicsFormat.R8G8B8A8_UNorm;
            desc.clearBuffer = true;
            desc.clearColor = Color.clear;
            desc.msaaSamples = MSAASamples.None;
            TextureHandle mask = renderGraph.CreateTexture(desc);
            using (IRasterRenderGraphBuilder draw = renderGraph.AddRasterRenderPass("MunShot Far Surface", out FarSurfaceData passData))
            {
                passData.Body = FarBody;
                passData.Material = _farMaterial;
                passData.Pass = _farSurfacePass;
                draw.SetRenderAttachment(mask, 0, AccessFlags.Write);
                draw.SetRenderFunc((FarSurfaceData data, RasterGraphContext context) =>
                {
                    context.cmd.DrawRenderer(data.Body, data.Material, 0, data.Pass);
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
        private void Draw(RenderGraph renderGraph, string name, int pass, TextureHandle target, PassData inputs)
        {
            using (IRasterRenderGraphBuilder draw = renderGraph.AddRasterRenderPass(name, out PassData passData))
            {
                passData.Depth = inputs.Depth;
                passData.Normals = inputs.Normals;
                passData.SceneDepth = inputs.SceneDepth;
                passData.Albedo = inputs.Albedo;
                passData.OpaqueDepth = inputs.OpaqueDepth;
                passData.KerbalInk = inputs.KerbalInk;
                passData.Edges = inputs.Edges;
                passData.Leaves = inputs.Leaves;
                passData.Picture = inputs.Picture;
                passData.Smoke = inputs.Smoke;
                passData.Terrain = inputs.Terrain;
                passData.Bodies = inputs.Bodies;
                passData.Clouds = inputs.Clouds;
                passData.CloudDistance = inputs.CloudDistance;
                passData.LineDetail = inputs.LineDetail;
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
                passData.FarSea = inputs.FarSea;
                UseIfThere(draw, inputs.Depth);
                UseIfThere(draw, inputs.Normals);
                UseIfThere(draw, inputs.SceneDepth);
                UseIfThere(draw, inputs.Albedo);
                UseIfThere(draw, inputs.OpaqueDepth);
                UseIfThere(draw, inputs.KerbalInk);
                UseIfThere(draw, inputs.Edges);
                UseIfThere(draw, inputs.Leaves);
                UseIfThere(draw, inputs.Picture);
                UseIfThere(draw, inputs.FarSurface);
                UseIfThere(draw, inputs.Smoke);
                UseIfThere(draw, inputs.Terrain);
                UseIfThere(draw, inputs.Bodies);
                UseIfThere(draw, inputs.Clouds);
                UseIfThere(draw, inputs.CloudDistance);
                // For the game's water depth and its clouds, which it sets as global textures
                draw.UseAllGlobalTextures(true);
                // The outlines are laid over the picture (blended by their coverage); every other pass covers all of
                // its target (the paper reads the picture's copy, not the picture), so what's there needn't be loaded
                draw.SetRenderAttachment(target, 0, pass == _outlinesPass ? AccessFlags.ReadWrite : AccessFlags.WriteAll);
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
                    properties.SetFloat(HasFarSurfaceId, data.FarSurface.IsValid() ? 1f : 0f);
                    properties.SetFloat(FarSeaId, data.FarSurface.IsValid() && data.FarSea ? 1f : 0f);
                    properties.SetFloat(TakenId, data.Taken ? 1f : 0f);
                    SetIfThere(properties, SceneDepthId, data.SceneDepth);
                    SetIfThere(properties, AlbedoId, data.Albedo);
                    SetIfThere(properties, OpaqueDepthId, data.OpaqueDepth);
                    SetIfThere(properties, KerbalInkId, data.KerbalInk);
                    SetIfThere(properties, EdgesId, data.Edges);
                    SetIfThere(properties, LeavesId, data.Leaves);
                    SetIfThere(properties, PictureId, data.Picture);
                    SetIfThere(properties, SmokeId, data.Smoke);
                    properties.SetFloat(HasSmokeId, data.Smoke.IsValid() ? 1f : 0f);
                    SetIfThere(properties, TerrainId, data.Terrain);
                    properties.SetFloat(HasTerrainId, data.Terrain.IsValid() ? 1f : 0f);
                    SetIfThere(properties, BodiesId, data.Bodies);
                    properties.SetFloat(HasBodiesId, data.Bodies.IsValid() ? 1f : 0f);
                    SetIfThere(properties, CloudsId, data.Clouds);
                    SetIfThere(properties, CloudDistanceId, data.CloudDistance);
                    properties.SetFloat(HasCloudsId, data.Clouds.IsValid() && data.CloudDistance.IsValid() ? 1f : 0f);
                    properties.SetFloat(LineDetailId, data.LineDetail);
                    properties.SetVector(FilterPasses.BlitScaleBiasId, FilterPasses.WholePicture);
                    context.cmd.DrawProcedural(Matrix4x4.identity, data.Material, data.Pass, MeshTopology.Triangles, 3, 1, properties);
                });
            }
        }

        private static void UseIfThere(IRasterRenderGraphBuilder draw, TextureHandle texture)
        {
            if (texture.IsValid()) draw.UseTexture(texture);
        }

        private static void SetIfThere(MaterialPropertyBlock properties, int id, TextureHandle texture)
        {
            if (texture.IsValid()) properties.SetTexture(id, texture);
        }

        /// <summary>Lets go of the materials.</summary>
        public void Destroy()
        {
            CoreUtils.Destroy(_material);
            CoreUtils.Destroy(_farMaterial);
        }
    }
}
