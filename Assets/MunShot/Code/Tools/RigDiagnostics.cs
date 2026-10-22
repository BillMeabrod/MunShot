using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using MunShot.Posing;
using UnityEngine;

namespace MunShot.Tools
{
    /// <summary>
    /// Developer diagnostics for the assembled kerbal in the running game, where the rig differs from the prefabs:
    /// <see cref="DumpRig"/> writes where the parts ended up and which bones each mesh is skinned to, and
    /// <see cref="Snapshot"/> / <see cref="Compare"/> list the transforms that moved between two moments.
    /// Paths are relative to the kerbal's Animator, which is how animation clips address bones.
    /// </summary>
    public static class RigDiagnostics
    {
        // Moves smaller than these are animation noise, not a bone being driven
        private const float PositionEpsilon = 0.0001f;
        private const float AngleEpsilonDegrees = 0.05f;
        private const float ScaleEpsilon = 0.0001f;

        /// <summary>Every transform under an Animator at one moment, by path.</summary>
        public sealed class RigSnapshot
        {
            internal RigSnapshot(Animator animator, string label, Dictionary<string, (Vector3 pos, Quaternion rot, Vector3 scale)> poses)
            {
                Animator = animator;
                Label = label;
                Poses = poses;
            }

            public Animator Animator { get; }
            public string Label { get; }
            internal Dictionary<string, (Vector3 pos, Quaternion rot, Vector3 scale)> Poses { get; }
        }

        /// <summary>
        /// Writes the assembled rig under <paramref name="animator"/> to a text file next to the game's log and
        /// returns the file's path. The file has every skinned mesh with its root bone and bones, then the full
        /// transform tree.
        /// </summary>
        public static string DumpRig(Animator animator, string kerbalName, string clipDescription)
        {
            if (animator == null) throw new ArgumentNullException(nameof(animator));

            Transform root = animator.transform;
            var report = new StringBuilder();
            report.AppendLine($"# MunShot live rig dump: {kerbalName}");
            report.AppendLine($"Animator: {TransformPath.From(null, root)} (avatar '{(animator.avatar ? animator.avatar.name : "<none>")}', "
                              + $"controller '{(animator.runtimeAnimatorController ? animator.runtimeAnimatorController.name : "<none>")}')");
            report.AppendLine($"Held clip: {clipDescription}");
            report.AppendLine();

            report.AppendLine("## Skinned meshes (paths relative to the Animator; '!' marks a bone outside it)");
            foreach (SkinnedMeshRenderer smr in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                Mesh mesh = smr.sharedMesh;
                report.AppendLine($"- {PathFrom(root, smr.transform)}: mesh '{(mesh ? mesh.name : "<none>")}', "
                                  + $"active={smr.gameObject.activeInHierarchy}, enabled={smr.enabled}, "
                                  + $"rootBone={DescribeBone(root, smr.rootBone)}");
                var bones = new List<string>();
                foreach (Transform bone in smr.bones) bones.Add(DescribeBone(root, bone));
                report.AppendLine($"    bones ({bones.Count}): {string.Join(", ", bones)}");
            }

            report.AppendLine();
            report.AppendLine("## Transform tree under the Animator");
            WriteTree(report, root, 0);

            string directory = Path.Combine(Application.persistentDataPath, "MunShot");
            Directory.CreateDirectory(directory);
            string file = Path.Combine(directory,
                $"rig-dump-{Sanitize(kerbalName)}-{DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.txt");
            File.WriteAllText(file, report.ToString());
            return file;
        }

        /// <summary>Records every transform under <paramref name="animator"/> as it is now.</summary>
        public static RigSnapshot Snapshot(Animator animator, string label)
        {
            if (animator == null) throw new ArgumentNullException(nameof(animator));

            Transform root = animator.transform;
            var poses = new Dictionary<string, (Vector3, Quaternion, Vector3)>();
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
                poses[PathFrom(root, t)] = (t.localPosition, t.localRotation, t.localScale);
            return new RigSnapshot(animator, label, poses);
        }

