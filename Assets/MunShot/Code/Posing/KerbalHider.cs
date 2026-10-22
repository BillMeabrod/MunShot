using System.Collections.Generic;
using UnityEngine;

namespace MunShot.Posing
{
    /// <summary>
    /// Hides a kerbal's model while their photo double stands in, and gives it back exactly as it was. Renderers are
    /// hidden with <see cref="Renderer.forceRenderingOff"/>, as Redux's first-person camera hides the helmet: it also
    /// drops their shadows and holds even when the game switches renderers on and off (seated crew's level-of-detail
    /// system toggles <c>enabled</c>). Lights on the model (helmet and face lights) are switched off.
    /// </summary>
    public sealed class KerbalHider
    {
        private readonly Dictionary<Renderer, bool> _renderers = new Dictionary<Renderer, bool>();
        private readonly Dictionary<Light, bool> _lights = new Dictionary<Light, bool>();

        public GameObject Target { get; }

        public KerbalHider(GameObject target)
        {
            Target = target;
        }

        /// <summary>Hides everything under the target, remembering each renderer's and light's own state first.</summary>
        public void Hide()
        {
            if (Target == null) return;
            foreach (Renderer renderer in Target.GetComponentsInChildren<Renderer>(true))
            {
                if (!_renderers.ContainsKey(renderer)) _renderers[renderer] = renderer.forceRenderingOff;
                renderer.forceRenderingOff = true;
            }
            foreach (Light light in Target.GetComponentsInChildren<Light>(true))
            {
                if (!_lights.ContainsKey(light)) _lights[light] = light.enabled;
                light.enabled = false;
            }
        }

        /// <summary>Puts every renderer and light back as it was before <see cref="Hide"/>.</summary>
        public void Restore()
        {
            foreach (KeyValuePair<Renderer, bool> entry in _renderers)
                if (entry.Key != null) entry.Key.forceRenderingOff = entry.Value;
            foreach (KeyValuePair<Light, bool> entry in _lights)
                if (entry.Key != null) entry.Key.enabled = entry.Value;
            _renderers.Clear();
            _lights.Clear();
        }
    }
}
