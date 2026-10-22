using System;
using System.Collections.Generic;
using UnityEngine;

namespace MunShot.Posing
{
    /// <summary>
    /// Standing photo doubles on things: which way is up near a kerbal, and lowering or raising a double so its lowest
    /// point rests on the ground or a vessel below it. KSP2 has no per-foot placement (no foot IK); like the game's own
    /// kerbals, the whole double moves, so on uneven ground the other foot may sit a little above or into the surface.
    /// </summary>
    public static class SurfaceSnap
    {
        // How far above the lowest bone the downward search starts, so a double slightly sunk into the ground still
        // finds the surface it's in
        private const float SearchStartAboveLowest = 0.5f;

        // Moves smaller than this aren't worth making (and would just jitter)
        private const float MinimumMove = 0.001f;

        /// <summary>The skeleton's bones under <paramref name="root"/>, for <see cref="TryDrop"/>.</summary>
        public static Transform[] FindBones(Transform root)
        {
            var bones = new List<Transform>();
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
                // Whichever bone is lowest (feet when standing, maybe a shoulder when lying down) touches the surface
                if (t.name.StartsWith(KerbalRig.BonePrefix, StringComparison.Ordinal))
                    bones.Add(t);
            return bones.ToArray();
        }

        /// <summary>
        /// Moves <paramref name="root"/> along <paramref name="up"/> so the lowest of <paramref name="bones"/> rests on
        /// the first surface within <paramref name="reach"/> below it. Colliders under any of
        /// <paramref name="ignore"/> don't count (e.g. hidden kerbals' own bodies).
        /// </summary>
        /// <returns>True when there was a surface within reach (whether or not it needed a move).</returns>
        public static bool TryDrop(Transform root, Transform[] bones, Vector3 up, float reach, IReadOnlyList<Transform> ignore)
        {
            if (root == null || bones == null || bones.Length == 0) return false;

            Transform lowest = null;
            float lowestHeight = float.MaxValue;
            foreach (Transform bone in bones)
            {
                if (bone == null) continue;
                float height = Vector3.Dot(bone.position, up);
                if (height < lowestHeight)
                {
                    lowestHeight = height;
                    lowest = bone;
                }
            }
            if (lowest == null) return false;

            Vector3 start = lowest.position + up * SearchStartAboveLowest;
            if (!TryFindSurface(start, -up, SearchStartAboveLowest + reach, ignore, root, out RaycastHit hit)) return false;

            float lift = Vector3.Dot(hit.point - lowest.position, up);
            if (Mathf.Abs(lift) > MinimumMove) root.position += up * lift;
            return true;
        }

        /// <summary>
        /// The nearest surface along a ray, skipping colliders under <paramref name="ignore"/> and under
        /// <paramref name="self"/> (the double itself, should it have any).
        /// </summary>
        public static bool TryFindSurface(Vector3 origin, Vector3 direction, float distance, IReadOnlyList<Transform> ignore,
                                          Transform self, out RaycastHit nearestHit)
        {
            nearestHit = default;
            float nearest = float.MaxValue;
            foreach (RaycastHit hit in Physics.RaycastAll(origin, direction, distance, Physics.DefaultRaycastLayers,
                                                          QueryTriggerInteraction.Ignore))
            {
                if (hit.distance >= nearest || IsUnder(hit.transform, self) || IsUnderAny(hit.transform, ignore)) continue;
                nearest = hit.distance;
                nearestHit = hit;
            }
            return nearest < float.MaxValue;
        }

        private static bool IsUnderAny(Transform t, IReadOnlyList<Transform> roots)
        {
            if (roots == null) return false;
            foreach (Transform root in roots)
                if (IsUnder(t, root))
                    return true;
            return false;
        }

        private static bool IsUnder(Transform t, Transform root) => root != null && t.IsChildOf(root);
    }
}
