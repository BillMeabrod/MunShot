using KSP.Game;
using KSP.Rendering.Planets;
using KSP.Sim;
using KSP.Sim.impl;
using KSP.VolumeCloud;
using UnityEngine;

namespace MunShot
{
    /// <summary>Where "up" is near the planet the active vessel is at, and where the planet is, as it is and as it's drawn.</summary>
    internal static class Planet
    {
        // What the game's cloud layers are drawn with (KSP2/Environment/Clouds/ScaledCloudsShader), in its faraway view
        private const string CloudShader = "/Clouds/";

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

        /// <summary>
        /// The body the active vessel orbits: its centre in physics space, its radius at sea level and how high its
        /// atmosphere reaches above that (0 when it has none), in metres. False when the game can't tell.
        /// </summary>
        public static bool Find(out Vector3 centre, out double radius, out double atmosphere)
        {
            centre = Vector3.zero;
            radius = 0;
            atmosphere = 0;
            GameInstance game = GameManager.Instance?.Game;
            CelestialBodyComponent body = game?.ViewController?.GetActiveSimVessel()?.mainBody;
            IPhysicsSpaceProvider physicsSpace = game?.UniverseView?.PhysicsSpace;
            if (body == null || physicsSpace == null) return false;
            centre = physicsSpace.PositionToPhysics(new Position(body.transform.celestialFrame, Vector3d.zero));
            radius = body.radius;
            atmosphere = body.hasAtmosphere ? body.atmosphereDepth : 0;
            return true;
        }

        /// <summary>
        /// How much the game's volume clouds show for a camera (0 to 1): none when it draws none for it, and less as they
        /// give way to the faraway view's clouds on the way up (as Redux's exhaust takes them).
        /// </summary>
        public static float CloudsShown(Camera camera)
        {
            if (camera == null || !camera.TryGetComponent(out VolumeCloudRenderer clouds) || !clouds.isActiveAndEnabled) return 0f;
            GameInstance game = GameManager.Instance?.Game;
            CelestialBodyComponent body = game?.ViewController?.GetActiveSimVessel()?.mainBody;
            if (body == null || game.CelestialBodies == null
                || !game.CelestialBodies.TryGetCelestialBodyBehavior(body.bodyName, out CelestialBodyBehavior behavior))
                return 0f;
            PQSRenderer surface = behavior != null && behavior.PqsController != null ? behavior.PqsController.PQSRenderer : null;
            return surface != null ? 1f - Mathf.Clamp01(surface.GetTransition(scaled: false)) : 0f;
        }

        /// <summary>
        /// The body the active vessel orbits as Redux's faraway view draws it: the camera drawing it, and its
        /// scaled-down copy's centre and radius in that view's world (its edge as seen: the top of its highest cloud
        /// layer that's shown, or its surface), with how many metres one of its units is; and the copy's own surface
        /// (its renderer) and whether it has a sea. False when there's none.
        /// </summary>
        public static bool FindDrawn(out Camera camera, out Vector3 centre, out float radius, out float metres, out Renderer surface, out bool sea)
        {
            surface = null;
            sea = false;
            centre = Vector3.zero;
            radius = 0f;
            metres = 0f;
            GameInstance game = GameManager.Instance?.Game;
            CelestialBodyComponent body = game?.ViewController?.GetActiveSimVessel()?.mainBody;
            camera = game?.GraphicsManager?.GetCurrentScaledUnityCamera();
            if (body == null || camera == null) return false;
            foreach (ScaledPlanetaryBodyView view in ScaledPlanetaryBodyView.Active)
                if (view != null && view.Model == body && view.ScaledRadius > 0f)
                {
                    surface = view.Renderer;
                    sea = body.hasOcean;
                    centre = view.transform.position;
                    radius = view.ScaledRadius;
                    metres = (float)(body.radius / view.ScaledRadius);
                    foreach (MeshFilter layer in view.GetComponentsInChildren<MeshFilter>())
                    {
                        Renderer shown = layer.GetComponent<Renderer>();
                        Shader shader = shown != null && shown.enabled ? shown.sharedMaterial?.shader : null;
                        if (layer.sharedMesh == null || shader == null || !shader.name.Contains(CloudShader)) continue;
                        Vector3 extents = Vector3.Scale(layer.sharedMesh.bounds.extents, layer.transform.lossyScale);
                        radius = Mathf.Max(radius, Mathf.Max(extents.x, Mathf.Max(extents.y, extents.z)));
                    }
                    return true;
                }
            return false;
        }
    }
}
