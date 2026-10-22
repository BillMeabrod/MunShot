using System;
using System.Collections.Generic;
using UnityEngine;

namespace MunShot.Posing
{
    /// <summary>
    /// A kerbal's shape for mouse picking: capsules along the skeleton (spine, neck, arms, legs, the jetpack) and a
    /// sphere for the head, following the current pose. The game's meshes can't be read (they're not readable at
    /// runtime) and a box around the renderers is far bigger than the kerbal, so a ray only counts as hitting a kerbal
    /// where its body is. Fingers and face bones are inside the hand and head shapes.
    /// </summary>
    public sealed class KerbalHitShape
    {
        private const string Prefix = "bone_kerbal_";

        // Radii in metres, by the bone a capsule runs to (kerbals are about a metre tall)
        private const float TorsoRadius = 0.2f;
        private const float BackpackRadius = 0.22f;
        private const float UpperArmRadius = 0.09f;
        private const float ForearmRadius = 0.08f;
        private const float HandRadius = 0.07f;
        private const float ThighRadius = 0.1f;
        private const float ShinRadius = 0.09f;
        private const float FootRadius = 0.08f;
        private const float JetpackArmRadius = 0.06f;
        private const float HeadRadius = 0.28f;

        private readonly List<(Transform from, Transform to, float radius)> _capsules = new List<(Transform, Transform, float)>();
        private readonly Transform _head;
        private readonly Transform _leftEye;
        private readonly Transform _rightEye;

        public KerbalHitShape(Transform root)
        {
            var bones = new Dictionary<string, Transform>(StringComparer.Ordinal);
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
                if (t.name.StartsWith(Prefix, StringComparison.Ordinal))
                    bones[t.name.Substring(Prefix.Length)] = t;

            Transform Bone(string name) => bones.TryGetValue(name, out Transform t) ? t : null;
            void Capsule(string from, string to, float radius)
            {
                Transform a = Bone(from), b = Bone(to);
                if (a != null && b != null) _capsules.Add((a, b, radius));
            }

            Capsule("pelvis", "spine_lower", TorsoRadius);
            Capsule("spine_lower", "spine_upper", TorsoRadius);
            Capsule("spine_upper", "neck", TorsoRadius);
            Capsule("eva_ach", "eva_root", BackpackRadius);
            foreach (string side in new[] { "l", "r" })
            {
                Capsule($"{side}_collar", $"{side}_shoulder", UpperArmRadius);
                Capsule($"{side}_shoulder", $"{side}_elbow", UpperArmRadius);
                Capsule($"{side}_elbow", $"{side}_hand", ForearmRadius);
                Capsule($"{side}_hand", $"{side}_middle_03", HandRadius);
                Capsule("pelvis", $"{side}_thigh", ThighRadius);
                Capsule($"{side}_thigh", $"{side}_knee", ThighRadius);
                Capsule($"{side}_knee", $"{side}_foot", ShinRadius);
                Capsule($"{side}_foot", $"{side}_toe", FootRadius);
                Capsule($"eva_{side}_arm01_pitcher", $"eva_{side}_arm03_pitcher", JetpackArmRadius);
            }
            _head = Bone("head");
            _leftEye = Bone("l_eye_offset");
            _rightEye = Bone("r_eye_offset");
        }

        /// <summary>Whether the shape has anything to hit (a kerbal's skeleton was found).</summary>
        public bool IsEmpty => _capsules.Count == 0 && _head == null;

        /// <summary>The distance along <paramref name="ray"/> to where it first meets the kerbal, if it does.</summary>
        public bool Raycast(Ray ray, out float distance)
        {
            distance = float.MaxValue;
            foreach ((Transform from, Transform to, float radius) in _capsules)
                if (from != null && to != null && RayCapsule(ray, from.position, to.position, radius, out float d) && d < distance)
                    distance = d;

            // The head's middle, between the head bone and the eyes (which sit at the front of the face)
            if (_head != null)
            {
                Vector3 centre = _leftEye != null && _rightEye != null
                    ? (_head.position + _leftEye.position + _rightEye.position) / 3f
                    : _head.position;
                if (RayCapsule(ray, centre, centre, HeadRadius, out float d) && d < distance) distance = d;
            }
            return distance < float.MaxValue;
        }

        // Where a ray first comes within radius of the segment a-b (a capsule; a sphere when a == b), approximately:
        // at the closest approach, stepped back along the ray by the depth into the capsule
        private static bool RayCapsule(Ray ray, Vector3 a, Vector3 b, float radius, out float distance)
        {
            Vector3 u = ray.direction, v = b - a, w = ray.origin - a;
            float uu = Vector3.Dot(u, u), uv = Vector3.Dot(u, v), vv = Vector3.Dot(v, v), uw = Vector3.Dot(u, w), vw = Vector3.Dot(v, w);
            float denominator = uu * vv - uv * uv;
            // Closest point on the segment to the ray, then the point on the ray nearest that
            float s = vv > 1e-8f && denominator > 1e-8f ? Mathf.Clamp01((uu * vw - uv * uw) / denominator) : 0f;
            Vector3 onSegment = a + v * s;
            float r = Mathf.Max(0f, Vector3.Dot(onSegment - ray.origin, u) / uu);
            float miss = (ray.origin + u * r - onSegment).magnitude;
            distance = 0f;
            if (miss > radius) return false;
            distance = Mathf.Max(0f, r - Mathf.Sqrt(radius * radius - miss * miss) / Mathf.Sqrt(uu));
            return true;
        }
    }
}
