using System;
using KSP.Game;
using KSP.Input;
using UnityEngine.InputSystem;

namespace MunShot.Capture
{
    /// <summary>
    /// Takes controls away from the game while photo mode is open. It's a lock in the game's own form (an
    /// <see cref="InputLockDefinition"/> given to the input manager), built from the game's action maps:
    /// <list type="bullet">
    /// <item>every flight action is switched off, camera ones included (photo mode has its own camera). That includes
    /// the "interact" clicks on parts, so clicking a double doesn't also select a part or vessel behind it;</item>
    /// <item>EVA controls are switched off entirely;</item>
    /// <item>quicksave and quickload are switched off, so a stray key doesn't save or reload the game in the middle of a
    /// shot.</item>
    /// </list>
    /// Each action's own state, and each set's state, are remembered when the lock goes on, and put back exactly as
    /// they were when it comes off. (The game's FlightInputEnabled only switches whole sets back on, which leaves
    /// individually switched-off actions off.)
    /// </summary>
    public sealed class PhotoModeInputLock
    {
        private static readonly string FlightSet = nameof(FlightInputDefinition);
        private static readonly string EvaSet = nameof(EVAInputDefinition);
        private static readonly string GlobalSet = nameof(GlobalInputDefinition);

        private readonly Action<string> _logInfo;
        private readonly Action<string> _logWarning;
        private InputLockDefinition _restore;

        public bool IsEngaged { get; private set; }

        public PhotoModeInputLock(Action<string> logInfo, Action<string> logWarning)
        {
            _logInfo = logInfo ?? throw new ArgumentNullException(nameof(logInfo));
            _logWarning = logWarning ?? throw new ArgumentNullException(nameof(logWarning));
        }

        public void Engage()
        {
            if (IsEngaged) return;
            GameInstance game = GameManager.Instance?.Game;
            InputManager inputManager = game?.InputManager;
            if (inputManager?.InputDefinitions == null || game.Input == null
                || !inputManager.InputDefinitions.TryGetValue(FlightSet, out InputDefinition flight))
            {
                _logWarning("Photo mode: can't lock flight controls, the game's flight input isn't available.");
                return;
            }
            inputManager.InputDefinitions.TryGetValue(EvaSet, out InputDefinition eva);
            inputManager.InputDefinitions.TryGetValue(GlobalSet, out InputDefinition global);

            var locking = new InputLockDefinition();
            _restore = new InputLockDefinition();
            int flightLocked = 0;
            foreach (InputAction action in game.Input.Flight.Get().actions)
                if (LockAction(locking, flight, FlightSet, action)) flightLocked++;
            if (eva != null)
            {
                locking.InputLocks.Add(SetLock(EvaSet, false));
                _restore.InputLocks.Add(SetLock(EvaSet, eva.Enabled));
            }

            string saving;
            if (global == null)
            {
                saving = "quicksave and quickload NOT locked";
                _logWarning("Photo mode: can't lock quicksave and quickload, the game's global input isn't available.");
            }
            else
            {
                int savingLocked = 0;
                foreach (InputAction action in new[] { game.Input.Global.QuickSave, game.Input.Global.QuickLoad, game.Input.Global.QuickLoadHold })
                    if (LockAction(locking, global, GlobalSet, action)) savingLocked++;
                saving = $"{savingLocked} quicksave/quickload actions off";
            }

            inputManager.SetInputLock(locking);
            IsEngaged = true;
            _logInfo($"Photo mode: controls locked ({flightLocked} flight actions off, EVA off, {saving}).");
        }

        /// <summary>
        /// Puts every locked action and set back as it was. Safe to call when not engaged, and while the game is being
        /// torn down.
        /// </summary>
        public void Release()
        {
            if (!IsEngaged) return;
            IsEngaged = false;
            InputLockDefinition restore = _restore;
            _restore = null;
            GameManager.Instance?.Game?.InputManager?.SetInputLock(restore);
            _logInfo("Photo mode: controls back as they were.");
        }

        // Switches one action off, remembering how it and its set were
        private bool LockAction(InputLockDefinition locking, InputDefinition set, string setName, InputAction action)
        {
            if (action == null || !set.TryGetAction(action.name, out ToggleableInputAction binder)) return false;
            locking.InputLocks.Add(ActionLock(setName, action.name, set.Enabled, false));
            _restore.InputLocks.Add(ActionLock(setName, action.name, set.Enabled, binder.Enabled));
            return true;
        }

        private static InputLockDefinition.InputLock ActionLock(string set, string action, bool setEnabled, bool actionEnabled) =>
            new InputLockDefinition.InputLock
            {
                DefinitionID = set,
                InputID = action,
                DefinitionEnabled = setEnabled,
                InputEnabled = actionEnabled
            };

        private static InputLockDefinition.InputLock SetLock(string set, bool enabled) =>
            new InputLockDefinition.InputLock { DefinitionID = set, DefinitionEnabled = enabled, InputEnabled = enabled };
    }
}
