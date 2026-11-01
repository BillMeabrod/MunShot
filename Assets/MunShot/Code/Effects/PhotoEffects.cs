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
    /// first time each kind is added, and kept until photo mode closes (the copies use their materials). Effects live
    /// under the object vessels live under, which the game moves when the floating origin shifts.
    /// </summary>
    public sealed class PhotoEffects : IPlaceableSet
    {
        /// <summary>How many there can be: plenty for a shot, few enough that working them out stays quick.</summary>
        public const int MaxEffects = 8;

        private readonly Action<string> _logInfo;
        private readonly Action<string> _logWarning;
        private readonly List<PhotoEffect> _effects = new List<PhotoEffect>();
        private readonly Dictionary<PhotoEffectKind, AsyncOperationHandle<GameObject>> _loaded =
            new Dictionary<PhotoEffectKind, AsyncOperationHandle<GameObject>>();
        private readonly Dictionary<PhotoEffectKind, List<Action<GameObject>>> _waiting =
            new Dictionary<PhotoEffectKind, List<Action<GameObject>>>();
        private readonly System.Random _seeds = new System.Random();
        private bool _markersShown = true;

        // Counts up on Clear, so a load that finishes after photo mode closed adds nothing
        private int _generation;

        public IReadOnlyList<PhotoEffect> All => _effects;
        public PhotoEffect Selected { get; private set; }
        public bool CanAdd => _effects.Count < MaxEffects;

        public PhotoEffects(Action<string> logInfo, Action<string> logWarning)
        {
            _logInfo = logInfo ?? throw new ArgumentNullException(nameof(logInfo));
            _logWarning = logWarning ?? throw new ArgumentNullException(nameof(logWarning));
        }

        /// <summary>
        /// Adds an effect in front of <paramref name="camera"/> (as far as it's big), facing the camera, under
        /// <paramref name="parent"/> (the scene's root when null), and selects it. The game's effect may have to load
        /// first: <paramref name="done"/> is called with the new effect, or null if it couldn't be added (logged).
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
            int generation = _generation;
            Load(kind, prefab =>
            {
                if (prefab == null || generation != _generation || !CanAdd)
                {
                    done(null);
                    return;
                }
                var effect = new PhotoEffect(kind, FreeNumber(kind), prefab, parent, position, rotation, shader,
                                             (uint)_seeds.Next(), _logInfo);
                effect.ShowMarker(_markersShown);
                _effects.Add(effect);
                Selected = effect;
                _logInfo($"Photo mode: effect {kind} {effect.Number} added {spec.DistanceInFront}m in front of the camera, under '{(parent != null ? parent.name : "<scene root>")}'.");
                done(effect);
            });
        }

        /// <summary>Called every frame: each effect follows its marker and is worked out again if it changed.</summary>
        public void Tick()
        {
            foreach (PhotoEffect effect in _effects) effect.Tick();
        }

        public void Remove(PhotoEffect effect)
        {
            if (effect == null || !_effects.Remove(effect)) return;
            effect.Destroy();
            if (Selected == effect) Selected = _effects.Count > 0 ? _effects[_effects.Count - 1] : null;
            _logInfo($"Photo mode: effect {effect.Kind} {effect.Number} removed.");
        }

        public void Select(PhotoEffect effect)
        {
            if (effect != null && _effects.Contains(effect)) Selected = effect;
        }

        /// <summary>The effect whose marker <paramref name="ray"/> points at, nearest first; null if none.</summary>
        public PhotoEffect Pick(Ray ray, out Vector3 point)
        {
            point = default;
            PhotoEffect nearest = null;
            float nearestDistance = float.MaxValue;
            foreach (PhotoEffect effect in _effects)
            {
                if (!effect.TryPick(ray, out Vector3 at)) continue;
                float distance = (at - ray.origin).sqrMagnitude;
                if (distance >= nearestDistance) continue;
                nearestDistance = distance;
                nearest = effect;
                point = at;
            }
            return nearest;
        }

        /// <summary>Shows or hides every effect's marker (hidden for the photo and while the UI is hidden).</summary>
        public void ShowMarkers(bool shown)
        {
            if (shown == _markersShown) return;
            _markersShown = shown;
            foreach (PhotoEffect effect in _effects) effect.ShowMarker(shown);
        }

        /// <summary>Takes every effect away and lets go of the game's effects loaded for them.</summary>
        public void Clear()
        {
            _generation++;
            if (_effects.Count > 0) _logInfo($"Photo mode: {_effects.Count} effect(s) removed.");
            foreach (PhotoEffect effect in _effects) effect.Destroy();
            _effects.Clear();
            Selected = null;
            _markersShown = true;
            foreach (AsyncOperationHandle<GameObject> handle in _loaded.Values)
                if (handle.IsValid()) Addressables.Release(handle);
            _loaded.Clear();
        }

        // The game's prefab for a kind: straight away once loaded, else when it loads (null if it can't be)
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
            _waiting[kind] = new List<Action<GameObject>> { loaded };
            AsyncOperationHandle<GameObject> handle = assets.LoadRaw<GameObject>(address);
            handle.Completed += op =>
            {
                GameObject prefab = op.Status == AsyncOperationStatus.Succeeded ? op.Result : null;
                if (prefab == null)
                    _logWarning($"Photo mode: the game's effect '{address}' didn't load: {op.OperationException?.Message ?? "no prefab"}.");
                _loaded[kind] = op;
                List<Action<GameObject>> callbacks = _waiting[kind];
                _waiting.Remove(kind);
                foreach (Action<GameObject> callback in callbacks) callback(prefab);
            };
        }

        // The lowest number not in use for the kind, so "Explosion 1" comes back after it's removed
        private int FreeNumber(PhotoEffectKind kind)
        {
            int number = 1;
            while (_effects.Exists(effect => effect.Kind == kind && effect.Number == number)) number++;
            return number;
        }

        // ---- For the handles (IPlaceableSet) ----

        IEnumerable<IPlaceable> IPlaceableSet.Placeables => _effects;

        bool IPlaceableSet.Holds(IPlaceable placeable) => placeable is PhotoEffect effect && _effects.Contains(effect);

        IPlaceable IPlaceableSet.PickPlaceable(Ray ray, out Vector3 point) => Pick(ray, out point);

        bool IPlaceableSet.SelectPlaceable(IPlaceable placeable)
        {
            if (!(placeable is PhotoEffect effect) || effect == Selected || !_effects.Contains(effect)) return false;
            Selected = effect;
            return true;
        }
    }
}
