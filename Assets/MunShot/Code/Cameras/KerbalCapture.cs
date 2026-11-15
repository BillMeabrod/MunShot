using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace MunShot.Cameras
{
    /// <summary>
    /// For the Ink filter, before the see-through things: the depth so far (<see cref="InkFrame.OpaqueDepth"/>), and
    /// the kerbals' own ink (<see cref="InkFrame.KerbalInk"/>, from <see cref="KerbalParts"/>): their pupils, drawn from
    /// their eyes' own texture, and dark hair, so neither light nor the visor in front changes them.
    /// </summary>
    internal sealed class KerbalCapture : InkCapture
    {
        private static readonly int MainTexId = Shader.PropertyToID("_MainTex");
        private static readonly int SolidId = Shader.PropertyToID("_MunShotSolid");
        private static readonly int OpaqueDepthId = Shader.PropertyToID("_MunShotOpaqueDepth");

        private sealed class KerbalInkData
        {
            public TextureHandle OpaqueDepth;
            public List<(Renderer part, Material material)> Parts;
            public int Pass;
        }

        private readonly int _keepDepthPass;
        private readonly int _kerbalInkPass;

        // The pupils' material for each eye texture (kerbals share a few), the dark hair's, and the parts drawn this
        // frame
        private readonly Dictionary<Texture, Material> _pupilMaterials = new Dictionary<Texture, Material>();
        private Material _hairMaterial;
        private readonly List<(Renderer, Material)> _drawn = new List<(Renderer, Material)>();

        public KerbalCapture(Shader shader) : base(shader)
        {
            _keepDepthPass = FilterPasses.Find(Material, "KeepDepth");
            _kerbalInkPass = FilterPasses.Find(Material, "KerbalInk");
        }

        /// <summary>The kerbals' eyeballs, whose pupils are drawn, and dark hair, drawn whole.</summary>
        public IReadOnlyList<(Renderer part, bool hair)> KerbalParts { get; set; }

        public override RenderPassEvent Event => RenderPassEvent.BeforeRenderingTransparents;

        protected override void Capture(RenderGraph renderGraph, InkFrame ink, UniversalResourceData resources,
                                        TextureHandle color, TextureHandle depth)
        {
            ink.OpaqueDepth = KeepDepth(renderGraph, color, depth, _keepDepthPass, "MunShot Ink Opaque Depth");
            ink.KerbalInk = DrawKerbalInk(renderGraph, color, ink.OpaqueDepth);
        }

        // The pupils and dark hair, where they're seen (not behind an eyelid, a hand or the helmet), and nothing elsewhere
        private TextureHandle DrawKerbalInk(RenderGraph renderGraph, TextureHandle color, TextureHandle opaqueDepth)
        {
            TextureHandle kerbalInk = ClearedMask(renderGraph, color, "MunShot Ink Kerbals");

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

        // The pupils' material for an eye's: MunShot's shader, with the eye's texture
        private Material PupilMaterial(Material eyeMaterial)
        {
            if (eyeMaterial == null || !eyeMaterial.HasTexture(MainTexId)) return null;
            Texture texture = eyeMaterial.GetTexture(MainTexId);
            if (texture == null) return null;
            if (_pupilMaterials.TryGetValue(texture, out Material material) && material != null) return material;
            material = CoreUtils.CreateEngineMaterial(Shader);
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
            _hairMaterial = CoreUtils.CreateEngineMaterial(Shader);
            _hairMaterial.SetFloat(SolidId, 1f);
            return _hairMaterial;
        }

        public override void Destroy()
        {
            base.Destroy();
            foreach (Material material in _pupilMaterials.Values) CoreUtils.Destroy(material);
            _pupilMaterials.Clear();
            CoreUtils.Destroy(_hairMaterial);
            _hairMaterial = null;
        }
    }
}
