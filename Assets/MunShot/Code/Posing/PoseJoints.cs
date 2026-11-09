using System;
using System.Collections.Generic;
using UnityEngine;

namespace MunShot.Posing
{
    /// <summary>
    /// The joints the player can bend in the pose controls window, and what each of their sliders turns.
    ///
    /// Each slider turns one or more bones by its value in degrees (times each bone's share), about an axis of the bone's
    /// own frame (on top of where the pose has it, so a bend stays a bend whatever the pose) or of its parent's frame
    /// (for the eyelids, which the game turns about the eye). Axes and directions come from the rig's rest pose
    /// (Tools/RigDiscovery out/body_spacesuit_01.prefab.axes.txt): the left and right sides' frames aren't mirror
    /// images of each other, so some sliders turn the other way on the right to move both sides alike.
    /// </summary>
    public static class PoseJoints
    {
        public enum Frame
        {
            /// <summary>About the bone's own axis, after the pose: a bend stays the same bend whatever the pose.</summary>
            Own,

            /// <summary>About the bone's parent's axis, before the pose (as <see cref="BonePose.Turns"/>).</summary>
            Parent,

            /// <summary>
            /// Slid along the bone's parent's axis, on top of the pose (as <see cref="BonePose.Moves"/>): the share is in
            /// metres per percent of the slider.
            /// </summary>
            Slide
        }

        /// <summary>What a slider's value means.</summary>
        public enum Unit
        {
            Degrees,
            Percent
        }

        /// <summary>
        /// One bone a slider turns: about which axis, in which frame, by how much of the slider's value, and on which
        /// side of 0 (a slider can move one way past 0 and another way below it).
        /// </summary>
        public readonly struct Part
        {
            public string Bone { get; }
            public Vector3 Axis { get; }
            public Frame Frame { get; }
            public float Share { get; }

            /// <summary>1: only for values above 0; -1: only below 0; 0: both.</summary>
            public int Side { get; }

            /// <summary>
            /// Whether the bone starts from its rest rotation (<see cref="KerbalRig.TryGetRest"/>) rather than the pose,
            /// once any slider of its group is moved, so the sliders' ends are always the same shapes.
            /// </summary>
            public bool FromRest { get; }

            public Part(string bone, Vector3 axis, float share = 1f, Frame frame = Frame.Own, int side = 0, bool fromRest = false)
            {
                Bone = bone;
                Axis = axis;
                Share = share;
                Frame = frame;
                Side = side;
                FromRest = fromRest;
            }

            public bool AppliesTo(float value) => Side == 0 || (Side > 0 ? value > 0f : value < 0f);
        }

        /// <summary>A slider: its name's key, its range (degrees, or percent for a slide), and the bones it moves.</summary>
        public sealed class Control
        {
            public string Id { get; }
            public string NameKey { get; }
            public float Min { get; }
            public float Max { get; }
            public IReadOnlyList<Part> Parts { get; }

            public Unit Unit => Parts.Count > 0 && Parts[0].Frame == Frame.Slide ? Unit.Percent : Unit.Degrees;

            public Control(string id, string nameKey, float min, float max, params Part[] parts)
            {
                Id = id;
                NameKey = nameKey;
                Min = min;
                Max = max;
                Parts = parts;
            }
        }

        /// <summary>A set of sliders under a heading (a finger), or with none (the joint's own).</summary>
        public sealed class Group
        {
            public string HeadingKey { get; }
            public IReadOnlyList<Control> Controls { get; }

            public Group(string headingKey, params Control[] controls)
            {
                HeadingKey = headingKey;
                Controls = controls;
            }
        }

        /// <summary>A dot on the kerbal: its name's key, where it sits on the drawing, and its sliders.</summary>
        public sealed class Joint
        {
            public string Id { get; }
            public string NameKey { get; }

            /// <summary>Where the dot sits on the pose controls window's 200 × 300 kerbal drawing (UI/Icons/pose-kerbal.png).</summary>
            public Vector2 Spot { get; }

            public IReadOnlyList<Group> Groups { get; }

            public Joint(string id, string nameKey, Vector2 spot, params Group[] groups)
            {
                Id = id;
                NameKey = nameKey;
                Spot = spot;
                Groups = groups;
            }
        }

        private static readonly Vector3 X = Vector3.right, Y = Vector3.up, Z = Vector3.forward;

