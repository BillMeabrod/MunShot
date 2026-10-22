using System.Collections.Generic;
using KSP.Game;
using KSP.Sim.impl;
using UnityEngine;

namespace MunShot.Posing
{
    /// <summary>A kerbal of the current mission that can be brought into photo mode.</summary>
    public sealed class KerbalCandidate
    {
        /// <summary>The kerbal's model in the scene (on EVA, or in their seat).</summary>
        public Kerbal3DModel Model { get; }
        public KerbalInfo Info { get; }
        public string Name { get; }

        /// <summary>"on EVA", or the vessel they're aboard.</summary>
        public string Whereabouts { get; }

        public KerbalCandidate(Kerbal3DModel model, KerbalInfo info, string name, string whereabouts)
        {
            Model = model;
            Info = info;
            Name = name;
            Whereabouts = whereabouts;
        }

        public override string ToString() => $"{Name} ({Whereabouts})";
    }

    /// <summary>
    /// Finds the kerbals that can be brought into photo mode: every built kerbal model that belongs to a part (on EVA
    /// or seated in a loaded vessel), whose vessel is in the camera's view. Models without a part, like the game's
    /// portrait booth or MunShot's own photo doubles, are left out.
    /// </summary>
    public static class KerbalCandidates
    {
        public static List<KerbalCandidate> FindOnScreen(Camera camera)
        {
            var found = new List<KerbalCandidate>();
            if (camera == null) return found;

            Plane[] frustum = GeometryUtility.CalculateFrustumPlanes(camera);
            var visibleVessels = new Dictionary<VesselBehavior, bool>();
            foreach (Kerbal3DModel model in Object.FindObjectsByType<Kerbal3DModel>(FindObjectsInactive.Exclude))
            {
                if (!model.CharacterExists || model.ThisKerbalInfo == null || model.partOwner == null) continue;

                VesselBehavior vessel = model.GetComponentInParent<VesselBehavior>();
                Transform vesselRoot = vessel != null ? vessel.transform : model.partOwner.transform;
                if (vessel == null || !visibleVessels.TryGetValue(vessel, out bool visible))
                {
                    visible = IsInView(vesselRoot, frustum);
                    if (vessel != null) visibleVessels[vessel] = visible;
                }
                if (!visible) continue;

                string whereabouts = model.IsEVAKerbal ? "on EVA" : $"aboard {vessel?.Model?.Name ?? model.partOwner.name}";
                found.Add(new KerbalCandidate(model, model.ThisKerbalInfo, model.ThisKerbalInfo.Attributes.GetFullName(), whereabouts));
            }

            found.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            return found;
        }

        // A vessel is in view when the box around its renderers touches the camera's view
        private static bool IsInView(Transform root, Plane[] frustum)
        {
            bool any = false;
            var bounds = new Bounds();
            foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>())
            {
                if (!renderer.enabled || renderer is ParticleSystemRenderer) continue;
                if (!any)
                {
                    bounds = renderer.bounds;
                    any = true;
                }
                else
                {
                    bounds.Encapsulate(renderer.bounds);
                }
            }
            return any && GeometryUtility.TestPlanesAABB(frustum, bounds);
        }
    }
}
