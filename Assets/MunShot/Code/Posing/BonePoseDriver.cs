using System.Collections.Generic;
using UnityEngine;

namespace MunShot.Posing
{
    /// <summary>
    /// Applies a kerbal's <see cref="BonePose"/> every frame after its animation, so it shows over the game's animation
    /// and over a held clip: set bones are set, turned bones are turned from where the animation put them this frame.
    /// On the Animator's GameObject; added and removed by <see cref="PosePlayer"/>. Runs late in LateUpdate (which
    /// still runs while the game is paused) so other scripts moving bones come first.
    /// </summary>
    [DefaultExecutionOrder(10000)]
    internal sealed class BonePoseDriver : MonoBehaviour
    {
        private readonly List<(Transform bone, Quaternion rotation)> _set = new List<(Transform, Quaternion)>();
        private readonly List<Turned> _turned = new List<Turned>();

        // A turned bone, with what the animation last gave it and what was written over that
        private sealed class Turned
        {
            public Transform Bone;
            public Quaternion Turn;
            public Quaternion Animated;
            public Quaternion Written;
            public bool HasWritten;
        }

        public void Set(IEnumerable<(Transform bone, Quaternion rotation)> set, IEnumerable<(Transform bone, Quaternion turn)> turned)
        {
            _set.Clear();
            _set.AddRange(set);
            _turned.Clear();
            foreach ((Transform bone, Quaternion turn) in turned)
                _turned.Add(new Turned { Bone = bone, Turn = turn });
        }

        private void LateUpdate()
        {
            foreach ((Transform bone, Quaternion rotation) in _set)
                if (bone != null)
                    bone.localRotation = rotation;

            foreach (Turned t in _turned)
            {
                if (t.Bone == null) continue;
                // The animation doesn't always write every bone every frame (e.g. a paused Animator): when the bone still
                // has what was written last, turn from the animation's last value again rather than turning further
                Quaternion now = t.Bone.localRotation;
                if (!t.HasWritten || Quaternion.Angle(now, t.Written) > 0.01f) t.Animated = now;
                t.Written = t.Turn * t.Animated;
                t.HasWritten = true;
                t.Bone.localRotation = t.Written;
            }
        }
    }
}
