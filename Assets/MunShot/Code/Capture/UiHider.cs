using System;
using KSP.Game;
using KSP.UI;

namespace MunShot.Capture
{
    /// <summary>
    /// Hides the game's UI while photo mode is open: by switching the UI to the game's own photo-mode view
    /// (<see cref="UIStateViews.PhotoModeView"/>, as the game's own metrics mode does). Switching to it also tells Redux,
    /// which hides mods' windows that hide on F2, and parts of the game that check for it (part outlines and info,
    /// Redux's impact prediction and resource scanner) step back too.
    ///
    /// While engaged, anything that switches the UI back (pressing F2, the game changing views) is undone on the next
    /// frame and logged. Released, the UI goes back to the view it was in, unless the UI was already hidden (the player
    /// had pressed F2 before opening photo mode).
    ///
    /// The view controller reports the photo-mode view as <see cref="UIStateViews.UIHiddenView"/> (Redux 26w46a on),
    /// so either of the two counts as hidden here.
    /// </summary>
    public sealed class UiHider
    {
        private readonly Action<string> _logInfo;
        private readonly Action<string> _logWarning;
        private UIViewController _viewController;
        private UIView _restoreView;
        private bool _viewChangedByOthers;

        public bool IsEngaged { get; private set; }

        public UiHider(Action<string> logInfo, Action<string> logWarning)
        {
            _logInfo = logInfo ?? throw new ArgumentNullException(nameof(logInfo));
            _logWarning = logWarning ?? throw new ArgumentNullException(nameof(logWarning));
        }

        /// <summary>Hides the UI. Safe to call when already engaged.</summary>
        public void Engage()
        {
            if (IsEngaged) return;
            UIViewController viewController = GameManager.Instance?.Game?.UI?.ViewController;
            if (viewController == null)
            {
                _logWarning("Photo mode: can't hide the UI, the game's UI view controller isn't available.");
                return;
            }

            _viewController = viewController;
            _restoreView = viewController.CurrentView;
            _viewChangedByOthers = false;
            IsEngaged = true;
            if (_restoreView != UIStateViews.PhotoModeView) viewController.PushView(UIStateViews.PhotoModeView);
            viewController.CurrentViewChanged += OnViewChanged;
            _logInfo("Photo mode: UI hidden.");
        }

        /// <summary>Called every frame: hides the UI again if anything brought it back while engaged.</summary>
        public void Tick()
        {
            if (!IsEngaged || !_viewChangedByOthers) return;
            _viewChangedByOthers = false;
            if (_viewController == null || IsHidden(_viewController.CurrentView)) return;

            // The game's newer view is the one to come back to
            _restoreView = _viewController.CurrentView;
            _viewController.PushView(UIStateViews.PhotoModeView);
            _logWarning("Photo mode: the UI was switched back while photo mode is open; hid it again.");
        }

        /// <summary>
        /// Shows the UI again as it was. Safe to call when not engaged, and when the game's UI is already being torn
        /// down.
        /// </summary>
        public void Release()
        {
            if (!IsEngaged) return;
            IsEngaged = false;
            if (_viewController == null) return;

            _viewController.CurrentViewChanged -= OnViewChanged;
            if (_restoreView != null && !IsHidden(_restoreView) && IsHidden(_viewController.CurrentView))
            {
                _viewController.PushView(_restoreView);
                _logInfo("Photo mode: UI shown again.");
            }
            _viewController = null;
            _restoreView = null;
        }

        // Switched back by someone else (our own pushes happen before subscribing or while handled in Tick);
        // undone next frame rather than inside the game's own view change
        private void OnViewChanged(UIView view)
        {
            if (!IsHidden(view)) _viewChangedByOthers = true;
        }

        private static bool IsHidden(UIView view) => view == UIStateViews.PhotoModeView || view == UIStateViews.UIHiddenView;
    }
}
