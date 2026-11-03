using System;
using System.Collections.Generic;
using KSP.Game;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace MunShot.Posing
{
    /// <summary>
    /// Redux's own helmet lights, for photo doubles: the light rig Redux puts on an EVA kerbal's helmet and the glow
    /// texture that makes its lamps shine (both loaded as Redux's <c>Module_Kerbal.TryInitEvaLightVfx</c> loads them).
    /// Loaded the first time a double's lights go on, through the game's assets, and let go of when photo mode closes.
    /// </summary>
    public sealed class HelmetLightAssets
    {
        private const string RigAddress = "ReduxAssets/Kerbal/helmet_lights.prefab";
        private const string GlowAddress = "ReduxAssets/Kerbal/kerbal_spacesuit_01_e.png";

        private readonly Action<string> _logWarning;
        private AsyncOperationHandle<GameObject> _rig;
        private AsyncOperationHandle<Texture2D> _glow;
        private readonly List<Action<GameObject, Texture2D>> _waiting = new List<Action<GameObject, Texture2D>>();
        private bool _loading;

        // Counts up on Release, so loads that finish after photo mode closed call nobody and are let go of
        private int _generation;

        public HelmetLightAssets(Action<string> logWarning)
        {
            _logWarning = logWarning ?? throw new ArgumentNullException(nameof(logWarning));
        }

        /// <summary>
        /// Calls <paramref name="done"/> with the light rig and glow texture (either null if it couldn't be loaded,
        /// logged): straight away once loaded, else when both have. Not called if photo mode closes first.
        /// </summary>
        public void Get(Action<GameObject, Texture2D> done)
        {
            if (_rig.IsValid() && _glow.IsValid() && _rig.IsDone && _glow.IsDone)
            {
                done(Result(_rig), Result(_glow));
                return;
            }
            _waiting.Add(done);
            if (_loading) return;

            var assets = GameManager.Instance?.Assets;
            if (assets == null)
            {
                _logWarning("Photo mode: the game's asset provider isn't available, so helmet lights can't be loaded.");
                Finish(null, null);
                return;
            }
            _loading = true;
            int generation = _generation;
            _rig = assets.LoadRaw<GameObject>(RigAddress);
            _glow = assets.LoadRaw<Texture2D>(GlowAddress);
            int left = 2;
            void Loaded()
            {
                if (generation != _generation || --left > 0) return;
                _loading = false;
                GameObject rig = Result(_rig);
                Texture2D glow = Result(_glow);
                if (rig == null) _logWarning($"Photo mode: Redux's helmet lights '{RigAddress}' didn't load: {_rig.OperationException?.Message ?? "no prefab"}.");
                if (glow == null) _logWarning($"Photo mode: Redux's helmet glow '{GlowAddress}' didn't load: {_glow.OperationException?.Message ?? "no texture"}.");
                Finish(rig, glow);
            }
            AsyncOperationHandle<GameObject> rigHandle = _rig;
            AsyncOperationHandle<Texture2D> glowHandle = _glow;
            rigHandle.Completed += op =>
            {
                if (generation != _generation) ReleaseIfValid(op);
                else Loaded();
            };
            glowHandle.Completed += op =>
            {
                if (generation != _generation) ReleaseIfValid(op);
                else Loaded();
            };
        }

        /// <summary>Lets go of what's loaded (when photo mode closes; the doubles using it are gone by then).</summary>
        public void Release()
        {
            _generation++;
            _waiting.Clear();
            // What's finished is let go of now; a load still under way is let go of when it finishes
            if (_rig.IsValid() && _rig.IsDone) Addressables.Release(_rig);
            if (_glow.IsValid() && _glow.IsDone) Addressables.Release(_glow);
            _loading = false;
            _rig = default;
            _glow = default;
        }

        private void Finish(GameObject rig, Texture2D glow)
        {
            var callbacks = new List<Action<GameObject, Texture2D>>(_waiting);
            _waiting.Clear();
            foreach (Action<GameObject, Texture2D> callback in callbacks) callback(rig, glow);
        }

        private static T Result<T>(AsyncOperationHandle<T> handle) where T : class =>
            handle.IsValid() && handle.Status == AsyncOperationStatus.Succeeded ? handle.Result : null;

        private static void ReleaseIfValid(AsyncOperationHandle handle)
        {
            if (handle.IsValid()) Addressables.Release(handle);
        }
    }
}
