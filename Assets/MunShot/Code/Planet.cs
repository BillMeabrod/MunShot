using System.Collections.Generic;
using KSP.Game;
using KSP.Rendering.Planets;
using KSP.Sim;
using KSP.Sim.impl;
using KSP.VolumeCloud;
using UnityEngine;

namespace MunShot
{
    /// <summary>
    /// Where "up" is near the planet the active vessel is at, and where the planet is, as it is and as it's drawn. A
    /// caller asking several things in one frame looks the planet up once (<see cref="Orbited"/>) and passes it in.
    /// </summary>
    internal static class Planet
    {
        // What the game's cloud layers are drawn with (KSP2/Environment/Clouds/ScaledCloudsShader), in its faraway view
        private const string CloudShader = "/Clouds/";

        // The faraway view's cloud layers of the planet last found (FindDrawn), looked for once per view
        private static ScaledPlanetaryBodyView _cloudLayersOf;
        private static readonly List<(MeshFilter layer, Renderer shown)> _cloudLayers = new List<(MeshFilter, Renderer)>();

        /// <summary>The body the active vessel orbits, or null when there's none.</summary>
        public static CelestialBodyComponent Orbited() => GameManager.Instance?.Game?.ViewController?.GetActiveSimVessel()?.mainBody;

        /// <summary>
        /// "Up" at a place in physics space, the way the game works it out for kerbals on EVA (KerbalBehavior's horizon
        /// up): away from the centre of the body the active vessel orbits. <paramref name="fallback"/> when the game
        /// can't tell (no active vessel, no physics space).
        /// </summary>
        public static Vector3 UpAt(Vector3 physicsPosition, Vector3 fallback) => UpAt(Orbited(), physicsPosition, fallback);

        /// <summary>"Up" at a place in physics space, away from the centre of <paramref name="body"/>.</summary>
        public static Vector3 UpAt(CelestialBodyComponent body, Vector3 physicsPosition, Vector3 fallback)
        {
            IPhysicsSpaceProvider physicsSpace = GameManager.Instance?.Game?.UniverseView?.PhysicsSpace;
            if (body == null || physicsSpace == null) return fallback;

            // Named as the game names them: Delta's parameters are declared end first
            Vector away = MathDP.Normalize(MathDP.Delta(start: new Position(body.transform.celestialFrame, Vector3d.zero),
                                                        end: physicsSpace.PhysicsToPosition(physicsPosition)));
            Vector3 up = physicsSpace.VectorToPhysics(away);
            return up.sqrMagnitude > 0.5f ? up.normalized : fallback;
        }

        /// <summary>
        /// <paramref name="body"/>'s centre in physics space, its radius at sea level and how high its atmosphere
        /// reaches above that (0 when it has none), in metres. False when the game can't tell.
        /// </summary>
        public static bool Find(CelestialBodyComponent body, out Vector3 centre, out double radius, out double atmosphere)
        {
            centre = Vector3.zero;
            radius = 0;
            atmosphere = 0;
            IPhysicsSpaceProvider physicsSpace = GameManager.Instance?.Game?.UniverseView?.PhysicsSpace;
            if (body == null || physicsSpace == null) return false;
            centre = physicsSpace.PositionToPhysics(new Position(body.transform.celestialFrame, Vector3d.zero));
            radius = body.radius;
            atmosphere = body.hasAtmosphere ? body.atmosphereDepth : 0;
            return true;
        }

        /// <summary>
        /// How much the game's volume clouds of <paramref name="body"/> show for a camera (0 to 1): none when it draws
        /// none for it (they're switched off, or the planet has no cloud layer), and less as they give way to the faraway
        /// view's clouds on the way up (as Redux's exhaust takes them).
        /// </summary>
        public static float CloudsShown(Camera camera, CelestialBodyComponent body)
        {
            if (camera == null || body == null || !VolumeCloudRenderer.IsEnabledClouds()
                || !camera.TryGetComponent(out VolumeCloudRenderer clouds) || !clouds.isActiveAndEnabled)
                return 0f;
            GameInstance game = GameManager.Instance?.Game;
            if (!HasCloudLayer(body.bodyName) || game?.CelestialBodies == null
                || !game.CelestialBodies.TryGetCelestialBodyBehavior(body.bodyName, out CelestialBodyBehavior behavior))
                return 0f;
            PQSRenderer surface = behavior != null && behavior.PqsController != null ? behavior.PqsController.PQSRenderer : null;
            return surface != null ? 1f - Mathf.Clamp01(surface.GetTransition(scaled: false)) : 0f;
        }

