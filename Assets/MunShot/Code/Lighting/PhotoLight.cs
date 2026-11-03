using System;
using MunShot.Posing;
using Redux.Rendering;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MunShot.Lighting
{
    /// <summary>What shape a <see cref="PhotoLight"/> shines in.</summary>
    public enum PhotoLightKind
    {
        /// <summary>All around, like a lamp.</summary>
        Point,

        /// <summary>A cone the way it faces, like a torch.</summary>
        Spot
    }

    /// <summary>
    /// One of photo mode's own lights: a Unity light placed in the scene, with a small marker showing where it is and
    /// its colour (and, for a spot light, which way it points). The marker is only for placing it: clicked to select
    /// it, outlined and moved with the handles as a photo double is (<see cref="IPlaceable"/>); photo mode hides it for
    /// the photo and when the UI is hidden.
    ///
    /// It lights only kerbals, or everything but the handles (<see cref="HandlesCamera.Layer"/>). Redux's lighting picks which objects a light reaches by URP rendering
    /// layers, which it works out from a light's Unity culling mask (<see cref="LegacyLightCullingMask"/>), as for the
    /// game's own lights, so this sets only the culling mask.
    /// </summary>
    public sealed class PhotoLight : IMarkedPlaceable
    {
        public const float MinBrightness = 0.1f;
        // Brighter than this, the game's bloom spreads a light's glow over the whole picture like fog
        public const float MaxBrightness = 8f;
        public const float MinRange = 1f;
        public const float MaxRange = 200f;
        public const float MinSpotAngle = 5f;
        public const float MaxSpotAngle = 150f;

        private readonly GameObject _root;
        private readonly Light _light;
        private readonly PlaceableMarker _marker;
        private readonly GameObject _pointer;
        private readonly Func<int> _kerbalLayer;

        private PhotoLightKind _kind = PhotoLightKind.Point;
        private Color _color = Color.white;
        private float _brightness = 2f;
        private float _range = 10f;
        private float _spotAngle = 45f;
        private bool _kerbalsOnly;
        private bool _gone;

        /// <summary>Which light this is, shown as "Light 1": kept while it's there, so names don't shift.</summary>
        public int Number { get; }

        /// <summary>Where it is and which way it shines; null once it's taken away.</summary>
        public Transform Root => _gone ? null : _root.transform;

        public bool IsPlaceable => !_gone;

        public Vector3 Centre => _root.transform.position;

        public bool CanTurn => _kind == PhotoLightKind.Spot;

        public PhotoLightKind Kind
        {
            get => _kind;
            set { _kind = value; Apply(); }
        }

        public Color Color
        {
            get => _color;
            set { _color = new Color(value.r, value.g, value.b, 1f); Apply(); }
        }

        public float Brightness
        {
            get => _brightness;
            set { _brightness = Mathf.Clamp(value, MinBrightness, MaxBrightness); Apply(); }
        }

        /// <summary>How far it reaches, in metres.</summary>
        public float Range
        {
            get => _range;
            set { _range = Mathf.Clamp(value, MinRange, MaxRange); Apply(); }
        }

        /// <summary>How wide a spot light's cone is, in degrees.</summary>
        public float SpotAngle
        {
            get => _spotAngle;
            set { _spotAngle = Mathf.Clamp(value, MinSpotAngle, MaxSpotAngle); Apply(); }
        }

        /// <summary>Whether it lights only kerbals (the photo kerbals' layer), or everything.</summary>
        public bool KerbalsOnly
        {
            get => _kerbalsOnly;
            set { _kerbalsOnly = value; Apply(); }
        }

        /// <param name="markerShader">An unlit shader for the marker.</param>
        /// <param name="kerbalLayer">The layer photo doubles are drawn on, which the light reaches for kerbals only.</param>
        public PhotoLight(int number, Transform parent, Vector3 position, Quaternion rotation, Shader markerShader,
                          Func<int> kerbalLayer)
        {
            Number = number;
            _kerbalLayer = kerbalLayer ?? throw new ArgumentNullException(nameof(kerbalLayer));
            _root = new GameObject($"MunShot light {number}");
            _root.transform.SetParent(parent, false);
            _root.transform.SetPositionAndRotation(position, rotation);

            _light = _root.AddComponent<Light>();
            _light.shadows = LightShadows.None;

            _marker = new PlaceableMarker(_root.transform, $"MunShot light {number}", markerShader, _color);
            // A spot light's marker has a short stick pointing the way it shines
            const float size = PlaceableMarker.Size;
            _pointer = _marker.AddPart(PrimitiveType.Cylinder, new Vector3(0f, 0f, size), new Vector3(0.03f, size * 0.5f, 0.03f));
            _pointer.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            Apply();
        }

        public void ShowMarker(bool shown)
        {
            _marker.Shown = shown;
            _pointer.SetActive(shown && _kind == PhotoLightKind.Spot);
        }

        /// <summary>
        /// Whether <paramref name="ray"/> passes near enough the marker to pick it, and where it does (nearest the
        /// marker along the ray).
        /// </summary>
        public bool TryPick(Ray ray, out Vector3 point)
        {
            point = default;
            return !_gone && _marker.TryPick(ray, out point);
        }

        public void Destroy()
        {
            _gone = true;
            Object.Destroy(_root);
            _marker.Destroy();
        }

        private void Apply()
        {
            _light.type = _kind == PhotoLightKind.Spot ? LightType.Spot : LightType.Point;
            _light.color = _color;
            _light.intensity = _brightness;
            _light.range = _range;
            _light.spotAngle = _spotAngle;
            _light.innerSpotAngle = _spotAngle * 0.6f;
            // Redux turns this into the light's rendering layers each frame it's seen (setting those here too would make
            // it skip that, and leave URP's own copy of them unset)
            // Never the handles, which are for placing, not part of the scene
            _light.cullingMask = _kerbalsOnly ? 1 << _kerbalLayer() : ~(1 << HandlesCamera.Layer);

            _marker.Color = _color;
            _pointer.SetActive(_marker.Shown && _kind == PhotoLightKind.Spot);
        }
    }
}