        private const string JointKey = "MunShot/Joint/";
        private const string ControlKey = "MunShot/PoseControl/";

        /// <summary>Every joint, head to feet; the kerbal's right side is on the drawing's left, as it faces the viewer.</summary>
        public static IReadOnlyList<Joint> All { get; } = Build();

        private static IReadOnlyList<Joint> Build()
        {
            var joints = new List<Joint>
            {
                // The head turns on its own and the helmet follows it (PosePlayer). The neck bends at its base
                // (neck_ach), which the helmet hangs from. The suit's air hose is attached to the neck bone, so it's
                // kept where the pose has it whatever these do (PosePlayer)
                new Joint("head", JointKey + "Head", new Vector2(100, 36), new Group(null,
                    new Control("head.nod", ControlKey + "Nod", -35, 35, new Part("head", Z)),
                    new Control("head.turn", ControlKey + "Turn", -60, 60, new Part("head", X)),
                    new Control("head.tilt", ControlKey + "Tilt", -25, 25, new Part("head", Y)))),
                new Joint("neck", JointKey + "Neck", new Vector2(100, 133), new Group(null,
                    new Control("neck.nod", ControlKey + "Nod", -35, 35, new Part("neck_ach", X)),
                    new Control("neck.turn", ControlKey + "Turn", -60, 60, new Part("neck_ach", Z)),
                    new Control("neck.tilt", ControlKey + "Tilt", -25, 25, new Part("neck_ach", Y)))),
                // Each eye on its own: where it looks, and each lid closing and slanting on its own (they rest slanted,
                // so closing alone would bring them over the eye at an angle). Closing turns about the eye's
                // side-to-side axis, slanting about its gaze; the right eye's frame is turned round, so its lids turn
                // the other way to match. The kerbal's right eye is on the drawing's left
                Eye("r", JointKey + "RightEye", new Vector2(88, 64), -1f),
                Eye("l", JointKey + "LeftEye", new Vector2(112, 64), 1f),
                // The lips are slid, in their jaw's frame: -X is up, -Y forward, -Z the kerbal's left
                new Joint("mouth", JointKey + "Mouth", new Vector2(100, 98), new Group(null,
                    new Control("mouth.open", ControlKey + "Open", 0, 25, new Part("jaw_d", Z)),
                    new Control("mouth.smile", ControlKey + "Smile", -100, 100,
                        Slide("jaw_u_lips_l_corner", -X, 0.00025f), Slide("jaw_u_lips_r_corner", -X, 0.00025f),
                        Slide("jaw_d_lips_l", -X, 0.00012f), Slide("jaw_d_lips_r", -X, 0.00012f)),
                    new Control("mouth.width", ControlKey + "Width", -100, 100,
                        Slide("jaw_u_lips_l_corner", -Z, 0.0002f), Slide("jaw_u_lips_r_corner", Z, 0.0002f),
                        Slide("jaw_u_lips_l", -Z, 0.00012f), Slide("jaw_u_lips_r", Z, 0.00012f),
                        Slide("jaw_d_lips_l", -Z, 0.00012f), Slide("jaw_d_lips_r", Z, 0.00012f)),
                    new Control("mouth.leftcorner", ControlKey + "LeftCorner", -100, 100, Slide("jaw_u_lips_l_corner", -X, 0.00025f)),
                    new Control("mouth.rightcorner", ControlKey + "RightCorner", -100, 100, Slide("jaw_u_lips_r_corner", -X, 0.00025f)),
                    new Control("mouth.upperlip", ControlKey + "UpperLip", -5, 15, new Part("jaw_u", -Z)),
                    // Pulled back only a little: the teeth sit just behind the lips
                    new Control("mouth.pout", ControlKey + "Pout", -30, 100,
                        Slide("jaw_u_lips_lc", -Y, 0.00015f), Slide("jaw_u_lips_rc", -Y, 0.00015f),
                        Slide("jaw_d_lips_lc", -Y, 0.00015f), Slide("jaw_d_lips_rc", -Y, 0.00015f))),
                    // Seen with the mouth open. It's slid forward in the head's frame (-Y is forward); along the
                    // tongue bones' own frames, Y runs across it (tip up) and Z up (tip to the side)
                    new Group(ControlKey + "Tongue",
                        new Control("tongue.out", ControlKey + "StickOut", 0, 100, Slide("tongue_01", -Y, 0.0008f)),
                        new Control("tongue.updown", ControlKey + "UpDown", -60, 60,
                            new Part("tongue_01", Y, 0.4f), new Part("tongue_02", Y, 0.6f)),
                        new Control("tongue.sideways", ControlKey + "Sideways", -45, 45,
                            new Part("tongue_01", Z, 0.5f), new Part("tongue_02", Z, 0.5f)))),
                new Joint("spine", JointKey + "Spine", new Vector2(100, 172), new Group(null,
                    new Control("spine.forward", ControlKey + "LeanForward", -30, 45, new Part("spine_lower", -Z)),
                    new Control("spine.sideways", ControlKey + "LeanSideways", -30, 30, new Part("spine_lower", Y)),
                    new Control("spine.turn", ControlKey + "Turn", -45, 45, new Part("spine_lower", X)))),
            };
            foreach (bool left in new[] { false, true })
            {
                string s = left ? "l_" : "r_";
                string side = left ? "Left" : "Right";
                string id = left ? "l" : "r";
                // Turns that need the other way on the right to move both sides alike
                float m = left ? 1f : -1f;
                float x = left ? 1f : -1f;   // the drawing's x: the right side is on the left
                joints.Add(new Joint(id + "shoulder", JointKey + side + "Shoulder", new Vector2(100 + 34 * x, 150), new Group(null,
                    new Control(id + "shoulder.raise", ControlKey + "Raise", -90, 90, new Part(s + "shoulder", -m * Y)),
                    new Control(id + "shoulder.forward", ControlKey + "SwingForward", -90, 90, new Part(s + "shoulder", -Z)),
                    // Shared with the collarbone (its axis runs along the arm too), so the sleeve doesn't pinch at the
                    // shoulder
                    new Control(id + "shoulder.twist", ControlKey + "Twist", -60, 60,
                        new Part(s + "collar", X, 0.4f), new Part(s + "shoulder", X, 0.6f)))));
                joints.Add(new Joint(id + "elbow", JointKey + side + "Elbow", new Vector2(100 + 55 * x, 174), new Group(null,
                    // No twist of its own: the wrist's twist turns the forearm, shared between elbow and hand so the
                    // sleeve doesn't pinch at either
                    new Control(id + "elbow.bend", ControlKey + "Bend", -10, 140, new Part(s + "elbow", -Z)))));
                joints.Add(new Joint(id + "hand", JointKey + side + "Hand", new Vector2(100 + 65 * x, 211),
                    new Group(ControlKey + "Wrist",
                        new Control(id + "hand.bend", ControlKey + "Bend", -70, 70, new Part(s + "hand", Z)),
                        new Control(id + "hand.tilt", ControlKey + "TiltSideways", -40, 40, new Part(s + "hand", m * Y)),
                        // Shared with the forearm: the skin between elbow and hand has no bones of its own, so the hand
                        // twisting alone pinches it at the wrist
                        new Control(id + "hand.twist", ControlKey + "Twist", -60, 60, new Part(s + "hand", X, 0.5f), new Part(s + "elbow", X, 0.5f))),
                    // Checked on renders of the suit's hand (Tools/RigDiscovery/render_hand.cs). Spread above 0 swings
                    // the thumb flat out until it's square to the index finger; below 0 it swings about the hand's
                    // length, under the palm, towards the pinky's base, a little way only (flat, it would go
                    // through the palm). Curl bends the two outer joints on a slant between the palm's side and the
                    // fingers', as a thumb closes: onto the palm at rest, into itself spread out. Both start from the
                    // thumb's rest (nearly straight, beside the open hand), not the pose, so their ends are always these shapes
                    new Group(ControlKey + "Thumb",
                        new Control(id + "thumb.spread", ControlKey + "Spread", -30, 30,
                            new Part(s + "thumb_01", m * Y, 1f, Frame.Own, 1, fromRest: true),
                            new Part(s + "thumb_01", m * X, -1.2f, Frame.Parent, -1, fromRest: true)),
                        new Control(id + "thumb.curl", ControlKey + "Curl", -10, 80,
                            new Part(s + "thumb_02", ThumbCurl(m), 0.5f, fromRest: true),
                            new Part(s + "thumb_03", ThumbCurl(m), 0.5f, fromRest: true))),
                    new Group(ControlKey + "Pointer", Curl(id + "pointer.curl", s + "pointer"),
                        new Control(id + "pointer.spread", ControlKey + "Spread", -20, 20, new Part(s + "pointer_01", m * Y))),
                    new Group(ControlKey + "Middle", Curl(id + "middle.curl", s + "middle")),
                    new Group(ControlKey + "Pinky", Curl(id + "pinky.curl", s + "pinky"),
                        new Control(id + "pinky.spread", ControlKey + "Spread", -20, 20, new Part(s + "pinky_01", -m * Y)))));
                joints.Add(new Joint(id + "hip", JointKey + side + "Hip", new Vector2(100 + 14 * x, 203), new Group(null,
                    new Control(id + "hip.lift", ControlKey + "LiftLeg", -45, 90, new Part(s + "thigh", -Z)),
                    new Control(id + "hip.spread", ControlKey + "Spread", -20, 45, new Part(s + "thigh", -m * Y)),
                    new Control(id + "hip.twist", ControlKey + "Twist", -45, 45, new Part(s + "thigh", -m * X)))));
                joints.Add(new Joint(id + "knee", JointKey + side + "Knee", new Vector2(100 + 17 * x, 228), new Group(null,
                    new Control(id + "knee.bend", ControlKey + "Bend", -5, 130, new Part(s + "knee", Z)))));
                joints.Add(new Joint(id + "foot", JointKey + side + "Foot", new Vector2(100 + 18 * x, 262), new Group(null,
                    new Control(id + "foot.point", ControlKey + "PointToes", -40, 40, new Part(s + "foot", Z)))));
            }
            return joints;
        }

