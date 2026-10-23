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
    /// </list>
    /// </summary>
    public sealed class BonePose
    {
        private static readonly IReadOnlyDictionary<string, Quaternion> NoRotations = new Dictionary<string, Quaternion>();
        private static readonly IReadOnlyDictionary<string, Vector3> NoMoves = new Dictionary<string, Vector3>();

        public IReadOnlyDictionary<string, Quaternion> Rotations { get; }
        public IReadOnlyDictionary<string, Quaternion> Turns { get; }
        public IReadOnlyDictionary<string, Vector3> Moves { get; }

        public BonePose(IReadOnlyDictionary<string, Quaternion> rotations, IReadOnlyDictionary<string, Quaternion> turns = null,
                        IReadOnlyDictionary<string, Vector3> moves = null)
        {
            Rotations = rotations != null ? new Dictionary<string, Quaternion>(rotations, StringComparer.Ordinal) : NoRotations;
            Turns = turns != null ? new Dictionary<string, Quaternion>(turns, StringComparer.Ordinal) : NoRotations;
            Moves = moves != null ? new Dictionary<string, Vector3>(moves, StringComparer.Ordinal) : NoMoves;
            if (Rotations.Count + Turns.Count + Moves.Count == 0)
                throw new ArgumentException("A bone pose needs at least one bone.", nameof(rotations));
            string both = Turns.Keys.FirstOrDefault(Rotations.ContainsKey);
            if (both != null)
                throw new ArgumentException($"Bone '{both}' is both set and turned; give it one or the other.", nameof(turns));
        }

        public override string ToString() => $"{Rotations.Count + Turns.Count + Moves.Count} posed bones";
    }
}
