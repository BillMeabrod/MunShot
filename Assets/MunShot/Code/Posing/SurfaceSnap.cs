using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SpaceWarp2.API;
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

        // What can be stood on: every layer a ray normally hits except the ones that aren't solid things in the scene
        // the camera is in (checked in game: on Duna the search hit the planet's scaled-space sphere, which the map
        // view draws, about 0.3m above its terrain). Layers the game adds later still count, so new kinds of ground work.
        public static readonly int GroundLayers = Physics.DefaultRaycastLayers & ~LayerMaskOf(
            Layers.LayerScaledScenery, Layers.LayerMap,
            Layers.LayerUi, Layers.LayerUiResearchDevelopment, Layers.LayerUiAdditional, Layers.LayerUiOab,
            Layers.LayerOabScenery, Layers.LayerOabParts,
            Layers.LayerTransparentFx, Layers.LayerRenderPostProcessing, Layers.LayerRenderSkybox,
            Layers.LayerRenderDebugVectors, Layers.LayerRenderDragRender, Layers.LayerRenderFlares,
            Layers.LayerPhysxInvisible, Layers.LayerPhysxPartTriggers);

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
        /// <paramref name="ignore"/> don't count (e.g. hidden kerbals' own bodies). <paramref name="landedOn"/> says
        /// what it rested on and every other collider along the way, for the log.
        /// </summary>
        /// <returns>True when there was a surface within reach (whether or not it needed a move).</returns>
        public static bool TryDrop(Transform root, Transform[] bones, Vector3 up, float reach, IReadOnlyList<Transform> ignore,
                                   out string landedOn)
        {
            landedOn = "nothing (no bones to rest)";
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
            float distance = SearchStartAboveLowest + reach;
            bool found = TryFindSurface(start, -up, distance, ignore, root, out RaycastHit hit);
            string along = DescribeRay(start, -up, distance, ignore, root);
            if (!found)
            {
                landedOn = $"nothing within {reach:0.##}m below '{lowest.name}'; along the way: {along}";
                return false;
            }

            float lift = Vector3.Dot(hit.point - lowest.position, up);
            if (Mathf.Abs(lift) > MinimumMove) root.position += up * lift;
            landedOn = $"{Describe(hit.collider)}, {Signed(-lift)} below '{lowest.name}' (moved {Signed(lift)} up); "
                       + $"along the way, from {SearchStartAboveLowest:0.##}m above it: {along}";
            return true;
        }

        // Every collider the downward search passes, nearest first, with how far down it is and whether it counts (those
        // on layers that aren't ground included, so the log shows what was looked through)
        private static string DescribeRay(Vector3 origin, Vector3 direction, float distance, IReadOnlyList<Transform> ignore,
                                          Transform self)
        {
            RaycastHit[] hits = Physics.RaycastAll(origin, direction, distance, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            if (hits.Length == 0) return "no colliders";
            return string.Join("; ", hits.OrderBy(h => h.distance).Select(h =>
            {
                string skipped = (GroundLayers & (1 << h.collider.gameObject.layer)) == 0 ? " (not ground; skipped)"
                    : IsUnder(h.transform, self) ? " (the double; skipped)"
                    : IsUnderAny(h.transform, ignore) ? " (a hidden kerbal's body; skipped)" : "";
                return $"{Describe(h.collider)} at {h.distance.ToString("0.000", CultureInfo.InvariantCulture)}m{skipped}";
            }));
        }

        private static string Describe(Collider c)
        {
            if (c == null) return "<no collider>";
            string layer = LayerMask.LayerToName(c.gameObject.layer);
            return $"{c.GetType().Name} '{TransformPath.From(null, c.transform)}' (layer {c.gameObject.layer} '{layer}')";
        }

        private static string Signed(float metres) => metres.ToString("+0.000;-0.000;0.000", CultureInfo.InvariantCulture) + "m";

        /// <summary>
        /// The nearest surface along a ray, on <see cref="GroundLayers"/>, skipping colliders under
        /// <paramref name="ignore"/> and under <paramref name="self"/> (the double itself, should it have any).
        /// </summary>
        public static bool TryFindSurface(Vector3 origin, Vector3 direction, float distance, IReadOnlyList<Transform> ignore,
                                          Transform self, out RaycastHit nearestHit)
        {
            nearestHit = default;
            float nearest = float.MaxValue;
            foreach (RaycastHit hit in Physics.RaycastAll(origin, direction, distance, GroundLayers,
                                                          QueryTriggerInteraction.Ignore))
            {
                if (hit.distance >= nearest || IsUnder(hit.transform, self) || IsUnderAny(hit.transform, ignore)) continue;
                nearest = hit.distance;
                nearestHit = hit;
            }
            return nearest < float.MaxValue;
        }

        private static int LayerMaskOf(params int[] layers) => layers.Aggregate(0, (mask, layer) => mask | (1 << layer));

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
