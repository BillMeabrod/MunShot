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
    public sealed class PhotoSession
    {
        /// <summary>A kerbal in the session: their double, the hider for their original, and the double's pose player.</summary>
        public sealed class Entry
        {
            public PhotoDouble Double { get; }
            /// <summary>Hides the original while the double stands in; null for a kerbal who isn't in the scene.</summary>
            public KerbalHider Hider { get; }
            public PosePlayer Player { get; }
            public bool IsReady => Double.State == PhotoDouble.BuildState.Ready;

            /// <summary>
            /// Which way is up where the double stands: away from the body the original's vessel orbits. Only for
            /// standing it on the ground (<see cref="DropToSurface"/>); upright otherwise means upright on screen.
            /// </summary>
            public Vector3 Up { get; }

            /// <summary>The pose the double holds, or null for its own animation.</summary>
            public PosePreset Preset { get; internal set; }

            /// <summary>Whether the double's jetpack is deployed for flying, or folded away.</summary>
            public bool Jetpack { get; internal set; }

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

        // Where a double appears: where the camera looks, this far ahead, or just short of whatever it's looking at
        // that's nearer
        private const float DistanceInFront = 3f;
        private const float StandOffSurface = 0.5f;

        // How far below a double's lowest point a surface can be for Snap to surface, or below where a new double
        // appears for it to stand there: only to settle a kerbal onto the ground just below, not to drop one from a
        // height. The search starts a little above, in case the spot is just inside the surface
        private const float DropReach = 1f;
        private const float SettleFromAbove = 0.25f;

        // About half a kerbal's height: a floating double is placed with its middle where the camera looks, so it's in
        // the middle of the shot
        private const float HalfHeight = 0.55f;

        private readonly PhotoDoubleFactory _factory;
        private readonly Func<PosePlayer> _newPlayer;
        private readonly Action<string> _logInfo;
        private readonly Action<string> _logError;
        private readonly List<Entry> _entries = new List<Entry>();
        private Transform _ignoredInSurfaceSearch;

        // Counts closes, so a bring-in still loading when the session closes is dropped instead of landing in the scene
        // after photo mode has gone
        private int _generation;

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
                result?.Invoke(Localization.Format("MunShot/Status/AlreadyInSelected", candidate.Name));
                return;
            }
            if (camera == null)
            {
                result?.Invoke(Localization.Translate("MunShot/Status/NoCamera"));
                return;
            }

            int generation = _generation;
            _factory.EnsureLoaded(loaded =>
            {
                if (generation != _generation)
                {
                    _logInfo($"Photo mode: closed before {candidate}'s double could be built; not bringing them in.");
                    return;
                }
                if (!loaded)
                {
                    result?.Invoke(Localization.Translate("MunShot/Status/DoublesNotLoaded"));
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
                    result?.Invoke(Localization.Format("MunShot/Status/NoActiveVessel", candidate.Name));
                    return;
                }
                // Brought in upright on screen (the camera's up, as in space, where there's no up), facing the camera;
                // the planet's up is kept for standing it on the ground later
                Transform cam = camera.transform;
                Vector3 planetUp = Planet.UpAt(cam.position + cam.forward * DistanceInFront, cam.up);
                Place(candidate, camera, cam.up, parent, out Vector3 position, out Quaternion rotation, out string placement);
                PhotoDouble photoDouble = _factory.Create(candidate, position, rotation, parent);
                var entry = new Entry(photoDouble, hideOriginal ? new KerbalHider(candidate.Model.gameObject) : null, _newPlayer(), planetUp);
                _entries.Add(entry);
                Select(entry);
                _logInfo($"Photo mode: building a double of {candidate}, {placement}.");
                result?.Invoke(Localization.Format("MunShot/Status/Bringing", candidate.Name));
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
                message = Localization.Translate("MunShot/Status/SnapNobody");
                return false;
            }

            entry.Bones ??= SurfaceSnap.FindBones(entry.Root);
            bool dropped = SurfaceSnap.TryDrop(entry.Root, entry.Bones, entry.Up, DropReach, IgnoredInSurfaceSearch(), out string landedOn);
            _logInfo($"Photo mode: snapping {entry.Double.Source.Name} to the surface: {landedOn}.");
            if (!dropped)
            {
                message = Localization.Format("MunShot/Status/NothingBelow", DropReach, entry.Double.Source.Name);
                return false;
            }
            message = Localization.Format("MunShot/Status/OnSurface", entry.Double.Source.Name);
            return true;
        }

        /// <summary>
        /// Turns a double upright on screen (its up along <paramref name="up"/>, the camera's), still facing the way it
        /// faced, turning about the middle of its body so it doesn't jump. A double lying down ends up facing the way its
        /// head pointed.
        /// </summary>
        /// <returns>False, with <paramref name="message"/> saying why, when there's no built double to stand up.</returns>
        public bool StandUpright(Entry entry, Vector3 up, out string message)
        {
            if (entry == null || !entry.IsReady || entry.Root == null)
            {
                message = Localization.Translate("MunShot/Status/UprightNobody");
                return false;
            }

            Transform root = entry.Root;
            Vector3 facing = Vector3.ProjectOnPlane(root.forward, up);
            if (facing.sqrMagnitude < 1e-4f) facing = Vector3.ProjectOnPlane(root.up, up);
            if (facing.sqrMagnitude < 1e-4f) facing = Vector3.ProjectOnPlane(root.right, up);
            Quaternion upright = Quaternion.LookRotation(facing.normalized, up);

            Vector3 centre = BoundsOf(entry, out Bounds bounds) ? bounds.center : root.position;
            Quaternion turn = upright * Quaternion.Inverse(root.rotation);
            root.SetPositionAndRotation(centre + turn * (root.position - centre), upright);
            message = Localization.Format("MunShot/Status/Upright", entry.Double.Source.Name);
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
            _generation++;
            for (int i = _entries.Count - 1; i >= 0; i--) Remove(_entries[i]);
            Selected = null;
        }

        /// <summary>Takes one double out: it's destroyed and its original shown again.</summary>
        public void Remove(Entry entry)
        {
            if (entry == null || !_entries.Contains(entry)) return;
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

        // Where the camera looks, facing it: standing on a surface just below that spot (none of the hidden kerbals' own
        // bodies), or floating there with its middle at the spot, so it's in the middle of the shot. Never moved far up
        // or down, where it would be out of the shot; Snap to surface puts a floating one down. Parented to the physics
        // space the original lives in, so the double moves with the floating origin.
        private void Place(KerbalCandidate candidate, Camera camera, Vector3 up, Transform parent, out Vector3 position,
                           out Quaternion rotation, out string placement)
        {
            Transform cam = camera.transform;
            List<Transform> ignore = IgnoredInSurfaceSearch();
            Transform ownBody = BodyOf(candidate);
            if (ownBody != null) ignore.Add(ownBody);

            // Aim: what the camera looks at, if it's nearer than the usual distance, so the double isn't put inside or
            // behind it
            Vector3 spot = cam.position + cam.forward * DistanceInFront;
            float distance = DistanceInFront;
            if (SurfaceSnap.TryFindSurface(cam.position, cam.forward, DistanceInFront, ignore, null, out RaycastHit ahead))
            {
                distance = Mathf.Max(0f, ahead.distance - StandOffSurface);
                spot = cam.position + cam.forward * distance;
            }

            bool grounded = SurfaceSnap.TryFindSurface(spot + up * SettleFromAbove, -up, SettleFromAbove + DropReach,
                                                       ignore, null, out RaycastHit below);
            position = grounded ? below.point : spot - up * HalfHeight;

            rotation = FacingCamera(position, cam, up);
            placement = $"{(grounded ? "standing on a surface just below" : "floating")} where the camera looks, "
                        + $"{distance:0.#}m in front of it, under '{(parent != null ? parent.name : "<scene root>")}'";
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
