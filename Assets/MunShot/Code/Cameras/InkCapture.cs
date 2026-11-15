using System.Collections.Generic;
using Redux.Rendering;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace MunShot.Cameras
{
    /// <summary>
    /// What <see cref="InkLines"/> takes from earlier in the frame (Ink's paper all of it, Cel shaded's outlines the
    /// ground's depth and colours; both where the terrain, distant planets, exhaust and smoke are), and <see cref="CelEffects"/> too, kept in the
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

        /// <summary>
        /// Where Redux's faraway view shows a distant planet or moon (1, 0 elsewhere), so Cel shaded puts them in flat
        /// tones and both filters outline them.
        /// </summary>
        public TextureHandle Bodies;

        /// <summary>The picture just before Redux draws the engines' exhaust and smoke over it.</summary>
        public TextureHandle BeforeEffects;

        /// <summary>
        /// Where the engines' exhaust and smoke are drawn as a cartoon cloud (1 inside, 0 outside): where drawing them
        /// changed the picture enough, evened out so its edge is smooth, so both filters can draw them so, the lines
        /// behind it hidden (<see cref="InkLines"/>, <see cref="CelEffects"/>).
        /// </summary>
        public TextureHandle Smoke;

        /// <summary>
        /// The game's volume clouds round each pixel, evened out as the game draws them grainy: how much they cover it
        /// and show, their own light, and how that light changes across it (Cel shaded's tones, both filters' lines; for
        /// the paper, the game has cleared its own by then).
        /// </summary>
        public TextureHandle Clouds;

        /// <summary>
        /// How far off the game's volume clouds are at each pixel (metres, 0 where there are none: the game's own cloud
        /// depth), so they fade and tone only what's behind them.
        /// </summary>
        public TextureHandle CloudDistance;

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
            Bodies = TextureHandle.nullHandle;
            BeforeEffects = TextureHandle.nullHandle;
            Smoke = TextureHandle.nullHandle;
            Clouds = TextureHandle.nullHandle;
            CloudDistance = TextureHandle.nullHandle;
        }
    }

    /// <summary>
    /// Takes what the Ink filter needs from the frame as it's drawn, into <see cref="InkFrame"/> (MunShot's CelShading
    /// shader). A Redux camera render hook, which <see cref="PhotoLook"/> registers once for each <see cref="Stage"/>
    /// (all but Opaque for Cel shaded too):
    /// - <see cref="Stage.BeforeTerrain"/>, after URP's G-buffer pass and before Redux draws the planet's terrain into
    ///   it (at 221): the surfaces' own colours so far, so <see cref="Stage.Scene"/> can tell where the terrain went.
    /// - <see cref="Stage.Scene"/>, right after the deferred lighting: the depth so far and the surfaces' own colours,
    ///   so the paper can tell what the deferred pass drew (the ground, buildings, trees, parts) from what's drawn after;
    ///   and where the faraway view shows distant planets and moons (<see cref="FarBodies"/>).
    /// - <see cref="Stage.Opaque"/>, before the see-through things: the depth so far, and the kerbals' own ink
    ///   (<see cref="KerbalParts"/>): their pupils, drawn from their eyes' own texture, and dark hair, so neither light
    ///   nor the visor in front changes them.
    /// - <see cref="Stage.BeforeEffects"/> and <see cref="Stage.Effects"/>, just before and just after Redux draws the
    ///   engines' exhaust and smoke (its volumetric plumes, their smoke trails and the ground flow, all in one pass at
    ///   AfterRenderingTransparents): a copy of the picture, then where drawing them changed it (the cartoon smoke
    ///   cloud); and while the clouds show (<see cref="CloudsShown"/>), the game's volume clouds evened out, and how far
    ///   off they are.
    /// </summary>
    internal sealed class InkCapture : IFilterHook
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
        private static readonly int MainTexId = Shader.PropertyToID("_MainTex");
        private static readonly int ParticlesId = Shader.PropertyToID("_MunShotParticles");
        private static readonly int PictureId = Shader.PropertyToID("_MunShotPicture");
        private static readonly int SolidId = Shader.PropertyToID("_MunShotSolid");
        private static readonly int OpaqueDepthId = Shader.PropertyToID("_MunShotOpaqueDepth");
        private static readonly int BeforeEffectsId = Shader.PropertyToID("_MunShotBeforeEffects");
        private static readonly int AfterEffectsId = Shader.PropertyToID("_MunShotAfterEffects");
        private static readonly int MinChangeId = Shader.PropertyToID("_MunShotMinChange");
        private static readonly int FarViewProjectionId = Shader.PropertyToID("_MunShotFarViewProjection");
        private static readonly int BodyShownId = Shader.PropertyToID("_MunShotBodyShown");
        private static readonly int PixelId = Shader.PropertyToID("_MunShotPixel");
        private static readonly int EffectsId = Shader.PropertyToID("_MunShotEffects");
        private static readonly int CloudsShownId = Shader.PropertyToID("_MunShotCloudsShown");

        // URP's G-buffer slot for the surface's colour
        private const int AlbedoSlot = 0;

        private sealed class DepthData
        {
            public TextureHandle Depth;
            public Material Material;
            public int Pass;
        }

        private sealed class EffectsData
        {
            public TextureHandle Before;
            public TextureHandle After;
            public float MinChange;
            public Material Material;
            public int Pass;
        }

        private sealed class WholeData
        {
            public TextureHandle Effects;
            public Vector4 Pixel;
            public float CloudsShown;
            public Material Material;
            public int Pass;
        }

        private sealed class KerbalInkData
        {
            public TextureHandle OpaqueDepth;
            public List<(Renderer part, Material material)> Parts;
            public int Pass;
        }

        private readonly Shader _shader;
        private readonly Material _material;
        private readonly Stage _stage;
        private readonly int _keepDepthPass;
        private readonly int _kerbalInkPass;
        private readonly int _effectsPass;
        private readonly int _bodiesPass;
        private readonly int _particlesPass;
        private readonly int _puffsPass;
        private readonly int _smokePass;
        private readonly int _cloudsPass;
        private readonly int _cloudDistancePass;

        // The pupils' material for each eye texture (kerbals share a few), the dark hair's, and the parts drawn this
        // frame
        private readonly Dictionary<Texture, Material> _pupilMaterials = new Dictionary<Texture, Material>();
        // The particle effects drawn this frame, baked into meshes (one kept for each, reused every frame) with their
        // own materials and settings
        private readonly List<(Mesh, Matrix4x4, Material, MaterialPropertyBlock)> _particlesDrawn = new List<(Mesh, Matrix4x4, Material, MaterialPropertyBlock)>();
        private readonly List<(Mesh mesh, MaterialPropertyBlock properties)> _particleMeshes = new List<(Mesh, MaterialPropertyBlock)>();
        // The same meshes again with MunShot's puff material for each particle texture
        private readonly List<(Mesh, Matrix4x4, Material, MaterialPropertyBlock)> _puffsDrawn = new List<(Mesh, Matrix4x4, Material, MaterialPropertyBlock)>();
        private readonly Dictionary<Texture, Material> _puffMaterials = new Dictionary<Texture, Material>();
        // A camera never drawn with, put where Camera is as seen from a particle system's own space, to bake that
        // system's particles facing Camera
        private Camera _bakeCamera;
        private Material _hairMaterial;

        // The distant planets and moons are drawn with the first, the body the vessel orbits (hiding what's behind it)
        // with the second
        private Material _bodyMaterial;
        private Material _orbitedMaterial;
        private readonly List<(Renderer, Material)> _drawn = new List<(Renderer, Material)>();

        public InkCapture(Shader shader, Stage stage)
        {
            _shader = shader;
            _material = CoreUtils.CreateEngineMaterial(shader);
            _stage = stage;
            _keepDepthPass = FilterPasses.Find(_material, "KeepDepth");
            _kerbalInkPass = FilterPasses.Find(_material, "KerbalInk");
            _effectsPass = FilterPasses.Find(_material, "Effects");
            _bodiesPass = FilterPasses.Find(_material, "Bodies");
            _particlesPass = FilterPasses.Find(_material, "Particles");
            _puffsPass = FilterPasses.Find(_material, "ParticlePuffs");
            _smokePass = FilterPasses.Find(_material, "Smoke");
            _cloudsPass = FilterPasses.Find(_material, "Clouds");
            _cloudDistancePass = FilterPasses.Find(_material, "CloudDistance");
        }

        /// <summary>The camera drawn from, and whether a filter that needs this is on; nothing is taken otherwise.</summary>
        public Camera Camera { get; set; }
        public bool IsOn { get; set; }

        /// <summary>
        /// The kerbals' eyeballs, whose pupils are drawn, and dark hair, drawn whole (<see cref="Stage.Opaque"/> only).
        /// </summary>
        public IReadOnlyList<(Renderer part, bool hair)> KerbalParts { get; set; }

        /// <summary>
        /// The planets and moons Redux's faraway view draws, and whether each is the body the vessel orbits, with that
        /// view's camera's view and projection (as drawn into a texture), for <see cref="InkFrame.Bodies"/>
        /// (<see cref="Stage.Scene"/> only; none while it's empty).
        /// </summary>
        public IReadOnlyList<(Renderer body, bool orbited)> FarBodies { get; set; }
        public Matrix4x4 FarViewProjection { get; set; } = Matrix4x4.identity;

        /// <summary>
        /// When Redux draws the engines' exhaust and smoke for the camera, for <see cref="Stage.BeforeEffects"/> and
        /// <see cref="Stage.Effects"/>: AfterRenderingTransparents, or later under the ocean's fog.
        /// </summary>
        public RenderPassEvent EffectsEvent { get; set; } = RenderPassEvent.AfterRenderingTransparents;

        /// <summary>How much the game's volume clouds show (0 to 1; <see cref="Stage.Effects"/> only, none taken at 0).</summary>
        public float CloudsShown { get; set; }

        /// <summary>
        /// The game's particle effects showing (explosions, debris smoke, RCS puffs), added to how much the exhaust and
        /// smoke cover each pixel: they're drawn among URP's see-through things, before Redux's (<see cref="Stage.Effects"/>
        /// only).
        /// </summary>
        public IReadOnlyList<ParticleSystemRenderer> Particles { get; set; }

        public RenderPassEvent Event
        {
            get
            {
                switch (_stage)
                {
                    case Stage.BeforeTerrain: return RenderPassEvent.AfterRenderingGbuffer;
                    case Stage.Scene: return RenderPassEvent.AfterRenderingDeferredLights;
                    case Stage.Opaque: return RenderPassEvent.BeforeRenderingTransparents;
                    // Either side of Redux's exhaust and smoke (EffectsEvent). Before it at the same time (any earlier, at
                    // AfterRenderingTransparents, is before URP's own see-through things, the atmosphere and the sea
                    // among them): passes at one time go in the order registered, so PhotoLook moves Redux's after
                    // this. After it, one later, as Redux registers its pass again whenever an engine starts
                    case Stage.BeforeEffects: return EffectsEvent;
                    default: return EffectsEvent + 1;
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
                    ink.BeforeTerrain = FilterPasses.CopyOf(renderGraph, gBuffer[AlbedoSlot], "MunShot Ink Before Terrain");
                    return;
                }
                ink.Albedo = FilterPasses.CopyOf(renderGraph, gBuffer[AlbedoSlot], "MunShot Ink Albedo");
                ink.SceneDepth = KeepDepth(renderGraph, color, depth, "MunShot Ink Scene Depth");
                if (FarBodies != null && FarBodies.Count > 0) ink.Bodies = DrawBodies(renderGraph, color);
                if (ink.BeforeTerrain.IsValid())
                    ink.Terrain = DrawChange(renderGraph, color, ink.BeforeTerrain, ink.Albedo, "MunShot Ink Terrain",
                                             GraphicsFormat.R8_UNorm, 0f);
                return;
            }

            if (_stage == Stage.BeforeEffects)
            {
                ink.BeforeEffects = FilterPasses.CopyOf(renderGraph, color, "MunShot Ink Before Effects");
                return;
            }

            if (_stage == Stage.Effects)
            {
                if (ink.BeforeEffects.IsValid())
                {
                    // Red: how much the exhaust covers each pixel; green: how much the game's particle smoke does
                    TextureHandle change = DrawChange(renderGraph, color, ink.BeforeEffects, color, "MunShot Ink Effects",
                                                      GraphicsFormat.R8G8_UNorm, ExhaustMinChange);
                    if (Particles != null && Particles.Count > 0) DrawParticles(renderGraph, color, change, depth);
                    ink.Smoke = DrawWhole(renderGraph, color, "MunShot Ink Smoke", GraphicsFormat.R8G8_UNorm, _smokePass, change);
                }
                if (CloudsShown > 0f)
                {
                    ink.Clouds = DrawWhole(renderGraph, color, "MunShot Ink Clouds", GraphicsFormat.R16G16B16A16_SFloat, _cloudsPass,
                                           TextureHandle.nullHandle);
                    ink.CloudDistance = DrawWhole(renderGraph, color, "MunShot Ink Cloud Distance", GraphicsFormat.R32_SFloat,
                                                  _cloudDistancePass, TextureHandle.nullHandle);
                }
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
                passData.Pass = _keepDepthPass;
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

        // How little the exhaust may change a pixel and still be counted, however dark the pixel: less is faint glow
        // (from the engines or re-entry), not exhaust to draw as a cartoon cloud
        private const float ExhaustMinChange = 0.03f;

        // How much what was drawn between before and after (the exhaust on the picture, or the terrain among the
        // surfaces' colours) changed each pixel, the size of the picture in the given format; a change under minChange
        // isn't counted
        private TextureHandle DrawChange(RenderGraph renderGraph, TextureHandle color, TextureHandle before, TextureHandle after, string name,
                                         GraphicsFormat format, float minChange)
        {
            TextureHandle effects = renderGraph.CreateTexture(Like(renderGraph, color, name, format));
            using (IRasterRenderGraphBuilder draw = renderGraph.AddRasterRenderPass(name, out EffectsData passData))
            {
                passData.Before = before;
                passData.After = after;
                passData.MinChange = minChange;
                passData.Material = _material;
                passData.Pass = _effectsPass;
                draw.UseTexture(before);
                draw.UseTexture(after);
                draw.SetRenderAttachment(effects, 0, AccessFlags.WriteAll);
                draw.SetRenderFunc((EffectsData data, RasterGraphContext context) =>
                {
                    MaterialPropertyBlock properties = context.renderGraphPool.GetTempMaterialPropertyBlock();
                    properties.SetTexture(BeforeEffectsId, data.Before);
                    properties.SetTexture(AfterEffectsId, data.After);
                    properties.SetFloat(MinChangeId, data.MinChange);
                    properties.SetVector(FilterPasses.BlitScaleBiasId, FilterPasses.WholePicture);
                    context.cmd.DrawProcedural(Matrix4x4.identity, data.Material, data.Pass, MeshTopology.Triangles, 3, 1, properties);
                });
            }
            return effects;
        }

        // One of the shader's passes over a texture the size of the picture, in the given format: the smoke cloud from
        // how much the exhaust and smoke changed each pixel (effects), or the game's clouds evened out or how far off
        private TextureHandle DrawWhole(RenderGraph renderGraph, TextureHandle color, string name, GraphicsFormat format, int pass,
                                        TextureHandle effects)
        {
            TextureHandle target = renderGraph.CreateTexture(Like(renderGraph, color, name, format));
            using (IRasterRenderGraphBuilder draw = renderGraph.AddRasterRenderPass(name, out WholeData passData))
            {
                passData.Effects = effects;
                passData.Pixel = FilterPasses.PixelOf(renderGraph, color);
                passData.CloudsShown = CloudsShown;
                passData.Material = _material;
                passData.Pass = pass;
                if (effects.IsValid()) draw.UseTexture(effects);
                // The game sets its clouds as a global texture
                draw.UseAllGlobalTextures(true);
                draw.SetRenderAttachment(target, 0, AccessFlags.WriteAll);
                draw.SetRenderFunc((WholeData data, RasterGraphContext context) =>
                {
                    MaterialPropertyBlock properties = context.renderGraphPool.GetTempMaterialPropertyBlock();
                    if (data.Effects.IsValid()) properties.SetTexture(EffectsId, data.Effects);
                    properties.SetVector(PixelId, data.Pixel);
                    properties.SetFloat(CloudsShownId, data.CloudsShown);
                    properties.SetVector(FilterPasses.BlitScaleBiasId, FilterPasses.WholePicture);
                    context.cmd.DrawProcedural(Matrix4x4.identity, data.Material, data.Pass, MeshTopology.Triangles, 3, 1, properties);
                });
            }
            return target;
        }

        private sealed class BodiesData
        {
            public IReadOnlyList<(Renderer body, bool orbited)> Bodies;
            public Material Shown;
            public Material Orbited;
            public int Pass;
        }

        // Where the faraway view shows a distant planet or moon, drawn from that view's camera with their own meshes,
        // nearest first (the body the vessel orbits hides what's behind it but isn't marked)
        private TextureHandle DrawBodies(RenderGraph renderGraph, TextureHandle color)
        {
            if (_bodyMaterial == null)
            {
                _bodyMaterial = CoreUtils.CreateEngineMaterial(_shader);
                _bodyMaterial.SetFloat(BodyShownId, 1f);
                _orbitedMaterial = CoreUtils.CreateEngineMaterial(_shader);
                _orbitedMaterial.SetFloat(BodyShownId, 0f);
            }
            _bodyMaterial.SetMatrix(FarViewProjectionId, FarViewProjection);
            _orbitedMaterial.SetMatrix(FarViewProjectionId, FarViewProjection);

            TextureDesc maskDesc = Like(renderGraph, color, "MunShot Ink Bodies", GraphicsFormat.R8_UNorm);
            maskDesc.clearBuffer = true;
            maskDesc.clearColor = Color.clear;
            TextureHandle mask = renderGraph.CreateTexture(maskDesc);
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
                passData.Pass = _kerbalInkPass;
                draw.UseTexture(opaqueDepth);
                draw.SetRenderAttachment(kerbalInk, 0, AccessFlags.Write);
                draw.SetRenderFunc((KerbalInkData data, RasterGraphContext context) =>
                {
                    foreach ((Renderer part, Material material) in data.Parts)
                    {
                        material.SetTexture(OpaqueDepthId, data.OpaqueDepth);
                        context.cmd.DrawRenderer(part, material, 0, data.Pass);
                    }
                });
            }
            return kerbalInk;
        }

        private sealed class ParticlesData
        {
            public List<(Mesh mesh, Matrix4x4 place, Material material, MaterialPropertyBlock properties)> Parts;
            public int Pass;
        }

        private sealed class ParticleShareData
        {
            public TextureHandle Particles;
            public TextureHandle Picture;
            public Material Material;
            public int Pass;
        }

        // The game's particle effects (drawn among URP's see-through things, before the copy the exhaust and smoke are
        // judged against), added to how much the exhaust and smoke cover each pixel (effects): drawn again on their own
        // over black, with their own materials so they look as the game draws them, where they're in front of what's
        // drawn (depth); then how much of each pixel of the picture (color) is theirs
        private void DrawParticles(RenderGraph renderGraph, TextureHandle color, TextureHandle effects, TextureHandle depth)
        {
            // Each one as the camera sees it, baked into a mesh (Unity's way to draw particles again: drawing their
            // renderer a second time would upset their own drawing)
            _particlesDrawn.Clear();
            foreach (ParticleSystemRenderer particles in Particles)
                if (particles != null && particles.enabled && particles.isVisible && particles.sharedMaterial != null)
                {
                    if (_particleMeshes.Count == _particlesDrawn.Count)
                        _particleMeshes.Add((new Mesh { name = "MunShot particles" }, new MaterialPropertyBlock()));
                    (Mesh mesh, MaterialPropertyBlock properties) = _particleMeshes[_particlesDrawn.Count];
                    // Particles kept in their system's own space (launch smoke) are baked there and placed with it
                    // (baking them into the world puts them in the wrong place), facing the camera as seen from there
                    // (else, once placed, they're turned away from it)
                    bool local = particles.TryGetComponent(out ParticleSystem system)
                                 && system.main.simulationSpace == ParticleSystemSimulationSpace.Local;
                    if (local) particles.BakeMesh(mesh, BakeCameraFor(particles.transform), 0);
                    else particles.BakeMesh(mesh, Camera, ParticleSystemBakeMeshOptions.Default);
                    particles.GetPropertyBlock(properties);
                    _particlesDrawn.Add((mesh, local ? particles.transform.localToWorldMatrix : Matrix4x4.identity,
                                         particles.sharedMaterial, properties));
                }
            if (_particlesDrawn.Count == 0) return;

            TextureDesc alone = renderGraph.GetTextureDesc(color);
            alone.name = "MunShot Ink Particles Alone";
            alone.depthBufferBits = DepthBits.None;
            alone.clearBuffer = true;
            alone.clearColor = Color.clear;
            alone.msaaSamples = MSAASamples.None;
            TextureHandle particlesAlone = renderGraph.CreateTexture(alone);
            using (IRasterRenderGraphBuilder draw = renderGraph.AddRasterRenderPass("MunShot Ink Particles Alone", out ParticlesData passData))
            {
                passData.Parts = _particlesDrawn;
                draw.SetRenderAttachment(particlesAlone, 0, AccessFlags.Write);
                draw.SetRenderAttachmentDepth(depth, AccessFlags.Read);
                // Their materials may read the camera's depth (soft particles), which URP sets as a global texture
                draw.UseAllGlobalTextures(true);
                draw.SetRenderFunc((ParticlesData data, RasterGraphContext context) =>
                {
                    // Where they are in the world; their materials' first pass, a particle shader's own, as drawn
                    // among the see-through things
                    foreach ((Mesh mesh, Matrix4x4 place, Material material, MaterialPropertyBlock properties) in data.Parts)
                        context.cmd.DrawMesh(mesh, place, material, 0, 0, properties);
                });
            }

            using (IRasterRenderGraphBuilder draw = renderGraph.AddRasterRenderPass("MunShot Ink Particles", out ParticleShareData passData))
            {
                passData.Particles = particlesAlone;
                passData.Picture = color;
                passData.Material = _material;
                passData.Pass = _particlesPass;
                draw.UseTexture(particlesAlone);
                draw.UseTexture(color);
                draw.SetRenderAttachment(effects, 0, AccessFlags.ReadWrite);
                draw.SetRenderFunc((ParticleShareData data, RasterGraphContext context) =>
                {
                    MaterialPropertyBlock properties = context.renderGraphPool.GetTempMaterialPropertyBlock();
                    properties.SetTexture(ParticlesId, data.Particles);
                    properties.SetTexture(PictureId, data.Picture);
                    properties.SetVector(FilterPasses.BlitScaleBiasId, FilterPasses.WholePicture);
                    context.cmd.DrawProcedural(Matrix4x4.identity, data.Material, data.Pass, MeshTopology.Triangles, 3, 1, properties);
                });
            }

            // And their see-through puffs (launch smoke): what the redraw above can miss, as their own shader may want
            // more of each particle than a baked mesh keeps
            _puffsDrawn.Clear();
            for (int i = 0; i < _particlesDrawn.Count; i++)
            {
                (Mesh mesh, Matrix4x4 place, Material material, MaterialPropertyBlock _) = _particlesDrawn[i];
                Material puffs = PuffMaterial(material);
                if (puffs != null) _puffsDrawn.Add((mesh, place, puffs, null));
            }
            if (_puffsDrawn.Count == 0) return;
            using (IRasterRenderGraphBuilder draw = renderGraph.AddRasterRenderPass("MunShot Ink Particle Puffs", out ParticlesData passData))
            {
                passData.Parts = _puffsDrawn;
                passData.Pass = _puffsPass;
                draw.SetRenderAttachment(effects, 0, AccessFlags.ReadWrite);
                draw.SetRenderAttachmentDepth(depth, AccessFlags.Read);
                draw.SetRenderFunc((ParticlesData data, RasterGraphContext context) =>
                {
                    foreach ((Mesh mesh, Matrix4x4 place, Material material, MaterialPropertyBlock _) in data.Parts)
                        context.cmd.DrawMesh(mesh, place, material, 0, data.Pass);
                });
            }
        }

        // _bakeCamera, put where Camera is as seen from space's own space
        private Camera BakeCameraFor(Transform space)
        {
            if (_bakeCamera == null)
            {
                var holder = new GameObject("MunShot particle bake camera") { hideFlags = HideFlags.HideAndDontSave };
                _bakeCamera = holder.AddComponent<Camera>();
                _bakeCamera.enabled = false;
            }
            Transform camera = Camera.transform;
            _bakeCamera.transform.SetPositionAndRotation(space.InverseTransformPoint(camera.position),
                                                         Quaternion.Inverse(space.rotation) * camera.rotation);
            return _bakeCamera;
        }

        // A puff's material for a particle's: MunShot's shader, with the particle's texture (none without one)
        private Material PuffMaterial(Material particleMaterial)
        {
            Texture texture = particleMaterial.HasTexture(MainTexId) ? particleMaterial.GetTexture(MainTexId) : null;
            if (texture == null) return null;
            if (_puffMaterials.TryGetValue(texture, out Material material) && material != null) return material;
            material = CoreUtils.CreateEngineMaterial(_shader);
            material.SetTexture(MainTexId, texture);
            material.SetTextureScale(MainTexId, particleMaterial.GetTextureScale(MainTexId));
            material.SetTextureOffset(MainTexId, particleMaterial.GetTextureOffset(MainTexId));
            _puffMaterials[texture] = material;
            return material;
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
            foreach ((Mesh mesh, MaterialPropertyBlock _) in _particleMeshes) CoreUtils.Destroy(mesh);
            _particleMeshes.Clear();
            foreach (Material material in _puffMaterials.Values) CoreUtils.Destroy(material);
            _puffMaterials.Clear();
            if (_bakeCamera != null) CoreUtils.Destroy(_bakeCamera.gameObject);
            _bakeCamera = null;
            CoreUtils.Destroy(_bodyMaterial);
            CoreUtils.Destroy(_orbitedMaterial);
            _bodyMaterial = null;
            _orbitedMaterial = null;
        }
    }
}
