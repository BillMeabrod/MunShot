using KSP.Game;
using KSP.Sim.impl;
using UnityEngine;

namespace MunShot.Posing
{
    /// <summary>
    /// Finds the kerbal on EVA that the player is controlling, the way Redux's first-person camera does: the active
    /// vessel, which is the kerbal itself while on EVA, then its loaded behaviour and the EVA animation manager's
    /// Animator.
    /// </summary>
    public static class EvaKerbalLocator
    {
        /// <summary>The active EVA kerbal's name and Animator.</summary>
        public readonly struct Result
        {
            public string KerbalName { get; }
            public Animator Animator { get; }

            public Result(string kerbalName, Animator animator)
            {
                KerbalName = kerbalName;
                Animator = animator;
            }
        }

        /// <summary>
        /// Finds the active EVA kerbal. When there isn't one, <paramref name="reason"/> says which step came up
        /// empty, for the pose lab to show and the log to record.
        /// </summary>
        public static bool TryFind(GameInstance game, out Result result, out string reason)
        {
            result = default;

            ViewController viewController = game?.ViewController;
            if (viewController == null)
            {
                reason = "No game view is loaded (not in flight).";
                return false;
            }

            VesselComponent vessel = viewController.GetActiveSimVessel();
            if (vessel == null)
            {
                reason = "There is no active vessel.";
                return false;
            }

            if (!vessel.IsKerbalEVA)
            {
                reason = $"The active vessel '{vessel.Name}' is not a kerbal on EVA.";
                return false;
            }

            KerbalComponent kerbal = vessel.SimulationObject?.Kerbal;
            if (kerbal == null)
            {
                reason = $"The EVA vessel '{vessel.Name}' has no kerbal component.";
                return false;
            }

            KerbalBehavior behavior = viewController.GetBehaviorIfLoaded(kerbal);
            if (behavior == null)
            {
                reason = $"The kerbal '{vessel.Name}' is not loaded.";
                return false;
            }

            Animator animator = behavior.EVAAnimationManager != null ? behavior.EVAAnimationManager.Animator : null;
            if (animator == null)
            {
                reason = $"The kerbal '{vessel.Name}' has no EVA animation manager or Animator yet.";
                return false;
            }

            result = new Result(vessel.Name, animator);
            reason = null;
            return true;
        }
    }
}
