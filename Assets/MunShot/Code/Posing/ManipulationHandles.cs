using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using KSP.Game;
using KSP.OAB;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using Object = UnityEngine.Object;

namespace MunShot.Posing
{
    /// <summary>Which handles are shown: move arrows or rotate rings.</summary>
    public enum HandleMode
    {
        Move,
        Rotate
    }

    /// <summary>A handle under the mouse: an arrow (move along an axis) or a ring (rotate about one).</summary>
    public readonly struct HandleHit
    {
        public HandleMode Kind { get; }

        /// <summary>0, 1, 2 for the handles' x, y and z axes.</summary>
        public int Axis { get; }

        public HandleHit(HandleMode kind, int axis)
        {
            Kind = kind;
            Axis = axis;
        }
    }

    /// <summary>
    /// The move and rotate handles shown on the selected photo double. They're placed every frame on the double, with
    /// the double's rotation (so axis 0/1/2 are its right/up/forward), and kept a constant size on screen as the VAB
    /// keeps its handles.
    /// </summary>
    public abstract class ManipulationHandles : IDisposable
    {
        public abstract void SetMode(HandleMode mode);
        public abstract void SetVisible(bool visible);
        public abstract void Follow(Vector3 position, Quaternion rotation, Camera camera);

        /// <summary>The nearest handle of the current mode under <paramref name="ray"/>, if any.</summary>
        public abstract bool TryPick(Ray ray, out HandleHit hit);

        /// <summary>The handles' root, so surface searches can skip it.</summary>
        public abstract Transform Root { get; }

        public abstract void Dispose();

        /// <summary>
        /// Makes the VAB's own move/rotate handles. The first time in a game they're copied out of the VAB (see
        /// <see cref="VabHandleTemplate"/>); after that, from the copy. Simple drawn handles are only a last resort
        /// when the VAB's can't be had, and that's logged as an error with the reason.
        /// </summary>
        public static void Create(int layer, Action<string> logInfo, Action<string> logError, Action<ManipulationHandles> done)
        {
            VabHandleTemplate.Ensure(logInfo, logError, template =>
                done(template != null ? (ManipulationHandles)template.Instantiate(layer) : new DrawnHandles(layer)));
        }

        // The VAB's rule: scale with distance to the camera so the handles stay the same size on screen
        protected static float ScreenSizeScale(Vector3 position, Camera camera, float multiplier) =>
            camera != null ? (camera.transform.position - position).magnitude * multiplier : 1f;
    }

    /// <summary>
    /// MunShot's own copy of the VAB's handles, made once per game. The handles (the VAB ManipulationWidget's arrows,
    /// arcs and click colliders) aren't addressable on their own: they're packed into the VAB's bundles without names,
    /// so the only way to them is the VAB prefab, which pulls in hundreds of bundles. So the prefab is loaded once,
    /// the widget is copied with copies of its meshes and materials (and its VAB scripts and animators stripped), and
    /// the VAB load is released straight away, so the game loads the VAB fresh when the player goes there. When the
    /// game already has the VAB loaded, its widget is copied from there and nothing is loaded.
    ///
    /// The widget keeps its parts in private fields, read here by name; a game update that renames them makes this
    /// fail with a log naming the field.
    /// </summary>
    internal sealed class VabHandleTemplate
    {
        public const string VabAddress = "OAB.prefab";
        private const float DefaultSizeOnScreen = 0.06f;

        private static VabHandleTemplate _instance;
        private static bool _failed;
        private static bool _loading;
        private static event Action<VabHandleTemplate> Ready;

        private readonly GameObject _root;
        public string MoveGroupPath { get; }
        public string RotateGroupPath { get; }
        public IReadOnlyList<(string path, int axis)> Arrows { get; }
        public IReadOnlyList<(string path, int axis)> Rings { get; }
        public float SizeOnScreen { get; }

