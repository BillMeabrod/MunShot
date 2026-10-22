using System;
using System.Globalization;

namespace MunShot.Posing
{
    /// <summary>
    /// A pose taken from one of the game's own animation clips: the clip's name and the moment in it to hold.
    /// The clip is looked up by name on the kerbal's Animator, so a pose keeps working as long as the game ships a
    /// clip with that name.
    /// </summary>
    [Serializable]
    public readonly struct ClipPose : IEquatable<ClipPose>
    {
        /// <summary>The clip's name, as in the kerbal Animator's clip list (e.g. "kerbal_emote_wave").</summary>
        public string ClipName { get; }

        /// <summary>The moment to hold, in seconds from the clip's start.</summary>
        public float Time { get; }

        public ClipPose(string clipName, float time)
        {
            if (string.IsNullOrEmpty(clipName))
                throw new ArgumentException("A clip pose needs a clip name.", nameof(clipName));
            if (float.IsNaN(time) || time < 0f)
                throw new ArgumentOutOfRangeException(nameof(time), time, "A clip pose's time must be zero or more.");

            ClipName = clipName;
            Time = time;
        }

        public bool Equals(ClipPose other) => ClipName == other.ClipName && Time.Equals(other.Time);
        public override bool Equals(object obj) => obj is ClipPose other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(ClipName, Time);

        /// <summary>"clip @ 1.350s", the form shown in the pose lab.</summary>
        public override string ToString() => $"{ClipName} @ {Time.ToString("0.000", CultureInfo.InvariantCulture)}s";
    }
}