        /// <summary>
        /// Lists the transforms that moved since <paramref name="before"/>, one line each, with how far they moved.
        /// Transforms added or removed since (e.g. a helmet toggled) are listed as such.
        /// </summary>
        public static string Compare(RigSnapshot before, string afterLabel)
        {
            if (before == null) throw new ArgumentNullException(nameof(before));
            if (before.Animator == null) return "The snapshot's kerbal no longer exists.";

            RigSnapshot after = Snapshot(before.Animator, afterLabel);
            var moved = new List<string>();
            foreach (KeyValuePair<string, (Vector3 pos, Quaternion rot, Vector3 scale)> entry in after.Poses)
            {
                if (!before.Poses.TryGetValue(entry.Key, out (Vector3 pos, Quaternion rot, Vector3 scale) old))
                {
                    moved.Add($"  + {entry.Key} (new)");
                    continue;
                }

                float distance = Vector3.Distance(old.pos, entry.Value.pos);
                float angle = Quaternion.Angle(old.rot, entry.Value.rot);
                float scale = Vector3.Distance(old.scale, entry.Value.scale);
                if (distance > PositionEpsilon || angle > AngleEpsilonDegrees || scale > ScaleEpsilon)
                    moved.Add($"  {entry.Key}: rot {angle.ToString("0.00", CultureInfo.InvariantCulture)}°, "
                              + $"pos {distance.ToString("0.0000", CultureInfo.InvariantCulture)}, "
                              + $"scale {scale.ToString("0.0000", CultureInfo.InvariantCulture)}");
            }
            foreach (string path in before.Poses.Keys)
                if (!after.Poses.ContainsKey(path))
                    moved.Add($"  - {path} (gone)");

            moved.Sort(StringComparer.Ordinal);
            return $"{moved.Count} of {after.Poses.Count} transforms moved from '{before.Label}' to '{afterLabel}':\n"
                   + string.Join("\n", moved);
        }

        /// <summary>
        /// Lists the bones a clip itself controls. <paramref name="applyAt"/> must pose the bones at a clip time
        /// immediately; all samples are taken within the same frame, so the game's live animation of other bones
        /// can't show up as movement. "Animates" are bones that move during the clip, with the largest rotation and
        /// move seen; "holds" are bones that stay still but differ from <paramref name="live"/>, the kerbal's own
        /// pose taken just before (e.g. a thumbs-up hand pinned for the whole loop). Both are grouped by area.
        /// </summary>
        public static string WhatMoves(Animator animator, string clipName, float clipLength, Func<float, bool> applyAt,
                                       RigSnapshot live, int samples = 9)
        {
            if (animator == null) throw new ArgumentNullException(nameof(animator));
            if (applyAt == null) throw new ArgumentNullException(nameof(applyAt));
            if (live == null) throw new ArgumentNullException(nameof(live));
            samples = Math.Max(2, samples);

            var snapshots = new List<RigSnapshot>();
            for (int i = 0; i < samples; i++)
            {
                float time = clipLength * i / (samples - 1);
                if (!applyAt(time))
                    return $"Could not sample '{clipName}' at {time.ToString("0.000", CultureInfo.InvariantCulture)}s (no pose held).";
                snapshots.Add(Snapshot(animator, $"{clipName} @ {time:0.000}s"));
            }

            // Largest change of each transform from the first sample
            var maxAngle = new Dictionary<string, float>();
            var maxMove = new Dictionary<string, float>();
            RigSnapshot first = snapshots[0];
            for (int s = 1; s < snapshots.Count; s++)
                foreach (KeyValuePair<string, (Vector3 pos, Quaternion rot, Vector3 scale)> entry in snapshots[s].Poses)
                {
                    if (!first.Poses.TryGetValue(entry.Key, out (Vector3 pos, Quaternion rot, Vector3 scale) start)) continue;
                    float angle = Quaternion.Angle(start.rot, entry.Value.rot);
                    float move = Vector3.Distance(start.pos, entry.Value.pos);
                    if (angle > AngleEpsilonDegrees || move > PositionEpsilon)
                    {
                        maxAngle[entry.Key] = Math.Max(angle, maxAngle.TryGetValue(entry.Key, out float a) ? a : 0f);
                        maxMove[entry.Key] = Math.Max(move, maxMove.TryGetValue(entry.Key, out float m) ? m : 0f);
                    }
                }

            var animates = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (string path in maxAngle.Keys)
                AddByArea(animates, path, $"({maxAngle[path].ToString("0.0", CultureInfo.InvariantCulture)}°"
                                          + (maxMove[path] > PositionEpsilon ? $", {Millimetres(maxMove[path])}" : string.Empty) + ")");

            // Still through the clip, but set away from the kerbal's own pose
            var holds = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
            int holdCount = 0;
            foreach (KeyValuePair<string, (Vector3 pos, Quaternion rot, Vector3 scale)> entry in first.Poses)
            {
                if (maxAngle.ContainsKey(entry.Key)) continue;
                if (!live.Poses.TryGetValue(entry.Key, out (Vector3 pos, Quaternion rot, Vector3 scale) own)) continue;
                float angle = Quaternion.Angle(own.rot, entry.Value.rot);
                float move = Vector3.Distance(own.pos, entry.Value.pos);
                if (angle <= AngleEpsilonDegrees && move <= PositionEpsilon) continue;
                AddByArea(holds, entry.Key, $"({angle.ToString("0.0", CultureInfo.InvariantCulture)}° from own pose"
                                            + (move > PositionEpsilon ? $", {Millimetres(move)}" : string.Empty) + ")");
                holdCount++;
            }

            var result = new StringBuilder();
            result.Append($"'{clipName}' ({clipLength.ToString("0.000", CultureInfo.InvariantCulture)}s, {samples} samples) "
                          + $"animates {maxAngle.Count} and holds {holdCount} transforms");
            AppendAreas(result, "animates", animates);
            AppendAreas(result, "holds", holds);
            return result.ToString();
        }

