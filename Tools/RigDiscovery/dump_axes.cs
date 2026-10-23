// Bone axes for one game prefab, loaded in memory from the game's bundles (nothing is added to the project): for every
// bone_kerbal_* transform, in the prefab's rest pose and in the prefab root's space (the kerbal faces +Z, up is +Y),
// where it is, its rest local rotation, which way each of its local axes points, and which way it points (towards its
// first child). Writes Tools/RigDiscovery/out/<address>.axes.txt, the exact rotations and positions to
// <address>.axes.quats.txt, and
// returns a summary.
// Run through run.sh, which fills in __ADDRESS__ (e.g. body_spacesuit_01.prefab).
// eval_file body: no using directives, fully qualified names only.
string ADDRESS = "__ADDRESS__";
string OUT = System.IO.Path.GetFullPath(System.IO.Path.Combine("Tools", "RigDiscovery", "out", ADDRESS + ".axes.txt"));
System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(OUT));

var SourceT = typeof(Ksp2UnityTools.Editor.LinkedAddressables.LinkedAddressableEditorCatalog).Assembly.GetType("Ksp2UnityTools.Editor.LinkedAddressables." + "LinkedAddressableEditorSource", true);
var BF = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
System.Action Begin = () => SourceT.GetMethod("BeginMaterialization", BF).Invoke(null, null);
System.Action End = () => SourceT.GetMethod("EndMaterialization", BF).Invoke(null, null);
System.Func<object, UnityEngine.Object> LoadMain = d => (UnityEngine.Object)SourceT.GetMethod("LoadMainAsset", BF).Invoke(null, new[] { d });
Ksp2UnityTools.Editor.LinkedAddressables.LinkedAddressableEditorCatalog.EnsureLoaded();
Ksp2UnityTools.Editor.LinkedAddressables.LinkedAddressableCatalogEntry entry = null;
foreach (var e in Ksp2UnityTools.Editor.LinkedAddressables.LinkedAddressableEditorCatalog.Entries)
    if (e.Address == ADDRESS && e.AssetType == typeof(UnityEngine.GameObject)) { entry = e; break; }
if (entry == null) return $"[MunShot] Axes dump: no GameObject entry with address '{ADDRESS}' in the linked catalog.";

var sb = new System.Text.StringBuilder();
// Exact values for tools: bone, local quaternion (x y z w), root-space quaternion (x y z w), parent, root-space position
// (x y z, metres)
var quats = new System.Text.StringBuilder();
quats.AppendLine("# bone lx ly lz lw rx ry rz rw parent px py pz");
int bones = 0;
Begin();
try
{
    var descriptor = Ksp2UnityTools.Editor.LinkedAddressables.LinkedAddressableAssetUtility.CreateDescriptor(entry.CatalogId, entry.Address, entry.Location);
    var root = LoadMain(descriptor) as UnityEngine.GameObject;
    if (root == null) return $"[MunShot] Axes dump: LoadMainAsset returned no GameObject for '{ADDRESS}'.";
    var r = root.transform;

    System.Func<UnityEngine.Vector3, string> V = v => $"({v.x,6:0.00},{v.y,6:0.00},{v.z,6:0.00})";
    // The nearest world axis a direction is, e.g. "+Y" (up) or "-X", or "~" when it's well off every axis
    System.Func<UnityEngine.Vector3, string> Name = v =>
    {
        float ax = UnityEngine.Mathf.Abs(v.x), ay = UnityEngine.Mathf.Abs(v.y), az = UnityEngine.Mathf.Abs(v.z);
        float m = UnityEngine.Mathf.Max(ax, UnityEngine.Mathf.Max(ay, az));
        string n = m == ax ? (v.x > 0 ? "+X" : "-X") : m == ay ? (v.y > 0 ? "+Y" : "-Y") : (v.z > 0 ? "+Z" : "-Z");
        return m > 0.9f ? n : m > 0.7f ? "~" + n : "~";
    };

    sb.AppendLine($"# Bone axes: {ADDRESS}  (rest pose, in the prefab root's space: +X = kerbal's left or right, +Y up, +Z forward; Unity {UnityEngine.Application.unityVersion})");
    sb.AppendLine("# bone | parent | position | rest local euler | local X, Y, Z as world directions | points along (to first child)");
    foreach (var t in root.GetComponentsInChildren<UnityEngine.Transform>(true))
    {
        if (!t.name.StartsWith("bone_kerbal_")) continue;
        bones++;
        UnityEngine.Vector3 x = r.InverseTransformDirection(t.right), y = r.InverseTransformDirection(t.up), z = r.InverseTransformDirection(t.forward);
        string along = "-";
        foreach (UnityEngine.Transform c in t)
        {
            if (!c.name.StartsWith("bone_kerbal_")) continue;
            UnityEngine.Vector3 d = r.InverseTransformPoint(c.position) - r.InverseTransformPoint(t.position);
            if (d.sqrMagnitude < 1e-8f) continue;
            d.Normalize();
            along = $"{V(d)} {Name(d)} -> {c.name.Replace("bone_kerbal_", "")}";
            break;
        }
        sb.AppendLine($"{t.name.Replace("bone_kerbal_", ""),-22} | {(t.parent ? t.parent.name.Replace("bone_kerbal_", "") : "-"),-18} | pos {V(r.InverseTransformPoint(t.position))} | euler {V(t.localEulerAngles)} | X {V(x)} {Name(x),-3} Y {V(y)} {Name(y),-3} Z {V(z)} {Name(z),-3} | {along}");
        UnityEngine.Quaternion lq = t.localRotation, wq = UnityEngine.Quaternion.Inverse(r.rotation) * t.rotation;
        UnityEngine.Vector3 rp = r.InverseTransformPoint(t.position);
        quats.AppendLine($"{t.name} {lq.x:R} {lq.y:R} {lq.z:R} {lq.w:R} {wq.x:R} {wq.y:R} {wq.z:R} {wq.w:R} {(t.parent ? t.parent.name : "-")} {rp.x:R} {rp.y:R} {rp.z:R}");
    }
}
finally { End(); }

System.IO.File.WriteAllText(OUT, sb.ToString());
System.IO.File.WriteAllText(System.IO.Path.ChangeExtension(OUT, ".quats.txt"), quats.ToString());
return $"[MunShot] Axes dump: {bones} bones from '{ADDRESS}' -> {OUT}";
