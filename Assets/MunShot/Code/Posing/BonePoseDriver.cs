using System.Collections.Generic;
using UnityEngine;

namespace MunShot.Posing
{
    /// <summary>
    /// Applies a kerbal's <see cref="BonePose"/> every frame after its animation, so it shows over the game's animation
    /// and over a held clip: set bones are set, turned and moved bones are turned or slid from where the animation put
    /// them this frame. On the Animator's GameObject; added and removed by <see cref="PosePlayer"/>. Runs late in
    /// LateUpdate (which still runs while the game is paused) so other scripts moving bones come first.
    /// </summary>
    [DefaultExecutionOrder(10000)]
    internal sealed class BonePoseDriver : MonoBehaviour
    {
        private readonly List<(Transform bone, Quaternion rotation)> _set = new List<(Transform, Quaternion)>();
        private readonly List<Turned> _turned = new List<Turned>();
        private readonly List<Moved> _moved = new List<Moved>();

        // The animation doesn't always write every bone every frame (e.g. a paused Animator). So each turned or moved
        // bone remembers what the animation last gave it and what was written over that: while the bone still holds
        // what was written, the turn or slide starts again from the animation's value rather than adding up
        private sealed class Turned
        {
            public Transform Bone;
            public Quaternion Turn;
            public Quaternion Animated;
            public Quaternion Written;
            public bool HasWritten;
        }

        private sealed class Moved
        {
            public Transform Bone;
            public Vector3 Offset;
            public Vector3 Animated;
            public Vector3 Written;
            public bool HasWritten;
        }

        public void Set(IEnumerable<(Transform bone, Quaternion rotation)> set, IEnumerable<(Transform bone, Quaternion turn)> turned,
                        IEnumerable<(Transform bone, Vector3 offset)> moved)
        {
            _set.Clear();
            _set.AddRange(set);
            _turned.Clear();
            foreach ((Transform bone, Quaternion turn) in turned)
                _turned.Add(new Turned { Bone = bone, Turn = turn });
            _moved.Clear();
            foreach ((Transform bone, Vector3 offset) in moved)
                _moved.Add(new Moved { Bone = bone, Offset = offset });
        }

        private void LateUpdate()
        {
            foreach ((Transform bone, Quaternion rotation) in _set)
                if (bone != null)
                    bone.localRotation = rotation;

            foreach (Turned t in _turned)
            {
                if (t.Bone == null) continue;
                Quaternion now = t.Bone.localRotation;
                if (!t.HasWritten || Quaternion.Angle(now, t.Written) > 0.01f) t.Animated = now;
                t.Written = t.Turn * t.Animated;
                t.HasWritten = true;
                t.Bone.localRotation = t.Written;
            }

            foreach (Moved m in _moved)
            {
                if (m.Bone == null) continue;
                Vector3 now = m.Bone.localPosition;
                if (!m.HasWritten || (now - m.Written).sqrMagnitude > 1e-10f) m.Animated = now;
                m.Written = m.Animated + m.Offset;
                m.HasWritten = true;
                m.Bone.localPosition = m.Written;
            }
        }
    }
}
