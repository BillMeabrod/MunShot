using System;
using System.Collections.Generic;
using MunShot.Cameras;
using MunShot.Lighting;
using UnityEngine;

namespace MunShot.Posing
{
    /// <summary>
    /// Moving and rotating what's in the shot with the mouse, VAB style, one or several at once: photo doubles and
    /// photo mode's lights alike (<see cref="IPlaceable"/>). There are two selections: the one being posed or set
    /// (<see cref="PhotoSession.Selected"/>, a single double, or <see cref="PhotoLights.Selected"/>, a single light)
    /// and the ones selected for moving (<see cref="Moving"/>, any number, doubles and lights together), which are
    /// outlined and carry the handles.
    /// <list type="bullet">
    /// <item>Clicking a double or a light's marker selects it for both. Shift+click adds it to (or takes it out of) the
    /// moving selection only. Dragging a box on empty space selects everything inside it for moving (Shift adds to the
    /// selection); it doesn't change who's posed. Clicking empty space clears the moving selection; the posed double
    /// stays.</item>
    /// <item>With the move tool, dragging a selected double moves the whole selection across the screen (up, down and
    /// sideways as seen, keeping its distance from the camera), and dragging an arrow moves it along that axis; with
    /// the rotate tool, dragging a ring turns the selection about that axis, around its middle. With angle snap on (as
    /// the VAB starts), a ring turns in 15° steps from where the drag began, or 5° with the fine modifier held, as the
    /// VAB's widget does.</item>
    /// <item>While dragging a selected double, holding the right button too turns the selection about "up" (away from
    /// the planet) as the mouse moves left and right; holding the tilt key (Ctrl) leans it sideways as the mouse moves
    /// left and right; and the tilt key with the tip key (Alt) tips it away from the camera or towards it as the mouse moves up and
    /// down. Each works on that one axis only, around the selection's middle, with either tool, and follows angle
    /// snap as the rings do (Shift for finer steps); letting go goes back to moving.</item>
    /// <item>Hovering a double or a light outlines it and plays a sound, as hovering a part does in the VAB; the moving
    /// selection stays outlined (<see cref="HoverFeedback"/>).</item>
    /// </list>
    /// The handles point the way the last one selected for moving faces (for a spot light, the way it shines). The UI
    /// feeds this the mouse each frame, so it has no input or UI code; the UI draws the selection box from
    /// <see cref="SelectionBox"/>.
    /// </summary>
    public sealed class PlaceableManipulator
    {
        private enum Drag
        {
            None,
            Body,
            Arrow,
            Ring,
            Box
        }

        // The VAB widget's rotation steps with angle snap on: normal, and with its speed modifier (Shift) held
        private const float SnapStep = 15f;
        private const float FineSnapStep = 5f;

        // A press on empty space that moves less than this (in pixels) is a click, not a box
        private const float BoxThreshold = 6f;

        // Turning or tilting a dragged selection: degrees per pixel the mouse moves
        private const float SpinDegreesPerPixel = 0.5f;

        private readonly PhotoSession _session;
        private readonly PhotoLights _lights;
        private readonly HoverFeedback _hover = new HoverFeedback();
        private readonly IScreenLens _lens;
        private readonly Action<string> _logInfo;
        private readonly Action<string> _logError;
        private ManipulationHandles _handles;
        private bool _handlesRequested;

        // Counts resets, so handles still loading when photo mode closes are thrown away when they arrive
        private int _generation;

        // Selected for moving, and the last one added (the handles face its way)
        private readonly List<IPlaceable> _moving = new List<IPlaceable>();
        private IPlaceable _primary;

        // The posed double as last seen: when it changes from outside (brought in, picked in the UI), it becomes the
        // moving selection too
        private PhotoSession.Entry _lastSelected;

        // A double selected for moving while it's still being built, which joins the selection once it's ready
        private IPlaceable _waitingFor;

