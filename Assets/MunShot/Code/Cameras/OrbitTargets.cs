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
            return new OrbitTarget($"{vessel.RevealDisplayName()} (active vessel)", () =>
            {
                IPhysicsSpaceProvider physicsSpace = GameManager.Instance?.Game?.UniverseView?.PhysicsSpace;
                if (physicsSpace == null || vessel.SimulationObject == null || vessel.SimulationObject.IsDestroyedOrBeingDestroyed)
                    return null;
                return (Vector3)physicsSpace.PositionToPhysics(vessel.CenterOfMass);
            });
        }
    }
}
