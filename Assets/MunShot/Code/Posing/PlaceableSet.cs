using System;
using System.Collections.Generic;
using UnityEngine;

namespace MunShot.Posing
{
    /// <summary>
    /// What photo mode's lists of its own things (its lights, its effects) share: up to a set number of them, one
    /// selected for its tab to set, markers shown or hidden together, picked by their markers, and all taken away when
    /// photo mode closes. The handles reach them through <see cref="IPlaceableSet"/>.
    /// </summary>
    public abstract class PlaceableSet<T> : IPlaceableSet where T : class, IMarkedPlaceable
    {
        private readonly int _max;
        private readonly List<T> _items = new List<T>();
        private bool _markersShown = true;

        protected readonly Action<string> LogInfo;

        public IReadOnlyList<T> All => _items;
        public T Selected { get; private set; }
        public bool CanAdd => _items.Count < _max;

        /// <param name="max">How many there can be.</param>
        protected PlaceableSet(int max, Action<string> logInfo)
        {
            _max = max;
            LogInfo = logInfo ?? throw new ArgumentNullException(nameof(logInfo));
        }

        public void Remove(T item)
        {
            if (item == null || !_items.Remove(item)) return;
            item.Destroy();
            if (Selected == item) Selected = _items.Count > 0 ? _items[_items.Count - 1] : null;
            LogInfo($"Photo mode: {Describe(item)} removed.");
        }

        public void Select(T item)
        {
            if (item != null && _items.Contains(item)) Selected = item;
        }

        /// <summary>The one whose marker <paramref name="ray"/> points at, nearest first; null if none.</summary>
        public T Pick(Ray ray, out Vector3 point)
        {
            point = default;
            T nearest = null;
            float nearestDistance = float.MaxValue;
            foreach (T item in _items)
            {
                if (!item.TryPick(ray, out Vector3 at)) continue;
                float distance = (at - ray.origin).sqrMagnitude;
                if (distance >= nearestDistance) continue;
                nearestDistance = distance;
                nearest = item;
                point = at;
            }
            return nearest;
        }

        /// <summary>Shows or hides every marker (hidden for the photo and while the UI is hidden).</summary>
        public void ShowMarkers(bool shown)
        {
            if (shown == _markersShown) return;
            _markersShown = shown;
            foreach (T item in _items) item.ShowMarker(shown);
        }

        /// <summary>Takes every one away.</summary>
        public virtual void Clear()
        {
            if (_items.Count > 0) LogInfo($"Photo mode: {_items.Count} {PluralName} removed.");
            foreach (T item in _items) item.Destroy();
            _items.Clear();
            Selected = null;
            _markersShown = true;
        }

        /// <summary>How a removed one is named in the log, e.g. "light 2".</summary>
        protected abstract string Describe(T item);

        /// <summary>How several are named in the log, e.g. "light(s)".</summary>
        protected abstract string PluralName { get; }

        /// <summary>Takes in a new one, with its marker shown as the others' are, and selects it.</summary>
        protected void Added(T item)
        {
            item.ShowMarker(_markersShown);
            _items.Add(item);
            Selected = item;
        }

        /// <summary>The lowest number not in use among those <paramref name="sameKind"/> picks, so "Light 1" comes back after it's removed.</summary>
        protected int FreeNumber(Func<T, bool> sameKind)
        {
            int number = 1;
            while (_items.Exists(item => sameKind(item) && item.Number == number)) number++;
            return number;
        }

        // ---- For the handles (IPlaceableSet) ----

        IEnumerable<IPlaceable> IPlaceableSet.Placeables => _items;

        bool IPlaceableSet.Holds(IPlaceable placeable) => placeable is T item && _items.Contains(item);

        IPlaceable IPlaceableSet.PickPlaceable(Ray ray, out Vector3 point) => Pick(ray, out point);

        bool IPlaceableSet.SelectPlaceable(IPlaceable placeable)
        {
            if (!(placeable is T item) || item == Selected || !_items.Contains(item)) return false;
            Selected = item;
            return true;
        }
    }
}
