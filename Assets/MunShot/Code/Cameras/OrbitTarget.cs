using System;
using UnityEngine;

namespace MunShot.Cameras
{
    /// <summary>
    /// Something the photo camera can circle: a name for the UI and where its middle is now, in physics space. Asked
    /// every frame, so a target that moves (or a double being dragged) is followed. A target that's gone (destroyed,
    /// unloaded) says so, and the camera drops back to free flying.
    /// </summary>
    public sealed class OrbitTarget
    {
        private readonly Func<Vector3?> _centre;

        public string Name { get; }

        /// <param name="name">What the UI calls it, e.g. the kerbal's or the vessel's name.</param>
        /// <param name="centre">Where its middle is in physics space, or null once it's gone.</param>
        public OrbitTarget(string name, Func<Vector3?> centre)
        {
            Name = string.IsNullOrEmpty(name) ? "<unnamed>" : name;
            _centre = centre ?? throw new ArgumentNullException(nameof(centre));
        }

        public bool TryGetCentre(out Vector3 centre)
        {
            Vector3? now = _centre();
            centre = now ?? default;
            return now.HasValue;
        }

        public override string ToString() => Name;
    }
}