        private VabHandleTemplate(GameObject root, string moveGroupPath, string rotateGroupPath, List<(string, int)> arrows,
                                  List<(string, int)> rings, float sizeOnScreen)
        {
            _root = root;
            MoveGroupPath = moveGroupPath;
            RotateGroupPath = rotateGroupPath;
            Arrows = arrows;
            Rings = rings;
            SizeOnScreen = sizeOnScreen;
        }

        /// <summary>A working set of handles from the template.</summary>
        public VabHandles Instantiate(int layer)
        {
            GameObject clone = Object.Instantiate(_root);
            clone.name = "MunShot handles";
            clone.SetLayerRecursive(layer);
            return new VabHandles(clone, this);
        }

        /// <summary>Calls back with the template, making it first if needed; null if it can't be made (logged).</summary>
        public static void Ensure(Action<string> logInfo, Action<string> logError, Action<VabHandleTemplate> done)
        {
            if (_instance != null || _failed)
            {
                done(_instance);
                return;
            }

            Ready += done;
            if (_loading) return;
            _loading = true;

            ManipulationWidget loaded = GameManager.Instance?.Game?.OAB?.Current?.BuilderAssets?.GetManipulationWidgetReference();
            if (loaded != null)
            {
                Finish(Build(loaded, logInfo, logError, "the loaded VAB"), logError);
                return;
            }

            var assets = GameManager.Instance?.Assets;
            if (assets == null)
            {
                logError("Handles: the game's asset provider isn't available, so the VAB's handles can't be copied; using simple drawn handles.");
                Finish(null, logError);
                return;
            }

            AsyncOperationHandle<GameObject> handle = assets.LoadRaw<GameObject>(VabAddress);
            handle.Completed += op =>
            {
                VabHandleTemplate template = null;
                GameObject vab = op.Status == AsyncOperationStatus.Succeeded ? op.Result : null;
                ObjectAssemblyBuilder builder = vab != null ? vab.GetComponentInChildren<ObjectAssemblyBuilder>(true) : null;
                ManipulationWidget widget = builder?.BuilderAssets?.GetManipulationWidgetReference();
                if (widget == null)
                    logError($"Handles: couldn't get the VAB's handles from '{VabAddress}' "
                             + $"({(vab == null ? $"it didn't load: {op.OperationException?.Message}" : builder == null ? "no ObjectAssemblyBuilder in it" : "no manipulation widget in its assets")}); "
                             + "using simple drawn handles.");
                else
                    template = Build(widget, logInfo, logError, $"'{VabAddress}', released after copying");

                // Let the VAB go: the game loads it fresh when the player goes there
                if (op.IsValid()) Addressables.Release(op);
                if (template != null && !template.Verify(logError)) template = null;
                Finish(template, logError);
            };
        }

        private static void Finish(VabHandleTemplate template, Action<string> logError)
        {
            _loading = false;
            _instance = template;
            _failed = template == null;
            Action<VabHandleTemplate> callbacks = Ready;
            Ready = null;
            callbacks?.Invoke(template);
        }

