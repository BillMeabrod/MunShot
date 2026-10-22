using System;
using System.Collections.Generic;
using KSP.Game;
using KSP.Input;
using UnityEngine.InputSystem;

namespace MunShot.Capture
{
    /// <summary>
    /// Takes flight and EVA controls away from the game while photo mode is open, but leaves the camera alone. It's a
    /// lock in the game's own form (an <see cref="InputLockDefinition"/> given to the input manager), built from the
    /// game's flight action map: every flight action is switched off except the camera's (zoom, pan, FOV, fine
    /// movement, gamepad look, reset, camera mode, free camera), and EVA controls are switched off entirely. That
    /// includes the "interact" clicks on parts, so clicking a double doesn't also select a part or vessel behind it.
    /// The game's own <see cref="InputLocks.FlightInputDisabled"/> isn't used because it switches off the whole flight
    /// input set, camera zoom with it.
    ///
    /// Each action's own state, and the flight and EVA sets' states, are remembered when the lock goes on, and put back
    /// exactly as they were when it comes off. (The game's FlightInputEnabled only switches whole sets back on, which
    /// leaves individually switched-off actions off.)
    /// </summary>
    public sealed class FlightInputLock
    {
        // Flight actions photo mode leaves on: the camera's. Moving the camera to frame a shot is the point.
        private static readonly HashSet<string> CameraActions = new HashSet<string>(StringComparer.Ordinal)
        {
            "CameraZoom", "toggleCameraZoom", "cameraPan", "CameraFOV", "CameraFineMovement", "CameraPitchGamepad",
            "CameraYawGamepad", "CameraReset", "CycleCameraMode", "ToggleFreeCamera"
        };

        private static readonly string FlightSet = nameof(FlightInputDefinition);
        private static readonly string EvaSet = nameof(EVAInputDefinition);

        private readonly Action<string> _logInfo;
        private readonly Action<string> _logWarning;
        private InputLockDefinition _restore;

        public bool IsEngaged { get; private set; }

        public FlightInputLock(Action<string> logInfo, Action<string> logWarning)
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

            var locking = new InputLockDefinition();
            _restore = new InputLockDefinition();
            int locked = 0, kept = 0;
            foreach (InputAction action in game.Input.Flight.Get().actions)
            {
                if (!flight.TryGetAction(action.name, out ToggleableInputAction binder)) continue;
                bool camera = CameraActions.Contains(action.name);
                if (camera) kept++;
                else locked++;
                locking.InputLocks.Add(ActionLock(action.name, flight.Enabled, camera && binder.Enabled));
                _restore.InputLocks.Add(ActionLock(action.name, flight.Enabled, binder.Enabled));
            }
            if (eva != null)
            {
                locking.InputLocks.Add(SetLock(EvaSet, false));
                _restore.InputLocks.Add(SetLock(EvaSet, eva.Enabled));
            }

            inputManager.SetInputLock(locking);
            IsEngaged = true;
            _logInfo($"Photo mode: flight and EVA controls locked ({locked} flight actions off, {kept} camera actions left on).");
        }

        /// <summary>
        /// Puts every flight action and the EVA controls back as they were. Safe to call when not engaged, and while
        /// the game is being torn down.
        /// </summary>
        public void Release()
        {
            if (!IsEngaged) return;
            IsEngaged = false;
            InputLockDefinition restore = _restore;
            _restore = null;
            GameManager.Instance?.Game?.InputManager?.SetInputLock(restore);
            _logInfo("Photo mode: flight and EVA controls back as they were.");
        }

        private static InputLockDefinition.InputLock ActionLock(string action, bool setEnabled, bool actionEnabled) =>
            new InputLockDefinition.InputLock
            {
                DefinitionID = FlightSet,
                InputID = action,
                DefinitionEnabled = setEnabled,
                InputEnabled = actionEnabled
            };

        private static InputLockDefinition.InputLock SetLock(string set, bool enabled) =>
            new InputLockDefinition.InputLock { DefinitionID = set, DefinitionEnabled = enabled, InputEnabled = enabled };
    }
}
