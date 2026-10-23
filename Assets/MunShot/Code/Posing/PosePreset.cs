using System;
using System.Collections.Generic;

namespace MunShot.Posing
{
    /// <summary>Which version of a preset suits the kerbal: standing (or floating) without, or flying with the jetpack.</summary>
    public enum PoseVariant
    {
        Ground,
        Jetpack
    }

    /// <summary>
    /// A named pose: a game animation held at a moment, with a version for each <see cref="PoseVariant"/> the game has
    /// a clip for (some poses only exist on the ground, the flying ones only with the jetpack), and/or bones set by
    /// hand (<see cref="Bones"/>), which go over the clip, or over the game's own animation when there's no clip.
    /// </summary>
    public sealed class PosePreset
    {
        /// <summary>Unique within its file, e.g. "wave".</summary>
        public string Id { get; }

        /// <summary>The name in the preset file, e.g. "Wave": English, and the fallback for <see cref="DisplayName"/>.</summary>
        public string Name { get; }

        /// <summary>
        /// What the player sees: the translation under "MunShot/Pose/&lt;id&gt;" in MunShot's localization file, or
        /// <see cref="Name"/> when there's none (a preset someone added without a translation).
        /// </summary>
        public string DisplayName => Localization.TranslateOr("MunShot/Pose/" + Id, Name);

        public ClipPose? Ground { get; }
        public ClipPose? Jetpack { get; }

        /// <summary>Bones set by hand, in both versions; null when the preset is only a clip.</summary>
        public BonePose Bones { get; }

        /// <summary>A look laid over the kerbal's eyes, in both versions; null for the kerbal's own eyes.</summary>
        public EyeStyle Eyes { get; }

        /// <summary>Whether the kerbal is given eyelashes (<see cref="EyeLashes"/>), in both versions.</summary>
        public bool Lashes { get; }

        public PosePreset(string id, string name, ClipPose? ground, ClipPose? jetpack, BonePose bones = null, EyeStyle eyes = null,
                          bool lashes = false)
        {
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("A pose preset needs an id.", nameof(id));
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException($"Pose preset '{id}' needs a name.", nameof(name));
            if (!ground.HasValue && !jetpack.HasValue && bones == null)
                throw new ArgumentException($"Pose preset '{id}' needs a ground or a jetpack version, or bones.");

            Id = id;
            Name = name;
            Ground = ground;
            Jetpack = jetpack;
            Bones = bones;
            Eyes = eyes;
            Lashes = lashes;
        }

        /// <summary>The clip for <paramref name="variant"/>, or null when the game has none.</summary>
        public ClipPose? Get(PoseVariant variant) => variant == PoseVariant.Jetpack ? Jetpack : Ground;

        /// <summary>Whether there's anything to hold for <paramref name="variant"/>: its clip, or bones (which suit both).</summary>
        public bool Offers(PoseVariant variant) => Get(variant).HasValue || Bones != null;

        /// <summary>
        /// The versions to list: one per clip, or just one for a pose made only of bones, since its bones are the same
        /// either way.
        /// </summary>
        public IEnumerable<PoseVariant> ListedVariants()
        {
            if (Ground.HasValue || (!Jetpack.HasValue && Bones != null)) yield return PoseVariant.Ground;
            if (Jetpack.HasValue) yield return PoseVariant.Jetpack;
        }
    }
}
