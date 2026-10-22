using System;
using UnityEngine;

namespace MunShot.Posing
{
    /// <summary>
    /// Moving and rotating photo doubles with the mouse, VAB style. Clicking a double selects it and shows the handles
    /// on it. With the move tool, dragging the double slides it along the ground plane (the plane across its up), and
    /// dragging an arrow moves it along that axis; with the rotate tool, dragging a ring turns it about that axis,
    /// around the middle of its body. Hovering a double outlines it and plays a sound, as hovering a part does in the
    /// VAB (<see cref="HoverFeedback"/>). The UI feeds it the mouse each frame, so this class has no input or UI code.
    /// </summary>
    public sealed class DoubleManipulator : IDisposable
    {
        private enum Drag
        {
            None,
            Body,
            Arrow,
            Ring
        }

        private readonly PhotoSession _session;
        private readonly HoverFeedback _hover = new HoverFeedback();
        private ManipulationHandles _handles;
        private bool _handlesRequested;
        private readonly Action<string> _logInfo;
        private readonly Action<string> _logError;

        private Drag _drag = Drag.None;
        private int _dragAxis;
        private Plane _dragPlane;
        private Vector3 _dragStartPoint;
        private Vector3 _dragStartPosition;
        private Vector3 _ringLast;

        public HandleMode Mode { get; private set; } = HandleMode.Move;

        /// <summary>Raised when a click selects a different double, for the UI to follow.</summary>
        public event Action<PhotoSession.Entry> Selected;

        public DoubleManipulator(PhotoSession session, Action<string> logInfo, Action<string> logError)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _logInfo = logInfo ?? throw new ArgumentNullException(nameof(logInfo));
            _logError = logError ?? throw new ArgumentNullException(nameof(logError));
        }

        public void SetMode(HandleMode mode)
        {
            Mode = mode;
            _handles?.SetMode(mode);
        }

        /// <summary>
        /// Called every frame with the mouse: <paramref name="pressed"/>/<paramref name="released"/> on the frames the
        /// left button goes down/up, <paramref name="held"/> while it's down. Presses over the UI are ignored.
        /// </summary>
        public void Tick(Camera camera, Vector2 mouse, bool pressed, bool held, bool released, bool overUi)
        {
            EnsureHandles();
            PhotoSession.Entry selected = _session.Selected;
            bool showHandles = camera != null && selected != null && selected.IsReady && selected.Root != null;
            _handles?.SetVisible(showHandles);
            if (showHandles) _handles?.Follow(Centre(selected), selected.Root.rotation, camera);
            if (camera == null) return;

            Ray ray = camera.ScreenPointToRay(mouse);
            if (pressed && !overUi) BeginDrag(ray, selected);
            else if (held && _drag != Drag.None) ContinueDrag(ray, camera);
            if (released || !held) _drag = Drag.None;

            // Hovered like a part in the VAB; a double being dragged stays lit, the window covers what's behind it
            if (_drag == Drag.None) _hover.SetHovered(overUi ? null : _session.PickDouble(ray, out _), camera);
        }

        public void Dispose()
        {
            _hover.Clear();
            _handles?.Dispose();
            _handles = null;
            _handlesRequested = false;
            _drag = Drag.None;
        }

        private void BeginDrag(Ray ray, PhotoSession.Entry selected)
        {
            // A handle of the selected double takes the click before any double does
            if (selected != null && selected.IsReady && _handles != null && _handles.TryPick(ray, out HandleHit hit))
            {
                _dragAxis = hit.Axis;
                Vector3 axis = AxisOf(selected, hit.Axis);
                Vector3 centre = Centre(selected);
                if (hit.Kind == HandleMode.Rotate)
                {
                    _drag = Drag.Ring;
                    _dragPlane = new Plane(axis, centre);
                    _ringLast = _dragPlane.Raycast(ray, out float enter) ? ray.GetPoint(enter) - centre : Vector3.zero;
                }
                else
                {
                    _drag = Drag.Arrow;
                    _dragStartPosition = selected.Root.position;
                    _dragStartPoint = ClosestOnAxis(ray, centre, axis);
                }
                return;
            }

            PhotoSession.Entry clicked = _session.PickDouble(ray, out Vector3 point);
            if (clicked == null) return;
            if (clicked != selected)
            {
                _session.Select(clicked);
                Selected?.Invoke(clicked);
            }
            if (Mode == HandleMode.Move)
            {
                _drag = Drag.Body;
                _dragPlane = new Plane(clicked.Up, point);
                _dragStartPoint = point;
                _dragStartPosition = clicked.Root.position;
            }
        }

        private void ContinueDrag(Ray ray, Camera camera)
        {
            PhotoSession.Entry selected = _session.Selected;
            if (selected == null || selected.Root == null)
            {
                _drag = Drag.None;
                return;
            }

            switch (_drag)
            {
                case Drag.Body:
                    if (_dragPlane.Raycast(ray, out float enter))
                        selected.Root.position = _dragStartPosition + (ray.GetPoint(enter) - _dragStartPoint);
                    break;

                case Drag.Arrow:
                {
                    Vector3 axis = AxisOf(selected, _dragAxis);
                    Vector3 along = ClosestOnAxis(ray, _dragStartPoint, axis);
                    selected.Root.position = _dragStartPosition + Vector3.Project(along - _dragStartPoint, axis);
                    break;
                }

                case Drag.Ring:
                {
                    Vector3 centre = Centre(selected);
                    Vector3 axis = _dragPlane.normal;
                    if (!_dragPlane.Raycast(ray, out float hitAt)) break;
                    Vector3 now = ray.GetPoint(hitAt) - centre;
                    if (_ringLast.sqrMagnitude > 1e-8f && now.sqrMagnitude > 1e-8f)
                    {
                        float angle = Vector3.SignedAngle(_ringLast, now, axis);
                        selected.Root.RotateAround(centre, axis, angle);
                    }
                    _ringLast = now;
                    break;
                }
            }
        }

        private void EnsureHandles()
        {
            if (_handles != null || _handlesRequested) return;
            PhotoSession.Entry anyReady = null;
            foreach (PhotoSession.Entry entry in _session.Entries)
                if (entry.IsReady) { anyReady = entry; break; }
            if (anyReady == null) return;

            _handlesRequested = true;
            ManipulationHandles.Create(anyReady.Root.gameObject.layer, _logInfo, _logError, handles =>
            {
                _handles = handles;
                _handles.SetMode(Mode);
                _session.SetIgnoredInSurfaceSearch(_handles.Root);
            });
        }

        // The middle of the double's body: where the handles sit and what rings turn it about
        private static Vector3 Centre(PhotoSession.Entry entry) => PhotoSession.BoundsOf(entry, out Bounds bounds) ? bounds.center : entry.Root.position;

        private static Vector3 AxisOf(PhotoSession.Entry entry, int axis) =>
            axis == 0 ? entry.Root.right : axis == 1 ? entry.Root.up : entry.Root.forward;

        // The point on the line through origin along axis that's nearest the ray
        private static Vector3 ClosestOnAxis(Ray ray, Vector3 origin, Vector3 axis)
        {
            Vector3 w = origin - ray.origin;
            float a = Vector3.Dot(axis, axis), b = Vector3.Dot(axis, ray.direction), c = Vector3.Dot(ray.direction, ray.direction);
            float d = Vector3.Dot(axis, w), e = Vector3.Dot(ray.direction, w);
            float denominator = a * c - b * b;
            float s = denominator > 1e-6f ? (b * e - c * d) / denominator : 0f;
            return origin + axis * s;
        }
    }
}
