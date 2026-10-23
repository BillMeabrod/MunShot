using System;
using System.Collections.Generic;
using KSP.Game;
using UnityEngine;

namespace MunShot.Posing
{
    /// <summary>
    /// Gives a posed kerbal eyelashes: the game's female eyes draw their lashes as part of their eyelid shapes (on the
    /// same head texture as everyone's), so a kerbal gets lashes by having its eyelid meshes swapped for the female
    /// ones. Only the drawn lids of the photo double are changed, and they get their own shapes back when released.
    /// The female eyes are loaded from the game's assets once, the first time they're needed.
    /// </summary>
    internal sealed class EyeLashes
    {
        public const string FemaleEyesAddress = "eyes_f_01.prefab";

        // The eyelid bones, and the part of the female lid meshes' names that says which lid they are
        private static readonly (string bone, string lid)[] Lids =
        {
            ("l_eye_lid_u", "_lu"), ("l_eye_lid_d", "_ld"), ("r_eye_lid_u", "_ru"), ("r_eye_lid_d", "_rd")
        };

        private static Dictionary<string, Mesh> _femaleLids;
        private static bool _loading, _failed;
        private static event Action<bool> Loaded;

        private readonly List<(MeshFilter filter, Mesh own)> _swapped = new List<(MeshFilter, Mesh)>();
        private bool _released;

        private EyeLashes()
        {
        }

        /// <summary>
        /// Gives the kerbal under <paramref name="kerbal"/> lashes, as soon as the female eyes are loaded (at once after
        /// the first time). Problems are logged and the kerbal keeps its own lids.
        /// </summary>
        public static EyeLashes Apply(Transform kerbal, Action<string> logInfo, Action<string> logError)
        {
            var lashes = new EyeLashes();
            EnsureLoaded(loaded =>
            {
                if (lashes._released || kerbal == null) return;
                if (loaded) lashes.Swap(kerbal, logInfo, logError);
                else logError($"Eyelashes: the female eyes ('{FemaleEyesAddress}') couldn't be loaded; '{kerbal.name}' keeps its own lids.");
            }, logError);
            return lashes;
        }

        /// <summary>Gives the kerbal its own lids back. Safe to call more than once, and before the lashes arrived.</summary>
        public void Release()
        {
            _released = true;
            foreach ((MeshFilter filter, Mesh own) in _swapped)
                if (filter != null)
                    filter.sharedMesh = own;
            _swapped.Clear();
        }

        private void Swap(Transform kerbal, Action<string> logInfo, Action<string> logError)
        {
            var done = new List<string>();
            foreach ((string boneName, string lid) in Lids)
            {
                Transform bone = Find(kerbal, KerbalRig.BonePrefix + boneName);
                MeshFilter filter = bone != null ? DrawnLid(bone) : null;
                if (filter == null)
                {
                    logError($"Eyelashes: '{kerbal.name}' has no drawn eyelid under '{KerbalRig.BonePrefix + boneName}'; that lid is left as it is.");
                    continue;
                }
                Mesh own = filter.sharedMesh;
                if (own != null && own.name.IndexOf("_f_", StringComparison.Ordinal) >= 0) continue;   // already a female lid
                _swapped.Add((filter, own));
                filter.sharedMesh = _femaleLids[lid];
                done.Add(boneName);
            }
            if (done.Count > 0) logInfo($"Eyelashes: gave '{kerbal.name}' the female lids on {string.Join(", ", done)}.");
        }

        // The lid that's drawn (the game keeps hidden spare copies under the bone too)
        private static MeshFilter DrawnLid(Transform bone)
        {
            foreach (MeshRenderer r in bone.GetComponentsInChildren<MeshRenderer>(true))
                if (r.enabled && r.gameObject.activeInHierarchy && !r.gameObject.name.StartsWith("MunShot", StringComparison.Ordinal))
                    return r.GetComponent<MeshFilter>();
            return null;
        }

        private static Transform Find(Transform root, string name)
        {
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
                if (t.name == name)
                    return t;
            return null;
        }

        private static void EnsureLoaded(Action<bool> done, Action<string> logError)
        {
            if (_femaleLids != null || _failed)
            {
                done(_femaleLids != null);
                return;
            }
            Loaded += done;
            if (_loading) return;
            _loading = true;

            var assets = GameManager.Instance?.Assets;
            if (assets == null)
            {
                Finish(null, "the game's asset provider isn't available", logError);
                return;
            }
            assets.Load<GameObject>(FemaleEyesAddress, eyes =>
            {
                if (eyes == null)
                {
                    Finish(null, $"'{FemaleEyesAddress}' did not load", logError);
                    return;
                }
                // The lid meshes are named like "mesh_eyelid_f_lu_original"; each is found by which lid it is
                var lids = new Dictionary<string, Mesh>(StringComparer.Ordinal);
                foreach (MeshFilter filter in eyes.GetComponentsInChildren<MeshFilter>(true))
                {
                    Mesh mesh = filter.sharedMesh;
                    if (mesh == null || mesh.name.IndexOf("eyelid_f", StringComparison.Ordinal) < 0) continue;
                    foreach ((string _, string lid) in Lids)
                        if (mesh.name.IndexOf("eyelid_f" + lid, StringComparison.Ordinal) >= 0 && !lids.ContainsKey(lid))
                            lids[lid] = mesh;
                }
                if (lids.Count < Lids.Length)
                {
                    Finish(null, $"'{FemaleEyesAddress}' has {lids.Count} of the 4 eyelid meshes (names with 'eyelid_f_lu', '_ld', '_ru', '_rd')", logError);
                    return;
                }
                Finish(lids, null, logError);
            });
        }

        private static void Finish(Dictionary<string, Mesh> lids, string failure, Action<string> logError)
        {
            _loading = false;
            _femaleLids = lids;
            _failed = lids == null;
            if (failure != null) logError($"Eyelashes: {failure}.");
            Action<bool> callbacks = Loaded;
            Loaded = null;
            callbacks?.Invoke(lids != null);
        }
    }
}
