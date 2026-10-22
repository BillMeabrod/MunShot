using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Experimental.Animations;
using UnityEngine.Playables;

namespace MunShot.Posing
{
    /// <summary>
    /// Holds one Animator in a <see cref="ClipPose"/>: plays the named clip through a Playables graph of its own,
    /// paused at the pose's time, and gives the Animator back when released.
    ///
    /// The game's own animation keeps running underneath; this graph's output is sorted after it, so the held frame
    /// is what's shown for every bone the clip animates. Bones it doesn't animate keep the game's animation, unless
    /// <see cref="FreezeBaseAnimation"/> is on. The graph runs on unscaled time, so the pose holds while the game is
    /// paused or time is frozen.
    /// </summary>
    public sealed class PosePlayer : IDisposable
    {
        // Sorted after the Animator's own controller output (which uses the default order), so ours is applied last
        private const ushort OutputSortingOrder = 1000;

        private readonly Action<string> _logError;
        private readonly Func<Animator, float> _gameSpeedFor;

        private PlayableGraph _graph;
        private AnimationClipPlayable _clipPlayable;
        private AnimationClip _clip;
        private Animator _animator;
        private bool _baseFrozen;

        /// <summary>The pose being held, or null when nothing is held.</summary>
        public ClipPose? Current { get; private set; }

        /// <summary>
        /// Whether a pose is held and its Animator still exists (the kerbal can be destroyed while posed, e.g. on
        /// a scene change or when the EVA ends).
        /// </summary>
        public bool IsHolding => Current.HasValue && _animator != null && _graph.IsValid();

        /// <summary>
        /// Whether the game's own animation is paused while a pose is held. The held clip only covers the bones it
        /// animates; everything else keeps playing the game's animation unless this is on.
        /// </summary>
        public bool FreezeBaseAnimation { get; private set; }

        /// <param name="logError">Where failures are reported.</param>
        /// <param name="gameSpeedFor">
        /// The speed the game would give an Animator right now, used to hand it back after freezing it. The game
        /// only sets the speed when the time-warp rate changes, so restoring a remembered value could leave a stale
        /// speed if warp changed while frozen.
        /// </param>
        public PosePlayer(Action<string> logError, Func<Animator, float> gameSpeedFor)
        {
            _logError = logError ?? throw new ArgumentNullException(nameof(logError));
            _gameSpeedFor = gameSpeedFor ?? throw new ArgumentNullException(nameof(gameSpeedFor));
        }

        /// <summary>
        /// The Animator's clips by name, one entry per name (the controller lists a clip once per state using it).
        /// </summary>
        public static IReadOnlyList<AnimationClip> GetClips(Animator animator)
        {
            var clips = new List<AnimationClip>();
            RuntimeAnimatorController controller = animator != null ? animator.runtimeAnimatorController : null;
            if (controller == null) return clips;

            var seen = new HashSet<string>();
            foreach (AnimationClip clip in controller.animationClips)
                if (clip != null && seen.Add(clip.name))
                    clips.Add(clip);
            clips.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
            return clips;
        }

        /// <summary>
        /// Holds <paramref name="animator"/> in <paramref name="pose"/>. Holding the same Animator in another pose
        /// swaps the clip if it changed and moves the held frame; a different Animator releases the previous one
        /// first.
        /// </summary>
        /// <returns>False, with the reason logged, when the Animator has no clip of that name.</returns>
        public bool Hold(Animator animator, ClipPose pose)
        {
            if (animator == null)
            {
                _logError($"Cannot hold {pose}: no Animator was given.");
                return false;
            }

            AnimationClip clip = FindClip(animator, pose.ClipName);
            if (clip == null)
            {
                RuntimeAnimatorController controller = animator.runtimeAnimatorController;
                _logError($"Cannot hold {pose} on '{animator.name}': its controller "
                          + $"'{(controller != null ? controller.name : "<none>")}' has no clip named '{pose.ClipName}'.");
                return false;
            }

            if (_animator != animator || !_graph.IsValid())
            {
                Release();
                Build(animator);
            }
            if (_clip != clip) SetClip(clip);

            // Held past the end means held on the last frame, as the pose lab's slider allows the full length
            float time = Mathf.Min(pose.Time, clip.length);
            _clipPlayable.SetTime(time);
            Current = new ClipPose(clip.name, time);
            ApplyBaseFreeze();
            return true;
        }

        /// <summary>Gives the Animator back to the game's own animation. Safe to call when nothing is held.</summary>
        public void Release()
        {
            UnfreezeBase();
            if (_graph.IsValid()) _graph.Destroy();
            _graph = default;
            _clipPlayable = default;
            _clip = null;
            _animator = null;
            Current = null;
        }

        public void Dispose() => Release();

        /// <summary>Turns <see cref="FreezeBaseAnimation"/> on or off, taking effect at once if a pose is held.</summary>
        public void SetFreezeBaseAnimation(bool freeze)
        {
            FreezeBaseAnimation = freeze;
            ApplyBaseFreeze();
        }

        /// <summary>
        /// Called every frame: keeps the game's animation paused while frozen, since the game sets the Animator's
        /// speed again whenever the time-warp rate changes.
        /// </summary>
        public void Tick() => ApplyBaseFreeze();

        /// <summary>
        /// Moves the held clip to <paramref name="time"/> and applies it to the bones immediately, within this frame,
        /// without changing <see cref="Current"/>. For sampling what a clip moves; follow with <see cref="Hold"/> to
        /// go back to the held frame.
        /// </summary>
        public bool ApplyNow(float time)
        {
            if (!IsHolding) return false;
            _clipPlayable.SetTime(Mathf.Clamp(time, 0f, _clip.length));
            _graph.Evaluate(0f);
            return true;
        }

        private void Build(Animator animator)
        {
            _graph = PlayableGraph.Create($"MunShot Pose ({animator.name})");
            _graph.SetTimeUpdateMode(DirectorUpdateMode.UnscaledGameTime);
            var output = AnimationPlayableOutput.Create(_graph, "MunShot Pose", animator);
            output.SetSortingOrder(OutputSortingOrder);
            _graph.Play();
            _animator = animator;
        }

        private void SetClip(AnimationClip clip)
        {
            if (_clipPlayable.IsValid()) _clipPlayable.Destroy();

            _clipPlayable = AnimationClipPlayable.Create(_graph, clip);
            _clipPlayable.SetApplyFootIK(false);
            // The graph is already playing, which new playables follow: pause this one so only we move its time
            _clipPlayable.Pause();
            _clipPlayable.SetSpeed(0d);
            _graph.GetOutput(0).SetSourcePlayable(_clipPlayable);
            _clip = clip;
        }

        private static AnimationClip FindClip(Animator animator, string clipName)
        {
            RuntimeAnimatorController controller = animator.runtimeAnimatorController;
            if (controller == null) return null;
            foreach (AnimationClip clip in controller.animationClips)
                if (clip != null && clip.name == clipName)
                    return clip;
            return null;
        }

        private void ApplyBaseFreeze()
        {
            if (FreezeBaseAnimation && IsHolding)
            {
                _animator.speed = 0f;
                _baseFrozen = true;
            }
            else
            {
                UnfreezeBase();
            }
        }

        private void UnfreezeBase()
        {
            if (!_baseFrozen) return;
            _baseFrozen = false;
            if (_animator != null) _animator.speed = _gameSpeedFor(_animator);
        }
    }
}
