using System;
using System.Collections.Generic;
using KSP.Game;
using UnityEngine;

namespace MunShot.Posing
{
    /// <summary>A kerbal who can be brought into photo mode: one in the scene, or one elsewhere in the roster.</summary>
    public sealed class KerbalCandidate
    {
        /// <summary>
        /// The kerbal's model in the scene (on EVA, or in their seat), for a kerbal who was in the scene; null for one
        /// elsewhere. Unity-null once that model has left the scene.
        /// </summary>
        public Kerbal3DModel Model { get; }

        /// <summary>Whether the kerbal was in the scene when listed (so has a model there to hide while doubled).</summary>
        public bool InScene { get; }

        public KerbalInfo Info { get; }
        public string Name { get; }

        private KerbalCandidate(Kerbal3DModel model, bool inScene, KerbalInfo info)
        {
            Model = model;
            InScene = inScene;
            Info = info;
            Name = info.Attributes.GetFullName();
        }

        public static KerbalCandidate InTheScene(Kerbal3DModel model) => new KerbalCandidate(model, true, model.ThisKerbalInfo);

        public static KerbalCandidate Elsewhere(KerbalInfo info) => new KerbalCandidate(null, false, info);

        public override string ToString() => Name;
    }

    /// <summary>
    /// Finds the kerbals that can be brought into photo mode, in two groups:
    /// <list type="bullet">
    /// <item>in the scene: every built kerbal model that belongs to a part (on EVA or seated in a loaded vessel),
    /// wherever it is and whether or not it's in view, nearest first. Models without a part, like the game's portrait
    /// booth or MunShot's own photo doubles, are left out;</item>
    /// <item>elsewhere: everyone else in the game's roster (on other vessels, or at KSC), by name. Their doubles are
    /// built from the roster alone.</item>
    /// </list>
    /// </summary>
    public static class KerbalCandidates
    {
        public sealed class Listing
        {
            public List<KerbalCandidate> InScene { get; } = new List<KerbalCandidate>();
            public List<KerbalCandidate> Elsewhere { get; } = new List<KerbalCandidate>();
            public int Count => InScene.Count + Elsewhere.Count;
        }

        /// <summary>
        /// Everyone who can be brought in, the scene's kerbals nearest to <paramref name="from"/> first.
        /// <paramref name="logInfo"/> is told how many were found, and why any kerbal models were left out.
        /// </summary>
        public static Listing FindAll(Vector3 from, Action<string> logInfo, Action<string> logWarning)
        {
            var listing = new Listing();
            var inScene = new HashSet<KerbalInfo>();
            var distances = new Dictionary<KerbalCandidate, float>();
            int notBuilt = 0, noPart = 0;
            Kerbal3DModel[] models = UnityEngine.Object.FindObjectsByType<Kerbal3DModel>(FindObjectsInactive.Exclude);
            foreach (Kerbal3DModel model in models)
            {
                if (!model.CharacterExists || model.ThisKerbalInfo == null)
                {
                    notBuilt++;
                    continue;
                }
                // Not on a part: the portrait booth's kerbal, or a photo double
                if (model.partOwner == null)
                {
                    noPart++;
                    continue;
                }
                if (!inScene.Add(model.ThisKerbalInfo)) continue;

                var candidate = KerbalCandidate.InTheScene(model);
                distances[candidate] = Vector3.Distance(from, model.transform.position);
                listing.InScene.Add(candidate);
            }
            listing.InScene.Sort((a, b) => distances[a].CompareTo(distances[b]));

            GameInstance game = GameManager.Instance?.Game;
            KerbalRosterManager roster = game?.SessionManager?.KerbalRosterManager;
            if (roster == null)
            {
                logWarning?.Invoke("Photo mode: the game's kerbal roster isn't available; only kerbals in the scene can be brought in.");
            }
            else
            {
                foreach (KerbalInfo kerbal in roster.GetAllKerbals())
                    if (kerbal != null && !inScene.Contains(kerbal))
                        listing.Elsewhere.Add(KerbalCandidate.Elsewhere(kerbal));
                listing.Elsewhere.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCulture));
            }

            logInfo?.Invoke($"Photo mode: {listing.InScene.Count} kerbal(s) in the scene and {listing.Elsewhere.Count} elsewhere can be brought in "
                            + $"({models.Length} kerbal models in the scene: {notBuilt} not built yet, {noPart} not aboard anything, e.g. photo doubles).");
            return listing;
        }
    }
}
