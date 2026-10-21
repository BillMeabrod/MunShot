# Rig discovery

Scripts for inspecting the game's kerbal prefabs from the command line, without adding anything to the project. They run inside the open MunShot editor through the Unity CLI (`unity command eval_file`), load prefabs in memory from the game's bundles with the SDK's Linked Addressables code, and unload them afterwards.

They live outside `Assets/` on purpose: they are `eval_file` snippets (a method body with a top-level `return`), not compilable classes.

## Requirements

- The MunShot editor open, with the `com.unity.pipeline` package (in `Packages/manifest.json`).
- `unity` (Unity Hub's CLI) on PATH.

## Usage

```bash
# Find prefab addresses (case-insensitive regex)
Tools/RigDiscovery/run.sh find '^(body|head|eyes|hair|helm)_'

# Dump one prefab's rig to Tools/RigDiscovery/out/<address>.txt (gitignored)
Tools/RigDiscovery/run.sh dump body_spacesuit_01.prefab
```

The dump lists component types, game behaviours by path, Animators (avatar, controller, layers and parameters when available), skinned meshes (readability, blend shapes, weighted bones), other renderers, and the full transform tree.

## How a kerbal is built (26w43a)

`eva_kerbal.prefab` holds the Animator and the driver scripts but no body. At runtime `KSP.Game.Kerbal3DModel` assembles the character from variety attributes: a `body_*` prefab with the `bone_kerbal_*` skeleton, plus `head_*`, `eyes_*`, `hair_*` and `helm_*` prefabs attached to named bones. Dump those parts to see the rig.

Animator controllers loaded from bundles come back without layers or parameters in the editor; read those in the running game.

## Notes

- `LinkedAddressableEditorSource` is internal to the SDK, so `dump_rig.cs` calls it through reflection. If an SDK update renames it, the script fails with a reflection error naming the missing type or method.
- Re-run the dumps after Redux snapshot updates to catch rig changes.