        private static void AddByArea(SortedDictionary<string, List<string>> byArea, string path, string detail)
        {
            string name = path.Substring(path.LastIndexOf('/') + 1);
            string area = AreaOf(name);
            if (!byArea.TryGetValue(area, out List<string> lines)) byArea[area] = lines = new List<string>();
            lines.Add($"{name.Replace(KerbalRig.BonePrefix, string.Empty)} {detail}");
        }

        private static void AppendAreas(StringBuilder result, string heading, SortedDictionary<string, List<string>> byArea)
        {
            if (byArea.Count == 0) return;
            result.Append($"\n {heading}:");
            foreach (KeyValuePair<string, List<string>> area in byArea)
            {
                area.Value.Sort(StringComparer.Ordinal);
                result.Append($"\n  {area.Key} ({area.Value.Count}): {string.Join(", ", area.Value)}");
            }
        }

        private static string Millimetres(float metres) =>
            (metres * 1000f).ToString("0.0", CultureInfo.InvariantCulture) + "mm";

        // Grouping for WhatMoves, by the kerbal skeleton's bone names
        private static string AreaOf(string transformName)
        {
            string n = transformName.ToLowerInvariant();
            if (!n.StartsWith(KerbalRig.BonePrefix)) return "other (not a kerbal bone)";
            if (n.Contains("thumb") || n.Contains("pointer") || n.Contains("middle") || n.Contains("pinky")) return "fingers";
            if (n.Contains("eye")) return "eyes";
            if (n.Contains("jaw") || n.Contains("lips") || n.Contains("teeth") || n.Contains("tongue")) return "mouth";
            if (n.EndsWith("_head") || n.Contains("neck") || n.Contains("helmet")) return "head";
            return "body";
        }

        private static void WriteTree(StringBuilder report, Transform t, int depth)
        {
            report.Append(' ', depth * 2).Append(t.name);
            if (!t.gameObject.activeSelf) report.Append("  (inactive)");
            report.AppendLine();
            for (int i = 0; i < t.childCount; i++) WriteTree(report, t.GetChild(i), depth + 1);
        }

        private static string DescribeBone(Transform root, Transform bone)
        {
            if (bone == null) return "<null>";
            return bone.IsChildOf(root) ? PathFrom(root, bone) : "!" + TransformPath.From(null, bone);
        }

        private static string PathFrom(Transform root, Transform t) => t == root ? "<animator>" : TransformPath.From(root, t);

        private static string Sanitize(string name)
        {
            var sb = new StringBuilder();
            foreach (char c in name ?? "kerbal")
                sb.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 || c == ' ' ? '_' : c);
            return sb.ToString();
        }
    }
}
