using System;
using System.Collections.Generic;
using KSP.Game;
using MunShot.Posing;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace MunShot.Effects
{
    /// <summary>
    /// Photo mode's own effects (<see cref="PhotoEffect"/>): added in front of the camera, moved with the handles, and
    /// all taken away when photo mode closes. One is selected, for the FX tab to set.
    ///
    /// The game's effect prefabs are loaded by address through the game's assets, as the game's own effects are, the
    /// first time each kind is added, and kept until photo mode closes (the copies use their materials). One that
    /// finishes loading after photo mode closed is let go of straight away. Effects live under the object vessels live
    /// under, which the game moves when the floating origin shifts.
    /// </summary>
    public sealed class PhotoEffects : PlaceableSet<PhotoEffect>
    {
        /// <summary>How many there can be: plenty for a shot, few enough that working them out stays quick.</summary>
        public const int MaxEffects = 8;

        private readonly Action<string> _logWarning;
        private readonly Dictionary<PhotoEffectKind, AsyncOperationHandle<GameObject>> _loaded =
            new Dictionary<PhotoEffectKind, AsyncOperationHandle<GameObject>>();
        private readonly Dictionary<PhotoEffectKind, List<Action<GameObject>>> _waiting =
            new Dictionary<PhotoEffectKind, List<Action<GameObject>>>();
        private readonly System.Random _seeds = new System.Random();

        // Counts up on Clear, so a load that finishes after photo mode closed adds nothing and is let go of
        private int _generation;

        public PhotoEffects(Action<string> logInfo, Action<string> logWarning) : base(MaxEffects, logInfo)
        {
            _logWarning = logWarning ?? throw new ArgumentNullException(nameof(logWarning));
        }

        /// <summary>
        /// Adds an effect in front of <paramref name="camera"/> (as far as it's big), facing the camera, under
        /// <paramref name="parent"/> (the scene's root when null), and selects it. The game's effect may have to load
        /// first: <paramref name="done"/> is called with the new effect, or null if it couldn't be added (logged). It
        /// isn't called at all if photo mode closes before the effect has loaded.
        /// </summary>
        public void Add(PhotoEffectKind kind, Camera camera, Transform parent, Action<PhotoEffect> done)
        {
            if (!CanAdd || camera == null)
            {
                done(null);
                return;
            }
            Shader shader = PlaceableMarker.FindShader(_logWarning);
            if (shader == null)
            {
                done(null);
                return;
            }
            if (parent == null) _logWarning("Photo mode: no vessel to place effects beside; the effect goes under the scene's root, so a floating origin shift would move it.");

            // Where it goes is taken now, as the camera is when the button's pressed
            PhotoEffectSpec spec = PhotoEffectSpec.Of(kind);
            Transform view = camera.transform;
            Vector3 position = view.position + view.forward * spec.DistanceInFront;
            Quaternion rotation = Quaternion.LookRotation(-view.forward, view.up);
            Load(kind, prefab =>
            {
                if (prefab == null || !CanAdd)
                {
                    if (prefab != null) _logWarning($"Photo mode: the effect '{spec.Address}' loaded after there were already {MaxEffects}; not added.");
                    done(null);
                    return;
                }
                var effect = new PhotoEffect(kind, FreeNumber(other => other.Kind == kind), prefab, parent, position, rotation, shader,
                                             (uint)_seeds.Next(), LogInfo);
                Added(effect);
                LogInfo($"Photo mode: effect {kind} {effect.Number} added {spec.DistanceInFront}m in front of the camera, under '{(parent != null ? parent.name : "<scene root>")}'.");
                done(effect);
            });
        }

        /// <summary>Called every frame: each effect follows its marker and is worked out again if it changed.</summary>
        public void Tick()
        {
            foreach (PhotoEffect effect in All) effect.Tick();
        }

        /// <summary>Takes every effect away and lets go of the game's effects loaded for them.</summary>
        public override void Clear()
        {
            _generation++;
            base.Clear();
            foreach (AsyncOperationHandle<GameObject> handle in _loaded.Values)
                if (handle.IsValid()) Addressables.Release(handle);
            _loaded.Clear();
            // Loads still under way are let go of when they finish (see Load)
            _waiting.Clear();
        }

        protected override string Describe(PhotoEffect effect) => $"effect {effect.Kind} {effect.Number}";

        protected override string PluralName => "effect(s)";

        // The game's prefab for a kind: straight away once loaded, else when it loads (null if it can't be). One that
        // finishes after photo mode closed calls nobody back and is let go of
        private void Load(PhotoEffectKind kind, Action<GameObject> loaded)
        {
            if (_loaded.TryGetValue(kind, out AsyncOperationHandle<GameObject> done) && done.IsValid())
            {
                loaded(done.Status == AsyncOperationStatus.Succeeded ? done.Result : null);
                return;
            }
            if (_waiting.TryGetValue(kind, out List<Action<GameObject>> waiting))
            {
                waiting.Add(loaded);
                return;
            }

            var assets = GameManager.Instance?.Assets;
            string address = PhotoEffectSpec.Of(kind).Address;
            if (assets == null)
            {
                _logWarning($"Photo mode: the game's asset provider isn't available, so the effect '{address}' can't be loaded.");
                loaded(null);
                return;
            }
            int generation = _generation;
            var callbacks = new List<Action<GameObject>> { loaded };
            _waiting[kind] = callbacks;
            AsyncOperationHandle<GameObject> handle = assets.LoadRaw<GameObject>(address);
            handle.Completed += op =>
            {
                if (generation != _generation)
                {
                    if (op.IsValid()) Addressables.Release(op);
                    return;
                }
                GameObject prefab = op.Status == AsyncOperationStatus.Succeeded ? op.Result : null;
                if (prefab == null)
                    _logWarning($"Photo mode: the game's effect '{address}' didn't load: {op.OperationException?.Message ?? "no prefab"}.");
                _loaded[kind] = op;
                _waiting.Remove(kind);
                foreach (Action<GameObject> callback in callbacks) callback(prefab);
            };
        }
    }
}