        private static VabHandleTemplate Build(ManipulationWidget prefab, Action<string> logInfo, Action<string> logError, string source)
        {
            // Made under an inactive holder so the VAB script never wakes up; it's removed before anything runs
            var holder = new GameObject("MunShot handles (building)");
            holder.SetActive(false);
            GameObject root = Object.Instantiate(prefab.gameObject, holder.transform);
            root.name = "MunShot handles template";
            try
            {
                var widget = root.GetComponent<ManipulationWidget>();
                if (!TryRead(widget, "translationGroup", out GameObject moveGroup, logError)
                    || !TryRead(widget, "rotationGroup", out GameObject rotateGroup, logError)
                    || !TryRead(widget, "translationAxis", out GameObject[] arrowObjects, logError)
                    || !TryRead(widget, "rotationAxis", out GameObject[] ringObjects, logError))
                {
                    Object.Destroy(root);
                    return null;
                }
                float sizeOnScreen = TryRead(widget, "_sizeOnScreenMultiplier", out float multiplier, null) ? multiplier : DefaultSizeOnScreen;

                // The VAB's scripts and the handles' little animations go: their assets are released with the VAB
                foreach (MonoBehaviour behaviour in root.GetComponentsInChildren<MonoBehaviour>(true)) Object.DestroyImmediate(behaviour);
                foreach (Animator animator in root.GetComponentsInChildren<Animator>(true)) Object.DestroyImmediate(animator);

                int copies = CopyAssets(root);
                var report = new StringBuilder($"Handles: copied the VAB's handles from {source} ({copies} meshes and materials): ");
                List<(string, int)> arrows = Describe(root.transform, arrowObjects, false, report);
                List<(string, int)> rings = Describe(root.transform, ringObjects, true, report);
                if (arrows.Count == 0 || rings.Count == 0)
                {
                    logError(report.Append("— missing arrows or rings; using simple drawn handles.").ToString());
                    Object.Destroy(root);
                    return null;
                }

                root.transform.SetParent(null, false);
                root.SetActive(false);
                Object.DontDestroyOnLoad(root);
                logInfo(report.ToString());
                return new VabHandleTemplate(root, TransformPath.From(root.transform, moveGroup.transform), TransformPath.From(root.transform, rotateGroup.transform),
                                             arrows, rings, sizeOnScreen);
            }
            finally
            {
                Object.Destroy(holder);
            }
        }

        // Replaces every mesh and material the handles use with MunShot's own copy, so they outlive the VAB's bundles
        private static int CopyAssets(GameObject root)
        {
            var meshCopies = new Dictionary<Mesh, Mesh>();
            var materialCopies = new Dictionary<Material, Material>();
            Mesh CopyMesh(Mesh mesh)
            {
                if (mesh == null) return null;
                if (!meshCopies.TryGetValue(mesh, out Mesh copy)) meshCopies[mesh] = copy = Object.Instantiate(mesh);
                copy.name = mesh.name;
                return copy;
            }
            Material[] CopyMaterials(Material[] materials)
            {
                var copies = new Material[materials.Length];
                for (int i = 0; i < materials.Length; i++)
                {
                    Material material = materials[i];
                    if (material == null) continue;
                    if (!materialCopies.TryGetValue(material, out Material copy))
                        materialCopies[material] = copy = new Material(material) { name = material.name };
                    copies[i] = copy;
                }
                return copies;
            }

            foreach (SkinnedMeshRenderer skinned in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                skinned.sharedMesh = CopyMesh(skinned.sharedMesh);
                skinned.sharedMaterials = CopyMaterials(skinned.sharedMaterials);
            }
            foreach (MeshFilter filter in root.GetComponentsInChildren<MeshFilter>(true)) filter.sharedMesh = CopyMesh(filter.sharedMesh);
            foreach (MeshRenderer renderer in root.GetComponentsInChildren<MeshRenderer>(true))
                renderer.sharedMaterials = CopyMaterials(renderer.sharedMaterials);
            foreach (MeshCollider collider in root.GetComponentsInChildren<MeshCollider>(true)) collider.sharedMesh = CopyMesh(collider.sharedMesh);
            return meshCopies.Count + materialCopies.Count;
        }

        // After the VAB is released: are the copies still whole? Logs each problem; false if the handles can't be used
        private bool Verify(Action<string> logError)
        {
            var problems = new List<string>();
            foreach (SkinnedMeshRenderer skinned in _root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (skinned.sharedMesh == null || skinned.sharedMesh.vertexCount == 0)
                    problems.Add($"mesh on '{skinned.name}' is empty");
                foreach (Material material in skinned.sharedMaterials)
                    if (material == null || material.shader == null || !material.shader.isSupported)
                        problems.Add($"material '{(material != null ? material.name : "<none>")}' on '{skinned.name}' lost its shader "
                                     + $"('{(material != null && material.shader != null ? material.shader.name : "<none>")}')");
            }
            foreach (MeshCollider collider in _root.GetComponentsInChildren<MeshCollider>(true))
                if (collider.sharedMesh == null || collider.sharedMesh.vertexCount == 0)
                    problems.Add($"click collider '{collider.name}' has no mesh");

            if (problems.Count == 0) return true;
            logError($"Handles: the copied VAB handles broke when the VAB was released: {string.Join("; ", problems)}. Using simple drawn handles.");
            Object.Destroy(_root);
            return false;
        }

