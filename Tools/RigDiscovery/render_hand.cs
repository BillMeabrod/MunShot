// Renders the space suit's left (or right) hand in its rest pose (a T-pose) with some bones turned, to see how a pose control
// looks without starting the game. Loads body_spacesuit_01.prefab in memory like dump_rig.cs, puts a copy far from
// the scene, turns the bones, renders it from three sides with a plain lit material, and writes PNGs to
// Tools/RigDiscovery/out/hand_<name>_<view>.png.
// Run through run.sh: __NAME__ is the file name part, __TURNS__ is "bone:axis:degrees;..." with axis x/y/z (own frame,
// after the rest rotation) or X/Y/Z (parent frame, before it), e.g. "l_thumb_01:y:-60;l_thumb_02:z:40".
// eval_file body: no using directives, fully qualified names only.
string NAME = "__NAME__";
string TURNS = "__TURNS__";
string ADDRESS = "body_spacesuit_01.prefab";
string OUTDIR = System.IO.Path.GetFullPath(System.IO.Path.Combine("Tools", "RigDiscovery", "out"));
System.IO.Directory.CreateDirectory(OUTDIR);

var SourceT = typeof(Ksp2UnityTools.Editor.LinkedAddressables.LinkedAddressableEditorCatalog).Assembly.GetType("Ksp2UnityTools.Editor.LinkedAddressables." + "LinkedAddressableEditorSource", true);
var BF = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
System.Action Begin = () => SourceT.GetMethod("BeginMaterialization", BF).Invoke(null, null);
System.Action End = () => SourceT.GetMethod("EndMaterialization", BF).Invoke(null, null);
System.Func<object, UnityEngine.Object> LoadMain = d => (UnityEngine.Object)SourceT.GetMethod("LoadMainAsset", BF).Invoke(null, new[] { d });
Ksp2UnityTools.Editor.LinkedAddressables.LinkedAddressableEditorCatalog.EnsureLoaded();
Ksp2UnityTools.Editor.LinkedAddressables.LinkedAddressableCatalogEntry entry = null;
foreach (var e in Ksp2UnityTools.Editor.LinkedAddressables.LinkedAddressableEditorCatalog.Entries)
    if (e.Address == ADDRESS && e.AssetType == typeof(UnityEngine.GameObject)) { entry = e; break; }
if (entry == null) return "[MunShot] Render hand: no body prefab in the linked catalog.";

