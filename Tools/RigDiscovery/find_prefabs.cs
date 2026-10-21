// Lists GameObject addresses in the game's Addressables catalog that match a
// case-insensitive regex, via the SDK's Linked Addressables editor catalog.
// Run through run.sh, which fills in __PATTERN__ (e.g. ^(body|head|eyes|helm)_).
// eval_file body: no using directives, fully qualified names only.
Ksp2UnityTools.Editor.LinkedAddressables.LinkedAddressableEditorCatalog.EnsureLoaded();
if (!Ksp2UnityTools.Editor.LinkedAddressables.LinkedAddressableEditorCatalog.IsLoaded)
    return "[MunShot] Prefab search: linked catalog not loaded. LastError: " + Ksp2UnityTools.Editor.LinkedAddressables.LinkedAddressableEditorCatalog.LastError
        + " | SourceRoot: " + Ksp2UnityTools.Editor.LinkedAddressables.LinkedAddressableEditorCatalog.SourceRoot;

var entries = Ksp2UnityTools.Editor.LinkedAddressables.LinkedAddressableEditorCatalog.Entries;
var pattern = new System.Text.RegularExpressions.Regex(@"__PATTERN__", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
var hits = new System.Collections.Generic.SortedSet<string>();
foreach (var e in entries)
{
    if (e.AssetType == typeof(UnityEngine.GameObject) && pattern.IsMatch(e.Address))
        hits.Add(e.Address);
}

var sb = new System.Text.StringBuilder();
sb.AppendLine($"entries={entries.Count} matches={hits.Count}");
int n = 0;
foreach (var h in hits) { if (n++ >= 500) break; sb.AppendLine(h); }
return sb.ToString();
