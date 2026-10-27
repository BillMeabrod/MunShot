using System;
using System.Collections.Generic;
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
    public sealed class PhotoLights
    {
        /// <summary>
        /// How many there can be: enough to light a shot (key, fill, rim and one more), few enough that the list and the
        /// scene stay easy to follow.
        /// </summary>
        public const int MaxLights = 4;

        // How far in front of the camera a new light appears: far enough for its marker to be seen and clicked
        private const float DistanceInFront = 2f;

        private readonly Action<string> _logInfo;
        private readonly Action<string> _logWarning;
        private readonly Func<int> _kerbalLayer;
        private readonly List<PhotoLight> _lights = new List<PhotoLight>();
        private Shader _markerShader;
        private bool _markersShown = true;

        public IReadOnlyList<PhotoLight> All => _lights;
        public PhotoLight Selected { get; private set; }
        public bool CanAdd => _lights.Count < MaxLights;

        /// <param name="kerbalLayer">The layer photo doubles are drawn on, which lights for kerbals only reach.</param>
        public PhotoLights(Func<int> kerbalLayer, Action<string> logInfo, Action<string> logWarning)
        {
            _kerbalLayer = kerbalLayer ?? throw new ArgumentNullException(nameof(kerbalLayer));
            _logInfo = logInfo ?? throw new ArgumentNullException(nameof(logInfo));
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
            Shader shader = MarkerShader();
            if (shader == null) return null;
            if (parent == null) _logWarning("Photo mode: no vessel to place lights beside; the light goes under the scene's root, so a floating origin shift would move it.");

            Transform view = camera.transform;
            var light = new PhotoLight(FreeNumber(), parent, view.position + view.forward * DistanceInFront, view.rotation, shader,
                                       _kerbalLayer);
            light.ShowMarker(_markersShown);
            _lights.Add(light);
            Selected = light;
            _logInfo($"Photo mode: light {light.Number} added {DistanceInFront}m in front of the camera, under '{(parent != null ? parent.name : "<scene root>")}'.");
            return light;
        }

        public void Remove(PhotoLight light)
        {
            if (light == null || !_lights.Remove(light)) return;
            light.Destroy();
            if (Selected == light) Selected = _lights.Count > 0 ? _lights[_lights.Count - 1] : null;
            _logInfo($"Photo mode: light {light.Number} removed.");
        }

        public void Select(PhotoLight light)
        {
            if (light != null && _lights.Contains(light)) Selected = light;
        }

        /// <summary>The light whose marker <paramref name="ray"/> points at, nearest first; null if none.</summary>
        public PhotoLight Pick(Ray ray, out Vector3 point)
        {
            point = default;
            PhotoLight nearest = null;
            float nearestDistance = float.MaxValue;
            foreach (PhotoLight light in _lights)
            {
                if (!light.TryPick(ray, out Vector3 at)) continue;
                float distance = (at - ray.origin).sqrMagnitude;
                if (distance >= nearestDistance) continue;
                nearestDistance = distance;
                nearest = light;
                point = at;
            }
            return nearest;
        }

        /// <summary>Shows or hides every light's marker (hidden for the photo and while the UI is hidden).</summary>
        public void ShowMarkers(bool shown)
        {
            if (shown == _markersShown) return;
            _markersShown = shown;
            foreach (PhotoLight light in _lights) light.ShowMarker(shown);
        }

        /// <summary>Takes every light away.</summary>
        public void Clear()
        {
            if (_lights.Count > 0) _logInfo($"Photo mode: {_lights.Count} light(s) removed.");
            foreach (PhotoLight light in _lights) light.Destroy();
            _lights.Clear();
            Selected = null;
            _markersShown = true;
        }

        // The lowest number not in use, so "Light 1" comes back after it's removed
        private int FreeNumber()
        {
            int number = 1;
            while (_lights.Exists(light => light.Number == number)) number++;
            return number;
        }

        // An unlit shader for the markers, as the fallback handles use; the first found is kept
        private Shader MarkerShader()
        {
            if (_markerShader != null) return _markerShader;
            foreach (string name in new[] { "Hidden/Internal-Colored", "Sprites/Default", "Unlit/Color" })
                if ((_markerShader = Shader.Find(name)) != null) return _markerShader;
            _logWarning("Photo mode: can't add a light; none of the unlit shaders for its marker is in the game.");
            return null;
        }
    }
}
