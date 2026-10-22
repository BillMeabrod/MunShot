using System;
using KSP.Game;
using KSP.Input;
using KSP.Sim.impl;

namespace MunShot.Capture
{
    /// <summary>
    /// Freezes the game while photo mode is open, and keeps it frozen until photo mode closes. Pauses the way Redux's
    /// own game-lifecycle API does (the view controller's pause and the time warp's pause together), which sets time
    /// scale to 0: physics, vessels and the game's animation stop, while MunShot's poses and doubles, on unscaled time,
    /// keep working.
    ///
    /// Nothing may unpause it while engaged: the time-warp keys are locked with the game's own input lock, and every
    /// frame anything else that unpaused the game (the pause menu's resume, the flight report, mission actions, other
    /// mods) is undone and logged. Released, it unpauses only if the game wasn't already paused when photo mode opened,
    /// and the time-warp keys go back to how they were (still locked, if something else had locked them).
    /// </summary>
    public sealed class GamePause
    {
        private readonly Action<string> _logInfo;
        private readonly Action<string> _logWarning;
        private bool _wasPaused;
        private InputLockDefinition _restoreTimeWarp;

        public bool IsEngaged { get; private set; }

        public GamePause(Action<string> logInfo, Action<string> logWarning)
        {
            _logInfo = logInfo ?? throw new ArgumentNullException(nameof(logInfo));
            _logWarning = logWarning ?? throw new ArgumentNullException(nameof(logWarning));
        }

        /// <summary>Pauses the game and locks the time-warp keys. Safe to call when already engaged.</summary>
        public void Engage()
        {
            if (IsEngaged) return;
            GameInstance game = GameManager.Instance?.Game;
            ViewController viewController = game?.ViewController;
            if (viewController == null)
            {
                _logWarning("Photo mode: can't pause, there's no game view (not in flight).");
                return;
            }

            _wasPaused = viewController.IsPaused;
            SetPaused(viewController, true);
            InputManager inputManager = game.InputManager;
            if (inputManager != null)
            {
                _restoreTimeWarp = CurrentState(inputManager, InputLocks.TimeWarpDisabled);
                inputManager.SetInputLock(InputLocks.TimeWarpDisabled);
            }
            IsEngaged = true;
            _logInfo($"Photo mode: game paused{(_wasPaused ? " (it already was)" : string.Empty)}; time warp locked.");
        }

        /// <summary>Called every frame: pauses the game again if anything unpaused it while engaged.</summary>
        public void Tick()
        {
            if (!IsEngaged) return;
            ViewController viewController = GameManager.Instance?.Game?.ViewController;
            if (viewController == null || viewController.IsPaused) return;

            SetPaused(viewController, true);
            _logWarning("Photo mode: something unpaused the game while photo mode is open; paused it again.");
        }

        /// <summary>
        /// Puts the time-warp keys back as they were and unpauses, unless the game was already paused before. Safe to
        /// call when not engaged, and when the game is already being torn down.
        /// </summary>
        public void Release()
        {
            if (!IsEngaged) return;
            IsEngaged = false;

            GameInstance game = GameManager.Instance?.Game;
            if (_restoreTimeWarp != null) game?.InputManager?.SetInputLock(_restoreTimeWarp);
            _restoreTimeWarp = null;
            ViewController viewController = game?.ViewController;
            if (viewController != null && !_wasPaused) SetPaused(viewController, false);
            _logInfo($"Photo mode: time-warp keys back as they were; game {(_wasPaused ? "left paused, as it was" : "unpaused")}.");
        }

        // The lock that puts back how each action the given lock touches (and its set) is right now
        private static InputLockDefinition CurrentState(InputManager inputManager, InputLockDefinition lockTo)
        {
            var current = new InputLockDefinition();
            foreach (InputLockDefinition.InputLock inputLock in lockTo.InputLocks)
            {
                if (inputManager.InputDefinitions == null
                    || !inputManager.InputDefinitions.TryGetValue(inputLock.DefinitionID, out InputDefinition set)
                    || !set.TryGetAction(inputLock.InputID, out ToggleableInputAction action))
                    continue;
                current.InputLocks.Add(new InputLockDefinition.InputLock
                {
                    DefinitionID = inputLock.DefinitionID,
                    InputID = inputLock.InputID,
                    DefinitionEnabled = set.Enabled,
                    InputEnabled = action.Enabled
                });
            }
            return current;
        }

        private static void SetPaused(ViewController viewController, bool paused)
        {
            viewController.SetPause(paused);
            viewController.TimeWarp?.SetIsPaused(paused);
        }
    }
}
