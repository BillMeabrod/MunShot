using System;
using UnityEngine;

namespace MunShot.Capture
{
    /// <summary>
    /// While photo mode is open, the photo camera's textures are loaded sharp from further off. The game streams its
    /// textures' detail (Unity's texture streaming, which KSP2 turns on): a texture far off has only its blurry levels
    /// loaded, and its sharp ones load as the camera comes near, which takes a few seconds; flying up to something (the
    /// VAB's logo) shows it blurry first. The camera's streaming controller is told to want each texture <see cref="Bias"/>
    /// levels sharper than its distance needs, so they're loaded before the camera gets there. Its own controller if it
    /// has one (its bias put back on release), or one added for photo mode (taken away on release); followed onto
    /// another camera when the game switches.
    /// </summary>
    public sealed class SharpTextures
    {
        // Each level is twice as sharp: at this, a texture is as sharp at four times the distance
        private const float Bias = -2f;

        private readonly Action<string> _logInfo;
        private Camera _camera;
        private StreamingController _controller;
        private bool _added;
        private float _priorBias;

        public bool IsEngaged { get; private set; }

        public SharpTextures(Action<string> logInfo)
        {
            _logInfo = logInfo ?? throw new ArgumentNullException(nameof(logInfo));
        }

        /// <summary>Starts wanting sharper textures for the camera <see cref="Tick"/> is given. Safe to call when engaged.</summary>
        public void Engage()
        {
            if (IsEngaged) return;
            if (!QualitySettings.streamingMipmapsActive)
            {
                _logInfo("Photo mode: texture streaming is off, so every texture is already as sharp as it gets.");
                return;
            }
            IsEngaged = true;
        }

        /// <summary>Called every frame with the camera drawing the picture; follows it when the game switches.</summary>
        public void Tick(Camera camera)
        {
            if (!IsEngaged || (camera == _camera && _controller != null)) return;
            Restore();
            if (camera == null) return;
            _camera = camera;
            _controller = camera.GetComponent<StreamingController>();
            _added = _controller == null;
            if (_added) _controller = camera.gameObject.AddComponent<StreamingController>();
            _priorBias = _controller.streamingMipmapBias;
            _controller.streamingMipmapBias = Bias;
        }

        /// <summary>Puts the camera's texture streaming back as it was. Safe to call when not engaged.</summary>
        public void Release()
        {
            IsEngaged = false;
            Restore();
        }

        private void Restore()
        {
            if (_controller != null)
            {
                if (_added) UnityEngine.Object.Destroy(_controller);
                else _controller.streamingMipmapBias = _priorBias;
            }
            _camera = null;
            _controller = null;
            _added = false;
        }
    }
}
