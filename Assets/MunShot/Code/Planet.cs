using KSP.Game;
using KSP.Sim;
using KSP.Sim.impl;
using UnityEngine;

namespace MunShot
{
    /// <summary>Where "up" is near the planet the active vessel is at.</summary>
    internal static class Planet
    {
        /// <summary>
        /// "Up" at a place in physics space, the way the game works it out for kerbals on EVA (KerbalBehavior's horizon
        /// up): away from the centre of the body the active vessel orbits. <paramref name="fallback"/> when the game
        /// can't tell (no active vessel, no physics space).
        /// </summary>
        public static Vector3 UpAt(Vector3 physicsPosition, Vector3 fallback)
        {
            GameInstance game = GameManager.Instance?.Game;
            CelestialBodyComponent body = game?.ViewController?.GetActiveSimVessel()?.mainBody;
            IPhysicsSpaceProvider physicsSpace = game?.UniverseView?.PhysicsSpace;
            if (body == null || physicsSpace == null) return fallback;

            // Named as the game names them: Delta's parameters are declared end first
            Vector away = MathDP.Normalize(MathDP.Delta(start: new Position(body.transform.celestialFrame, Vector3d.zero),
                                                        end: physicsSpace.PhysicsToPosition(physicsPosition)));
            Vector3 up = physicsSpace.VectorToPhysics(away);
            return up.sqrMagnitude > 0.5f ? up.normalized : fallback;
        }
    }
}
