using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace MunShot.Cameras
{
    /// <summary>
    /// Just before Redux draws the engines' exhaust and smoke (its volumetric plumes, their smoke trails and the ground
    /// flow, all in one pass): a copy of the picture (<see cref="InkFrame.BeforeEffects"/>), for
    /// <see cref="EffectsCapture"/> to see what they changed. At the same time as Redux's pass, and registered just
    /// before it (<see cref="CelAndInk"/> sees to that: passes at one time go in the order registered). Taken even while
    /// Redux has no such pass: the game's own particle effects are added to the smoke against it too.
    /// </summary>
    internal sealed class BeforeEffectsCapture : InkCapture
    {
        public BeforeEffectsCapture(Shader shader) : base(shader) { }

        /// <summary>When Redux draws the exhaust and smoke for the camera: AfterRenderingTransparents, or later under the ocean's fog.</summary>
        public RenderPassEvent EffectsEvent { get; set; } = RenderPassEvent.AfterRenderingTransparents;

        public override RenderPassEvent Event => EffectsEvent;

        protected override void Capture(RenderGraph renderGraph, InkFrame ink, UniversalResourceData resources,
                                        TextureHandle color, TextureHandle depth)
        {
            ink.BeforeEffects = FilterPasses.CopyOf(renderGraph, color, "MunShot Ink Before Effects");
        }
    }

    /// <summary>
    /// Just after Redux draws the engines' exhaust and smoke (one later than <see cref="BeforeEffectsCapture"/>, as Redux
    /// registers its pass again whenever an engine starts): where drawing them changed the picture, with the game's own
    /// particle effects (<see cref="Particles"/>) added, as a cartoon smoke cloud (<see cref="InkFrame.Smoke"/>: red the
    /// whole cloud, green its particle smoke part); and while the planet's clouds show (<see cref="CloudsShown"/>), the game's
    /// volume clouds evened out, and how far off they are (<see cref="InkFrame.Clouds"/>,
    /// <see cref="InkFrame.CloudDistance"/>).
    /// </summary>
    internal sealed class EffectsCapture : InkCapture
    {
        private static readonly int PixelId = Shader.PropertyToID("_MunShotPixel");
        private static readonly int EffectsId = Shader.PropertyToID("_MunShotEffects");
        private static readonly int CloudsShownId = Shader.PropertyToID("_MunShotCloudsShown");
        private static readonly int MainTexId = Shader.PropertyToID("_MainTex");
        private static readonly int ParticlesId = Shader.PropertyToID("_MunShotParticles");
        private static readonly int PictureId = Shader.PropertyToID("_MunShotPicture");

        // How little the exhaust may change a pixel and still be counted, however dark the pixel: less is faint glow
        // (from the engines or re-entry), not exhaust to draw as a cartoon cloud
        private const float ExhaustMinChange = 0.03f;

        private sealed class WholeData
        {
            public TextureHandle Effects;
            public Vector4 Pixel;
            public float CloudsShown;
            public Material Material;
            public int Pass;
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

        private readonly int _changePass;
        private readonly int _particlesPass;
        private readonly int _puffsPass;
        private readonly int _smokePass;
        private readonly int _cloudsPass;
        private readonly int _cloudDistancePass;

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

        public EffectsCapture(Shader shader) : base(shader)
        {
            _changePass = FilterPasses.Find(Material, "Effects");
            _particlesPass = FilterPasses.Find(Material, "Particles");
            _puffsPass = FilterPasses.Find(Material, "ParticlePuffs");
            _smokePass = FilterPasses.Find(Material, "Smoke");
            _cloudsPass = FilterPasses.Find(Material, "Clouds");
            _cloudDistancePass = FilterPasses.Find(Material, "CloudDistance");
        }

        /// <summary>When Redux draws the exhaust and smoke for the camera (<see cref="BeforeEffectsCapture.EffectsEvent"/>).</summary>
        public RenderPassEvent EffectsEvent { get; set; } = RenderPassEvent.AfterRenderingTransparents;

        /// <summary>How much the game's volume clouds show (0 to 1; none taken at 0).</summary>
        public float CloudsShown { get; set; }

        /// <summary>
        /// The game's particle effects showing (explosions, debris smoke, RCS puffs), added to how much the exhaust and
        /// smoke cover each pixel: they're drawn among URP's see-through things, before the copy.
        /// </summary>
        public IReadOnlyList<ParticleSystemRenderer> Particles { get; set; }

        public override RenderPassEvent Event => EffectsEvent + 1;

        protected override void Capture(RenderGraph renderGraph, InkFrame ink, UniversalResourceData resources,
                                        TextureHandle color, TextureHandle depth)
        {
            if (ink.BeforeEffects.IsValid())
            {
                // Red: how much the exhaust covers each pixel; green: how much the game's particle smoke does
                TextureHandle change = DrawChange(renderGraph, color, ink.BeforeEffects, color, _changePass, "MunShot Ink Effects",
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
        }

        public override void Destroy()
        {
            foreach ((Mesh mesh, MaterialPropertyBlock _) in _particleMeshes) CoreUtils.Destroy(mesh);
            _particleMeshes.Clear();
            foreach (Material material in _puffMaterials.Values) CoreUtils.Destroy(material);
            _puffMaterials.Clear();
            if (_bakeCamera != null) CoreUtils.Destroy(_bakeCamera.gameObject);
            _bakeCamera = null;
            base.Destroy();
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
                passData.Material = Material;
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

        // The game's particle effects (drawn among URP's see-through things, before the copy the exhaust and smoke are
        // judged against), added to how much the exhaust and smoke cover each pixel (effects): drawn again on their own
        // over black, with their own materials so they look as the game draws them, where they're in front of what's
        // drawn (depth); then how much of each pixel of the picture (color) is theirs
        private void DrawParticles(RenderGraph renderGraph, TextureHandle color, TextureHandle effects, TextureHandle depth)
        {
            // Each one the camera draws (isVisible is any camera's, and drawing a mesh doesn't check the camera's
            // layers) as it sees it, baked into a mesh (Unity's way to draw particles again: drawing their renderer a
            // second time would upset their own drawing)
            _particlesDrawn.Clear();
            int layers = Camera.cullingMask;
            foreach (ParticleSystemRenderer particles in Particles)
                if (particles != null && particles.enabled && particles.isVisible && particles.sharedMaterial != null
                    && (layers & (1 << particles.gameObject.layer)) != 0)
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
                passData.Material = Material;
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
            material = CoreUtils.CreateEngineMaterial(Shader);
            material.SetTexture(MainTexId, texture);
            material.SetTextureScale(MainTexId, particleMaterial.GetTextureScale(MainTexId));
            material.SetTextureOffset(MainTexId, particleMaterial.GetTextureOffset(MainTexId));
            _puffMaterials[texture] = material;
            return material;
        }
    }
}
