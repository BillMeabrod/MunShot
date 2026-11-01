using System.Collections.Generic;
using UnityEngine;

namespace MunShot.Posing
{
    /// <summary>
    /// A collection of photo mode's own things the handles move besides the doubles (its lights, its effects): what
    /// <see cref="PlaceableManipulator"/> needs to pick, box-select and keep track of them, and to tell the collection
    /// which one was clicked, for its tab to set.
    /// </summary>
    public interface IPlaceableSet
    {
        /// <summary>Every one there is now.</summary>
        IEnumerable<IPlaceable> Placeables { get; }

        /// <summary>Whether <paramref name="placeable"/> is one of these, and still there.</summary>
        bool Holds(IPlaceable placeable);

        /// <summary>The one whose marker <paramref name="ray"/> points at, nearest first; null if none.</summary>
        IPlaceable PickPlaceable(Ray ray, out Vector3 point);

        /// <summary>Selects one of these for its tab; false if it was already selected (or isn't one of these).</summary>
        bool SelectPlaceable(IPlaceable placeable);
    }
}
