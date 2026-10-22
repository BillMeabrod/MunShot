using System;
using System.Collections.Generic;
using KSP.Game;
using KSP.Sim.impl;
using UnityEngine;

namespace MunShot.Posing
{
    /// <summary>
    /// The kerbals brought into photo mode. Bringing a kerbal in builds a <see cref="PhotoDouble"/> in front of the
    /// camera and, once it's built, hides the original if they're in the scene (<see cref="KerbalHider"/>); kerbals
    /// elsewhere in the roster are built from their roster record alone. Each double has its own
    /// <see cref="PosePlayer"/>. Doubles are moved and rotated with a <see cref="DoubleManipulator"/>, and dropped onto
    /// the surface below with <see cref="DropToSurface"/>. The real kerbals are never moved, so <see cref="Close"/> only
    /// has to destroy the doubles and un-hide the originals for everything to be exactly as it was.
    /// </summary>
    public sealed class PhotoSession : IDisposable
    {
        /// <summary>A kerbal in the session: their double, the hider for their original, and the double's pose player.</summary>
        public sealed class Entry
        {
            public PhotoDouble Double { get; }
            /// <summary>Hides the original while the double stands in; null for a kerbal who isn't in the scene.</summary>
            public KerbalHider Hider { get; }
            public PosePlayer Player { get; }
            public bool IsReady => Double.State == PhotoDouble.BuildState.Ready;

            /// <summary>Which way is up where the double stands: away from the body the original's vessel orbits.</summary>
            public Vector3 Up { get; }

            internal Transform[] Bones;
            internal KerbalHitShape HitShape;

            internal Entry(PhotoDouble photoDouble, KerbalHider hider, PosePlayer player, Vector3 up)
            {
                Double = photoDouble;
                Hider = hider;
                Player = player;
                Up = up;
            }

            internal Transform Root => Double.Model != null ? Double.Model.transform : null;
        }

        // Where a double appears: this far in front of the camera, on whatever surface is below within reach
        private const float DistanceInFront = 3f;
        private const float GroundSearchAbove = 3f;
        private const float GroundSearchBelow = 30f;

        // How far below a double's lowest point a surface can be for Snap to surface: only to settle a kerbal onto the
        // ground just below, not to drop one from a height
        private const float DropReach = 1f;

        private readonly PhotoDoubleFactory _factory;
        private readonly Func<PosePlayer> _newPlayer;
        private readonly Action<string> _logInfo;
        private readonly Action<string> _logError;
        private readonly List<Entry> _entries = new List<Entry>();
        private Transform _ignoredInSurfaceSearch;

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
                // In the scene when listed, but gone since (vessel unloaded or destroyed): built from the roster like
                // anyone elsewhere, with nothing to hide
                bool hideOriginal = candidate.InScene && candidate.Model != null;
                if (candidate.InScene && !hideOriginal)
                    _logInfo($"Photo mode: {candidate} has left the scene since photo mode opened; building their double from the roster.");

                Transform parent = PhysicsSpaceFor(candidate);
                if (parent == null)
                {
                    _logError($"Photo mode: can't bring in {candidate}: there's no active vessel in the scene to stand them by.");
                    result?.Invoke($"Can't bring {candidate.Name} in without an active vessel; see the log.");
                    return;
                }
                Transform cam = camera.transform;
                Vector3 up = SurfaceSnap.PlanetUpAt(cam.position + cam.forward * DistanceInFront, cam.up);
                Place(candidate, camera, up, parent, out Vector3 position, out Quaternion rotation, out string placement);
                PhotoDouble photoDouble = _factory.Create(candidate, position, rotation, parent);
                var entry = new Entry(photoDouble, hideOriginal ? new KerbalHider(candidate.Model.gameObject) : null, _newPlayer(), up);
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

        /// <summary>
        /// The nearest built double under <paramref name="ray"/>, and where the ray meets it. Only the kerbal's body
        /// counts (<see cref="KerbalHitShape"/>), so a kerbal behind and just beside another can still be picked.
        /// </summary>
        public Entry PickDouble(Ray ray, out Vector3 point)
        {
            point = default;
            Entry nearest = null;
            float nearestDistance = float.MaxValue;
            foreach (Entry entry in _entries)
            {
                if (!entry.IsReady || entry.Root == null) continue;
                entry.HitShape ??= new KerbalHitShape(entry.Root);
                if (entry.HitShape.IsEmpty)
                {
                    _logError($"Photo mode: {entry.Double.Source.Name}'s double has no kerbal skeleton to click on.");
                    continue;
                }
                if (entry.HitShape.Raycast(ray, out float distance) && distance < nearestDistance)
                {
                    nearestDistance = distance;
                    nearest = entry;
                }
            }
            if (nearest != null) point = ray.GetPoint(nearestDistance);
            return nearest;
        }

        /// <summary>
        /// Drops (or raises) a double straight along its up until its lowest point rests on the first surface below,
        /// keeping its rotation. The lowest point is its lowest bone in the current pose and rotation, so a kerbal lying
        /// down or tipped over lands as well as one standing.
        /// </summary>
        /// <returns>False, with <paramref name="message"/> saying why, when there's nothing below within reach.</returns>
        public bool DropToSurface(Entry entry, out string message)
        {
            if (entry == null || !entry.IsReady || entry.Root == null)
            {
                message = "Bring a kerbal in first; Snap to surface drops the selected double.";
                return false;
            }

            entry.Bones ??= SurfaceSnap.FindBones(entry.Root);
            if (!SurfaceSnap.TryDrop(entry.Root, entry.Bones, entry.Up, DropReach, IgnoredInSurfaceSearch()))
            {
                message = $"Nothing to stand on within {DropReach:0.#} m below {entry.Double.Source.Name}.";
                return false;
            }
            message = $"{entry.Double.Source.Name} is standing on the surface.";
            return true;
        }

