using System;
using MunShot.Posing;
using UnityEngine;

namespace MunShot.Lighting
{
    /// <summary>
    /// Photo mode's own lights (<see cref="PhotoLight"/>): added a little in front of the camera, shining where it
    /// looks, moved with the handles, and all taken away when photo mode closes. One is selected, for the Light tab to
    /// set.
    /// They live under the object vessels live under, which the game moves when the floating origin shifts, so they
    /// stay where they were put.
    /// </summary>
    public sealed class PhotoLights : PlaceableSet<PhotoLight>
    {
        /// <summary>
        /// How many there can be: enough to light a shot (key, fill, rim and one more), few enough that the list and the
        /// scene stay easy to follow.
        /// </summary>
        public const int MaxLights = 4;

        // How far in front of the camera a new light appears: far enough for its marker to be seen and clicked
        private const float DistanceInFront = 2f;

        private readonly Action<string> _logWarning;
        private readonly Func<int> _kerbalLayer;

        /// <param name="kerbalLayer">The layer photo doubles are drawn on, which lights for kerbals only reach.</param>
        public PhotoLights(Func<int> kerbalLayer, Action<string> logInfo, Action<string> logWarning) : base(MaxLights, logInfo)
        {
            _kerbalLayer = kerbalLayer ?? throw new ArgumentNullException(nameof(kerbalLayer));
            _logWarning = logWarning ?? throw new ArgumentNullException(nameof(logWarning));
        }

        /// <summary>
        /// Adds a light a little in front of <paramref name="camera"/>, facing where it looks, under
        /// <paramref name="parent"/> (the scene's root when null), and selects it.
        /// </summary>
        /// <returns>The new light, or null when there are already <see cref="MaxLights"/> or there's no marker shader.</returns>
        public PhotoLight Add(Camera camera, Transform parent)
        {
            if (!CanAdd || camera == null) return null;
            Shader shader = PlaceableMarker.FindShader(_logWarning);
            if (shader == null) return null;
            if (parent == null) _logWarning("Photo mode: no vessel to place lights beside; the light goes under the scene's root, so a floating origin shift would move it.");

            Transform view = camera.transform;
            var light = new PhotoLight(FreeNumber(_ => true), parent, view.position + view.forward * DistanceInFront, view.rotation,
                                       shader, _kerbalLayer);
            Added(light);
            LogInfo($"Photo mode: light {light.Number} added {DistanceInFront}m in front of the camera, under '{(parent != null ? parent.name : "<scene root>")}'.");
            return light;
        }

        protected override string Describe(PhotoLight light) => $"light {light.Number}";

        protected override string PluralName => "light(s)";
    }
}
