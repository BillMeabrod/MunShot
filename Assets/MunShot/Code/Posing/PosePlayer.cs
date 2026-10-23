using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Experimental.Animations;
using UnityEngine.Playables;

namespace MunShot.Posing
{
    /// <summary>
    /// Holds one Animator in a pose: a <see cref="ClipPose"/>, a <see cref="BonePose"/>, or both, and gives the
    /// Animator back when released.
    ///
    /// A clip plays through a Playables graph of its own, paused at the pose's time. The game's own animation keeps
    /// running underneath; this graph's output is sorted after it, so the held frame is what's shown for every bone the
    /// clip animates. Bones it doesn't animate keep the game's animation, unless <see cref="FreezeBaseAnimation"/> is
    /// on. The graph runs on unscaled time, so the pose holds while the game is paused or time is frozen.
    ///
    /// A bone pose is set after all of that, every frame (<see cref="BonePoseDriver"/>), so its bones show exactly as
    /// given, over the clip and the game's animation alike.
    /// </summary>
    public sealed class PosePlayer
    {
        // Sorted after the Animator's own controller output (which uses the default order), so ours is applied last
        private const ushort OutputSortingOrder = 1000;

        private readonly Action<string> _logError;
        private readonly Action<string> _logInfo;
        private readonly Func<Animator, float> _gameSpeedFor;

        private PlayableGraph _graph;
        private AnimationClipPlayable _clipPlayable;
        private AnimationClip _clip;
        private Animator _animator;
        private BonePoseDriver _driver;
        private EyeOverlay _eyes;
        private EyeLashes _lashes;
        private bool _baseFrozen;

        /// <summary>The clip pose being held, or null when no clip is held.</summary>
        public ClipPose? Current { get; private set; }

        /// <summary>The bone pose being held, or null when none is.</summary>
        public BonePose CurrentBones { get; private set; }

        /// <summary>The eye look shown, or null when the kerbal's own eyes show.</summary>
        public EyeStyle CurrentEyes { get; private set; }

        /// <summary>Whether the kerbal has been given eyelashes (<see cref="EyeLashes"/>).</summary>
        public bool CurrentLashes { get; private set; }

        /// <summary>
        /// Whether a pose is held and its Animator still exists (the kerbal can be destroyed while posed, e.g. on
        /// a scene change or when the EVA ends).
        /// </summary>
        public bool IsHolding => _animator != null && ((Current.HasValue && _graph.IsValid()) || CurrentBones != null);

        /// <summary>
        /// Whether the game's own animation is paused while a pose is held. The held clip only covers the bones it
        /// animates; everything else keeps playing the game's animation unless this is on.
        /// </summary>
        public bool FreezeBaseAnimation { get; private set; }

