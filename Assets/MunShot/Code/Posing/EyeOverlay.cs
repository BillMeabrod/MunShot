using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MunShot.Posing
{
    /// <summary>
    /// Lays an <see cref="EyeStyle"/>'s image over the front of a kerbal's eyeballs. Each eye gets a small curved patch
    /// hugging the eyeball's front, parented to the eye bone so it turns with the eye, and just inside the lids so they
    /// still cover it when they close. Kerbal eyes are drawn from the head's own texture, so this is how they get a
    /// different look without touching the real kerbal's materials. Made and taken away by <see cref="PosePlayer"/>.
    /// </summary>
    internal sealed class EyeOverlay
    {
        // In an eye bone's own frame, a kerbal eyeball looks along -Y, with +Z up; the viewer's right is then -X
        private static readonly Vector3 Gaze = Vector3.down;
        private static readonly Vector3 Up = Vector3.forward;
        private static readonly Vector3 ViewerRight = Vector3.left;

        // Just outside the eyeball, under the lids
        private const float Lift = 1.01f;
        private const int Rings = 8;
        private const int Segments = 32;

        // Shaders that draw an image with its transparency, unlit; the first one the game has is used
        private static readonly string[] ShaderNames = { "Sprites/Default", "Unlit/Transparent", "UI/Default" };

        private readonly List<Object> _made = new List<Object>();

        private EyeOverlay()
        {
        }

        /// <summary>
        /// Lays <paramref name="style"/> over the eyes under <paramref name="kerbal"/>. Problems (no eye bones, no
        /// eyeball, no usable shader) are logged; an eye that can't be done is left as it is.
        /// </summary>
        public static EyeOverlay Create(Transform kerbal, EyeStyle style, Action<string> logInfo, Action<string> logError)
        {
            var overlay = new EyeOverlay();
            Shader shader = null;
            foreach (string name in ShaderNames)
                if ((shader = Shader.Find(name)) != null) break;
            if (shader == null)
            {
                logError($"Eye overlay {style}: none of the shaders {string.Join(", ", ShaderNames)} is in the game; eyes left as they are.");
                return overlay;
            }

            var material = new Material(shader) { name = $"MunShot eyes ({style.Image})", mainTexture = style.Texture };
            overlay._made.Add(material);
            foreach (string side in new[] { "l", "r" })
                overlay.AddEye(kerbal, side, style, material, logInfo, logError);
            return overlay;
        }

        /// <summary>Takes the overlay away.</summary>
        public void Destroy()
        {
            foreach (Object made in _made)
                if (made != null)
                    Object.Destroy(made);
            _made.Clear();
        }

        private void AddEye(Transform kerbal, string side, EyeStyle style, Material material, Action<string> logInfo,
                            Action<string> logError)
        {
            string boneName = KerbalRig.BonePrefix + side + "_eye";
            Transform bone = Find(kerbal, boneName);
            Renderer eyeball = bone != null ? Eyeball(bone) : null;
            if (eyeball == null)
            {
                logError($"Eye overlay {style}: '{kerbal.name}' has no {(bone == null ? $"bone '{boneName}'" : $"eyeball under '{boneName}'")}; that eye is left as it is.");
                return;
            }

            // The eyeball measured in its own frame, from its mesh's box (which the game keeps even for meshes it won't
            // let us read): kerbal eyeballs are egg-shaped, so how far its front is along the gaze and how wide it is
            // across are measured separately, and the image sits on the front with the curve of its width
            Mesh shape = eyeball.GetComponent<MeshFilter>()?.sharedMesh;
            Bounds box = shape != null ? shape.bounds : eyeball.localBounds;
            Transform t = eyeball.transform;
            Vector3 gaze = t.InverseTransformDirection(bone.TransformDirection(Gaze)).normalized;
            Vector3 e = box.extents;
            float front = Mathf.Abs(gaze.x) * e.x + Mathf.Abs(gaze.y) * e.y + Mathf.Abs(gaze.z) * e.z;
            float across = Mathf.Sqrt((e.x * e.x * (1 - gaze.x * gaze.x) + e.y * e.y * (1 - gaze.y * gaze.y)
                                       + e.z * e.z * (1 - gaze.z * gaze.z)) / 2f);
            Vector3 apex = bone.InverseTransformPoint(t.TransformPoint(box.center + gaze * front * style.Depth));
            float scale = t.lossyScale.x / Mathf.Max(bone.lossyScale.x, 1e-6f);
            float width = across * scale;
            logInfo($"Eye overlay {style} on '{kerbal.name}' {side}: eyeball '{eyeball.name}', front {front * scale:0.000} m "
                    + $"from its middle, {width:0.000} m across; image placed {apex.magnitude:0.000} m from the eye bone.");

            // The cap is built around its sphere's centre, a width behind the front
            var go = new GameObject($"MunShot eye overlay ({side})") { layer = eyeball.gameObject.layer };
            go.transform.SetParent(bone, false);
            go.transform.localPosition = apex - Gaze * width * Lift;
            Mesh mesh = Cap(width * Lift, style.Size);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            _made.Add(mesh);
            _made.Add(go);
        }

        // The eyeball's renderer: one that's drawn (the game keeps hidden spare copies), named "...ball..." on the game's
        // eye prefabs; else any eyeball, else the first renderer under the bone
        private static Renderer Eyeball(Transform bone)
        {
            Renderer shown = null, ball = null, first = null;
            foreach (Renderer r in bone.GetComponentsInChildren<Renderer>(true))
            {
                if (r.gameObject.name.StartsWith("MunShot", StringComparison.Ordinal)) continue;
                bool isBall = r.gameObject.name.IndexOf("ball", StringComparison.OrdinalIgnoreCase) >= 0;
                if (isBall && r.enabled && r.gameObject.activeInHierarchy) return r;
                if (isBall) ball ??= r;
                if (r.enabled && r.gameObject.activeInHierarchy) shown ??= r;
                first ??= r;
            }
            return ball ?? shown ?? first;
        }

        private static Transform Find(Transform root, string name)
        {
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
                if (t.name == name)
                    return t;
            return null;
        }

        // A cap of a sphere of the given radius around the gaze, covering `size` of it (its edge's distance from the
        // gaze line over the radius), with the image projected straight on: as seen from the front, the image's right
        // is the viewer's right and its top is up
        private static Mesh Cap(float radius, float size)
        {
            float edge = Mathf.Asin(size);
            var vertices = new List<Vector3> { Gaze * radius };
            var uvs = new List<Vector2> { new Vector2(0.5f, 0.5f) };
            for (int ring = 1; ring <= Rings; ring++)
            {
                float a = edge * ring / Rings;
                float across = Mathf.Sin(a) / size;
                for (int s = 0; s < Segments; s++)
                {
                    float phi = 2f * Mathf.PI * s / Segments;
                    Vector3 sideways = ViewerRight * Mathf.Cos(phi) + Up * Mathf.Sin(phi);
                    vertices.Add((Gaze * Mathf.Cos(a) + sideways * Mathf.Sin(a)) * radius);
                    uvs.Add(new Vector2(0.5f + 0.5f * across * Mathf.Cos(phi), 0.5f + 0.5f * across * Mathf.Sin(phi)));
                }
            }

            var triangles = new List<int>();
            for (int s = 0; s < Segments; s++)
                triangles.AddRange(new[] { 0, 1 + s, 1 + (s + 1) % Segments });
            for (int ring = 1; ring < Rings; ring++)
            {
                int inner = 1 + (ring - 1) * Segments, outer = 1 + ring * Segments;
                for (int s = 0; s < Segments; s++)
                {
                    int s1 = (s + 1) % Segments;
                    triangles.AddRange(new[] { inner + s, outer + s, outer + s1, inner + s, outer + s1, inner + s1 });
                }
            }

            var mesh = new Mesh { name = "MunShot eye overlay" };
            mesh.SetVertices(vertices);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
