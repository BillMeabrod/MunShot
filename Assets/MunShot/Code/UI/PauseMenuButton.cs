using System;
using KSP.Api.CoreTypes;
using KSP.Game;
using KSP.Messages;
using KSP.UI;
using UnityEngine;
using ILogger = SpaceWarp2.API.Logging.ILogger;

namespace MunShot.UI
{
    /// <summary>
    /// A "Photo Mode" button in the game's pause menu, in flight. The pause menu has no API for mods' buttons; this
    /// adds one the way Redux adds its own (Report Bugs, Game Statistics in ESCMenuActionElementsController): a copy of
    /// an existing button, renamed, with a localized name and a <see cref="DelegateAction"/> bound to it. It's made
    /// when the pause menu first opens and shown only in flight, in campaigns with MunShot switched on. Pressing it
    /// resumes the game and opens photo mode (which pauses it again). Everything that could go missing in a game update
    /// is checked and logged.
    /// </summary>
    internal sealed class PauseMenuButton
    {
        // Redux's Game Statistics button, copied and placed just before it
        private const string TemplateName = "22GameStatistics";
        private const string ButtonName = "21MunShotPhotoMode";
        private const string ActionKey = "munShotPhotoMode";

        private readonly Action _openPhotoMode;
        private readonly Func<bool> _modEnabled;
        private readonly ILogger _logger;
        private GameObject _button;
        private bool _reportedMissing;

        /// <param name="modEnabled">Whether MunShot is switched on in this campaign; the button only shows when it is.</param>
        public PauseMenuButton(Action openPhotoMode, Func<bool> modEnabled, ILogger logger)
        {
            _openPhotoMode = openPhotoMode ?? throw new ArgumentNullException(nameof(openPhotoMode));
            _modEnabled = modEnabled ?? throw new ArgumentNullException(nameof(modEnabled));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>Call when the pause menu opens (<see cref="EscapeMenuOpenedMessage"/>).</summary>
        public void OnPauseMenuOpened()
        {
            // In flight, in campaigns with MunShot switched on (Redux's app bar hides mods' buttons the same way)
            bool show = _modEnabled() && GameManager.Instance?.Game?.GlobalGameState?.GetState() == GameState.FlightView;
            if (_button == null && show) _button = Create();
            if (_button == null) return;
            _button.SetActive(show);
            Renumber(_button.transform.parent);
        }

        // The pause menu numbers its lines once, when it's built (ESCMenuActionElementsController's
        // SetIDsForAvailableActionElements): each line's place among them, set on the ones showing. The button is added
        // after that, so the lines are numbered again the same way, with it among them
        private static void Renumber(Transform menu)
        {
            if (menu == null) return;
            ESCMenuActionElements[] lines = menu.GetComponentsInChildren<ESCMenuActionElements>();
            for (int i = 0; i < lines.Length; i++)
                if (lines[i].gameObject.activeInHierarchy)
                    lines[i].SetID(i.ToString());
        }

        private GameObject Create()
        {
            var menu = UnityEngine.Object.FindAnyObjectByType<ESCMenuActionElementsController>(FindObjectsInactive.Include);
            Transform template = menu != null ? menu.transform.Find(TemplateName) : null;
            if (template == null)
            {
                if (!_reportedMissing)
                    _logger.LogError(menu == null
                        ? "Photo mode: no Photo Mode button in the pause menu; the pause menu (ESCMenuActionElementsController) wasn't found."
                        : $"Photo mode: no Photo Mode button in the pause menu; its '{TemplateName}' button to copy wasn't found. Use the app tray or the keyboard shortcut.");
                _reportedMissing = true;
                return null;
            }

            GameObject button = UnityEngine.Object.Instantiate(template.gameObject, menu.transform);
            button.name = ButtonName;
            button.transform.SetSiblingIndex(template.GetSiblingIndex());

            var elements = button.GetComponent<ESCMenuActionElements>();
            var action = button.GetComponentInChildren<UIAction_Void_Button>();
            if (elements == null || action == null)
            {
                _logger.LogError($"Photo mode: the pause menu's '{TemplateName}' button no longer has "
                                 + $"{(elements == null ? nameof(ESCMenuActionElements) : nameof(UIAction_Void_Button))}; no Photo Mode button.");
                UnityEngine.Object.Destroy(button);
                _reportedMissing = true;
                return null;
            }

            // The pause menu looks its names up under "Menu/Escape/"
            elements.NameText = Localization.PauseMenuButton;
            elements.SetLocalizedNameText(Localization.PauseMenuButton);

            var delegateAction = new DelegateAction();
            delegateAction.BindDelegate(OnPressed);
            action.BindAction(delegateAction);
            action.ActionKey = ActionKey;
            _logger.LogInfo("Photo mode: added a Photo Mode button to the pause menu.");
            return button;
        }

        private void OnPressed()
        {
            GameManager.Instance?.Game?.UI?.EscapeMenu?.ResumeGame();
            _openPhotoMode();
        }
    }
}
