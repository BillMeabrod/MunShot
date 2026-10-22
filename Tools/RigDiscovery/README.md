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

# Read Animator layers, parameters and clips to out/<address>.animator.txt
Tools/RigDiscovery/run.sh animator eva_kerbal.prefab
```

The dump lists component types, game behaviours by path, Animators (avatar, controller, layers and parameters when available), skinned meshes (readability, blend shapes, weighted bones), other renderers, and the full transform tree.

The Animator probe instantiates a hidden, unsaved copy of the prefab, initializes its Animators by hand (`Rebind()` + `Update(0)`), and reports layer names and weights, parameters with defaults, and clips with length and event count. It works where the dump's Animator section comes back empty.

## How a kerbal is built (26w43a)

`eva_kerbal.prefab` holds the Animator and the driver scripts but no body. At runtime `KSP.Game.Kerbal3DModel` assembles the character from variety attributes: a `body_*` prefab with the `bone_kerbal_*` skeleton, plus `head_*`, `eyes_*`, `hair_*` and `helm_*` prefabs attached to named bones. Dump those parts to see the rig.

Animator controllers loaded from bundles come back without layers or parameters when read as assets; use `run.sh animator` instead. Clip curves are stripped too, so which bones a clip moves (and which `iEmote` index plays which emote) has to be checked in the running game.

## Findings (26w43a, 2026-10-21)

From these scripts and the in-game pose lab (`Alt+L`, on an EVA kerbal or photo mode's selected kerbal).

**Rig**
- One skeleton. At runtime the head, eyes and helmet are re-parented onto the body prefab's `bone_kerbal_*` bones (`body_spacesuit_01(Clone)` under the Animator), so one set of bones drives everything.
- Hands: four digits each (thumb, pointer, middle, pinky), three joints each, independent.
- Face is bone-driven, with no blend shapes: jaw up/down, lips and corners, teeth, a two-bone tongue, and per-side eye, upper lid and lower lid. No brow bones.

**Stock animations as poses**
- The EVA Animator (`animtree_kerbal_eva`, 5 layers: Body, Expressions, Eye_Symmetry, Gender, Blinking) holds 251 clips: 19 emotes plus jetpack variants, 5 expressions × 2, idles, locomotion, science, flag planting.
- A pose is a clip and a moment: `{clip, time}`. Holding it through our own Playables graph, sorted after the game's, overrides the game's animation and holds an exact frame. Releasing gives control straight back.
- A held clip only overrides the bones it animates. Everything else keeps the game's live animation (idle sway, eyes) unless the game's Animator is paused too ("Freeze game animation").
- Emotes are full performances: body, the gesturing hand's fingers, eyes and lids, and mouth. Expression clips (`kerbal_exp_*`) animate the mouth only (jaw, lips, tongue), and only by a few millimetres, so they're subtle on screen.
- "What moves" only counts bones that change during a clip; it also lists bones a clip holds still away from the kerbal's own pose (e.g. a thumbs-up hand pinned for the whole loop).

**Mixing clips by region (deferred to a later version)**
- Goal: take a region's bones from another clip, e.g. a happy mouth on a full-body emote, or a thumbs-up hand on the idle.
- Unity `AvatarMask` layers did not work: the override layers had no effect, most likely because the masks' paths don't match the generic avatar's bindings on the runtime-assembled kerbal.
- What works: after the Animator runs, sample the override clip onto the kerbal and restore every bone outside the region. Confirmed in game with a right-hand override: the fingers took the thumbs-up shape. The code was removed for v1; the core of it, in a component on the Animator's GameObject:

  ```csharp
  // Region bones: transforms under the Animator whose names match the region, e.g. bone_kerbal_r_{thumb,pointer,middle,pinky}_*
  private void LateUpdate()   // after the Animator has written this frame's pose
  {
      foreach (var o in overrides)                       // (clip, time, HashSet<Transform> regionBones)
      {
          for (int i = 0; i < transforms.Length; i++)    // every transform under the Animator
              saved[i] = (transforms[i].localPosition, transforms[i].localRotation, transforms[i].localScale);
          o.Clip.SampleAnimation(gameObject, o.Time);    // binds bones the same way the Animator does
          for (int i = 0; i < transforms.Length; i++)
              if (!o.RegionBones.Contains(transforms[i]))
                  (transforms[i].localPosition, transforms[i].localRotation, transforms[i].localScale) = saved[i];
      }
  }
  ```
- Open: finger-only hand regions leave the arm where the base clip puts it. A whole gesture needs arm regions (collar, shoulder, elbow, hand, fingers) next to the finger-only hand regions.
- Decision: v1 uses single stock clips as poses. Region mixing is a later-version feature.

## Notes

- `LinkedAddressableEditorSource` is internal to the SDK, so `dump_rig.cs` calls it through reflection. If an SDK update renames it, the script fails with a reflection error naming the missing type or method.
- Re-run the dumps after Redux snapshot updates to catch rig changes.
