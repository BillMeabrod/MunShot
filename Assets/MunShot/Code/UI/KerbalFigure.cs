using System;
using System.Collections.Generic;
using MunShot.Posing;
using UnityEngine;
using UnityEngine.UIElements;

namespace MunShot.UI
{
    /// <summary>
    /// The pose controls window's kerbal: a full-body drawing in the style of the Kerbals tab icon (UI/Icons/
    /// pose-kerbal.png, made by Tools/Icons/make_pose_kerbal.ps1), facing the viewer, with a dot button on each joint of
    /// <see cref="PoseJoints"/> at its spot on the drawing's 200 × 300 canvas.
    /// </summary>
    internal sealed class KerbalFigure : VisualElement
    {
        private const float CanvasWidth = 200f;
        private const float CanvasHeight = 300f;
        private const float DotSize = 22f;

        private static readonly Color Fill = new Color32(0x16, 0x1B, 0x21, 0xFF);
        private static readonly Color Accent = new Color32(0x00, 0xCC, 0x51, 0xFF);

        private readonly Dictionary<PoseJoints.Joint, Button> _dots = new Dictionary<PoseJoints.Joint, Button>();

        /// <summary>A dot was clicked.</summary>
        public event Action<PoseJoints.Joint> JointClicked;

        public KerbalFigure(float width, Texture2D drawing)
        {
            style.width = width;
            style.height = width * CanvasHeight / CanvasWidth;
            style.flexShrink = 0;
            if (drawing != null) style.backgroundImage = drawing;

            foreach (PoseJoints.Joint joint in PoseJoints.All)
            {
                PoseJoints.Joint clicked = joint;
                // Buttons, so a click is a click (the window's drag doesn't take it) and the menu sounds play
                var dot = new Button(() => JointClicked?.Invoke(clicked)) { name = "joint-" + joint.Id, text = "" };
                dot.ClearClassList();
                dot.style.position = Position.Absolute;
                dot.style.width = DotSize;
                dot.style.height = DotSize;
                dot.style.left = new StyleLength(Length.Percent(joint.Spot.x / CanvasWidth * 100f));
                dot.style.top = new StyleLength(Length.Percent(joint.Spot.y / CanvasHeight * 100f));
                dot.style.marginLeft = -DotSize / 2f;
                dot.style.marginTop = -DotSize / 2f;
                dot.style.paddingLeft = dot.style.paddingRight = dot.style.paddingTop = dot.style.paddingBottom = 0;
                SetRadius(dot, DotSize / 2f);
                SetBorder(dot, 3f);
                dot.RegisterCallback<PointerEnterEvent>(_ => SetBorder(dot, 5f));
                dot.RegisterCallback<PointerLeaveEvent>(_ => SetBorder(dot, 3f));
                _dots[joint] = dot;
                Add(dot);
            }
            Relocalize();
            Select(null);
        }

        /// <summary>Fills the chosen joint's dot; the others show as rings.</summary>
        public void Select(PoseJoints.Joint selected)
        {
            foreach (KeyValuePair<PoseJoints.Joint, Button> dot in _dots)
                dot.Value.style.backgroundColor = dot.Key == selected ? Accent : Fill;
        }

        /// <summary>The dots' names (shown on hover), in the current language.</summary>
        public void Relocalize()
        {
            foreach (KeyValuePair<PoseJoints.Joint, Button> dot in _dots)
                dot.Value.tooltip = Localization.Translate(dot.Key.NameKey);
        }

        private static void SetBorder(VisualElement e, float width)
        {
            e.style.borderLeftWidth = e.style.borderRightWidth = e.style.borderTopWidth = e.style.borderBottomWidth = width;
            e.style.borderLeftColor = e.style.borderRightColor = e.style.borderTopColor = e.style.borderBottomColor = Accent;
        }

        private static void SetRadius(VisualElement e, float radius)
        {
            e.style.borderTopLeftRadius = e.style.borderTopRightRadius = radius;
            e.style.borderBottomLeftRadius = e.style.borderBottomRightRadius = radius;
        }
    }
}
