using System;
using System.Collections.Generic;
using KSP.Game;
using KSP.Sim.impl;
using UnityEngine;

namespace MunShot.Posing
{
    /// <summary>
    /// The kerbals brought into photo mode. Bringing a kerbal in builds a <see cref="PhotoDouble"/> in front of the
    /// camera and, once it's built, hides the original (<see cref="KerbalHider"/>); each double has its own
    /// <see cref="PosePlayer"/>. The real kerbals are never moved, so <see cref="Close"/> only has to destroy the doubles
    /// and un-hide the originals for everything to be exactly as it was.
    /// </summary>
    public sealed class PhotoSession : IDisposable
    {
        /// <summary>A kerbal in the session: their double, the hider for their original, and the double's pose player.</summary>
        public sealed class Entry
        {
            public PhotoDouble Double { get; }
            public KerbalHider Hider { get; }
            public PosePlayer Player { get; }
            public bool IsReady => Double.State == PhotoDouble.BuildState.Ready;

            internal Entry(PhotoDouble photoDouble, KerbalHider hider, PosePlayer player)
            {
                Double = photoDouble;
                Hider = hider;
                Player = player;
            }
        }

        // Where a double appears: this far in front of the camera, on whatever surface is below within reach
        private const float DistanceInFront = 3f;
        private const float GroundSearchAbove = 3f;
        private const float GroundSearchBelow = 30f;

        private readonly PhotoDoubleFactory _factory;
        private readonly Func<PosePlayer> _newPlayer;
        private readonly Action<string> _logInfo;
        private readonly Action<string> _logError;
        private readonly List<Entry> _entries = new List<Entry>();

        public IReadOnlyList<Entry> Entries => _entries;

        /// <summary>The kerbal being posed: the last one brought in, or the one picked since.</summary>
        public Entry Selected { get; private set; }

        /// <summary>Raised when a double finishes building or fails, for the UI to update.</summary>
        public event Action<Entry> EntryChanged;

        public PhotoSession(PhotoDoubleFactory factory, Func<PosePlayer> newPlayer, Action<string> logInfo, Action<string> logError)
        {
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
            _newPlayer = newPlayer ?? throw new ArgumentNullException(nameof(newPlayer));
            _logInfo = logInfo ?? throw new ArgumentNullException(nameof(logInfo));
            _logError = logError ?? throw new ArgumentNullException(nameof(logError));
        }

        /// <summary>Whether <paramref name="kerbal"/> already has a double in the session.</summary>
        public bool Contains(KerbalInfo kerbal) => Find(kerbal) != null;

        /// <summary>
        /// Brings <paramref name="candidate"/> in front of <paramref name="camera"/>, or selects their double if they're
        /// already in. Building finishes over the next frames; <paramref name="result"/> gets a message either way.
        /// </summary>
        public void Bring(KerbalCandidate candidate, Camera camera, Action<string> result)
        {
            if (candidate == null) throw new ArgumentNullException(nameof(candidate));
            Entry existing = Find(candidate.Info);
            if (existing != null)
            {
                Select(existing);
                result?.Invoke($"{candidate.Name} is already in; selected.");
                return;
            }
            if (camera == null)
            {
                result?.Invoke("There's no camera to place the kerbal in front of.");
                return;
            }

            _factory.EnsureLoaded(loaded =>
            {
                if (!loaded)
                {
                    result?.Invoke("Could not load what photo doubles are built from; see the log.");
                    return;
                }
                if (candidate.Model == null)
                {
                    result?.Invoke($"{candidate.Name} is no longer in the scene.");
                    return;
                }

                Place(candidate, camera, out Vector3 position, out Quaternion rotation, out Transform parent, out string placement);
                PhotoDouble photoDouble = _factory.Create(candidate, position, rotation, parent);
                var entry = new Entry(photoDouble, new KerbalHider(candidate.Model.gameObject), _newPlayer());
                _entries.Add(entry);
                Select(entry);
                _logInfo($"Photo mode: building a double of {candidate}, {placement}.");
                result?.Invoke($"Bringing in {candidate.Name}…");
            });
        }

