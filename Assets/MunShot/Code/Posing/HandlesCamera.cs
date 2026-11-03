using System;
using SpaceWarp2.API;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using Object = UnityEngine.Object;

namespace MunShot.Posing
{
    /// <summary>
    /// A small camera that draws only the move and rotate handles, after the game's cameras and with no post-processing,
    /// so the picture's colour, blur, vignette and fisheye never reach them: they aren't part of the scene.
    ///
    /// It's an ordinary game camera above the game's own: Redux's <c>Redux.Rendering.UrpCameraStacker</c> stacks every
    /// such camera as an overlay on the game's, so it's drawn into the same picture after them, keeping their depth
    /// (it clears nothing), so what's in front still hides the handles. It's set up as the game sets up its own
    /// overlay cameras (UIManager's screen space UI camera: no post-processing, shadows or volumes), and follows the
    /// game's current camera every frame.
    ///
    /// The handles go on <see cref="Layer"/>, the VAB's scenery layer, which the flight cameras don't draw (the game's
    /// VAB-only effects are invisible in flight for that reason), so only this camera sees them.
    /// </summary>
    public sealed class HandlesCamera : IDisposable
    {
        /// <summary>The layer the handles are drawn on.</summary>
        public const int Layer = Layers.LayerOabScenery;

        private readonly Action<string> _logInfo;
        private Camera _camera;
        private Camera[] _all = new Camera[16];

        public HandlesCamera(Action<string> logInfo)
        {
            _logInfo = logInfo ?? throw new ArgumentNullException(nameof(logInfo));
        }

        /// <summary>Called every frame while photo mode is open: follows <paramref name="view"/> (made the first time).</summary>
        public void Follow(Camera view)
        {
            if (view == null)
            {
                if (_camera != null) _camera.enabled = false;
                return;
            }
            if (_camera == null) Create();

            Transform from = view.transform, to = _camera.transform;
            to.SetPositionAndRotation(from.position, from.rotation);
            _camera.fieldOfView = view.fieldOfView;
            _camera.nearClipPlane = view.nearClipPlane;
            _camera.farClipPlane = view.farClipPlane;
            _camera.rect = view.rect;
            _camera.targetTexture = view.targetTexture;
            _camera.targetDisplay = view.targetDisplay;
            // Above every game camera, so it's stacked last
            _camera.depth = HighestDepth() + 1f;
            _camera.enabled = true;
        }

        public void Dispose()
        {
            if (_camera != null) Object.Destroy(_camera.gameObject);
            _camera = null;
        }

        private void Create()
        {
            var holder = new GameObject("MunShot handles camera") { hideFlags = HideFlags.DontSave };
            Object.DontDestroyOnLoad(holder);
            _camera = holder.AddComponent<Camera>();
            _camera.cullingMask = 1 << Layer;
            // Clears nothing: drawn over the game's picture, tested against its depth
            _camera.clearFlags = CameraClearFlags.Nothing;
            _camera.allowHDR = true;
            _camera.allowMSAA = false;
            _camera.useOcclusionCulling = false;
            UniversalAdditionalCameraData data = _camera.GetUniversalAdditionalCameraData();
            data.renderPostProcessing = false;
            data.renderShadows = false;
            data.requiresColorOption = CameraOverrideOption.Off;
            data.requiresDepthOption = CameraOverrideOption.Off;
            data.volumeLayerMask = 0;
            _logInfo($"Photo mode: handles camera made, drawing layer {Layer} ({LayerMask.LayerToName(Layer)}) over the game's cameras without post-processing.");
        }

        private float HighestDepth()
        {
            if (_all.Length < Camera.allCamerasCount) _all = new Camera[Camera.allCamerasCount * 2];
            int count = Camera.GetAllCameras(_all);
            float highest = float.MinValue;
            for (int i = 0; i < count; i++)
            {
                Camera other = _all[i];
                if (other != _camera && other.cameraType == CameraType.Game && other.depth > highest) highest = other.depth;
            }
            return highest == float.MinValue ? 0f : highest;
        }
    }
}