        // One eye: where it looks, and each lid closing (the upper down, the lower up) and slanting on its own
        private static Joint Eye(string side, string nameKey, Vector2 spot, float m) =>
            new Joint(side + "eye", nameKey, spot, new Group(null,
                new Control(side + "eye.sideways", ControlKey + "LookSideways", -35, 35, new Part(side + "_eye", Z)),
                new Control(side + "eye.updown", ControlKey + "LookUpDown", -30, 30, new Part(side + "_eye", -X)),
                new Control(side + "eye.upper", ControlKey + "UpperLid", -30, 110, new Part(side + "_eye_lid_u", m * X, 1f, Frame.Parent)),
                new Control(side + "eye.lower", ControlKey + "LowerLid", -30, 90, new Part(side + "_eye_lid_d", -m * X, 1f, Frame.Parent)),
                new Control(side + "eye.upperslant", ControlKey + "UpperSlant", -60, 60, new Part(side + "_eye_lid_u", m * Y, 1f, Frame.Parent)),
                new Control(side + "eye.lowerslant", ControlKey + "LowerSlant", -60, 60, new Part(side + "_eye_lid_d", m * Y, 1f, Frame.Parent))));

        // A thumb joint's bending axis, in its own frame: between its Z (towards the palm's side) and its Y (towards
        // the fingers'); the right thumb's frame is mirrored, so its Y part turns the other way
        private static Vector3 ThumbCurl(float m) => new Vector3(0f, -0.53f * m, 0.85f);

