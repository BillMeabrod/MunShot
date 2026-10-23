using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace MunShot.Posing
{
    /// <summary>
    /// A pose given bone by bone, set after the game's animation (and a held clip) has run. Bones it doesn't list keep
    /// their animation. Names are the kerbal skeleton's without the "bone_kerbal_" prefix, e.g. "r_shoulder".
    /// <list type="bullet">
    /// <item><see cref="Rotations"/>: the local rotation (relative to its parent bone) a bone is set to, whatever the
    /// animation had it at.</item>
    /// <item><see cref="Turns"/>: a turn given in the bone's parent's frame, added on top of where the animation has
    /// the bone, e.g. tilting the closed lids of a held clip.</item>
    /// <item><see cref="Moves"/>: a slide in metres, in the bone's parent's frame, added on top of where the animation
    /// has the bone, e.g. drawing the lip corners together.</item>
    /// <item><see cref="Positions"/>: the local position a bone is set to, e.g. the jetpack's arms as a clip has them
    /// (<see cref="JetpackBones"/>).</item>
    /// </list>
    /// </summary>
    public sealed class BonePose
    {
        private static readonly IReadOnlyDictionary<string, Quaternion> NoRotations = new Dictionary<string, Quaternion>();
        private static readonly IReadOnlyDictionary<string, Vector3> NoMoves = new Dictionary<string, Vector3>();

        public IReadOnlyDictionary<string, Quaternion> Rotations { get; }
        public IReadOnlyDictionary<string, Quaternion> Turns { get; }
        public IReadOnlyDictionary<string, Vector3> Moves { get; }
        public IReadOnlyDictionary<string, Vector3> Positions { get; }

        public BonePose(IReadOnlyDictionary<string, Quaternion> rotations, IReadOnlyDictionary<string, Quaternion> turns = null,
                        IReadOnlyDictionary<string, Vector3> moves = null, IReadOnlyDictionary<string, Vector3> positions = null)
        {
            Rotations = rotations != null ? new Dictionary<string, Quaternion>(rotations, StringComparer.Ordinal) : NoRotations;
            Turns = turns != null ? new Dictionary<string, Quaternion>(turns, StringComparer.Ordinal) : NoRotations;
            Moves = moves != null ? new Dictionary<string, Vector3>(moves, StringComparer.Ordinal) : NoMoves;
            Positions = positions != null ? new Dictionary<string, Vector3>(positions, StringComparer.Ordinal) : NoMoves;
            if (Rotations.Count + Turns.Count + Moves.Count + Positions.Count == 0)
                throw new ArgumentException("A bone pose needs at least one bone.", nameof(rotations));
            string both = Turns.Keys.FirstOrDefault(Rotations.ContainsKey);
            if (both != null)
                throw new ArgumentException($"Bone '{both}' is both set and turned; give it one or the other.", nameof(turns));
        }

        /// <summary>
        /// <paramref name="over"/> laid over <paramref name="under"/>: where both pose a bone, <paramref name="over"/>'s
        /// way wins (a bone it sets isn't turned by <paramref name="under"/>, and the other way round); the rest comes
        /// from both. Either may be null.
        /// </summary>
        public static BonePose Over(BonePose under, BonePose over)
        {
            if (under == null) return over;
            if (over == null) return under;
            Dictionary<string, Quaternion> rotations = Merge(under.Rotations, over.Rotations), turns = Merge(under.Turns, over.Turns);
            foreach (string bone in over.Rotations.Keys) turns.Remove(bone);
            foreach (string bone in over.Turns.Keys) rotations.Remove(bone);
            return new BonePose(rotations, turns, Merge(under.Moves, over.Moves), Merge(under.Positions, over.Positions));
        }

        private static Dictionary<string, T> Merge<T>(IReadOnlyDictionary<string, T> under, IReadOnlyDictionary<string, T> over)
        {
            var merged = new Dictionary<string, T>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, T> bone in under) merged[bone.Key] = bone.Value;
            foreach (KeyValuePair<string, T> bone in over) merged[bone.Key] = bone.Value;
            return merged;
        }

        public override string ToString() => $"{Rotations.Count + Turns.Count + Moves.Count + Positions.Count} posed bones";
    }
}
