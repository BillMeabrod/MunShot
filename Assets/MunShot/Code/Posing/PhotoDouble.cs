using System;
using KSP.Game;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MunShot.Posing
{
    /// <summary>
    /// A visual-only copy of a kerbal for photo mode: the game's own kerbal model, built from the kerbal's roster
    /// record (so the original needn't be in the scene), with the EVA animation controller so stock poses can be held on it. It has no physics and no part in
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
        // finish (and set the face) only after that
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

            SetFace();
            State = BuildState.Ready;
            return true;
        }

        /// <summary>Whether the helmet is on (it is when a double is built, as for any kerbal outside).</summary>
        public bool HelmetOn { get; private set; } = true;

        /// <summary>Whether the visor glass is shown on the helmet.</summary>
        public bool VisorOn { get; private set; } = true;

        /// <summary>
        /// Puts the helmet on or takes it off, the game's own way (<see cref="Kerbal3DModel.SetHelmetVisible"/>, as
        /// the IVA helmet toggle does): the helmet, its visor and the neck ring all go together.
        /// </summary>
        public void SetHelmet(bool on)
        {
            HelmetOn = on;
            if (Model != null) Model.SetHelmetVisible(on);
        }

        /// <summary>
        /// Shows or hides the visor glass, so the face can be seen without reflections. The game has no visor switch,
        /// so the visor's renderers are hidden the way originals are (forceRenderingOff); the helmet stays on.
        /// </summary>
        public void SetVisor(bool on)
        {
            VisorOn = on;
            if (Model == null) return;
            int found = 0;
            foreach (Renderer renderer in Model.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer.gameObject.name.IndexOf(VisorName, StringComparison.OrdinalIgnoreCase) < 0) continue;
                renderer.forceRenderingOff = !on;
                found++;
            }
            if (found == 0) VisorWarning = $"Photo double of {Source.Name}: no visor found (no renderer named '*{VisorName}*').";
        }

        /// <summary>Set when <see cref="SetVisor"/> found no visor to show or hide.</summary>
        public string VisorWarning { get; private set; }

        // The helmet's glass is "mesh_helmet_01_visor" on the kerbal rig
        private const string VisorName = "visor";

        /// <summary>Removes the double from the scene and from the game's update list.</summary>
        public void Destroy()
        {
            if (Model != null)
            {
                GameManager.Instance?.Game?.UnregisterUpdate(Model);
                Object.Destroy(Model.gameObject);
            }
        }

        // The game's animation names for a kerbal's own eye height and eye symmetry, and the attributes they come from
        private const string EyeOffsetParameter = "eyeOffsetPercentage";
        private const string EyeSymmetryParameter = "EyeSymmetry_Phase";
        private const string EyeHeightAttribute = "EYEHEIGHT";
        private const string EyeSymmetryAttribute = "EYESYMMETRY";

        /// <summary>Set when the face couldn't be set up fully: the double still works, it may just look a little off.</summary>
        public string FaceWarning { get; private set; }

        // Gives the double this kerbal's own face, the way the game does for every kerbal in flight
        // (AnimationManager.InitAttributeValues): eye height and eye symmetry from the kerbal's attributes
        private void SetFace()
        {
            var problems = new System.Collections.Generic.List<string>();
            SetFloatFromAttribute(EyeOffsetParameter, EyeHeightAttribute, value => Convert.ToInt32(value), problems);
            SetFloatFromAttribute(EyeSymmetryParameter, EyeSymmetryAttribute, value => Convert.ToSingle(value), problems);
            if (problems.Count > 0) FaceWarning = $"Photo double of {Source.Name}: face not fully set up ({string.Join("; ", problems)}).";
        }

        private void SetFloatFromAttribute(string parameter, string attribute, Func<object, float> convert,
                                           System.Collections.Generic.List<string> problems)
        {
            if (!HasFloatParameter(parameter))
            {
                problems.Add($"the animation controller has no '{parameter}'");
                return;
            }
            if (Model.Attributes == null || !Model.Attributes.TryGetValue(attribute, out object value) || value == null)
            {
                problems.Add($"the kerbal has no '{attribute}' attribute");
                return;
            }
            try
            {
                Animator.SetFloat(parameter, convert(value));
            }
            catch (Exception e) when (e is FormatException || e is InvalidCastException || e is OverflowException)
            {
                problems.Add($"'{attribute}' is {value.GetType().Name} '{value}', not a number");
            }
        }

        private bool HasFloatParameter(string name)
        {
            if (Animator == null) return false;
            foreach (AnimatorControllerParameter parameter in Animator.parameters)
                if (parameter.type == AnimatorControllerParameterType.Float && parameter.name == name)
                    return true;
            return false;
        }

        private bool Fail(string reason)
        {
            State = BuildState.Failed;
            Failure = $"Photo double of {Source.Name} failed: {reason}.";
            return true;
        }
    }
}
