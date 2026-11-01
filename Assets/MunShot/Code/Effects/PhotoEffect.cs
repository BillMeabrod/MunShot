using System;
using System.Collections.Generic;
using MunShot.Posing;
using SpaceWarp2.API;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MunShot.Effects
{
    /// <summary>
    /// One of photo mode's effects: a copy of one of the game's particle effects, held still at a chosen moment, with a
    /// marker for placing it (<see cref="IPlaceable"/>), as a light has.
    ///
    /// Photo mode pauses the game, so the particles don't play by themselves: the effect is worked out from its start up
    /// to <see cref="Moment"/> (ParticleSystem.Simulate, in small steps, from the same random seed each time, so the
    /// same moment always looks the same), again whenever it's moved, turned, resized or the moment changes. How long
    /// it's worth holding is measured once when it's made, by playing it through: the Moment slider runs to when it's
    /// mostly gone, and starts at the moment picked for its kind (<see cref="PhotoEffectSpec.StartMoment"/>). The copy
    /// is only the particles and lights: the game's scripts for it (driven by the part that exploded), its sounds,
    /// colliders and bodies are taken out before it wakes up.
    ///
    /// The marker is the effect's root, which the handles move and outline; the particles are beside it and follow it,
    /// so they're never outlined, and their size doesn't change the marker's.
    /// </summary>
    public sealed class PhotoEffect : IPlaceable
    {
        public const float MinSize = 0.1f;
        public const float MaxSize = 5f;

        // The step the effect is worked out in: small enough for bursts and fast particles to come out as they play
        private const float SimulationStep = 1f / 30f;

        // An effect counts as over once this share of the most it ever shows is left: what's left is too thin to see.
        // How much shows is each particle's size squared times its opacity, so a few big fireballs count for more than
        // many small sparks
        private const float FadedShare = 0.1f;

        // The longest an effect is played through to measure it; one that keeps going (a loop) is held up to this
        private const float LongestMeasured = 10f;

        // The Moment slider's end is rounded up to this, so it reads as a round number
        private const float MomentRounding = 0.1f;

        private static readonly Color MarkerColor = new Color(1f, 0.55f, 0.1f);

        private readonly GameObject _root;
        private readonly GameObject _look;
        private readonly PlaceableMarker _marker;
        private readonly ParticleSystem[] _systems;

        // The systems at the top of each tree, which take their sub-emitters and children with them when worked out
        private readonly List<ParticleSystem> _tops = new List<ParticleSystem>();

        private float _moment;
        private float _size = 1f;
        private bool _stale = true;
        private bool _gone;

        public PhotoEffectKind Kind { get; }

        /// <summary>Which of its kind this is, shown as "Explosion 1": kept while it's there, so names don't shift.</summary>
        public int Number { get; }

        public PhotoEffectSpec Spec { get; }

        /// <summary>How far in the Moment slider reaches, in seconds: when the effect is mostly gone, as measured.</summary>
        public float MaxMoment { get; }

        /// <summary>Where it is and which way it faces; null once it's taken away.</summary>
        public Transform Root => _gone ? null : _root.transform;

        public bool IsPlaceable => !_gone;

        public Vector3 Centre => _root.transform.position;

        // Turning an effect hardly changes how it looks (most spread out all round), so it's only moved
        public bool CanTurn => false;

        /// <summary>How far into the effect it's held, in seconds, up to <see cref="MaxMoment"/>.</summary>
        public float Moment
        {
            get => _moment;
            set
            {
                _moment = Mathf.Clamp(value, 0f, MaxMoment);
                _stale = true;
            }
        }

        /// <summary>How big it is against the game's own size: <see cref="MinSize"/> to <see cref="MaxSize"/>.</summary>
        public float Size
        {
            get => _size;
            set
            {
                _size = Mathf.Clamp(value, MinSize, MaxSize);
                _stale = true;
            }
        }

        /// <param name="prefab">The game's effect, loaded; copied, never changed.</param>
        /// <param name="seed">The random seed its particles are worked out with, so it looks the same each time.</param>
        public PhotoEffect(PhotoEffectKind kind, int number, GameObject prefab, Transform parent, Vector3 position, Quaternion rotation,
                           Shader markerShader, uint seed, Action<string> logInfo)
        {
            Kind = kind;
            Number = number;
            Spec = PhotoEffectSpec.Of(kind);

            _root = new GameObject($"MunShot effect {kind} {number}");
            _root.transform.SetParent(parent, false);
            _root.transform.SetPositionAndRotation(position, rotation);
            _marker = new PlaceableMarker(_root.transform, _root.name, markerShader, MarkerColor);

            // Copied under an inactive holder, so none of the game's scripts on it wake up before they're taken out
            _look = new GameObject(_root.name + " particles");
            _look.SetActive(false);
            _look.transform.SetParent(parent, false);
            GameObject copy = Object.Instantiate(prefab, _look.transform, false);
            int removed = Strip(copy);
            int moved = OntoFlightLayer(copy);
            _systems = copy.GetComponentsInChildren<ParticleSystem>(true);
            for (int i = 0; i < _systems.Length; i++) Hold(_systems[i], seed + (uint)i);
            foreach (ParticleSystem system in _systems)
            {
                Transform above = system.transform.parent;
                if (above == null || above.GetComponentInParent<ParticleSystem>() == null) _tops.Add(system);
            }
            _look.SetActive(true);

            Measure(out float end, out float most);
            MaxMoment = Mathf.Max(MomentRounding, Mathf.Ceil(end / MomentRounding) * MomentRounding);
            _moment = Mathf.Min(Spec.StartMoment, MaxMoment);
            Follow();
            logInfo($"Photo mode: effect '{prefab.name}' copied with {_systems.Length} particle systems; {removed} game components taken out of it"
                    + (moved > 0 ? $", {moved} objects moved from the VAB's layer to the flight camera's" : "")
                    + $". Measured: showing at most {most:0.#}, mostly gone by {end:0.00}s; held at {_moment:0.00}s to start.");
        }

        /// <summary>Called every frame: follows the marker, and works the effect out again if anything changed.</summary>
        public void Tick()
        {
            if (_gone) return;
            Transform marker = _root.transform, look = _look.transform;
            if (marker.position != look.position || marker.rotation != look.rotation) Follow();
            if (!_stale) return;
            _stale = false;
            Simulate();
        }

        public void ShowMarker(bool shown) => _marker.Shown = shown;

        /// <summary>Whether <paramref name="ray"/> points at the marker, and where.</summary>
        public bool TryPick(Ray ray, out Vector3 point)
        {
            point = default;
            return !_gone && _marker.TryPick(ray, out point);
        }

        public void Destroy()
        {
            _gone = true;
            Object.Destroy(_root);
            Object.Destroy(_look);
            _marker.Destroy();
        }

        private void Follow()
        {
            _look.transform.SetPositionAndRotation(_root.transform.position, _root.transform.rotation);
            _stale = true;
        }

        // From the start up to the moment, in small steps
        private void Simulate()
        {
            _look.transform.localScale = Vector3.one * _size;
            float remaining = _moment;
            bool first = true;
            do
            {
                float step = Mathf.Min(SimulationStep, remaining);
                Step(step, first);
                first = false;
                remaining -= step;
            } while (remaining > 0f);
        }

        // Plays it through once (it's worked out again for showing afterwards): the most it shows, and when it's faded
        // to a little of that. One that never fades (a loop) runs to the longest measured
        private void Measure(out float end, out float most)
        {
            end = LongestMeasured;
            most = 0f;
            bool first = true;
            var particles = new ParticleSystem.Particle[0];
            for (float time = SimulationStep; time <= LongestMeasured; time += SimulationStep)
            {
                Step(SimulationStep, first);
                first = false;
                float showing = 0f;
                foreach (ParticleSystem system in _systems)
                {
                    if (particles.Length < system.particleCount) particles = new ParticleSystem.Particle[system.particleCount];
                    int count = system.GetParticles(particles);
                    for (int i = 0; i < count; i++)
                    {
                        float size = particles[i].GetCurrentSize(system);
                        showing += size * size * particles[i].GetCurrentColor(system).a / 255f;
                    }
                }
                if (showing > most) most = showing;
                else if (most > 0f && showing <= most * FadedShare)
                {
                    end = time;
                    return;
                }
            }
        }

        private void Step(float seconds, bool restart)
        {
            foreach (ParticleSystem system in _tops) system.Simulate(seconds, true, restart, false);
        }

        // Only what draws is kept: the game's scripts (and its sounds, which are scripts too), colliders and bodies go
        private static int Strip(GameObject copy)
        {
            int removed = 0;
            foreach (MonoBehaviour behaviour in copy.GetComponentsInChildren<MonoBehaviour>(true))
            {
                Object.DestroyImmediate(behaviour);
                removed++;
            }
            foreach (Collider collider in copy.GetComponentsInChildren<Collider>(true))
            {
                Object.DestroyImmediate(collider);
                removed++;
            }
            foreach (Rigidbody body in copy.GetComponentsInChildren<Rigidbody>(true))
            {
                Object.DestroyImmediate(body);
                removed++;
            }
            foreach (AudioSource audio in copy.GetComponentsInChildren<AudioSource>(true))
            {
                Object.DestroyImmediate(audio);
                removed++;
            }
            return removed;
        }

        // Some of the game's effects are made for the VAB (the sparks are on its OAB.Scenery layer), which the flight
        // camera doesn't draw: those parts go on the default layer, where the game's flight effects (the explosions)
        // are. Returns how many were moved
        private static int OntoFlightLayer(GameObject copy)
        {
            int moved = 0;
            foreach (Transform part in copy.GetComponentsInChildren<Transform>(true))
            {
                if (part.gameObject.layer != Layers.LayerOabScenery) continue;
                part.gameObject.layer = Layers.LayerDefault;
                moved++;
            }
            return moved;
        }

        // Stopped, cleared, and set to be worked out by hand: never playing by itself, the same seed every time, sized
        // with the effect, and at full strength
        private static void Hold(ParticleSystem system, uint seed)
        {
            system.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear);
            ParticleSystem.MainModule main = system.main;
            main.playOnAwake = false;
            main.scalingMode = ParticleSystemScalingMode.Hierarchy;
            system.useAutoRandomSeed = false;
            system.randomSeed = seed;
            FullBursts(system);
        }

        // A burst of "0 to N" particles is the game's to scale by how big the event was (its scripts, taken out, set
        // it from the part); left to chance, it can come out thin or empty. Each burst gives its most instead
        private static void FullBursts(ParticleSystem system)
        {
            ParticleSystem.EmissionModule emission = system.emission;
            for (int i = 0; i < emission.burstCount; i++)
            {
                ParticleSystem.Burst burst = emission.GetBurst(i);
                if (burst.count.mode != ParticleSystemCurveMode.TwoConstants) continue;
                burst.count = new ParticleSystem.MinMaxCurve(burst.count.constantMax);
                emission.SetBurst(i, burst);
            }
        }
    }
}