        // Each handle's path in the widget and its axis: from its name when it ends in X, Y or Z (the VAB's are MoveX…,
        // RotateX…), else from its shape (an arrow is longest along its axis, a ring thinnest along it)
        private static List<(string, int)> Describe(Transform root, GameObject[] handles, bool rings, StringBuilder report)
        {
            var described = new List<(string, int)>();
            report.Append(rings ? "rings " : "arrows ");
            foreach (GameObject handle in handles)
            {
                if (handle == null || handle.GetComponentInChildren<Collider>(true) == null) continue;
                int axis = AxisFromName(handle.name);
                string how = "name";
                if (axis < 0)
                {
                    Vector3 size = LocalSize(root, handle);
                    axis = rings ? Smallest(size) : Largest(size);
                    how = $"shape {size}";
                }
                described.Add((TransformPath.From(root, handle.transform), axis));
                report.Append($"{handle.name}→{"xyz"[axis]} ({how}) ");
            }
            return described;
        }

        private static int AxisFromName(string name)
        {
            if (string.IsNullOrEmpty(name)) return -1;
            char last = char.ToLowerInvariant(name[name.Length - 1]);
            return last == 'x' ? 0 : last == 'y' ? 1 : last == 'z' ? 2 : -1;
        }

        private static Vector3 LocalSize(Transform space, GameObject handle)
        {
            bool any = false;
            var bounds = new Bounds();
            foreach (Renderer renderer in handle.GetComponentsInChildren<Renderer>(true))
            {
                Bounds local = renderer.localBounds;
                for (int i = 0; i < 8; i++)
                {
                    Vector3 corner = local.center + Vector3.Scale(local.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                    Vector3 point = space.InverseTransformPoint(renderer.transform.TransformPoint(corner));
                    if (!any) bounds = new Bounds(point, Vector3.zero);
                    else bounds.Encapsulate(point);
                    any = true;
                }
            }
            return any ? bounds.size : Vector3.one;
        }

        private static int Largest(Vector3 v) => v.x >= v.y && v.x >= v.z ? 0 : v.y >= v.z ? 1 : 2;
        private static int Smallest(Vector3 v) => v.x <= v.y && v.x <= v.z ? 0 : v.y <= v.z ? 1 : 2;

        private static bool TryRead<T>(ManipulationWidget widget, string field, out T value, Action<string> logError)
        {
            value = default;
            FieldInfo info = typeof(ManipulationWidget).GetField(field, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (info == null || !typeof(T).IsAssignableFrom(info.FieldType))
            {
                logError?.Invoke($"Handles: the VAB's ManipulationWidget has no field '{field}' of type {typeof(T).Name} "
                                 + "(a game update may have renamed it); using simple drawn handles.");
                return false;
            }
            value = (T)info.GetValue(widget);
            if (value == null)
            {
                logError?.Invoke($"Handles: the VAB's ManipulationWidget field '{field}' is empty; using simple drawn handles.");
                return false;
            }
            return true;
        }
    }

    /// <summary>The VAB's handles in use: a clone of <see cref="VabHandleTemplate"/>, picked by their click colliders.</summary>
    internal sealed class VabHandles : ManipulationHandles
    {
        private const float PickDistance = 10000f;

        private readonly GameObject _root;
        private readonly GameObject _moveGroup;
        private readonly GameObject _rotateGroup;
        private readonly List<(Collider collider, int axis)> _arrows = new List<(Collider, int)>();
        private readonly List<(Collider collider, int axis)> _rings = new List<(Collider, int)>();
        private readonly Vector3 _originalScale;
        private readonly float _sizeOnScreen;
        private HandleMode _mode = HandleMode.Move;

        public override Transform Root => _root != null ? _root.transform : null;

        internal VabHandles(GameObject root, VabHandleTemplate template)
        {
            _root = root;
            _moveGroup = root.transform.Find(template.MoveGroupPath)?.gameObject;
            _rotateGroup = root.transform.Find(template.RotateGroupPath)?.gameObject;
            foreach ((string path, int axis) in template.Arrows) Add(path, axis, _arrows);
            foreach ((string path, int axis) in template.Rings) Add(path, axis, _rings);
            _originalScale = root.transform.localScale;
            _sizeOnScreen = template.SizeOnScreen;
        }

        public override void SetMode(HandleMode mode)
        {
            _mode = mode;
            if (_moveGroup != null) _moveGroup.SetActive(mode == HandleMode.Move);
            if (_rotateGroup != null) _rotateGroup.SetActive(mode == HandleMode.Rotate);
        }

        public override void SetVisible(bool visible)
        {
            if (_root == null) return;
            _root.SetActive(visible);
            if (visible) SetMode(_mode);
        }

        public override void Follow(Vector3 position, Quaternion rotation, Camera camera)
        {
            if (_root == null) return;
            _root.transform.SetPositionAndRotation(position, rotation);
            _root.transform.localScale = _originalScale * ScreenSizeScale(position, camera, _sizeOnScreen);
        }

        public override bool TryPick(Ray ray, out HandleHit hit)
        {
            hit = default;
            if (_root == null || !_root.activeInHierarchy) return false;
            float nearest = float.MaxValue;
            foreach ((Collider collider, int axis) in _mode == HandleMode.Move ? _arrows : _rings)
            {
                if (collider == null || !collider.gameObject.activeInHierarchy) continue;
                if (collider.Raycast(ray, out RaycastHit h, PickDistance) && h.distance < nearest)
                {
                    nearest = h.distance;
                    hit = new HandleHit(_mode, axis);
                }
            }
            return nearest < float.MaxValue;
        }

        public override void Dispose()
        {
            if (_root != null) Object.Destroy(_root);
        }

        private void Add(string path, int axis, List<(Collider, int)> into)
        {
            Collider collider = _root.transform.Find(path)?.GetComponentInChildren<Collider>(true);
            if (collider != null) into.Add((collider, axis));
        }
    }

    /// <summary>
    /// Last-resort handles when the VAB's can't be had (always logged as an error): three coloured rings and three
    /// arrows drawn as lines, on top of everything, with the same overlay material settings the game's own debug shapes
    /// use. Picked by maths rather than colliders.
    /// </summary>
    internal sealed class DrawnHandles : ManipulationHandles
    {
        private const float SizeOnScreen = 0.06f;
        private const float RingRadius = 1f;
        private const float ArrowLength = 1.2f;
        private const float LineWidth = 0.04f;
        private const float PickTolerance = 0.12f;
        private const int RingSegments = 64;

        private static readonly Color[] AxisColors = { new Color(0.9f, 0.25f, 0.25f), new Color(0.3f, 0.85f, 0.3f), new Color(0.3f, 0.5f, 1f) };

        private readonly GameObject _root;
        private readonly GameObject _moveGroup;
        private readonly GameObject _rotateGroup;
        private HandleMode _mode = HandleMode.Move;

        public override Transform Root => _root != null ? _root.transform : null;

        public DrawnHandles(int layer)
        {
            _root = new GameObject("MunShot handles (drawn)");
            _moveGroup = new GameObject("move");
            _rotateGroup = new GameObject("rotate");
            _moveGroup.transform.SetParent(_root.transform, false);
            _rotateGroup.transform.SetParent(_root.transform, false);

            for (int axis = 0; axis < 3; axis++)
            {
                Material material = OverlayMaterial(AxisColors[axis]);
                Vector3 direction = Axis(axis);
                AddLine(_moveGroup, $"arrow {"xyz"[axis]}", material, new[] { Vector3.zero, direction * ArrowLength });

                Vector3 u = Axis((axis + 1) % 3), v = Axis((axis + 2) % 3);
                var points = new Vector3[RingSegments + 1];
                for (int i = 0; i <= RingSegments; i++)
                {
                    float a = i * Mathf.PI * 2f / RingSegments;
                    points[i] = (u * Mathf.Cos(a) + v * Mathf.Sin(a)) * RingRadius;
                }
                AddLine(_rotateGroup, $"ring {"xyz"[axis]}", material, points);
            }

            _root.SetLayerRecursive(layer);
            _root.SetActive(false);
        }

        public override void SetMode(HandleMode mode)
        {
            _mode = mode;
            _moveGroup.SetActive(mode == HandleMode.Move);
            _rotateGroup.SetActive(mode == HandleMode.Rotate);
        }

        public override void SetVisible(bool visible)
        {
            if (_root == null) return;
            _root.SetActive(visible);
            if (visible) SetMode(_mode);
        }

        public override void Follow(Vector3 position, Quaternion rotation, Camera camera)
        {
            if (_root == null) return;
            _root.transform.SetPositionAndRotation(position, rotation);
            _root.transform.localScale = Vector3.one * ScreenSizeScale(position, camera, SizeOnScreen);
        }

        public override bool TryPick(Ray ray, out HandleHit hit)
        {
            hit = default;
            if (_root == null || !_root.activeInHierarchy) return false;
            Transform t = _root.transform;
            float scale = t.lossyScale.x;
            float best = PickTolerance * scale;
            bool found = false;
            for (int axis = 0; axis < 3; axis++)
            {
                Vector3 direction = t.TransformDirection(Axis(axis));
                float miss;
                if (_mode == HandleMode.Rotate)
                {
                    var plane = new Plane(direction, t.position);
                    if (!plane.Raycast(ray, out float enter)) continue;
                    miss = Mathf.Abs((ray.GetPoint(enter) - t.position).magnitude - RingRadius * scale);
                }
                else
                {
                    miss = DistanceRayToSegment(ray, t.position, t.position + direction * ArrowLength * scale);
                }
                if (miss < best)
                {
                    best = miss;
                    hit = new HandleHit(_mode, axis);
                    found = true;
                }
            }
            return found;
        }

        public override void Dispose()
        {
            if (_root != null) Object.Destroy(_root);
        }

        private static Vector3 Axis(int axis) => axis == 0 ? Vector3.right : axis == 1 ? Vector3.up : Vector3.forward;

        private static void AddLine(GameObject parent, string name, Material material, Vector3[] points)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent.transform, false);
            var line = go.AddComponent<LineRenderer>();
            line.useWorldSpace = false;
            line.sharedMaterial = material;
            line.widthMultiplier = LineWidth;
            line.positionCount = points.Length;
            line.SetPositions(points);
            line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            line.receiveShadows = false;
        }

