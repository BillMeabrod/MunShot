using System;
using MunShot.Capture;
using UnityEngine;
using UnityEngine.UIElements;

namespace MunShot.UI
{
    /// <summary>Which lines are drawn over the picture to help compose it.</summary>
    public enum FrameGrid
    {
        None,

        /// <summary>Two lines each way, a third in: things of interest sit on the lines or where they cross.</summary>
        Thirds,

        /// <summary>A small cross in the middle, for lining things up on the centre.</summary>
        Centre,

        /// <summary>As thirds, but at the golden ratio (0.382 and 0.618 of the way across).</summary>
        GoldenRatio
    }

    /// <summary>
    /// Photo mode's frame guides: a grid, and the photo's frame shape (<see cref="PhotoFrame"/>) with what the crop cuts
    /// off shaded, drawn over the picture on an element covering the screen behind photo mode's panels. They're never
    /// in the photo: photo mode's whole UI hides while one's taken. They stay up when the rest of the UI is hidden (the
    /// Hide UI key), as that's when a shot is framed.
    /// </summary>
    public sealed class FrameGuides
    {
        // Lines light with a dark edge, so they show on snow and on space alike
        private static readonly Color LineColor = new Color(1f, 1f, 1f, 0.6f);
        private static readonly Color LineEdgeColor = new Color(0f, 0f, 0f, 0.35f);
        private const float LineWidth = 1f;
        private const float LineEdgeWidth = 3f;

        // Outside the frame shape, what the crop cuts off: dark enough to see the shape, light enough to still see what's there
        private static readonly Color ShadeColor = new Color(0f, 0f, 0f, 0.6f);

        // The centre cross's arms, as a share of the frame's shorter side
        private const float CentreArm = 0.03f;

        private const float GoldenSection = 0.381966f;

        private readonly VisualElement _element;
        private FrameGrid _grid = FrameGrid.None;
        private FrameShape _shape = FrameShape.Full;

        /// <summary>Draws on <paramref name="element"/>, which should cover the screen and take no clicks.</summary>
        public FrameGuides(VisualElement element)
        {
            _element = element ?? throw new ArgumentNullException(nameof(element));
            _element.pickingMode = PickingMode.Ignore;
            _element.generateVisualContent += Draw;
        }

        public FrameGrid Grid
        {
            get => _grid;
            set
            {
                if (_grid == value) return;
                _grid = value;
                _element.MarkDirtyRepaint();
            }
        }

        public FrameShape Shape
        {
            get => _shape;
            set
            {
                if (_shape == value) return;
                _shape = value;
                _element.MarkDirtyRepaint();
            }
        }

        private void Draw(MeshGenerationContext context)
        {
            if (_grid == FrameGrid.None && _shape == FrameShape.Full) return;
            Rect screen = _element.contentRect;
            if (screen.width <= 0f || screen.height <= 0f) return;
            Rect fit = PhotoFrame.Fit(screen.width, screen.height, _shape);
            Rect frame = new Rect(screen.x + fit.x, screen.y + fit.y, fit.width, fit.height);
            Painter2D painter = context.painter2D;

            if (_shape != FrameShape.Full)
            {
                Shade(painter, screen, frame);
                Outline(painter, frame);
            }

            switch (_grid)
            {
                case FrameGrid.Thirds:
                    GridLines(painter, frame, 1f / 3f);
                    break;
                case FrameGrid.GoldenRatio:
                    GridLines(painter, frame, GoldenSection);
                    break;
                case FrameGrid.Centre:
                    float arm = Mathf.Min(frame.width, frame.height) * CentreArm;
                    Vector2 middle = frame.center;
                    Lines(painter,
                          (middle - new Vector2(arm, 0f), middle + new Vector2(arm, 0f)),
                          (middle - new Vector2(0f, arm), middle + new Vector2(0f, arm)));
                    break;
            }
        }

        // Lines `share` in from each side, both ways
        private static void GridLines(Painter2D painter, Rect frame, float share)
        {
            float x1 = frame.xMin + frame.width * share, x2 = frame.xMax - frame.width * share;
            float y1 = frame.yMin + frame.height * share, y2 = frame.yMax - frame.height * share;
            Lines(painter,
                  (new Vector2(x1, frame.yMin), new Vector2(x1, frame.yMax)),
                  (new Vector2(x2, frame.yMin), new Vector2(x2, frame.yMax)),
                  (new Vector2(frame.xMin, y1), new Vector2(frame.xMax, y1)),
                  (new Vector2(frame.xMin, y2), new Vector2(frame.xMax, y2)));
        }

        // The screen outside the frame, as up to four bands
        private static void Shade(Painter2D painter, Rect screen, Rect frame)
        {
            painter.fillColor = ShadeColor;
            FillRect(painter, Rect.MinMaxRect(screen.xMin, screen.yMin, screen.xMax, frame.yMin));
            FillRect(painter, Rect.MinMaxRect(screen.xMin, frame.yMax, screen.xMax, screen.yMax));
            FillRect(painter, Rect.MinMaxRect(screen.xMin, frame.yMin, frame.xMin, frame.yMax));
            FillRect(painter, Rect.MinMaxRect(frame.xMax, frame.yMin, screen.xMax, frame.yMax));
        }

        private static void FillRect(Painter2D painter, Rect rect)
        {
            if (rect.width <= 0f || rect.height <= 0f) return;
            painter.BeginPath();
            painter.MoveTo(new Vector2(rect.xMin, rect.yMin));
            painter.LineTo(new Vector2(rect.xMax, rect.yMin));
            painter.LineTo(new Vector2(rect.xMax, rect.yMax));
            painter.LineTo(new Vector2(rect.xMin, rect.yMax));
            painter.ClosePath();
            painter.Fill();
        }

        private static void Outline(Painter2D painter, Rect frame)
        {
            Vector2 a = new Vector2(frame.xMin, frame.yMin), b = new Vector2(frame.xMax, frame.yMin);
            Vector2 c = new Vector2(frame.xMax, frame.yMax), d = new Vector2(frame.xMin, frame.yMax);
            Lines(painter, (a, b), (b, c), (c, d), (d, a));
        }

        // Each line twice: a wide dark edge, then the light line on top
        private static void Lines(Painter2D painter, params (Vector2 from, Vector2 to)[] lines)
        {
            Stroke(painter, lines, LineEdgeColor, LineEdgeWidth);
            Stroke(painter, lines, LineColor, LineWidth);
        }

        private static void Stroke(Painter2D painter, (Vector2 from, Vector2 to)[] lines, Color color, float width)
        {
            painter.strokeColor = color;
            painter.lineWidth = width;
            painter.lineCap = LineCap.Butt;
            painter.BeginPath();
            foreach ((Vector2 from, Vector2 to) in lines)
            {
                painter.MoveTo(from);
                painter.LineTo(to);
            }
            painter.Stroke();
        }
    }
}