        private Drag _drag = Drag.None;
        private int _dragAxis;
        private Plane _dragPlane;
        private Vector3 _dragStartPoint;
        private readonly List<(IPlaceable placeable, Vector3 position, Quaternion rotation)> _dragStart =
            new List<(IPlaceable, Vector3, Quaternion)>();

        // A ring drag: what it turns about, and how far the mouse has turned it
        private Vector3 _ringCentre;
        private Vector3 _ringLast;
        private float _ringAngle;
        private bool _fine;

        // What a body drag does right now, and, while turning or tilting, where that started and about what
        private enum BodyAction
        {
            Move,
            Turn,
            Lean,
            Tip
        }

        private BodyAction _bodyAction;
        private Vector2 _spinStartMouse;
        private Vector3 _spinCentre;
        private Vector3 _turnAxis;
        private Vector3 _tipAxis;
        private Vector3 _leanAxis;

        // A box drag: where it started (screen), whether it adds to the selection, and where the mouse is now
        private Vector2 _boxStart;
        private Vector2 _boxEnd;
        private bool _boxAdds;

        public HandleMode Mode { get; private set; } = HandleMode.Move;

        /// <summary>Whether rings turn in steps (on, as the VAB starts) or freely.</summary>
        public bool AngleSnap { get; set; } = true;

        /// <summary>Whether any drag (a double, a handle or a selection box) is under way, so the mouse is taken.</summary>
        public bool IsDragging => _drag != Drag.None;

        /// <summary>Whether selected things are being dragged by the body, so turning and tilting them are on offer.</summary>
        public bool IsDraggingDoubles => _drag == Drag.Body;

        /// <summary>What's selected for moving, doubles and lights: outlined, and moved and turned together by the handles.</summary>
        public IReadOnlyList<IPlaceable> Moving => _moving;

        /// <summary>
        /// The box being dragged to select doubles, in screen coordinates (y up), while one is; null otherwise. Only
        /// counts once the mouse has moved far enough for it not to be a click.
        /// </summary>
        public Rect? SelectionBox => _drag == Drag.Box && (_boxEnd - _boxStart).magnitude >= BoxThreshold
            ? Rect.MinMaxRect(Mathf.Min(_boxStart.x, _boxEnd.x), Mathf.Min(_boxStart.y, _boxEnd.y),
                              Mathf.Max(_boxStart.x, _boxEnd.x), Mathf.Max(_boxStart.y, _boxEnd.y))
            : (Rect?)null;

        /// <summary>
        /// Raised when a click selects a different double for posing, or a light for setting, for the UI to follow.
        /// </summary>
        public event Action<IPlaceable> Selected;

        /// <param name="lens">
        /// What the picture goes through on its way to the screen (the fisheye): the mouse is aimed through it, and
        /// what a selection box takes is judged where it shows; while it bends, outlines are left off, as the game
        /// draws them after it, unbent, beside what they outline.
        /// </param>
        public PlaceableManipulator(PhotoSession session, PhotoLights lights, IScreenLens lens, Action<string> logInfo,
                                    Action<string> logError)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _lights = lights ?? throw new ArgumentNullException(nameof(lights));
            _lens = lens ?? throw new ArgumentNullException(nameof(lens));
            _logInfo = logInfo ?? throw new ArgumentNullException(nameof(logInfo));
            _logError = logError ?? throw new ArgumentNullException(nameof(logError));
        }

        /// <summary>Makes the posed double the whole moving selection, e.g. when it's picked again in the UI.</summary>
        public void ShowHandles() => SelectOnly(_session.Selected);

        /// <summary>Makes <paramref name="placeable"/> the whole moving selection, e.g. a light picked in the UI.</summary>
        public void Select(IPlaceable placeable) => SelectOnly(placeable);

        public void SetMode(HandleMode mode)
        {
            Mode = mode;
            _handles?.SetMode(mode);
        }