        // Whether the game draws volume clouds for a planet: whether it has a cloud layer to draw
        private static bool HasCloudLayer(string bodyName)
        {
            CloudRenderList layers = CloudRenderList.Instance();
            int count = layers?.GetCount() ?? 0;
            for (int i = 0; i < count; i++)
                if (layers.GetHelperByIndex(i)?.CelestialBodyName == bodyName)
                    return true;
            return false;
        }

        /// <summary>The camera of Redux's faraway view (the scaled-down copy of the universe), or null.</summary>
        public static Camera FarCamera() => GameManager.Instance?.Game?.GraphicsManager?.GetCurrentScaledUnityCamera();

        /// <summary>
        /// <paramref name="body"/> as Redux's faraway view draws it: its scaled-down copy's centre and radius in that
        /// view's world (its edge as seen: the top of its highest cloud layer that's shown, or its surface), with how
        /// many metres one of its units is; and the copy's own surface (its renderer) and whether it has a sea. False
        /// when there's none.
        /// </summary>
        public static bool FindDrawn(CelestialBodyComponent body, out Vector3 centre, out float radius, out float metres,
                                     out Renderer surface, out bool sea)
        {
            surface = null;
            sea = false;
            centre = Vector3.zero;
            radius = 0f;
            metres = 0f;
            if (body == null) return false;
            foreach (ScaledPlanetaryBodyView view in ScaledPlanetaryBodyView.Active)
                if (view != null && view.Model == body && view.ScaledRadius > 0f)
                {
                    surface = view.Renderer;
                    sea = body.hasOcean;
                    centre = view.transform.position;
                    radius = view.ScaledRadius;
                    metres = (float)(body.radius / view.ScaledRadius);
                    foreach ((MeshFilter layer, Renderer shown) in CloudLayersOf(view))
                    {
                        if (layer == null || shown == null || !shown.enabled || layer.sharedMesh == null) continue;
                        Vector3 extents = Vector3.Scale(layer.sharedMesh.bounds.extents, layer.transform.lossyScale);
                        radius = Mathf.Max(radius, Mathf.Max(extents.x, Mathf.Max(extents.y, extents.z)));
                    }
                    return true;
                }
            return false;
        }

        /// <summary>
        /// The planets and moons Redux's faraway view draws (not the stars): each one's renderer there, and whether it's
        /// <paramref name="orbited"/>. <paramref name="into"/> is cleared first.
        /// </summary>
        public static void FindDrawnBodies(CelestialBodyComponent orbited, List<(Renderer body, bool orbited)> into)
        {
            into.Clear();
            foreach (ScaledPlanetaryBodyView view in ScaledPlanetaryBodyView.Active)
                if (view != null && view.Renderer != null && view.Model != null && !view.Model.IsStar)
                    into.Add((view.Renderer, view.Model == orbited));
        }

        // A faraway planet's cloud layers (drawn with the game's cloud shader), found once per view
        private static List<(MeshFilter, Renderer)> CloudLayersOf(ScaledPlanetaryBodyView view)
        {
            if (view == _cloudLayersOf) return _cloudLayers;
            _cloudLayersOf = view;
            _cloudLayers.Clear();
            foreach (MeshFilter layer in view.GetComponentsInChildren<MeshFilter>(true))
            {
                Renderer shown = layer.GetComponent<Renderer>();
                // Unity's own null check: ?. would miss a destroyed renderer or material
                Material material = shown != null ? shown.sharedMaterial : null;
                Shader shader = material != null ? material.shader : null;
                if (shader != null && shader.name.Contains(CloudShader)) _cloudLayers.Add((layer, shown));
            }
            return _cloudLayers;
        }
    }
}
