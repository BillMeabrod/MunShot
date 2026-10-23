using System;
using UnityEngine;

namespace MunShot.Posing
{
    /// <summary>
    /// A look for a posed kerbal's eyes: an image (a PNG in the Poses folder, transparent around the iris) laid over
    /// the front of each eyeball by <see cref="EyeOverlay"/>, e.g. an anime iris with highlights.
    /// </summary>
    public sealed class EyeStyle
    {
        /// <summary>The image's file name, for logs.</summary>
        public string Image { get; }

        public Texture2D Texture { get; }

        /// <summary>How much of the eyeball the image covers: its radius over the eyeball's, from 0.1 to 0.95.</summary>
        public float Size { get; }

        /// <summary>
        /// How far out the image sits, as a share of the way from the eyeball's middle to its front (0.5 to 1.5; 1 is on
        /// the front), to fine-tune the fit on the game's egg-shaped eyeballs.
        /// </summary>
        public float Depth { get; }

        public EyeStyle(string image, Texture2D texture, float size, float depth = 1f)
        {
            if (string.IsNullOrWhiteSpace(image)) throw new ArgumentException("An eye style needs an image name.", nameof(image));
            if (size < 0.1f || size > 0.95f)
                throw new ArgumentOutOfRangeException(nameof(size), size, "An eye style's size must be from 0.1 to 0.95 of the eyeball.");
            if (depth < 0.5f || depth > 1.5f)
                throw new ArgumentOutOfRangeException(nameof(depth), depth, "An eye style's depth must be from 0.5 to 1.5.");
            Image = image;
            Texture = texture != null ? texture : throw new ArgumentNullException(nameof(texture));
            Size = size;
            Depth = depth;
        }

        public override string ToString() => $"eyes '{Image}' at {Size:0.##} (depth {Depth:0.##})";
    }
}
