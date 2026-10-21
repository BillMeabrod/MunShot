using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MunShot.Posing
{
    /// <summary>
    /// Pose presets read from JSON files in the mod's Poses folder. File format:
    /// <code>
    /// { "version": 1, "poses": [
    ///   { "id": "wave", "name": "Wave",
    ///     "ground":  { "clip": "kerbal_emote_wave",    "time": 0 },
    ///     "jetpack": { "clip": "kerbal_jp_emote_wave", "time": 0 } } ] }
    /// </code>
    /// "ground" and "jetpack" are each optional, but a preset needs at least one. Clip names are checked when a pose
    /// is held, against the kerbal's own clips, not here.
    /// </summary>
    public static class PosePresetLibrary
    {
        public const int SupportedVersion = 1;

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
                    || !TryReadVersion(entry, "jetpack", where, warn, out ClipPose? jetpack))
                    continue;
                if (!ground.HasValue && !jetpack.HasValue)
                {
                    warn($"{where} has neither a \"ground\" nor a \"jetpack\" version; skipped.");
                    continue;
                }

                presets.Add(new PosePreset(id, name, ground, jetpack));
            }

            return presets;
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
