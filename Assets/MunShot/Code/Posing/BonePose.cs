using System;
using System.Collections.Generic;
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
    /// </list>
    /// </summary>
    public sealed class BonePose
    {
        private static readonly IReadOnlyDictionary<string, Quaternion> None = new Dictionary<string, Quaternion>();

        public IReadOnlyDictionary<string, Quaternion> Rotations { get; }
        public IReadOnlyDictionary<string, Quaternion> Turns { get; }

        public BonePose(IReadOnlyDictionary<string, Quaternion> rotations, IReadOnlyDictionary<string, Quaternion> turns = null)
        {
            Rotations = rotations != null ? new Dictionary<string, Quaternion>(rotations, StringComparer.Ordinal) : None;
            Turns = turns != null ? new Dictionary<string, Quaternion>(turns, StringComparer.Ordinal) : None;
            if (Rotations.Count + Turns.Count == 0)
                throw new ArgumentException("A bone pose needs at least one bone.", nameof(rotations));
            foreach (string bone in Turns.Keys)
                if (Rotations.ContainsKey(bone))
                    throw new ArgumentException($"Bone '{bone}' is both set and turned; give it one or the other.", nameof(turns));
        }

        public override string ToString() => $"{Rotations.Count + Turns.Count} posed bones";
    }
}