        /// <summary>
        /// Called every frame with the mouse: <paramref name="pressed"/>/<paramref name="released"/> on the frames the
        /// left button goes down/up, <paramref name="held"/> while it's down. Presses over the UI are ignored.
        /// <paramref name="adding"/>: the add-to-selection modifier (Shift) is held. <paramref name="fine"/>: the fine
        /// modifier is held, so rings snap in small steps. <paramref name="turn"/>: the right button is held too, which
        /// turns doubles being dragged instead of moving them; <paramref name="tilt"/>: the tilt key is held, which leans
        /// them sideways, or tips them forward and back with <paramref name="tip"/> (Alt) held too. Turning wins if
        /// both are held.
        /// </summary>
        public void Tick(Camera camera, Vector2 mouse, bool pressed, bool held, bool released, bool overUi, bool adding, bool fine,
                         bool turn, bool tilt, bool tip)
        {
            _fine = fine;
            EnsureHandles();
            PruneMoving();
            PhotoSession.Entry selected = _session.Selected;
            if (selected != _lastSelected)
            {
                _lastSelected = selected;
                SelectOnly(selected);
            }
            if (_waitingFor != null && _waitingFor.IsPlaceable)
            {
                IPlaceable ready = _waitingFor;
                SelectOnly(ready);
            }

            bool showHandles = camera != null && _primary != null;
            _handles?.SetVisible(showHandles);
            if (showHandles) _handles?.Follow(Centre(), _primary.Root.rotation, camera);
            if (camera == null) return;

            // Aimed at what's under the mouse as it's seen: through the fisheye, when there is one
            Ray ray = camera.ScreenPointToRay(_lens.ToScene(mouse));
            if (pressed && !overUi) BeginDrag(ray, mouse, camera, adding);
            else if (held && _drag != Drag.None)
                ContinueDrag(ray, mouse, camera,
                             turn ? BodyAction.Turn : tilt ? (tip ? BodyAction.Tip : BodyAction.Lean) : BodyAction.Move);
            if (released || !held) EndDrag(camera);

            // Hovered like a part in the VAB; the moving selection stays lit. While a box is dragged, what it would
            // select is lit instead, so it shows before letting go; during other drags nothing new lights up
            IPlaceable hovered = _drag == Drag.None && !overUi ? Pick(ray, out _) : null;
            Rect? box = SelectionBox;
            _hover.Show(box.HasValue ? InBox(box.Value, camera, _boxAdds) : _moving, hovered, camera, outlined: !_lens.Bends);
        }

        /// <summary>
        /// Keeps the handles and outlines out of sight (call every frame instead of <see cref="Tick"/> while a photo is
        /// taken or photo mode's UI is hidden), and drops any drag. The next Tick shows them again as they were.
        /// </summary>
        public void HideAll()
        {
            _handles?.SetVisible(false);
            _hover.Clear();
            _drag = Drag.None;
            _bodyAction = BodyAction.Move;
        }

        /// <summary>Lets go of everything (handles, outlines, selection) when photo mode closes; it's used again next time.</summary>
        public void Reset()
        {
            _generation++;
            _hover.Clear();
            _handles?.Dispose();
            _handles = null;
            _handlesRequested = false;
            _drag = Drag.None;
            _bodyAction = BodyAction.Move;
            _moving.Clear();
            _primary = _waitingFor = null;
            _lastSelected = null;
        }

        // ---- Selection ----

        private void SelectOnly(IPlaceable placeable)
        {
            _moving.Clear();
            _primary = null;
            _waitingFor = null;
            if (placeable == null || !IsInShot(placeable)) return;
            if (placeable.IsPlaceable)
            {
                _moving.Add(placeable);
                _primary = placeable;
            }
            else
            {
                _waitingFor = placeable;
            }
        }

        private void Toggle(IPlaceable placeable)
        {
            if (_moving.Remove(placeable))
            {
                if (_primary == placeable) _primary = _moving.Count > 0 ? _moving[_moving.Count - 1] : null;
                return;
            }
            _moving.Add(placeable);
            _primary = placeable;
        }

