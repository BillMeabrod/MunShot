using System.Collections.Generic;
using UnityEngine;

namespace MunShot.Posing
{
    /// <summary>
    /// Applies a kerbal's <see cref="BonePose"/> and the player's slider edits (<see cref="PoseEdits"/>) every frame
    /// after its animation, so they show over the game's animation and over a held clip: set bones are set (and
    /// placed), turned and moved bones are turned or slid from where the animation put them this frame, and edited
    /// bones are turned on top of all that. With edits, a bone can also follow another it doesn't hang from (the
    /// helmet with the head), and a mesh can be kept as the pose has it (the air hose, on the neck). On the Animator's
    /// GameObject; added and removed by
    /// <see cref="PosePlayer"/>. Runs late in LateUpdate (which still runs while the game is paused) so other scripts
    /// moving bones come first.
    /// </summary>
    [DefaultExecutionOrder(10000)]
    internal sealed class BonePoseDriver : MonoBehaviour
    {
        private readonly List<(Transform bone, Vector3 position)> _placed = new List<(Transform, Vector3)>();

        // The pose's and the edits' slides, kept apart like the rotations
        private readonly List<(Transform bone, Vector3 offset)> _moves = new List<(Transform, Vector3)>();
        private readonly List<(Transform bone, Vector3 offset)> _slides = new List<(Transform, Vector3)>();
        private readonly Dictionary<Transform, Moved> _moved = new Dictionary<Transform, Moved>();

        // The pose's and the edits' rotations, kept apart so either can change without the other
        private readonly List<(Transform bone, Quaternion rotation)> _set = new List<(Transform, Quaternion)>();
        private readonly List<(Transform bone, Quaternion turn)> _turns = new List<(Transform, Quaternion)>();
        private readonly List<(Transform bone, Quaternion parent, Quaternion own)> _edits = new List<(Transform, Quaternion, Quaternion)>();
        private readonly Dictionary<Transform, Rotated> _rotated = new Dictionary<Transform, Rotated>();

        // Edited bones that start from their rest rotation rather than the pose
        private readonly List<(Transform bone, Quaternion rest)> _rests = new List<(Transform, Quaternion)>();

        // The animation doesn't always write every bone every frame (e.g. a paused Animator). So each bone turned or
        // moved from where the animation has it remembers what the animation last gave it and what was written over
        // that: while the bone still holds what was written, the turn or slide starts again from the animation's value
        // rather than adding up
        private sealed class Rotated
        {
            public Transform Bone;
            public bool IsSet;
            public Quaternion Set;
            public bool FromRest;
            public Quaternion Rest;
            public Quaternion Turn = Quaternion.identity;
            public Quaternion Parent = Quaternion.identity;
            public Quaternion Own = Quaternion.identity;
            public Quaternion Animated;
            public Quaternion Written;
            public bool HasWritten;
        }

        // A bone moved with another it isn't the child of, as if it were (the helmet with the head): each frame it's put
        // back where the animation has it, then turned about the leader by the leader's edit
        private sealed class Follower
        {
            public Transform Bone;
            public Transform Leader;
            public Quaternion AnimatedRotation;
            public Vector3 AnimatedPosition;
            public Quaternion WrittenRotation;
            public Vector3 WrittenPosition;
            public bool HasWritten;
        }

        private readonly List<Follower> _followers = new List<Follower>();

        // A mesh kept as the pose has it, whatever the edits do to two of its bones (the suit's air hose on the neck): it's
        // drawn from stand-ins for the neck's base and the neck, which copy the real ones without the edits
        private SkinnedMeshRenderer _keptMesh;
        private Transform[] _keptMeshBones;
        private Transform _neckBase, _neck, _standInBase, _standInNeck;

        private sealed class Moved
        {
            public Transform Bone;
            public Vector3 Offset;
            public Vector3 Edit;
            public Vector3 Animated;
            public Vector3 Written;
            public bool HasWritten;
        }

        public void Set(IEnumerable<(Transform bone, Quaternion rotation)> set, IEnumerable<(Transform bone, Quaternion turn)> turned,
                        IEnumerable<(Transform bone, Vector3 offset)> moved, IEnumerable<(Transform bone, Vector3 position)> placed)
        {
            _set.Clear();
            _set.AddRange(set);
            _turns.Clear();
            _turns.AddRange(turned);
            _placed.Clear();
            _placed.AddRange(placed);
            _moves.Clear();
            _moves.AddRange(moved);
            Combine();
        }