        /// <summary>
        /// Turns a double back to standing straight up (its up along the planet's up), still facing the way it faced,
        /// turning about the middle of its body so it doesn't jump. A double lying down ends up facing the way its head
        /// pointed.
        /// </summary>
        /// <returns>False, with <paramref name="message"/> saying why, when there's no built double to stand up.</returns>
        public bool StandUpright(Entry entry, out string message)
        {
            if (entry == null || !entry.IsReady || entry.Root == null)
            {
                message = "Bring a kerbal in first; Stand upright turns the selected double.";
                return false;
            }

            Transform root = entry.Root;
            Vector3 facing = Vector3.ProjectOnPlane(root.forward, entry.Up);
            if (facing.sqrMagnitude < 1e-4f) facing = Vector3.ProjectOnPlane(root.up, entry.Up);
            if (facing.sqrMagnitude < 1e-4f) facing = Vector3.ProjectOnPlane(root.right, entry.Up);
            Quaternion upright = Quaternion.LookRotation(facing.normalized, entry.Up);

            Vector3 centre = BoundsOf(entry, out Bounds bounds) ? bounds.center : root.position;
            Quaternion turn = upright * Quaternion.Inverse(root.rotation);
            root.SetPositionAndRotation(centre + turn * (root.position - centre), upright);
            message = $"{entry.Double.Source.Name} is standing upright.";
            return true;
        }

        /// <summary>Something surface searches should look through, e.g. the manipulation handles.</summary>
        public void SetIgnoredInSurfaceSearch(Transform ignored) => _ignoredInSurfaceSearch = ignored;

        /// <summary>The box around a double's visible body, if it has one yet.</summary>
        public static bool BoundsOf(Entry entry, out Bounds bounds)
        {
            bounds = default;
            bool any = false;
            if (entry?.Root == null) return false;
            foreach (Renderer renderer in entry.Root.GetComponentsInChildren<Renderer>())
            {
                if (!renderer.enabled || renderer is ParticleSystemRenderer) continue;
                if (!any) bounds = renderer.bounds;
                else bounds.Encapsulate(renderer.bounds);
                any = true;
            }
            return any;
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
                        entry.Hider?.Hide();
                        _logInfo($"Photo mode: {entry.Double.Source.Name}'s double is ready"
                                 + (entry.Hider != null ? "; the original is hidden." : " (built from the roster; they're not in the scene)."));
                        if (entry.Double.FaceWarning != null) _logError(entry.Double.FaceWarning);
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
            entry.Hider?.Restore();
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

        // In front of the camera, standing on the first surface below that isn't a hidden kerbal's own body, facing the
        // camera. Parented to the physics space the original lives in, so the double moves with the floating origin.
        private void Place(KerbalCandidate candidate, Camera camera, Vector3 up, Transform parent, out Vector3 position,
                           out Quaternion rotation, out string placement)
        {
            Transform cam = camera.transform;
            position = cam.position + cam.forward * DistanceInFront;

            List<Transform> ignore = IgnoredInSurfaceSearch();
            Transform ownBody = BodyOf(candidate);
            if (ownBody != null) ignore.Add(ownBody);
            bool grounded = SurfaceSnap.TryFindSurface(position + up * GroundSearchAbove, -up, GroundSearchAbove + GroundSearchBelow,
                                                       ignore, null, out RaycastHit hit);
            if (grounded) position = hit.point;

            rotation = FacingCamera(position, cam, up);
            placement = $"{(grounded ? "standing on the surface" : "floating")} {DistanceInFront:0}m in front of the camera, "
                        + $"under '{(parent != null ? parent.name : "<scene root>")}'";
        }

        private static Quaternion FacingCamera(Vector3 position, Transform camera, Vector3 up)
        {
            Vector3 toCamera = Vector3.ProjectOnPlane(camera.position - position, up);
            if (toCamera.sqrMagnitude < 1e-6f) toCamera = Vector3.ProjectOnPlane(-camera.forward, up);
            return Quaternion.LookRotation(toCamera.normalized, up);
        }

        // What surface searches look through: the bodies of the kerbals in the session who are on EVA (hidden, but
        // their colliders are still there; seated crew's vessels are real surfaces), and the handles
        private List<Transform> IgnoredInSurfaceSearch()
        {
            var ignored = new List<Transform>();
            foreach (Entry entry in _entries)
            {
                Transform body = BodyOf(entry.Double.Source);
                if (body != null) ignored.Add(body);
            }
            if (_ignoredInSurfaceSearch != null) ignored.Add(_ignoredInSurfaceSearch);
            return ignored;
        }

        private static Transform BodyOf(KerbalCandidate candidate) =>
            candidate.Model != null && candidate.Model.IsEVAKerbal
                ? candidate.Model.GetComponentInParent<VesselBehavior>()?.transform
                : null;

        // What a double lives under: the object vessels live under (the original's vessel's parent, or for a kerbal not
        // in the scene, the active vessel's), which the game moves when the floating origin shifts
        private static Transform PhysicsSpaceFor(KerbalCandidate candidate)
        {
            VesselBehavior vessel = candidate.InScene && candidate.Model != null
                ? candidate.Model.GetComponentInParent<VesselBehavior>()
                : null;
            if (vessel == null)
            {
                GameInstance game = GameManager.Instance?.Game;
                VesselComponent active = game?.ViewController?.GetActiveSimVessel();
                vessel = active != null ? game.ViewController.GetBehaviorIfLoaded(active) : null;
            }
            return vessel != null ? vessel.transform.parent : null;
        }
    }
}
