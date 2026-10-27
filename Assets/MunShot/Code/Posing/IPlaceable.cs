using UnityEngine;

namespace MunShot.Posing
{
    /// <summary>
    /// Something in the shot the handles move and turn (<see cref="PlaceableManipulator"/>): a photo double or one of
    /// photo mode's lights. Hovered and selected ones are outlined, as parts are in the VAB.
    /// </summary>
    public interface IPlaceable
    {
        /// <summary>Where it is and how it's turned; null once it's gone.</summary>
        Transform Root { get; }

        /// <summary>Whether it can be moved now: there, and built (a double still being built can't be).</summary>
        bool IsPlaceable { get; }

        /// <summary>Its middle: where the handles sit, and what it turns about.</summary>
        Vector3 Centre { get; }

        /// <summary>Whether it has a way it faces to turn (a point light shines all round, so it doesn't).</summary>
        bool CanTurn { get; }
    }
}