var made = new System.Collections.Generic.List<UnityEngine.Object>();
var written = new System.Collections.Generic.List<string>();
Begin();
try
{
    var descriptor = Ksp2UnityTools.Editor.LinkedAddressables.LinkedAddressableAssetUtility.CreateDescriptor(entry.CatalogId, entry.Address, entry.Location);
    var prefab = LoadMain(descriptor) as UnityEngine.GameObject;
    if (prefab == null) return "[MunShot] Render hand: the body prefab didn't load.";

    var origin = new UnityEngine.Vector3(5000f, 5000f, 5000f);
    var body = UnityEngine.Object.Instantiate(prefab, origin, UnityEngine.Quaternion.identity);
    body.hideFlags = UnityEngine.HideFlags.DontSave;
    made.Add(body);
    body.SetActive(true);
    foreach (var t in body.GetComponentsInChildren<UnityEngine.Transform>(true)) t.gameObject.SetActive(true);

    // A plain lit material on everything, so the shape reads whatever the game's shaders do here
    var shader = UnityEngine.Shader.Find("Universal Render Pipeline/Lit") ?? UnityEngine.Shader.Find("Standard");
    var mat = new UnityEngine.Material(shader) { color = new UnityEngine.Color(0.85f, 0.85f, 0.85f) };
    made.Add(mat);
    var thumbMat = new UnityEngine.Material(shader) { color = new UnityEngine.Color(0.9f, 0.55f, 0.2f) };
    made.Add(thumbMat);
    foreach (var r in body.GetComponentsInChildren<UnityEngine.Renderer>(true))
    {
        var mats = new UnityEngine.Material[r.sharedMaterials.Length];
        for (int i = 0; i < mats.Length; i++) mats[i] = mat;
        r.sharedMaterials = mats;
        if (r is UnityEngine.SkinnedMeshRenderer s) { s.updateWhenOffscreen = true; s.forceMatrixRecalculationPerRender = true; }
    }

    var bones = new System.Collections.Generic.Dictionary<string, UnityEngine.Transform>();
    foreach (var t in body.GetComponentsInChildren<UnityEngine.Transform>(true))
        if (t.name.StartsWith("bone_kerbal_")) bones[t.name.Substring(12)] = t;

    // Hide everything but the suit (no helmet, no jetpack) so the hand is clear
    foreach (var r in body.GetComponentsInChildren<UnityEngine.Renderer>(true))
        if (r.name != "mesh_spacesuit_01") r.enabled = false;

    foreach (string turn in TURNS.Split(new[] { ';' }, System.StringSplitOptions.RemoveEmptyEntries))
    {
        string[] p = turn.Split(':');
        if (!bones.TryGetValue(p[0], out var bone)) return $"[MunShot] Render hand: no bone '{p[0]}'.";
        float deg = float.Parse(p[2], System.Globalization.CultureInfo.InvariantCulture);
        char a = p[1][0];
        var axis = char.ToLower(a) == 'x' ? UnityEngine.Vector3.right : char.ToLower(a) == 'y' ? UnityEngine.Vector3.up : UnityEngine.Vector3.forward;
        var q = UnityEngine.Quaternion.AngleAxis(deg, axis);
        bone.localRotation = char.IsUpper(a) ? q * bone.localRotation : bone.localRotation * q;
    }

    // The right hand when the turns are on it, else the left
    bool right = TURNS.Contains("r_");
    var hand = bones[right ? "r_hand" : "l_hand"];
    var focus = hand.position + new UnityEngine.Vector3(right ? 0.05f : -0.05f, -0.01f, 0f);

    var lightGo = new UnityEngine.GameObject("MunShot render light") { hideFlags = UnityEngine.HideFlags.DontSave };
    made.Add(lightGo);
    var light = lightGo.AddComponent<UnityEngine.Light>();
    light.type = UnityEngine.LightType.Directional;
    light.intensity = 1.2f;

    var camGo = new UnityEngine.GameObject("MunShot render camera") { hideFlags = UnityEngine.HideFlags.DontSave };
    made.Add(camGo);
    var cam = camGo.AddComponent<UnityEngine.Camera>();
    cam.clearFlags = UnityEngine.CameraClearFlags.SolidColor;
    cam.backgroundColor = new UnityEngine.Color(0.12f, 0.14f, 0.17f);
    cam.fieldOfView = 30f;
    cam.nearClipPlane = 0.01f;
    cam.farClipPlane = 10f;
    var rt = new UnityEngine.RenderTexture(640, 480, 24);
    made.Add(rt);
    cam.targetTexture = rt;

    // Front (the kerbal's face side), from below (the palm), from above (the back of the hand)
    var views = new (string name, UnityEngine.Vector3 dir, UnityEngine.Vector3 up)[]
    {
        ("front", new UnityEngine.Vector3(0, 0, 1), UnityEngine.Vector3.up),
        ("below", new UnityEngine.Vector3(0, -1, 0.15f), UnityEngine.Vector3.forward),
        ("above", new UnityEngine.Vector3(0, 1, 0.15f), UnityEngine.Vector3.forward),
    };
    var tex = new UnityEngine.Texture2D(640, 480, UnityEngine.TextureFormat.RGB24, false);
    made.Add(tex);
    foreach (var v in views)
    {
        cam.transform.position = focus + v.dir.normalized * 0.35f;
        cam.transform.LookAt(focus, v.up);
        lightGo.transform.rotation = UnityEngine.Quaternion.LookRotation(-v.dir.normalized + new UnityEngine.Vector3(0.3f, -0.3f, 0f));
        cam.Render();
        var prev = UnityEngine.RenderTexture.active;
        UnityEngine.RenderTexture.active = rt;
        tex.ReadPixels(new UnityEngine.Rect(0, 0, 640, 480), 0, 0);
        tex.Apply();
        UnityEngine.RenderTexture.active = prev;
        string file = System.IO.Path.Combine(OUTDIR, $"hand_{NAME}_{v.name}.png");
        System.IO.File.WriteAllBytes(file, tex.EncodeToPNG());
        written.Add(file);
    }
    cam.targetTexture = null;
}
finally
{
    foreach (var o in made) if (o != null) UnityEngine.Object.DestroyImmediate(o);
    End();
}
return "[MunShot] Render hand: " + string.Join(", ", written);