        private static Part Slide(string bone, Vector3 axis, float metresPerPercent) =>
            new Part(bone, axis, metresPerPercent, Frame.Slide);

        // A finger closing: its three joints bend together, the middle one most, as a real finger curls
        private static Control Curl(string id, string finger) =>
            new Control(id, ControlKey + "Curl", -20, 45,
                new Part(finger + "_01", Z, 0.9f), new Part(finger + "_02", Z, 1.1f), new Part(finger + "_03", Z, 0.9f));

        /// <summary>The joint a slider belongs to, or null.</summary>
        public static Joint JointOf(string controlId)
        {
            foreach (Joint joint in All)
                foreach (Group group in joint.Groups)
                    foreach (Control control in group.Controls)
                        if (control.Id == controlId) return joint;
            return null;
        }

        /// <summary>The slider with this id, or null.</summary>
        public static Control Find(string controlId)
        {
            foreach (Joint joint in All)
                foreach (Group group in joint.Groups)
                    foreach (Control control in group.Controls)
                        if (control.Id == controlId) return control;
            return null;
        }

        /// <summary>Every slider of a joint.</summary>
        public static IEnumerable<Control> ControlsOf(Joint joint)
        {
            if (joint == null) throw new ArgumentNullException(nameof(joint));
            foreach (Group group in joint.Groups)
                foreach (Control control in group.Controls)
                    yield return control;
        }
    }
}
