using UnityEngine;

namespace MunShot.Posing
{
    /// <summary>
    /// One of photo mode's own placeable things with a marker to click and move it by (a light, an effect), kept in a
    /// <see cref="PlaceableSet{T}"/>.
    /// </summary>
    public interface IMarkedPlaceable : IPlaceable
    {
        /// <summary>Which one of its kind this is, shown in its name: kept while it's there, so names don't shift.</summary>
        int Number { get; }

        void ShowMarker(bool shown);

        /// <summary>Whether <paramref name="ray"/> points at the marker, and where.</summary>
        bool TryPick(Ray ray, out Vector3 point);

        void Destroy();
    }
}
