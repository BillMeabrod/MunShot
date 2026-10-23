using System;
using System.Collections.Generic;
using UnityEngine;

namespace MunShot.Posing
{
    /// <summary>
    /// The jetpack's own bones (its side arms and hand controllers, bone_kerbal_eva_*) deployed for flying or folded
    /// away, as the game's idle clips have them: "kerbal_jp_idle" (deployed) and "kerbal_idle" (folded). The game's
    /// jetpack and ground clips differ in these bones, so laying them over any pose gives it the other version. They're
    /// read once by sampling the clip onto a kerbal for a moment (the game's animation puts the kerbal back the same
    /// frame) and kept, since every kerbal has the same rig.
    /// </summary>
    internal static class JetpackBones
    {
        public const string DeployedClip = "kerbal_jp_idle";
        public const string FoldedClip = "kerbal_idle";

        private const string JetpackPrefix = "eva_";
        private const string AttachPoint = "eva_ach";

        private static readonly Dictionary<string, BonePose> Sampled = new Dictionary<string, BonePose>(StringComparer.Ordinal);

        /// <summary>
        /// The jetpack's bones deployed or folded, for kerbals with <paramref name="animator"/>'s rig; null (with
        /// <paramref name="problem"/> saying why) if the clip or the bones can't be found.
        /// </summary>
        public static BonePose For(Animator animator, bool deployed, out string problem)
        {
            problem = null;
            string clipName = deployed ? DeployedClip : FoldedClip;
            if (Sampled.TryGetValue(clipName, out BonePose cached)) return cached;

            AnimationClip clip = null;
            RuntimeAnimatorController controller = animator != null ? animator.runtimeAnimatorController : null;
            if (controller != null)
                foreach (AnimationClip c in controller.animationClips)
                    if (c != null && c.name == clipName) { clip = c; break; }
            if (clip == null)
            {
                problem = $"the kerbal's animations have no '{clipName}' to take the jetpack's {(deployed ? "deployed" : "folded")} position from";
                return null;
            }

            // Sampling writes the whole clip onto the kerbal, its own transform included; that is put back at once,
            // and the bones are put back by the game's animation later this frame
            Transform root = animator.transform;
            Vector3 position = root.localPosition;
            Quaternion rotation = root.localRotation;
            clip.SampleAnimation(animator.gameObject, 0f);
            root.localPosition = position;
            root.localRotation = rotation;

            var rotations = new Dictionary<string, Quaternion>(StringComparer.Ordinal);
            var positions = new Dictionary<string, Vector3>(StringComparer.Ordinal);
            foreach (Transform t in animator.GetComponentsInChildren<Transform>(true))
            {
                if (!t.name.StartsWith(KerbalRig.BonePrefix + JetpackPrefix, StringComparison.Ordinal)) continue;
                string bone = t.name.Substring(KerbalRig.BonePrefix.Length);
                if (bone == AttachPoint) continue;
                rotations[bone] = t.localRotation;
                positions[bone] = t.localPosition;
            }
            if (rotations.Count == 0)
            {
                problem = $"the kerbal has no jetpack bones ('{KerbalRig.BonePrefix + JetpackPrefix}*')";
                return null;
            }

            var pose = new BonePose(rotations, positions: positions);
            Sampled[clipName] = pose;
            return pose;
        }
    }
}