        /// <param name="logError">Where failures are reported.</param>
        /// <param name="logInfo">Where what was done is reported (e.g. how an eye look was fitted).</param>
        /// <param name="gameSpeedFor">
        /// The speed the game would give an Animator right now, used to hand it back after freezing it. The game
        /// only sets the speed when the time-warp rate changes, so restoring a remembered value could leave a stale
        /// speed if warp changed while frozen.
        /// </param>
        public PosePlayer(Action<string> logError, Action<string> logInfo, Func<Animator, float> gameSpeedFor)
        {
            _logError = logError ?? throw new ArgumentNullException(nameof(logError));
            _logInfo = logInfo ?? throw new ArgumentNullException(nameof(logInfo));
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

        /// <summary>Holds <paramref name="animator"/> in a clip pose, with no bones posed over it.</summary>
        public bool Hold(Animator animator, ClipPose pose) => Hold(animator, pose, null);

        /// <summary>
        /// Holds <paramref name="animator"/> in <paramref name="clip"/> (or the game's own animation, if null) with
        /// <paramref name="bones"/> (if any) set over it, <paramref name="eyes"/> (if any) laid over its eyes, and
        /// eyelashes if <paramref name="lashes"/>.
        /// Holding the same Animator in another pose swaps the clip if it changed and moves the held frame; a
        /// different Animator releases the previous one first.
        /// </summary>
        /// <returns>
        /// False, with the reason logged, when there's nothing to hold or the Animator has no clip of that name. Bones
        /// the kerbal doesn't have are logged and skipped; the rest of the pose still holds.
        /// </returns>
        public bool Hold(Animator animator, ClipPose? clip, BonePose bones, EyeStyle eyes = null, bool lashes = false)
        {
            string what = clip.HasValue ? (bones != null ? $"{clip.Value} with {bones}" : clip.Value.ToString()) : bones?.ToString();
            if (animator == null)
            {
                _logError($"Cannot hold {what ?? "a pose"}: no Animator was given.");
                return false;
            }
            if (what == null)
            {
                _logError($"Cannot hold a pose on '{animator.name}': it has neither a clip nor bones.");
                return false;
            }

            AnimationClip found = null;
            if (clip.HasValue)
            {
                found = FindClip(animator, clip.Value.ClipName);
                if (found == null)
                {
                    RuntimeAnimatorController controller = animator.runtimeAnimatorController;
                    _logError($"Cannot hold {what} on '{animator.name}': its controller "
                              + $"'{(controller != null ? controller.name : "<none>")}' has no clip named '{clip.Value.ClipName}'.");
                    return false;
                }
            }

            if (_animator != animator) Release();
            _animator = animator;

            if (found != null)
            {
                if (!_graph.IsValid()) Build(animator);
                if (_clip != found) SetClip(found);
                // Held past the end means held on the last frame, as the pose lab's slider allows the full length
                float time = Mathf.Min(clip.Value.Time, found.length);
                _clipPlayable.SetTime(time);
                Current = new ClipPose(found.name, time);
            }
            else
            {
                ReleaseClip();
            }

            SetBones(bones);
            SetEyes(eyes);
            SetLashes(lashes);
            ApplyBaseFreeze();
            return true;
        }

        /// <summary>Gives the Animator back to the game's own animation. Safe to call when nothing is held.</summary>
        public void Release()
        {
            UnfreezeBase();
            ReleaseClip();
            SetBones(null);
            SetEyes(null);
            SetLashes(false);
            _animator = null;
        }

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
            if (!IsHolding || !Current.HasValue || !_graph.IsValid()) return false;
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
        }

        private void ReleaseClip()
        {
            if (_graph.IsValid()) _graph.Destroy();
            _graph = default;
            _clipPlayable = default;
            _clip = null;
            Current = null;
        }

        // Finds the pose's bones on the kerbal and hands them to the driver; null takes the driver away
        private void SetBones(BonePose bones)
        {
            CurrentBones = bones;
            if (bones == null || _animator == null)
            {
                if (_driver != null) UnityEngine.Object.Destroy(_driver);
                _driver = null;
                return;
            }

            var byName = new Dictionary<string, Transform>(StringComparer.Ordinal);
            foreach (Transform t in _animator.GetComponentsInChildren<Transform>(true))
                if (t.name.StartsWith(KerbalRig.BonePrefix, StringComparison.Ordinal))
                    byName[t.name.Substring(KerbalRig.BonePrefix.Length)] = t;

            var missing = new List<string>();
            List<(Transform, T)> Find<T>(IReadOnlyDictionary<string, T> list)
            {
                var found = new List<(Transform, T)>();
                foreach (KeyValuePair<string, T> bone in list)
                {
                    if (byName.TryGetValue(bone.Key, out Transform t)) found.Add((t, bone.Value));
                    else missing.Add(bone.Key);
                }
                return found;
            }
            List<(Transform, Quaternion)> set = Find(bones.Rotations), turned = Find(bones.Turns);
            List<(Transform, Vector3)> moved = Find(bones.Moves), placed = Find(bones.Positions);
            if (missing.Count > 0)
                _logError($"Pose on '{_animator.name}': no bones named {string.Join(", ", missing)} (without the "
                          + $"'{KerbalRig.BonePrefix}' prefix); those parts keep their animation.");

            if (_driver == null)
                _driver = _animator.GetComponent<BonePoseDriver>() ?? _animator.gameObject.AddComponent<BonePoseDriver>();
            _driver.Set(set, turned, moved, placed);
        }

        // Gives the kerbal eyelashes, or its own lids back
        private void SetLashes(bool lashes)
        {
            if (lashes == CurrentLashes && (_lashes != null) == lashes) return;
            _lashes?.Release();
            _lashes = null;
            CurrentLashes = lashes;
            if (lashes && _animator != null) _lashes = EyeLashes.Apply(_animator.transform, _logInfo, _logError);
        }

        // Shows the eye look over the kerbal's eyes, in place of any shown before; null takes it away
        private void SetEyes(EyeStyle eyes)
        {
            _eyes?.Destroy();
            _eyes = null;
            CurrentEyes = eyes;
            if (eyes != null && _animator != null) _eyes = EyeOverlay.Create(_animator.transform, eyes, _logInfo, _logError);
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
