using System;

namespace MunShot.Posing
{
    /// <summary>Which version of a preset suits the kerbal: standing (or floating) without, or flying with the jetpack.</summary>
    public enum PoseVariant
    {
        Ground,
        Jetpack
    }

    /// <summary>
    /// A named pose from the game's animations, with a version for each <see cref="PoseVariant"/> the game has a
    /// clip for (some poses only exist on the ground, the flying ones only with the jetpack).
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

        public PosePreset(string id, string name, ClipPose? ground, ClipPose? jetpack)
        {
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("A pose preset needs an id.", nameof(id));
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException($"Pose preset '{id}' needs a name.", nameof(name));
            if (!ground.HasValue && !jetpack.HasValue)
                throw new ArgumentException($"Pose preset '{id}' needs a ground or a jetpack version.");

            Id = id;
            Name = name;
            Ground = ground;
            Jetpack = jetpack;
        }

        /// <summary>The version for <paramref name="variant"/>, or null when the game has none.</summary>
        public ClipPose? Get(PoseVariant variant) => variant == PoseVariant.Jetpack ? Jetpack : Ground;
    }
}
