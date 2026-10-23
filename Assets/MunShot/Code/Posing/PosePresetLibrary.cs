using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace MunShot.Posing
{
    /// <summary>
    /// Pose presets read from JSON files in the mod's Poses folder. File format:
    /// <code>
    /// { "version": 1, "poses": [
    ///   { "id": "wave", "name": "Wave",
    ///     "ground":  { "clip": "kerbal_emote_wave",    "time": 0 },
    ///     "jetpack": { "clip": "kerbal_jp_emote_wave", "time": 0 },
    ///     "bones":   { "r_shoulder": [0, 90, 0], "jaw_d": [0, 0, 0, 1] },
    ///     "turns":   { "l_eye_lid_u": [0, 0, 15] },
    ///     "moves":   { "jaw_u_lips_l_corner": [0.03, 0, 0] },
    ///     "eyes":    { "image": "anime_teal.png", "size": 0.6 },
    ///     "lashes":  true } ] }
    /// </code>
    /// A preset needs a "ground" or "jetpack" version, or some "bones", "turns" or "moves"; the rest is optional.
    /// "bones" sets bones by hand over the clip (or over the game's animation, with no clip): each is the bone's name
    /// without "bone_kerbal_" and its local rotation. "turns" turns bones further from where the animation has them, by
    /// a rotation in the bone's parent's frame; "moves" slides them, by metres in the parent's frame. Rotations are
    /// Unity's Euler angles in degrees [x, y, z] (as the Inspector shows them) or a quaternion [x, y, z, w]. "eyes" lays
    /// an image from the same folder over the eyeballs (<see cref="EyeOverlay"/>), covering "size" of each; "lashes"
    /// gives the kerbal eyelashes (<see cref="EyeLashes"/>). Clip and
    /// bone names are checked when a pose is held, against the kerbal's own, not here.
    /// </summary>
    public static class PosePresetLibrary
    {
        public const int SupportedVersion = 1;

        /// <summary>
        /// Reads every pose file in <paramref name="folder"/>: <paramref name="firstFile"/> (the stock poses) first,
        /// then the others in name order, so custom poses can sit in their own files next to it. A pose whose id an
        /// earlier file already used is skipped and reported, as are problems within a file (see <see cref="Load"/>).
        /// </summary>
        public static IReadOnlyList<PosePreset> LoadFolder(string folder, string firstFile, Action<string> warn)
        {
            if (warn == null) throw new ArgumentNullException(nameof(warn));
            var presets = new List<PosePreset>();
            if (!Directory.Exists(folder))
            {
                warn($"No pose presets: the folder '{folder}' doesn't exist; the mod's install is incomplete.");
                return presets;
            }

            string first = Path.Combine(folder, firstFile);
            IEnumerable<string> files = Directory.GetFiles(folder, "*.json")
                .Where(f => !string.Equals(Path.GetFileName(f), firstFile, StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase);
            if (File.Exists(first)) files = new[] { first }.Concat(files);
            else warn($"Stock pose presets not found at '{first}'; the mod's install is incomplete.");

            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (string file in files)
            {
                foreach (PosePreset preset in Load(file, warn))
                {
                    if (ids.Add(preset.Id)) presets.Add(preset);
                    else warn($"'{file}': pose '{preset.Id}' has the same id as one in an earlier file; skipped.");
                }
            }
            return presets;
        }

        /// <summary>
        /// Reads the presets in <paramref name="file"/>. An entry with a problem is skipped and reported through
        /// <paramref name="warn"/> (with the file, entry number and what's wrong), so one mistake doesn't lose the
        /// rest of the file. A file that can't be read at all returns no presets, reported the same way.
        /// </summary>
        public static IReadOnlyList<PosePreset> Load(string file, Action<string> warn)
        {
            if (warn == null) throw new ArgumentNullException(nameof(warn));
            var presets = new List<PosePreset>();

            JObject root;
            try
            {
                root = JObject.Parse(File.ReadAllText(file));
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is JsonException)
            {
                warn($"Could not read pose presets from '{file}': {e.Message}");
                return presets;
            }

            int version = root.Value<int?>("version") ?? 0;
            if (version != SupportedVersion)
            {
                warn($"Pose presets '{file}' have version {version}; this MunShot reads version {SupportedVersion}. Skipping the file.");
                return presets;
            }

            if (!(root["poses"] is JArray entries))
            {
                warn($"Pose presets '{file}' have no \"poses\" list.");
                return presets;
            }

            var ids = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < entries.Count; i++)
            {
                string where = $"'{file}', pose #{i + 1}";
                if (!(entries[i] is JObject entry))
                {
                    warn($"{where} is not an object; skipped.");
                    continue;
                }

                string id = entry.Value<string>("id");
                if (string.IsNullOrWhiteSpace(id))
                {
                    warn($"{where} has no \"id\"; skipped.");
                    continue;
                }
                where += $" ('{id}')";
                if (!ids.Add(id))
                {
                    warn($"{where} repeats an id used earlier in the file; skipped.");
                    continue;
                }

                string name = entry.Value<string>("name");
                if (string.IsNullOrWhiteSpace(name))
                {
                    warn($"{where} has no \"name\"; skipped.");
                    continue;
                }

                if (!TryReadVersion(entry, "ground", where, warn, out ClipPose? ground)
                    || !TryReadVersion(entry, "jetpack", where, warn, out ClipPose? jetpack)
                    || !TryReadRotations(entry, "bones", where, warn, out Dictionary<string, Quaternion> set)
                    || !TryReadRotations(entry, "turns", where, warn, out Dictionary<string, Quaternion> turns)
                    || !TryReadMoves(entry, where, warn, out Dictionary<string, Vector3> moves)
                    || !TryReadEyes(entry, Path.GetDirectoryName(file), where, warn, out EyeStyle eyes))
                    continue;
                JToken lashesToken = entry["lashes"];
                if (lashesToken != null && lashesToken.Type != JTokenType.Boolean && lashesToken.Type != JTokenType.Null)
                {
                    warn($"{where}: \"lashes\" must be true or false; skipped.");
                    continue;
                }
                bool lashes = lashesToken != null && lashesToken.Type == JTokenType.Boolean && lashesToken.Value<bool>();
                if (!ground.HasValue && !jetpack.HasValue && set == null && turns == null && moves == null)
                {
                    warn($"{where} has no \"ground\" or \"jetpack\" version and no \"bones\", \"turns\" or \"moves\"; skipped.");
                    continue;
                }
                string both = set != null && turns != null ? set.Keys.FirstOrDefault(turns.ContainsKey) : null;
                if (both != null)
                {
                    warn($"{where}: bone '{both}' is in both \"bones\" and \"turns\"; give it one or the other; skipped.");
                    continue;
                }
                BonePose bones = set != null || turns != null || moves != null ? new BonePose(set, turns, moves) : null;

                presets.Add(new PosePreset(id, name, ground, jetpack, bones, eyes, lashes));
            }

            return presets;
        }

        // "bones" or "turns": missing is fine (null); present but malformed (or any bone malformed) skips the whole
        // preset, since a pose with some of its bones dropped wouldn't be the pose
        private static bool TryReadRotations(JObject entry, string key, string where, Action<string> warn,
                                             out Dictionary<string, Quaternion> rotations)
        {
            rotations = null;
            JToken token = entry[key];
            if (token == null || token.Type == JTokenType.Null) return true;
            if (!(token is JObject list) || !list.HasValues)
            {
                warn($"{where}: \"{key}\" must be an object of bone names and rotations, with at least one bone; skipped.");
                return false;
            }

            rotations = new Dictionary<string, Quaternion>(StringComparer.Ordinal);
            foreach (JProperty bone in list.Properties())
            {
                float[] values = bone.Value is JArray array
                                 && array.All(v => v.Type == JTokenType.Float || v.Type == JTokenType.Integer)
                    ? array.Select(v => v.Value<float>()).ToArray()
                    : null;
                if (values == null || (values.Length != 3 && values.Length != 4) || values.Any(float.IsNaN) || values.Any(float.IsInfinity))
                {
                    warn($"{where}: {key} '{bone.Name}' must be [x, y, z] Euler angles in degrees or an [x, y, z, w] quaternion; skipped.");
                    return false;
                }
                if (values.Length == 3)
                {
                    rotations[bone.Name] = Quaternion.Euler(values[0], values[1], values[2]);
                    continue;
                }
                var q = new Quaternion(values[0], values[1], values[2], values[3]);
                float length = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
                if (length < 1e-4f)
                {
                    warn($"{where}: {key} '{bone.Name}' has a zero quaternion; skipped.");
                    return false;
                }
                rotations[bone.Name] = new Quaternion(q.x / length, q.y / length, q.z / length, q.w / length);
            }
            return true;
        }

        // "moves": bone names and slides in metres [x, y, z]; missing is fine (null), malformed skips the preset
        private static bool TryReadMoves(JObject entry, string where, Action<string> warn, out Dictionary<string, Vector3> moves)
        {
            moves = null;
            JToken token = entry["moves"];
            if (token == null || token.Type == JTokenType.Null) return true;
            if (!(token is JObject list) || !list.HasValues)
            {
                warn($"{where}: \"moves\" must be an object of bone names and slides, with at least one bone; skipped.");
                return false;
            }

            moves = new Dictionary<string, Vector3>(StringComparer.Ordinal);
            foreach (JProperty bone in list.Properties())
            {
                float[] values = bone.Value is JArray array
                                 && array.All(v => v.Type == JTokenType.Float || v.Type == JTokenType.Integer)
                    ? array.Select(v => v.Value<float>()).ToArray()
                    : null;
                if (values == null || values.Length != 3 || values.Any(float.IsNaN) || values.Any(float.IsInfinity))
                {
                    warn($"{where}: move '{bone.Name}' must be [x, y, z] in metres; skipped.");
                    return false;
                }
                moves[bone.Name] = new Vector3(values[0], values[1], values[2]);
            }
            return true;
        }

        // "eyes": { "image": "<png next to the pose file>", "size": <0.1 to 0.95>, "depth": <0.5 to 1.5, default 1> }. Missing is fine (null); a
        // malformed entry or an image that can't be read skips the preset, so a pose never shows with half its look
        private static bool TryReadEyes(JObject entry, string folder, string where, Action<string> warn, out EyeStyle eyes)
        {
            eyes = null;
            JToken token = entry["eyes"];
            if (token == null || token.Type == JTokenType.Null) return true;
            string image = (token as JObject)?.Value<string>("image");
            JToken sizeToken = (token as JObject)?["size"];
            float size = sizeToken != null && (sizeToken.Type == JTokenType.Float || sizeToken.Type == JTokenType.Integer)
                ? sizeToken.Value<float>()
                : float.NaN;
            JToken depthToken = (token as JObject)?["depth"];
            float depth = depthToken == null || depthToken.Type == JTokenType.Null
                ? 1f
                : depthToken.Type == JTokenType.Float || depthToken.Type == JTokenType.Integer ? depthToken.Value<float>() : float.NaN;
            if (string.IsNullOrWhiteSpace(image) || !(size >= 0.1f && size <= 0.95f) || !(depth >= 0.5f && depth <= 1.5f))
            {
                warn($"{where}: \"eyes\" must be {{ \"image\": \"<file>.png\", \"size\": <0.1 to 0.95>, \"depth\": <0.5 to 1.5, "
                     + "optional> }; skipped.");
                return false;
            }
            if (Path.GetFileName(image) != image)
            {
                warn($"{where}: eye image '{image}' must be a file name in the poses folder, without folders; skipped.");
                return false;
            }

            Texture2D texture = LoadImage(Path.Combine(folder, image), out string problem);
            if (texture == null)
            {
                warn($"{where}: eye image '{image}' {problem}; skipped.");
                return false;
            }
            eyes = new EyeStyle(image, texture, size, depth);
            return true;
        }

        // Eye images, kept while their file is unchanged, since pose files are read again each time photo mode opens
        private static readonly Dictionary<string, (DateTime written, Texture2D texture)> Images =
            new Dictionary<string, (DateTime, Texture2D)>(StringComparer.OrdinalIgnoreCase);

        private static Texture2D LoadImage(string path, out string problem)
        {
            problem = null;
            if (!File.Exists(path))
            {
                problem = $"isn't there ('{path}')";
                return null;
            }
            DateTime written = File.GetLastWriteTimeUtc(path);
            if (Images.TryGetValue(path, out (DateTime written, Texture2D texture) cached) && cached.written == written && cached.texture != null)
                return cached.texture;

            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(path);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                problem = $"couldn't be read ({e.Message})";
                return null;
            }
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, true) { name = Path.GetFileName(path), wrapMode = TextureWrapMode.Clamp };
            if (!ImageConversion.LoadImage(texture, bytes))
            {
                UnityEngine.Object.Destroy(texture);
                problem = "isn't a PNG or JPG Unity can read";
                return null;
            }
            // A changed file gets a new texture; kerbals posed with the old one keep it until they're posed again
            Images[path] = (written, texture);
            return texture;
        }

        // Missing is fine (null); present but malformed skips the whole preset
        private static bool TryReadVersion(JObject entry, string key, string where, Action<string> warn, out ClipPose? pose)
        {
            pose = null;
            JToken token = entry[key];
            if (token == null || token.Type == JTokenType.Null) return true;

            if (!(token is JObject version))
            {
                warn($"{where}: \"{key}\" must be an object with \"clip\" and \"time\"; skipped.");
                return false;
            }

            string clip = version.Value<string>("clip");
            if (string.IsNullOrWhiteSpace(clip))
            {
                warn($"{where}: \"{key}\" has no \"clip\"; skipped.");
                return false;
            }

            JToken timeToken = version["time"];
            float time = 0f;
            if (timeToken != null && timeToken.Type != JTokenType.Null)
            {
                if (timeToken.Type != JTokenType.Float && timeToken.Type != JTokenType.Integer)
                {
                    warn($"{where}: \"{key}\".time must be a number of seconds; skipped.");
                    return false;
                }
                time = timeToken.Value<float>();
                if (float.IsNaN(time) || time < 0f)
                {
                    warn($"{where}: \"{key}\".time is {time}; it must be zero or more; skipped.");
                    return false;
                }
            }

            pose = new ClipPose(clip, time);
            return true;
        }
    }
}