        public void Select(Entry entry)
        {
            if (entry != null && !_entries.Contains(entry)) throw new ArgumentException("That kerbal isn't in this session.", nameof(entry));
            Selected = entry;
        }

        /// <summary>Called every frame: finishes doubles being built (hiding their originals) and keeps poses held.</summary>
        public void Tick()
        {
            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                Entry entry = _entries[i];
                if (entry.Double.Tick())
                {
                    if (entry.IsReady)
                    {
                        // Shown and hidden in the same frame, so the kerbal is never seen twice
                        entry.Hider.Hide();
                        _logInfo($"Photo mode: {entry.Double.Source.Name}'s double is ready; the original is hidden.");
                    }
                    else
                    {
                        _logError(entry.Double.Failure);
                        Remove(entry);
                    }
                    EntryChanged?.Invoke(entry);
                }
                entry.Player.Tick();
            }
        }

        /// <summary>Destroys every double and un-hides every original: the scene is as it was before photo mode.</summary>
        public void Close()
        {
            for (int i = _entries.Count - 1; i >= 0; i--) Remove(_entries[i]);
            Selected = null;
        }

        public void Dispose() => Close();

        private void Remove(Entry entry)
        {
            entry.Player.Release();
            entry.Double.Destroy();
            entry.Hider.Restore();
            _entries.Remove(entry);
            if (Selected == entry) Selected = _entries.Count > 0 ? _entries[_entries.Count - 1] : null;
        }

        private Entry Find(KerbalInfo kerbal)
        {
            foreach (Entry entry in _entries)
                if (entry.Double.Source.Info == kerbal)
                    return entry;
            return null;
        }

        // In front of the camera, standing on the first surface below that isn't the kerbal's own EVA body, facing the
        // camera. "Up" is the EVA kerbal's own up (they stand on the local surface), or the camera's for seated crew.
        // Parented to the physics space the original lives in, so the double moves with the floating origin.
        private static void Place(KerbalCandidate candidate, Camera camera, out Vector3 position, out Quaternion rotation,
                                  out Transform parent, out string placement)
        {
            Transform cam = camera.transform;
            Vector3 up = candidate.Model.IsEVAKerbal ? candidate.Model.transform.up : cam.up;
            position = cam.position + cam.forward * DistanceInFront;

            Transform ownBody = candidate.Model.IsEVAKerbal ? candidate.Model.GetComponentInParent<VesselBehavior>()?.transform : null;
            bool grounded = false;
            RaycastHit[] hits = Physics.RaycastAll(position + up * GroundSearchAbove, -up, GroundSearchAbove + GroundSearchBelow,
                                                   Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            float nearest = float.MaxValue;
            foreach (RaycastHit hit in hits)
            {
                if (ownBody != null && hit.transform.IsChildOf(ownBody)) continue;
                if (hit.distance < nearest)
                {
                    nearest = hit.distance;
                    position = hit.point;
                    grounded = true;
                }
            }

            Vector3 toCamera = Vector3.ProjectOnPlane(cam.position - position, up);
            if (toCamera.sqrMagnitude < 1e-6f) toCamera = Vector3.ProjectOnPlane(-cam.forward, up);
            rotation = Quaternion.LookRotation(toCamera.normalized, up);

            parent = PhysicsSpaceOf(candidate.Model.transform);
            placement = $"{(grounded ? "standing on the surface" : "floating")} {DistanceInFront:0}m in front of the camera, "
                        + $"under '{(parent != null ? parent.name : "<scene root>")}'";
        }

        // The object the original's vessel lives under: the vessel's own parent, which the game moves when the
        // floating origin shifts
        private static Transform PhysicsSpaceOf(Transform kerbal)
        {
            VesselBehavior vessel = kerbal.GetComponentInParent<VesselBehavior>();
            return vessel != null ? vessel.transform.parent : null;
        }
    }
}