        /// <summary>
        /// The player's edits: a turn in each bone's parent's frame (before the pose) and its own (after), and slides in
        /// the parent's frame (on top of the pose's).
        /// </summary>
        public void SetEdits(IEnumerable<(Transform bone, Quaternion parent, Quaternion own)> edits,
                             IEnumerable<(Transform bone, Vector3 offset)> slides,
                             IEnumerable<(Transform bone, Quaternion rest)> fromRest)
        {
            _edits.Clear();
            _edits.AddRange(edits);
            _rests.Clear();
            _rests.AddRange(fromRest);
            _slides.Clear();
            _slides.AddRange(slides);
            Combine();
        }

        /// <summary>Bones moved with another they aren't the child of, as if they were; empty for none.</summary>
        public void SetFollowers(IEnumerable<(Transform bone, Transform leader)> followers)
        {
            // A follower that stays keeps what it remembers of the animation and stays where it was written: put back
            // between frames, the helmet would leave the turned head for a frame, and the game's hair squashing
            // (HairCompression, which measures the helmet in Update) would hide the hair
            var kept = new List<Follower>();
            foreach ((Transform bone, Transform leader) in followers)
                if (bone != null && leader != null)
                    kept.Add(_followers.Find(f => f.Bone == bone && f.Leader == leader)
                             ?? new Follower { Bone = bone, Leader = leader });
            // The rest go back where the animation had them
            foreach (Follower f in _followers)
                if (!kept.Contains(f)) Restore(f);
            _followers.Clear();
            _followers.AddRange(kept);
        }

        /// <summary>
        /// Draws <paramref name="mesh"/> from stand-ins for <paramref name="neckBase"/> and <paramref name="neck"/> that
        /// keep the pose without the edits, so the mesh stays put while they move; null mesh stops.
        /// </summary>
        public void KeepUnedited(SkinnedMeshRenderer mesh, Transform neckBase, Transform neck)
        {
            if (mesh == _keptMesh && neckBase == _neckBase && neck == _neck) return;
            StopKeeping();
            if (mesh == null || neckBase == null || neck == null || neckBase.parent == null) return;

            Transform[] bones = mesh.bones;
            int index = System.Array.IndexOf(bones, neck);
            if (index < 0) return;
            _keptMesh = mesh;
            _keptMeshBones = bones;
            _neckBase = neckBase;
            _neck = neck;
            _standInBase = new GameObject("MunShot unedited " + neckBase.name).transform;
            _standInBase.SetParent(neckBase.parent, false);
            _standInNeck = new GameObject("MunShot unedited " + neck.name).transform;
            _standInNeck.SetParent(_standInBase, false);
            CopyUnedited();
            var swapped = (Transform[])bones.Clone();
            swapped[index] = _standInNeck;
            mesh.bones = swapped;
        }

        private void StopKeeping()
        {
            if (_keptMesh != null && _keptMeshBones != null) _keptMesh.bones = _keptMeshBones;
            if (_standInBase != null) Destroy(_standInBase.gameObject);
            _keptMesh = null;
            _keptMeshBones = null;
            _neckBase = _neck = _standInBase = _standInNeck = null;
        }

        // A follower back where the animation last had it
        private static void Restore(Follower f)
        {
            if (f.Bone == null || !f.HasWritten) return;
            f.Bone.localRotation = f.AnimatedRotation;
            f.Bone.localPosition = f.AnimatedPosition;
        }

        private void OnDestroy()
        {
            StopKeeping();
            foreach (Follower f in _followers) Restore(f);
        }

        // A bone's rotation as the pose has it, without the edits
        private Quaternion Unedited(Transform bone)
        {
            if (!_rotated.TryGetValue(bone, out Rotated r)) return bone.localRotation;
            if (r.IsSet) return r.Set;
            // Not written yet: the bone still holds the animation's rotation
            return r.HasWritten ? r.Turn * r.Animated : r.Turn * bone.localRotation;
        }

        private void CopyUnedited()
        {
            if (_standInBase == null || _neckBase == null || _neck == null) return;
            _standInBase.localPosition = _neckBase.localPosition;
            _standInBase.localRotation = Unedited(_neckBase);
            _standInBase.localScale = _neckBase.localScale;
            _standInNeck.localPosition = _neck.localPosition;
            _standInNeck.localRotation = Unedited(_neck);
            _standInNeck.localScale = _neck.localScale;
        }

