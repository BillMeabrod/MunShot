// Animator probe for one game prefab: instantiates a hidden, unsaved copy in the
// editor, initializes its Animators by hand (Rebind + Update(0)) and reports
// layers, parameters and clips, then destroys the copy. Works where the controller
// asset itself comes back empty (bundle loads strip editor-side controller data).
// Clip curves are also stripped, so "curves" reads 0 for game clips.
// Writes Tools/RigDiscovery/out/<address>.animator.txt and returns a summary.
// Run through run.sh, which fills in __ADDRESS__ (e.g. eva_kerbal.prefab).
// eval_file body: no using directives, fully qualified names only.
string ADDRESS = "__ADDRESS__";
string OUT = System.IO.Path.GetFullPath(System.IO.Path.Combine("Tools", "RigDiscovery", "out", ADDRESS + ".animator.txt"));
System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(OUT));

// LinkedAddressableEditorSource is internal to the SDK: call it via reflection.
var SourceT = typeof(Ksp2UnityTools.Editor.LinkedAddressables.LinkedAddressableEditorCatalog).Assembly.GetType("Ksp2UnityTools.Editor.LinkedAddressables." + "LinkedAddressableEditorSource", true);
var BF = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
System.Action Begin = () => SourceT.GetMethod("BeginMaterialization", BF).Invoke(null, null);
System.Action End = () => SourceT.GetMethod("EndMaterialization", BF).Invoke(null, null);
System.Func<object, UnityEngine.Object> LoadMain = d => (UnityEngine.Object)SourceT.GetMethod("LoadMainAsset", BF).Invoke(null, new[] { d });
Ksp2UnityTools.Editor.LinkedAddressables.LinkedAddressableEditorCatalog.EnsureLoaded();
Ksp2UnityTools.Editor.LinkedAddressables.LinkedAddressableCatalogEntry entry = null;
foreach (var e in Ksp2UnityTools.Editor.LinkedAddressables.LinkedAddressableEditorCatalog.Entries)
    if (e.Address == ADDRESS && e.AssetType == typeof(UnityEngine.GameObject)) { entry = e; break; }
if (entry == null) return $"[MunShot] Animator probe: no GameObject entry with address '{ADDRESS}' in the linked catalog (entries={Ksp2UnityTools.Editor.LinkedAddressables.LinkedAddressableEditorCatalog.Entries.Count}).";

var sb = new System.Text.StringBuilder();
Begin();
try
{
    var descriptor = Ksp2UnityTools.Editor.LinkedAddressables.LinkedAddressableAssetUtility.CreateDescriptor(entry.CatalogId, entry.Address, entry.Location);
    var root = LoadMain(descriptor) as UnityEngine.GameObject;
    if (root == null) return $"[MunShot] Animator probe: LoadMainAsset returned no GameObject for '{ADDRESS}'.";

    if (root.GetComponentsInChildren<UnityEngine.Animator>(true).Length == 0)
        return $"[MunShot] Animator probe: '{ADDRESS}' has no Animator (body-part prefabs are driven by eva_kerbal.prefab's Animator).";

    sb.AppendLine($"# Animator probe: {ADDRESS}  (root '{root.name}', Unity {UnityEngine.Application.unityVersion})");
    var inst = (UnityEngine.GameObject)UnityEngine.Object.Instantiate(root);
    inst.hideFlags = UnityEngine.HideFlags.HideAndDontSave;
    try
    {
        foreach (var an in inst.GetComponentsInChildren<UnityEngine.Animator>(true))
        {
            an.gameObject.SetActive(true);
            an.Rebind();
            an.Update(0f);
            sb.AppendLine($"Animator on {an.name}: initialized={an.isInitialized} hasBoundPlayables={an.hasBoundPlayables} layerCount={an.layerCount} parameterCount={an.parameterCount}");
            for (int i = 0; i < an.layerCount; i++)
                sb.AppendLine($"  layer[{i}] {an.GetLayerName(i)} weight={an.GetLayerWeight(i)}");
            foreach (var p in an.parameters)
                sb.AppendLine($"  param {p.type} {p.name} (f={p.defaultFloat} i={p.defaultInt} b={p.defaultBool})");
            var rac = an.runtimeAnimatorController;
            if (rac != null)
            {
                sb.AppendLine($"  clips ({rac.animationClips.Length}):");
                var seen = new System.Collections.Generic.HashSet<string>();
                foreach (var clip in rac.animationClips)
                {
                    if (!seen.Add(clip.name)) continue;
                    var bindings = UnityEditor.AnimationUtility.GetCurveBindings(clip);
                    var objs = UnityEditor.AnimationUtility.GetObjectReferenceCurveBindings(clip);
                    var bones = new System.Collections.Generic.SortedSet<string>();
                    foreach (var b in bindings) bones.Add(b.path.Substring(b.path.LastIndexOf('/') + 1));
                    sb.AppendLine($"    {clip.name} len={clip.length:0.##} curves={bindings.Length} objCurves={objs.Length} events={clip.events.Length} bones=[{string.Join(" ", bones)}]");
                }
            }
        }
    }
    finally { UnityEngine.Object.DestroyImmediate(inst); }
    System.IO.File.WriteAllText(OUT, sb.ToString());
    return $"[MunShot] Animator probe OK: {ADDRESS} -> {OUT} ({sb.Length} chars)";
}
finally
{
    End();
}
