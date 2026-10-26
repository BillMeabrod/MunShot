using System;
using Redux.Rendering.PostProcessing;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace MunShot.Cameras
{
    /// <summary>
    /// Photo mode's own look, laid over the game's while photo mode is open: depth of field, so far.
    ///
    /// Redux renders the game's post-processing from URP volumes (it mirrors the game's own effects into them every
    /// frame, see Redux's PostProcessingBridge) and draws depth of field with its own effect,
    /// <see cref="ReduxDepthOfField"/>. This adds one more volume: global, ranked above the game's, on a layer the
    /// game's camera reads volumes from. Only what photo mode changes is overridden; everything else stays the game's.
    /// The volume is made on opening and destroyed on closing, so the game's look comes back untouched, and every
    /// setting starts afresh the next time.
    /// </summary>
    public sealed class PhotoLook
    {
        public const float MinFocusDistance = 0.3f;
        public const float MaxFocusDistance = 5000f;

        /// <summary>The widest and narrowest apertures, as f-numbers: the lower, the more blur.</summary>
        public const float MinAperture = 1.4f;
        public const float MaxAperture = 22f;

        private const float DefaultFocusDistance = 5f;
        private const float DefaultAperture = 4f;

        // Above the game's own volumes, which Redux mirrors at the priorities the game gave them
        private const float Priority = 1000f;

        // Redux's depth of field works out the blur for a 24 mm high film, as a 35 mm still camera has
        private const float FilmHeightMillimetres = 24f;

        private readonly Action<string> _logInfo;
        private readonly Action<string> _logWarning;

        private GameObject _holder;
        private VolumeProfile _profile;
        private ReduxDepthOfField _depthOfField;

        // The camera's volume layers last seen, so a change is only looked at (and logged) once
        private int _volumeLayers = -1;

        public bool IsEngaged => _holder != null;

        /// <summary>Whether things nearer and further than <see cref="FocusDistance"/> are blurred.</summary>
        public bool DepthOfField { get; set; }

        /// <summary>How far from the camera things are sharp, in metres.</summary>
        public float FocusDistance { get; set; } = DefaultFocusDistance;

        /// <summary>The aperture, as an f-number: lower blurs what's out of focus more.</summary>
        public float Aperture { get; set; } = DefaultAperture;

        public PhotoLook(Action<string> logInfo, Action<string> logWarning)
        {
            _logInfo = logInfo ?? throw new ArgumentNullException(nameof(logInfo));
            _logWarning = logWarning ?? throw new ArgumentNullException(nameof(logWarning));
        }

        /// <summary>Lays photo mode's look over the game's, starting from the defaults. Safe to call when engaged.</summary>
        public void Engage()
        {
            if (IsEngaged) return;
            DepthOfField = false;
            FocusDistance = DefaultFocusDistance;
            Aperture = DefaultAperture;
            _volumeLayers = -1;

            _profile = ScriptableObject.CreateInstance<VolumeProfile>();
            _profile.name = "MunShot photo look";
            _profile.hideFlags = HideFlags.DontSave;
            _depthOfField = _profile.Add<ReduxDepthOfField>(overrides: false);

            _holder = new GameObject("MunShot photo look") { hideFlags = HideFlags.DontSave };
            var volume = _holder.AddComponent<Volume>();
            volume.isGlobal = true;
            volume.priority = Priority;
            volume.weight = 1f;
            volume.sharedProfile = _profile;
            Apply(null);
            _logInfo("Photo mode: photo look laid over the game's.");
        }

        /// <summary>
        /// Called every frame while engaged: passes the settings on, with the lens worked out from
        /// <paramref name="camera"/>'s field of view, and keeps the volume on a layer the camera reads.
        /// </summary>
        public void Tick(Camera camera)
        {
            if (!IsEngaged) return;
            if (camera != null) FollowVolumeLayers(camera);
            Apply(camera);
        }

        /// <summary>Takes photo mode's look away again. Safe to call when not engaged.</summary>
        public void Release()
        {
            if (!IsEngaged) return;
            UnityEngine.Object.Destroy(_holder);
            UnityEngine.Object.Destroy(_depthOfField);
            UnityEngine.Object.Destroy(_profile);
            _holder = null;
            _depthOfField = null;
            _profile = null;
            _logInfo("Photo mode: photo look taken away.");
        }

        private void Apply(Camera camera)
        {
            _depthOfField.Enabled.Override(DepthOfField);
            _depthOfField.FocusDistance.Override(Mathf.Clamp(FocusDistance, MinFocusDistance, MaxFocusDistance));
            _depthOfField.Aperture.Override(Mathf.Clamp(Aperture, MinAperture, MaxAperture));
            // A narrower field of view is a longer lens, which blurs more, as a real telephoto does
            if (camera != null) _depthOfField.FocalLength.Override(FocalLength(camera.fieldOfView));
            _depthOfField.KernelSize.Override(BokehKernelSize.Large);
        }

        // The focal length (mm) that sees the camera's vertical field of view on Redux's film
        private static float FocalLength(float verticalFieldOfView) =>
            FilmHeightMillimetres * 0.5f / Mathf.Tan(Mathf.Clamp(verticalFieldOfView, 1f, 179f) * 0.5f * Mathf.Deg2Rad);

        // A camera only blends volumes on its volume layers, which Redux copies from the game's post-processing each
        // frame; the volume goes on the first of them
        private void FollowVolumeLayers(Camera camera)
        {
            int layers = camera.GetUniversalAdditionalCameraData().volumeLayerMask.value;
            if (layers == _volumeLayers) return;
            _volumeLayers = layers;
            if (layers == 0)
            {
                _logWarning($"Photo mode: the camera '{camera.name}' reads volumes from no layer, so the photo look can't show.");
                return;
            }
            if ((layers & (1 << _holder.layer)) != 0) return;
            int layer = 0;
            while ((layers & (1 << layer)) == 0) layer++;
            _holder.layer = layer;
            _logInfo($"Photo mode: photo look on layer {layer} ({LayerMask.LayerToName(layer)}), read by the camera '{camera.name}' (volume layers 0x{layers:X}).");
        }
    }
}