        // Doubles taken out or not built yet, and lights taken away, can't be moved
        private void PruneMoving()
        {
            _moving.RemoveAll(p => p == null || !p.IsPlaceable || !IsInShot(p));
            if (_waitingFor != null && !IsInShot(_waitingFor)) _waitingFor = null;
            if (_primary != null && !_moving.Contains(_primary)) _primary = _moving.Count > 0 ? _moving[_moving.Count - 1] : null;
        }

        // Whether it's still in the shot: a double still in the session, or a light still there
        private bool IsInShot(IPlaceable placeable) => placeable switch
        {
            PhotoSession.Entry entry => Contains(_session.Entries, entry),
            PhotoLight light => Contains(_lights.All, light),
            _ => false
        };

        // What's under the mouse: the nearest double or light marker along the ray
        private IPlaceable Pick(Ray ray, out Vector3 point)
        {
            PhotoSession.Entry entry = _session.PickDouble(ray, out Vector3 onDouble);
            PhotoLight light = _lights.Pick(ray, out Vector3 onLight);
            bool lightNearer = light != null
                               && (entry == null || (onLight - ray.origin).sqrMagnitude < (onDouble - ray.origin).sqrMagnitude);
            point = lightNearer ? onLight : onDouble;
            return lightNearer ? light : (IPlaceable)entry;
        }

        // ---- Dragging ----

        private void BeginDrag(Ray ray, Vector2 mouse, Camera camera, bool adding)
        {
            // A shown handle takes the click before any double does
            if (_primary != null && _handles != null && _handles.TryPick(ray, out HandleHit hit))
            {
                _dragAxis = hit.Axis;
                Vector3 axis = AxisOf(_primary, hit.Axis);
                Vector3 centre = Centre();
                RememberStart();
                if (hit.Kind == HandleMode.Rotate)
                {
                    _drag = Drag.Ring;
                    _dragPlane = new Plane(axis, centre);
                    _ringCentre = centre;
                    _ringAngle = 0f;
                    _ringLast = _dragPlane.Raycast(ray, out float enter) ? ray.GetPoint(enter) - centre : Vector3.zero;
                }
                else
                {
                    _drag = Drag.Arrow;
                    _dragStartPoint = ClosestOnAxis(ray, centre, axis);
                }
                return;
            }

            IPlaceable clicked = Pick(ray, out Vector3 point);
            if (clicked == null)
            {
                // Empty space: a click clears the moving selection (as clicking away from a part does in the VAB), a
                // drag draws a selection box; which one is decided on release
                _drag = Drag.Box;
                _boxStart = _boxEnd = mouse;
                _boxAdds = adding;
                return;
            }

            if (adding)
            {
                Toggle(clicked);
            }
            else
            {
                // Clicking one of several selected keeps them all, to drag them together
                if (!_moving.Contains(clicked)) SelectOnly(clicked);
                else _primary = clicked;
                // The posed double, or the light the Light tab sets, follows the click
                if (clicked is PhotoSession.Entry entry && entry != _session.Selected)
                {
                    _session.Select(entry);
                    _lastSelected = entry;
                    Selected?.Invoke(entry);
                }
                else if (clicked is PhotoLight light && light != _lights.Selected)
                {
                    _lights.Select(light);
                    Selected?.Invoke(light);
                }
            }

            if (_moving.Contains(clicked))
            {
                // The plane facing the camera through where it was grabbed: the mouse moves them as they're seen,
                // never nearer or further (with the move tool; either tool turns them with both buttons)
                _drag = Drag.Body;
                _bodyAction = BodyAction.Move;
                _dragPlane = new Plane(-camera.transform.forward, point);
                _dragStartPoint = point;
                RememberStart();
            }
        }