        // One entry per rotated bone. A bone that was rotated before keeps what it remembers of the animation, so
        // changing a turn (a slider being dragged) doesn't take the last turn for part of the animation
        private void Combine()
        {
            var old = new Dictionary<Transform, Rotated>(_rotated);
            _rotated.Clear();
            Rotated For(Transform bone)
            {
                if (_rotated.TryGetValue(bone, out Rotated r)) return r;
                r = new Rotated { Bone = bone };
                if (old.TryGetValue(bone, out Rotated before))
                {
                    r.Animated = before.Animated;
                    r.Written = before.Written;
                    r.HasWritten = before.HasWritten;
                }
                return _rotated[bone] = r;
            }
            foreach ((Transform bone, Quaternion rotation) in _set)
                if (bone != null)
                {
                    Rotated r = For(bone);
                    r.IsSet = true;
                    r.Set = rotation;
                }
            foreach ((Transform bone, Quaternion turn) in _turns)
                if (bone != null)
                    For(bone).Turn = turn;
            foreach ((Transform bone, Quaternion parent, Quaternion own) in _edits)
                if (bone != null)
                {
                    Rotated r = For(bone);
                    r.Parent = parent;
                    r.Own = own;
                }
            foreach ((Transform bone, Quaternion rest) in _rests)
                if (bone != null)
                {
                    Rotated r = For(bone);
                    r.FromRest = true;
                    r.Rest = rest;
                }

            var oldMoved = new Dictionary<Transform, Moved>(_moved);
            _moved.Clear();
            Moved MovedFor(Transform bone)
            {
                if (_moved.TryGetValue(bone, out Moved m)) return m;
                m = new Moved { Bone = bone };
                if (oldMoved.TryGetValue(bone, out Moved before))
                {
                    m.Animated = before.Animated;
                    m.Written = before.Written;
                    m.HasWritten = before.HasWritten;
                }
                return _moved[bone] = m;
            }
            foreach ((Transform bone, Vector3 offset) in _moves)
                if (bone != null)
                    MovedFor(bone).Offset = offset;
            foreach ((Transform bone, Vector3 offset) in _slides)
                if (bone != null)
                    MovedFor(bone).Edit = offset;
        }

        private void LateUpdate()
        {
            foreach ((Transform bone, Vector3 position) in _placed)
                if (bone != null)
                    bone.localPosition = position;

            foreach (Rotated r in _rotated.Values)
            {
                if (r.Bone == null) continue;
                Quaternion pose;
                if (r.FromRest)
                {
                    pose = r.Rest;
                }
                else if (r.IsSet)
                {
                    pose = r.Set;
                }
                else
                {
                    Quaternion now = r.Bone.localRotation;
                    if (!r.HasWritten || Quaternion.Angle(now, r.Written) > 0.01f) r.Animated = now;
                    pose = r.Turn * r.Animated;
                }
                r.Written = r.Parent * pose * r.Own;
                r.HasWritten = true;
                r.Bone.localRotation = r.Written;
            }

            foreach (Moved m in _moved.Values)
            {
                if (m.Bone == null) continue;
                Vector3 now = m.Bone.localPosition;
                if (!m.HasWritten || (now - m.Written).sqrMagnitude > 1e-10f) m.Animated = now;
                m.Written = m.Animated + m.Offset + m.Edit;
                m.HasWritten = true;
                m.Bone.localPosition = m.Written;
            }

            // Every bone is written by now, so world rotations are final
            foreach (Follower f in _followers)
            {
                if (f.Bone == null || f.Leader == null) continue;
                Quaternion nowRotation = f.Bone.localRotation;
                Vector3 nowPosition = f.Bone.localPosition;
                if (!f.HasWritten || Quaternion.Angle(nowRotation, f.WrittenRotation) > 0.01f
                    || (nowPosition - f.WrittenPosition).sqrMagnitude > 1e-10f)
                {
                    f.AnimatedRotation = nowRotation;
                    f.AnimatedPosition = nowPosition;
                }
                f.Bone.localRotation = f.AnimatedRotation;
                f.Bone.localPosition = f.AnimatedPosition;

                // The leader's edit as a turn in the world, about the leader
                Quaternion parent = f.Leader.parent != null ? f.Leader.parent.rotation : Quaternion.identity;
                Quaternion turn = f.Leader.rotation * Quaternion.Inverse(parent * Unedited(f.Leader));
                Vector3 pivot = f.Leader.position;
                f.Bone.SetPositionAndRotation(pivot + turn * (f.Bone.position - pivot), turn * f.Bone.rotation);
                f.WrittenRotation = f.Bone.localRotation;
                f.WrittenPosition = f.Bone.localPosition;
                f.HasWritten = true;
            }

            CopyUnedited();
        }
    }
}