        // As the game's DebugShapes overlay material: drawn last, on top of everything, both sides, alpha blended
        private static Material OverlayMaterial(Color color)
        {
            Shader shader = Shader.Find("Hidden/Internal-Colored") ?? Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color");
            var material = new Material(shader) { color = color, renderQueue = 4000 };
            SetIfPresent(material, "_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
            SetIfPresent(material, "_ZWrite", 0);
            SetIfPresent(material, "_Cull", (int)UnityEngine.Rendering.CullMode.Off);
            SetIfPresent(material, "_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            SetIfPresent(material, "_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            return material;
        }

        private static void SetIfPresent(Material material, string property, int value)
        {
            if (material.HasProperty(property)) material.SetInt(property, value);
        }

        private static float DistanceRayToSegment(Ray ray, Vector3 a, Vector3 b)
        {
            Vector3 u = ray.direction, v = b - a, w = ray.origin - a;
            float uu = Vector3.Dot(u, u), uv = Vector3.Dot(u, v), vv = Vector3.Dot(v, v), uw = Vector3.Dot(u, w), vw = Vector3.Dot(v, w);
            float d = uu * vv - uv * uv;
            float s = d > 1e-6f ? Mathf.Clamp01((uu * vw - uv * uw) / d) : 0f;
            float r = Mathf.Max(0f, (uv * s - uw) / uu);
            return (ray.origin + u * r - (a + v * s)).magnitude;
        }
    }
}
