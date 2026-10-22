using System;
using KSP.Game;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MunShot.Posing
{
    /// <summary>
    /// Makes <see cref="PhotoDouble"/>s the way the game's portrait booth (<c>KerbalPhotoBooth</c>) makes crew
    /// portraits: from the booth's bare kerbal model (a <see cref="Kerbal3DModel"/> and an Animator, nothing else),
    /// told which kerbal to be. The double then gets the EVA animation controller, which shares the kerbal skeleton, so
    /// every stock pose works on it. Both templates are loaded from the game's assets once, the first time they're
    /// needed.
    /// </summary>
    public sealed class PhotoDoubleFactory
    {
        public const string BoothAddress = "KerbalPhotoBooth.prefab";
        public const string BoothModelPath = "Kerbal3DModel";
        public const string EvaKerbalAddress = "eva_kerbal.prefab";

        // The outfit rules every crew member is built with, on EVA or seated
        private const string AttributeRules = "KERBALATTRIBUTES_EVA";

        private readonly Action<string> _logError;
        private Kerbal3DModel _template;
        private RuntimeAnimatorController _evaController;

        // The layer EVA kerbals are drawn on. Doubles always use it: seated crew are on the cockpit-interior layer,
        // which the flight camera doesn't draw outside, and the booth's model is on its own portrait layer
        private int _evaLayer;
        private bool _loading;
        private event Action<bool> Loaded;

        public bool IsReady => _template != null && _evaController != null;

        public PhotoDoubleFactory(Action<string> logError)
        {
            _logError = logError ?? throw new ArgumentNullException(nameof(logError));
        }

        /// <summary>
        /// Loads the booth model and the EVA controller if needed, then calls <paramref name="done"/> with whether
        /// both are available (failures are logged with what was missing).
        /// </summary>
        public void EnsureLoaded(Action<bool> done)
        {
            if (IsReady)
            {
                done(true);
                return;
            }

            Loaded += done;
            if (_loading) return;
            _loading = true;

            var assets = GameManager.Instance?.Assets;
            if (assets == null)
            {
                Finish("the game's asset provider isn't available");
                return;
            }

            int pending = 2;
            void OneLoaded()
            {
                if (--pending == 0) Finish(null);
            }

            assets.Load<GameObject>(BoothAddress, booth =>
            {
                Transform model = booth != null ? booth.transform.Find(BoothModelPath) : null;
                _template = model != null ? model.GetComponent<Kerbal3DModel>() : null;
                if (_template == null)
                    _logError($"Photo doubles: '{BoothAddress}' {(booth == null ? "did not load" : $"has no {nameof(Kerbal3DModel)} at '{BoothModelPath}'")}.");
                OneLoaded();
            });
            assets.Load<GameObject>(EvaKerbalAddress, eva =>
            {
                Animator animator = eva != null ? eva.GetComponentInChildren<Animator>(true) : null;
                _evaController = animator != null ? animator.runtimeAnimatorController : null;
                if (animator != null) _evaLayer = animator.gameObject.layer;
                if (_evaController == null)
                    _logError($"Photo doubles: '{EvaKerbalAddress}' {(eva == null ? "did not load" : "has no Animator controller")}.");
                OneLoaded();
            });
        }

        /// <summary>
        /// Starts building a double of <paramref name="source"/> at the given place, under <paramref name="parent"/>.
        /// The game finishes building it over the next frames; tick the double until it's ready.
        /// </summary>
        public PhotoDouble Create(KerbalCandidate source, Vector3 position, Quaternion rotation, Transform parent)
        {
            if (!IsReady) throw new InvalidOperationException("Photo double templates aren't loaded yet; call EnsureLoaded first.");
            if (source == null) throw new ArgumentNullException(nameof(source));

            // Told who to be before the game's first update, which is when it starts building
            Kerbal3DModel model = Object.Instantiate(_template, position, rotation, parent);
            model.name = $"MunShot double of {source.Name}";
            model.AttributesRulesSetName = AttributeRules;
            // Always built as an EVA kerbal, whoever the original is: the double stands outside, so it needs the full
            // EVA kit. A seated kerbal's model is built without the jetpack, which the jetpack poses rely on
            model.IsEVAKerbal = true;
            model.IsInExternalSeat = false;
            model.ThisKerbalInfo = source.Info;
            // The game puts each body part on the model's layer as it attaches it
            model.gameObject.SetLayerRecursive(_evaLayer);

            Animator animator = model.GetComponent<Animator>();
            animator.runtimeAnimatorController = _evaController;
            return new PhotoDouble(source, model, animator);
        }

        private void Finish(string failure)
        {
            _loading = false;
            if (failure != null) _logError($"Photo doubles: {failure}.");
            Action<bool> callbacks = Loaded;
            Loaded = null;
            callbacks?.Invoke(IsReady);
        }
    }
}
