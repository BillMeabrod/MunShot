using KSP.Game;
using KSP.Sim;
using KSP.Sim.impl;
using UnityEngine;

namespace MunShot.Cameras
{
    /// <summary>The <see cref="OrbitTarget"/>s that come from the game itself, rather than from photo mode.</summary>
    public static class OrbitTargets
    {
        /// <summary>
        /// <paramref name="vessel"/>, circled around its centre of mass; null for no vessel. The target is gone when the
        /// vessel is (destroyed, unloaded).
        /// </summary>
        public static OrbitTarget Vessel(VesselComponent vessel)
        {
            if (vessel == null) return null;
            // The name is looked up each time it's shown (so it follows a language change), and remembered for after
            // the vessel's gone, when the game can no longer say it
            string lastName = vessel.RevealDisplayName();
            string Name()
            {
                if (vessel.SimulationObject != null && !vessel.SimulationObject.IsDestroyedOrBeingDestroyed) lastName = vessel.RevealDisplayName();
                return Localization.Format("MunShot/UI/ActiveVessel", "{0} (active vessel)", lastName);
            }

            return new OrbitTarget(Name, () =>
            {
                IPhysicsSpaceProvider physicsSpace = GameManager.Instance?.Game?.UniverseView?.PhysicsSpace;
                if (physicsSpace == null || vessel.SimulationObject == null || vessel.SimulationObject.IsDestroyedOrBeingDestroyed)
                    return null;
                return (Vector3)physicsSpace.PositionToPhysics(vessel.CenterOfMass);
            });
        }
    }
}
