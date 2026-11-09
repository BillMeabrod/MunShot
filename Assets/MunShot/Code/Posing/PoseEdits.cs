using System;
using System.Collections.Generic;
using UnityEngine;

namespace MunShot.Posing
{
    /// <summary>
    /// A kerbal's slider settings in the pose controls window (<see cref="PoseJoints"/>), by slider id, and the bone
    /// turns and slides they make. Laid over whatever pose the kerbal holds.
    /// </summary>
    public sealed class PoseEdits
    {
        private readonly Dictionary<string, float> _values = new Dictionary<string, float>(StringComparer.Ordinal);

        public bool IsEmpty => _values.Count == 0;

        /// <summary>A slider's setting (degrees, or percent for a slide); 0 when it hasn't been moved.</summary>
        public float Get(string controlId) => _values.TryGetValue(controlId, out float value) ? value : 0f;

        /// <summary>Sets a slider (clamped to its range); 0 takes the setting away.</summary>
        public void Set(PoseJoints.Control control, float value)
        {
            if (control == null) throw new ArgumentNullException(nameof(control));
            value = Mathf.Clamp(value, control.Min, control.Max);
            if (Mathf.Approximately(value, 0f)) _values.Remove(control.Id);
            else _values[control.Id] = value;
        }

        /// <summary>Puts every slider of <paramref name="joint"/> back to 0.</summary>
        public void Reset(PoseJoints.Joint joint)
        {
            foreach (PoseJoints.Control control in PoseJoints.ControlsOf(joint))
                _values.Remove(control.Id);
        }

        public void Clear() => _values.Clear();

        /// <summary>What the settings do to the bones, by bone name (without the "bone_kerbal_" prefix).</summary>
        public sealed class BoneEdits
        {
            /// <summary>A turn in the bone's parent's frame, put before the pose's rotation, and one in its own, after.</summary>
            public IReadOnlyDictionary<string, (Quaternion parent, Quaternion own)> Turns { get; }

            /// <summary>A slide in metres, in the bone's parent's frame, on top of where the pose has it.</summary>
            public IReadOnlyDictionary<string, Vector3> Slides { get; }

            /// <summary>Bones turned from their rest rotation rather than from the pose.</summary>
            public IReadOnlyCollection<string> FromRest { get; }

            public BoneEdits(IReadOnlyDictionary<string, (Quaternion parent, Quaternion own)> turns, IReadOnlyDictionary<string, Vector3> slides,
                             IReadOnlyCollection<string> fromRest)
            {
                Turns = turns;
                Slides = slides;
                FromRest = fromRest;
            }
        }

        /// <summary>The turns and slides the settings make.</summary>
        public BoneEdits ToBones()
        {
            var turns = new Dictionary<string, (Quaternion parent, Quaternion own)>(StringComparer.Ordinal);
            var slides = new Dictionary<string, Vector3>(StringComparer.Ordinal);
            var fromRest = new HashSet<string>(StringComparer.Ordinal);
            // In the catalogue's order, so a bone's turns always combine the same way
            foreach (PoseJoints.Joint joint in PoseJoints.All)
                foreach (PoseJoints.Group group in joint.Groups)
                {
                    // Any slider of a group moved: all its bones that start from rest do, so they keep their shape
                    // together (a straight thumb base under a curled tip from the pose would look broken)
                    bool moved = false;
                    foreach (PoseJoints.Control control in group.Controls)
                        moved |= _values.ContainsKey(control.Id);
                    if (moved)
                        foreach (PoseJoints.Control control in group.Controls)
                            foreach (PoseJoints.Part part in control.Parts)
                                if (part.FromRest) fromRest.Add(part.Bone);

                    foreach (PoseJoints.Control control in group.Controls)
                    {
                        if (!_values.TryGetValue(control.Id, out float value)) continue;
                        foreach (PoseJoints.Part part in control.Parts)
                        {
                            if (!part.AppliesTo(value)) continue;
                            if (part.Frame == PoseJoints.Frame.Slide)
                            {
                                slides.TryGetValue(part.Bone, out Vector3 slide);
                                slides[part.Bone] = slide + part.Axis * (value * part.Share);
                                continue;
                            }
                            Quaternion turn = Quaternion.AngleAxis(value * part.Share, part.Axis);
                            if (!turns.TryGetValue(part.Bone, out (Quaternion parent, Quaternion own) both))
                                both = (Quaternion.identity, Quaternion.identity);
                            if (part.Frame == PoseJoints.Frame.Parent) both.parent = turn * both.parent;
                            else both.own = both.own * turn;
                            turns[part.Bone] = both;
                        }
                    }
                }
            return new BoneEdits(turns, slides, fromRest);
        }
    }
}
