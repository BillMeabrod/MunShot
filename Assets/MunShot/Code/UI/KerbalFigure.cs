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
    /// <see cref="PoseJoints"/> at its spot on the drawing's 200 × 300 canvas (styled by PhotoModeWindow.uss's pose-dot).
    /// </summary>
    internal sealed class KerbalFigure : VisualElement
    {
        private const float CanvasWidth = 200f;
        private const float CanvasHeight = 300f;

        private readonly Dictionary<PoseJoints.Joint, Button> _dots = new Dictionary<PoseJoints.Joint, Button>();

        /// <summary>A dot was clicked.</summary>
        public event Action<PoseJoints.Joint> JointClicked;

        public KerbalFigure(float width, Texture2D drawing)
        {
            AddToClassList("pose-figure");
            style.width = width;
            style.height = width * CanvasHeight / CanvasWidth;
            if (drawing != null) style.backgroundImage = drawing;

            foreach (PoseJoints.Joint joint in PoseJoints.All)
            {
                PoseJoints.Joint clicked = joint;
                // Buttons, so a click is a click (the window's drag doesn't take it) and the menu sounds play
                var dot = new Button(() => JointClicked?.Invoke(clicked)) { name = "joint-" + joint.Id, text = "" };
                dot.ClearClassList();
                dot.AddToClassList("pose-dot");
                dot.style.left = new StyleLength(Length.Percent(joint.Spot.x / CanvasWidth * 100f));
                dot.style.top = new StyleLength(Length.Percent(joint.Spot.y / CanvasHeight * 100f));
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
                dot.Value.EnableInClassList("pose-dot--chosen", dot.Key == selected);
        }

        /// <summary>The dots' names (shown on hover), in the current language.</summary>
        public void Relocalize()
        {
            foreach (KeyValuePair<PoseJoints.Joint, Button> dot in _dots)
                dot.Value.tooltip = Localization.Translate(dot.Key.NameKey);
        }
    }
}