        private void ContinueDrag(Ray ray, Vector2 mouse, Camera camera, BodyAction bodyAction)
        {
            switch (_drag)
            {
                case Drag.Box:
                    _boxEnd = mouse;
                    break;

                case Drag.Body:
                    DragBody(ray, mouse, camera, bodyAction);
                    break;

                case Drag.Arrow:
                {
                    if (_primary == null) break;
                    Vector3 axis = AxisOf(_primary, _dragAxis);
                    Vector3 along = ClosestOnAxis(ray, _dragStartPoint, axis);
                    MoveAll(Vector3.Project(along - _dragStartPoint, axis));
                    break;
                }

                case Drag.Ring:
                {
                    // The mouse's turn is added up (so it can go past half a turn), then applied from the starting
                    // rotations, snapped to whole steps
                    Vector3 axis = _dragPlane.normal;
                    if (!_dragPlane.Raycast(ray, out float hitAt)) break;
                    Vector3 now = ray.GetPoint(hitAt) - _ringCentre;
                    if (_ringLast.sqrMagnitude > 1e-8f && now.sqrMagnitude > 1e-8f)
                        _ringAngle += Vector3.SignedAngle(_ringLast, now, axis);
                    _ringLast = now;

                    float step = _fine ? FineSnapStep : SnapStep;
                    float angle = AngleSnap ? Mathf.Round(_ringAngle / step) * step : _ringAngle;
                    Quaternion turn = Quaternion.AngleAxis(angle, axis);
                    foreach ((IPlaceable placeable, Vector3 position, Quaternion rotation) in _dragStart)
                        if (placeable.Root != null)
                            placeable.Root.SetPositionAndRotation(_ringCentre + turn * (position - _ringCentre), turn * rotation);
                    break;
                }
            }
        }

        // Moves the dragged doubles with the mouse, turns them while the right button is held too, or leans or tips them
        // while the tilt key is. Each switch starts from where they are, so nothing jumps
        private void DragBody(Ray ray, Vector2 mouse, Camera camera, BodyAction action)
        {
            if (action != _bodyAction)
            {
                _bodyAction = action;
                RememberStart();
                if (action == BodyAction.Move)
                {
                    if (_dragPlane.Raycast(ray, out float at)) _dragStartPoint = ray.GetPoint(at);
                }
                else
                {
                    // About the camera's own axes, as in space, where there's no up: turning about the screen's
                    // vertical, tipping about its sideways, leaning about its depth. So the mouse moves the kerbal
                    // the way it moves across the screen, however the camera is turned
                    _spinStartMouse = mouse;
                    _spinCentre = Centre();
                    _turnAxis = camera.transform.up;
                    _tipAxis = camera.transform.right;
                    _leanAxis = camera.transform.forward;
                }
            }

            Vector2 moved = (mouse - _spinStartMouse) * SpinDegreesPerPixel;
            switch (_bodyAction)
            {
                case BodyAction.Turn:
                    // Mouse right turns the side facing the camera to the right
                    SpinAll(Quaternion.AngleAxis(-Snap(moved.x, _fine), _turnAxis));
                    break;
                case BodyAction.Lean:
                    // Mouse right leans the top to the right
                    SpinAll(Quaternion.AngleAxis(-Snap(moved.x, _fine), _leanAxis));
                    break;
                case BodyAction.Tip:
                    // Mouse up tips the top away from the camera
                    SpinAll(Quaternion.AngleAxis(Snap(moved.y, _fine), _tipAxis));
                    break;
                default:
                    if (Mode == HandleMode.Move && _dragPlane.Raycast(ray, out float enter))
                        MoveAll(ray.GetPoint(enter) - _dragStartPoint);
                    break;
            }
        }

        // Angle snap applies to turning and tilting as on the rings: whole steps, finer with the fine modifier
        private float Snap(float degrees, bool fine)
        {
            if (!AngleSnap) return degrees;
            float step = fine ? FineSnapStep : SnapStep;
            return Mathf.Round(degrees / step) * step;
        }

        // Turns the dragged ones from where the turn or tilt started, around their middle
        private void SpinAll(Quaternion spin)
        {
            foreach ((IPlaceable placeable, Vector3 position, Quaternion rotation) in _dragStart)
                if (placeable.Root != null)
                    placeable.Root.SetPositionAndRotation(_spinCentre + spin * (position - _spinCentre), spin * rotation);
        }

