using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.RenderGraphModule.Util;

namespace MunShot.Cameras
{
    /// <summary>
    /// What Cel shaded's and Ink's render hooks (<see cref="CelShading"/>, <see cref="CelEffects"/>,
    /// <see cref="InkCapture"/>, <see cref="InkLines"/>) share in drawing with MunShot's CelShading shader: its passes,
    /// found by name; the size of the pixel its lines and evening-out are measured in; a copy of a texture; and the
    /// blit's whole picture.
    /// </summary>
    internal static class FilterPasses
    {
        // Sizes are in pixels of a picture 1080 high, and as big for the picture's size at others
        private const float ReferenceHeight = 1080f;

        /// <summary>URP's blit scale and offset (Blit.hlsl's _BlitScaleBias), and the whole picture as it.</summary>
        public static readonly int BlitScaleBiasId = Shader.PropertyToID("_BlitScaleBias");
        public static readonly Vector4 WholePicture = new Vector4(1f, 1f, 0f, 0f);

        /// <summary>
        /// The pass of the shader <paramref name="material"/> draws with that's named <paramref name="name"/> (as its
        /// Pass block names it); throws if there's none, as the shader and its hooks must match.
        /// </summary>
        public static int Find(Material material, string name)
        {
            int pass = material.FindPass(name);
            if (pass < 0) throw new InvalidOperationException($"MunShot's {material.shader.name} shader has no \"{name}\" pass.");
            return pass;
        }

        /// <summary>One pixel of a picture 1080 high, as a fraction of <paramref name="texture"/>'s width and height (xy).</summary>
        public static Vector4 PixelOf(RenderGraph renderGraph, TextureHandle texture)
        {
            RenderTargetInfo size = renderGraph.GetRenderTargetInfo(texture);
            float pixel = Mathf.Max(1f, size.height / ReferenceHeight);
            return new Vector4(pixel / Mathf.Max(1, size.width), pixel / Mathf.Max(1, size.height), 0f, 0f);
        }

        /// <summary>A copy of a texture as it is now.</summary>
        public static TextureHandle CopyOf(RenderGraph renderGraph, TextureHandle source, string name)
        {
            TextureDesc desc = renderGraph.GetTextureDesc(source);
            desc.name = name;
            desc.clearBuffer = false;
            desc.depthBufferBits = DepthBits.None;
            TextureHandle copy = renderGraph.CreateTexture(desc);
            renderGraph.AddBlitPass(source, copy, Vector2.one, Vector2.zero, passName: name);
            return copy;
        }
    }
}
