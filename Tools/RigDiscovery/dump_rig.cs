// Rig dump for one game prefab, loaded in memory from the game's bundles through
// the SDK's Linked Addressables (nothing is added to the project). Writes the
// full report to Tools/RigDiscovery/out/<address>.txt and returns a summary.
// Run through run.sh, which fills in __ADDRESS__ (e.g. body_spacesuit_01.prefab).
// eval_file body: no using directives, fully qualified names only.
string ADDRESS = "__ADDRESS__";
string OUT = System.IO.Path.GetFullPath(System.IO.Path.Combine("Tools", "RigDiscovery", "out", ADDRESS + ".txt"));
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
if (entry == null) return $"[MunShot] Rig dump: no GameObject entry with address '{ADDRESS}' in the linked catalog (entries={Ksp2UnityTools.Editor.LinkedAddressables.LinkedAddressableEditorCatalog.Entries.Count}).";

var sb = new System.Text.StringBuilder();
Begin();
try
{
    var descriptor = Ksp2UnityTools.Editor.LinkedAddressables.LinkedAddressableAssetUtility.CreateDescriptor(entry.CatalogId, entry.Address, entry.Location);
    var root = LoadMain(descriptor) as UnityEngine.GameObject;
    if (root == null) return $"[MunShot] Rig dump: LoadMainAsset returned no GameObject for '{ADDRESS}'.";

    System.Func<UnityEngine.Transform, string> PathOf = t =>
    {
        var parts = new System.Collections.Generic.List<string>();
        for (var c = t; c != null && c != root.transform; c = c.parent) parts.Insert(0, c.name);
        return parts.Count == 0 ? "<root>" : string.Join("/", parts);
    };

    sb.AppendLine($"# Rig dump: {ADDRESS}  (root '{root.name}', Unity {UnityEngine.Application.unityVersion})");

    // 1. Component types across the hierarchy
    sb.AppendLine("\n## Component types (count)");
    var counts = new System.Collections.Generic.SortedDictionary<string, int>();
    foreach (var c in root.GetComponentsInChildren<UnityEngine.Component>(true))
    {
        string n = c == null ? "<missing script>" : c.GetType().FullName;
        counts[n] = counts.TryGetValue(n, out var k) ? k + 1 : 1;
    }
    foreach (var kv in counts) sb.AppendLine($"{kv.Value,4}  {kv.Key}");

    // 2. Non-Unity behaviours with their paths (likely rig drivers)
    sb.AppendLine("\n## Game behaviours by path");
    foreach (var mb in root.GetComponentsInChildren<UnityEngine.MonoBehaviour>(true))
    {
        if (mb == null) continue;
        var ns = mb.GetType().Namespace ?? "";
        if (ns.StartsWith("UnityEngine") || ns.StartsWith("TMPro")) continue;
        sb.AppendLine($"{PathOf(mb.transform)} :: {mb.GetType().FullName} (enabled={mb.enabled})");
    }

    // 3. Animators
    sb.AppendLine("\n## Animators");
    foreach (var an in root.GetComponentsInChildren<UnityEngine.Animator>(true))
    {
        var av = an.avatar;
        sb.AppendLine($"- {PathOf(an.transform)}: avatar={(av ? av.name : "none")} isHuman={(av ? av.isHuman.ToString() : "-")} isValid={(av ? av.isValid.ToString() : "-")} applyRootMotion={an.applyRootMotion} cullingMode={an.cullingMode} updateMode={an.updateMode}");
        var rac = an.runtimeAnimatorController;
        sb.AppendLine($"  controller={(rac ? rac.name + " [" + rac.GetType().FullName + "]" : "none")}");
        UnityEditor.Animations.AnimatorController ac = rac as UnityEditor.Animations.AnimatorController;
        if (ac == null && rac is UnityEngine.AnimatorOverrideController aoc) { ac = aoc.runtimeAnimatorController as UnityEditor.Animations.AnimatorController; sb.AppendLine($"  (override of {(aoc.runtimeAnimatorController ? aoc.runtimeAnimatorController.name : "none")})"); }
        if (ac != null)
        {
            sb.AppendLine("  parameters:");
            foreach (var p in ac.parameters) sb.AppendLine($"    {p.type} {p.name} (default f={p.defaultFloat} i={p.defaultInt} b={p.defaultBool})");
            sb.AppendLine("  layers:");
            foreach (var l in ac.layers)
            {
                sb.AppendLine($"    [{l.name}] weight={l.defaultWeight} blending={l.blendingMode} mask={(l.avatarMask ? l.avatarMask.name : "none")} ikPass={l.iKPass} syncedIndex={l.syncedLayerIndex}");
                if (l.stateMachine == null) continue;
                foreach (var cs in l.stateMachine.states)
                {
                    var m = cs.state.motion;
                    string motion = m == null ? "none" : (m is UnityEditor.Animations.BlendTree bt ? $"BlendTree({bt.blendType}; {bt.blendParameter}{(string.IsNullOrEmpty(bt.blendParameterY) ? "" : "," + bt.blendParameterY)}; {bt.children.Length} children)" : m.name);
                    sb.AppendLine($"      state {cs.state.name} -> {motion}");
                }
                foreach (var sub in l.stateMachine.stateMachines) sb.AppendLine($"      sub-machine {sub.stateMachine.name} ({sub.stateMachine.states.Length} states)");
            }
        }
        else if (rac != null)
        {
            sb.AppendLine($"  clips ({rac.animationClips.Length}):");
            foreach (var clip in rac.animationClips) sb.AppendLine($"    {clip.name} len={clip.length:0.##}");
        }
    }

    // 4. Skinned meshes
    sb.AppendLine("\n## SkinnedMeshRenderers");
    foreach (var smr in root.GetComponentsInChildren<UnityEngine.SkinnedMeshRenderer>(true))
    {
        var mesh = smr.sharedMesh;
        sb.AppendLine($"- {PathOf(smr.transform)}: mesh={(mesh ? mesh.name : "none")} verts={(mesh ? mesh.vertexCount : 0)} isReadable={(mesh ? mesh.isReadable.ToString() : "-")} bones={smr.bones.Length} rootBone={(smr.rootBone ? PathOf(smr.rootBone) : "none")} enabled={smr.enabled} active={smr.gameObject.activeInHierarchy}");
        var weighted = new System.Collections.Generic.List<string>();
        foreach (var b in smr.bones) weighted.Add(b ? b.name : "null");
        sb.AppendLine($"    weighted bones: {string.Join(", ", weighted)}");
        foreach (var mat in smr.sharedMaterials) sb.AppendLine($"    material {(mat ? mat.name : "none")} shader={(mat && mat.shader ? mat.shader.name : "none")}");
        if (mesh != null && mesh.blendShapeCount > 0)
        {
            sb.Append($"    blendShapes({mesh.blendShapeCount}):");
            for (int i = 0; i < mesh.blendShapeCount; i++) sb.Append(" " + mesh.GetBlendShapeName(i));
            sb.AppendLine();
        }
    }

    // 5. Other renderers (attached meshes: helmet, visor, eyes, etc.)
    sb.AppendLine("\n## Other renderers");
    foreach (var r in root.GetComponentsInChildren<UnityEngine.Renderer>(true))
    {
        if (r is UnityEngine.SkinnedMeshRenderer) continue;
        var mf = r.GetComponent<UnityEngine.MeshFilter>();
        var mats = new System.Collections.Generic.List<string>();
        foreach (var mat in r.sharedMaterials) mats.Add(mat ? $"{mat.name}<{(mat.shader ? mat.shader.name : "?")}>" : "none");
        sb.AppendLine($"- {PathOf(r.transform)} [{r.GetType().Name}] mesh={(mf && mf.sharedMesh ? mf.sharedMesh.name + " readable=" + mf.sharedMesh.isReadable : "-")} active={r.gameObject.activeInHierarchy} mats={string.Join(", ", mats)}");
    }

    // 6. Full transform tree
    sb.AppendLine("\n## Transform tree");
    void Walk(UnityEngine.Transform t, int depth)
    {
        sb.AppendLine(new string(' ', depth * 2) + t.name + (t.gameObject.activeSelf ? "" : "  (inactive)"));
        for (int i = 0; i < t.childCount; i++) Walk(t.GetChild(i), depth + 1);
    }
    Walk(root.transform, 0);

    System.IO.File.WriteAllText(OUT, sb.ToString());
    return $"[MunShot] Rig dump OK: {ADDRESS} -> {OUT} ({sb.Length} chars, {root.GetComponentsInChildren<UnityEngine.Transform>(true).Length} transforms)";
}
finally
{
    End();
}
