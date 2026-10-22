using System;
using KSP.Game;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MunShot.Posing
{
    /// <summary>
    /// A visual-only copy of a kerbal for photo mode: the game's own kerbal model, built from the original's
    /// appearance, with the EVA animation controller so stock poses can be held on it. It has no physics and no part in
    /// the simulation. Made by <see cref="PhotoDoubleFactory"/>; the game builds the body, head and helmet over a few
    /// frames, and <see cref="Tick"/> finishes the double once it has.
    /// </summary>
    public sealed class PhotoDouble
    {
        public enum BuildState
        {
            Building,
            Ready,
            Failed
        }

        // The game builds a kerbal over several frames (it loads the parts first); give up after this
        private const float BuildTimeoutSeconds = 20f;

        // The game rebinds the Animator on its first update after the kerbal is built, which resets parameters:
        // finish (and copy the face settings) only after that
        private const int FramesAfterBuilt = 2;

        private readonly float _startedAt;
        private int _framesSinceBuilt;

        public KerbalCandidate Source { get; }
        public Kerbal3DModel Model { get; }
        public Animator Animator { get; }
        public BuildState State { get; private set; } = BuildState.Building;

        /// <summary>Why the double failed, when <see cref="State"/> is <see cref="BuildState.Failed"/>.</summary>
        public string Failure { get; private set; }

        internal PhotoDouble(KerbalCandidate source, Kerbal3DModel model, Animator animator)
        {
            Source = source;
            Model = model;
            Animator = animator;
            _startedAt = Time.unscaledTime;
        }

        /// <summary>Called every frame while building; returns true on the frame the double becomes ready or fails.</summary>
        public bool Tick()
        {
            if (State != BuildState.Building) return false;

            if (Model == null)
                return Fail("its model was destroyed while being built");

            if (!Model.CharacterExists)
            {
                if (Time.unscaledTime - _startedAt > BuildTimeoutSeconds)
                    return Fail($"the game didn't finish building it within {BuildTimeoutSeconds:0} seconds");
                return false;
            }

            if (++_framesSinceBuilt < FramesAfterBuilt) return false;

            CopyFaceSettings();
            State = BuildState.Ready;
            return true;
        }

        /// <summary>Removes the double from the scene and from the game's update list.</summary>
        public void Destroy()
        {
            if (Model != null)
            {
                GameManager.Instance?.Game?.UnregisterUpdate(Model);
                Object.Destroy(Model.gameObject);
            }
        }

        // Copies the Animator settings that give this kerbal their own look (eye offset, eye symmetry, the eye look
        // the game derives from stupidity, set per kerbal as the portrait booth does) from the original's Animator.
        // Parameters are matched by name and type, so only those both controllers have are copied.
        private void CopyFaceSettings()
        {
            Animator original = Source.Model != null ? Source.Model.GetComponent<Animator>() : null;
            if (original == null || original.runtimeAnimatorController == null || Animator == null) return;

            var originalFloats = new System.Collections.Generic.Dictionary<int, float>();
            foreach (AnimatorControllerParameter parameter in original.parameters)
                if (parameter.type == AnimatorControllerParameterType.Float)
                    originalFloats[parameter.nameHash] = original.GetFloat(parameter.nameHash);

            foreach (AnimatorControllerParameter parameter in Animator.parameters)
                if (parameter.type == AnimatorControllerParameterType.Float
                    && originalFloats.TryGetValue(parameter.nameHash, out float value))
                    Animator.SetFloat(parameter.nameHash, value);
        }

        private bool Fail(string reason)
        {
            State = BuildState.Failed;
            Failure = $"Photo double of {Source.Name} failed: {reason}.";
            return true;
        }
    }
}