        private void EndDrag(Camera camera)
        {
            if (_drag == Drag.Box)
            {
                Rect? box = SelectionBox;
                if (box.HasValue) SelectInBox(box.Value, camera);
                else if (!_boxAdds) SelectOnly(null);
            }
            _drag = Drag.None;
            _bodyAction = BodyAction.Move;
            _dragStart.Clear();
        }

        // Every built double and every light whose middle is inside the box (and in front of the camera) is selected
        // for moving
        private void SelectInBox(Rect box, Camera camera)
        {
            List<IPlaceable> selected = InBox(box, camera, _boxAdds);
            _moving.Clear();
            _moving.AddRange(selected);
            _primary = selected.Count > 0 ? selected[selected.Count - 1] : null;
            _waitingFor = null;
        }

        // What a box selects: the doubles and lights whose middle is inside it (and in front of the camera), after the
        // current selection when adding to it. Kept in a list reused each frame while the box is dragged
        private readonly List<IPlaceable> _inBox = new List<IPlaceable>();

        private List<IPlaceable> InBox(Rect box, Camera camera, bool adding)
        {
            _inBox.Clear();
            if (adding) _inBox.AddRange(_moving);
            foreach (PhotoSession.Entry entry in _session.Entries) AddIfInBox(entry, box, camera);
            foreach (PhotoLight light in _lights.All) AddIfInBox(light, box, camera);
            return _inBox;
        }

        private void AddIfInBox(IPlaceable placeable, Rect box, Camera camera)
        {
            if (!placeable.IsPlaceable || _inBox.Contains(placeable)) return;
            Vector3 screen = camera.WorldToScreenPoint(placeable.Centre);
            if (screen.z > 0f && box.Contains(_lens.ToScreen(new Vector2(screen.x, screen.y)))) _inBox.Add(placeable);
        }

        private void RememberStart()
        {
            _dragStart.Clear();
            foreach (IPlaceable placeable in _moving)
                _dragStart.Add((placeable, placeable.Root.position, placeable.Root.rotation));
        }

        private void MoveAll(Vector3 offset)
        {
            foreach ((IPlaceable placeable, Vector3 position, Quaternion _) in _dragStart)
                if (placeable.Root != null)
                    placeable.Root.position = position + offset;
        }

        // The handles are made once there's something to move: a built double, or a light
        private void EnsureHandles()
        {
            if (_handles != null || _handlesRequested) return;
            IPlaceable anyReady = null;
            foreach (PhotoSession.Entry entry in _session.Entries)
                if (entry.IsPlaceable) { anyReady = entry; break; }
            if (anyReady == null && _lights.All.Count > 0) anyReady = _lights.All[0];
            if (anyReady == null) return;

            _handlesRequested = true;
            int generation = _generation;
            ManipulationHandles.Create(anyReady.Root.gameObject.layer, _logInfo, _logError, handles =>
            {
                if (generation != _generation || _handles != null)
                {
                    handles.Dispose();
                    return;
                }
                _handles = handles;
                _handles.SetMode(Mode);
                _session.SetIgnoredInSurfaceSearch(_handles.Root);
            });
        }

        // The middle of the moving selection: where the handles sit and what rings turn it about
        private Vector3 Centre()
        {
            if (_moving.Count == 1) return _moving[0].Centre;
            Vector3 sum = Vector3.zero;
            foreach (IPlaceable placeable in _moving) sum += placeable.Centre;
            return _moving.Count > 0 ? sum / _moving.Count : Vector3.zero;
        }

        private static Vector3 AxisOf(IPlaceable placeable, int axis) =>
            axis == 0 ? placeable.Root.right : axis == 1 ? placeable.Root.up : placeable.Root.forward;

        private static bool Contains<T>(IReadOnlyList<T> items, T item) where T : class
        {
            for (int i = 0; i < items.Count; i++)
                if (items[i] == item) return true;
            return false;
        }

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
