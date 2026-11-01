using System;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MunShot.Posing
{
    /// <summary>
    /// The small marker photo mode shows where one of its own things is (a light, an effect): a ball in one colour, plus
    /// any parts its owner adds, under the thing's root. It's only for placing: clicked to select the thing (picked by
    /// where the mouse points, it has no collider), and hidden for the photo and when the UI is hidden.
    /// </summary>
    public sealed class PlaceableMarker
    {
        /// <summary>The ball's size, in metres.</summary>
        public const float Size = 0.12f;

        // How near the mouse has to point to the ball to pick it: its size up close, and a share of the distance
        // further off, so a far marker stays as easy to click as a near one
        private const float PickShareOfDistance = 0.02f;

        // The unlit shader markers are drawn with, found once
        private static Shader _shader;

        private readonly Transform _root;
        private readonly string _name;
        private readonly Material _material;
        private readonly GameObject _ball;

        /// <summary>A marker at <paramref name="root"/>, named after it, in <paramref name="color"/>.</summary>
        public PlaceableMarker(Transform root, string name, Shader shader, Color color)
        {
            _root = root ?? throw new ArgumentNullException(nameof(root));
            _name = name;
            _material = new Material(shader ?? throw new ArgumentNullException(nameof(shader))) { name = name + " marker", color = color };
            _ball = AddPart(PrimitiveType.Sphere, Vector3.zero, Vector3.one * Size);
        }

        /// <summary>The marker's colour.</summary>
        public Color Color
        {
            get => _material.color;
            set => _material.color = value;
        }

        /// <summary>Whether the marker shows (and can be picked).</summary>
        public bool Shown
        {
            get => _ball.activeSelf;
            set => _ball.SetActive(value);
        }

        /// <summary>
        /// An unlit shader for markers, as the fallback handles use; the first found is kept. Null, with a warning, if
        /// the game has none of them.
        /// </summary>
        public static Shader FindShader(Action<string> logWarning)
        {
            if (_shader != null) return _shader;
            foreach (string name in new[] { "Hidden/Internal-Colored", "Sprites/Default", "Unlit/Color" })
                if ((_shader = Shader.Find(name)) != null) return _shader;
            logWarning("Photo mode: none of the unlit shaders for placing markers is in the game, so nothing that needs one can be added.");
            return null;
        }

        /// <summary>
        /// Adds a part to the marker, in its colour: no collider (it has nothing to bump into), no shadow. Its owner
        /// shows and hides it along with <see cref="Shown"/>.
        /// </summary>
        public GameObject AddPart(PrimitiveType shape, Vector3 localPosition, Vector3 localScale)
        {
            GameObject part = GameObject.CreatePrimitive(shape);
            part.name = _name + " marker";
            Object.Destroy(part.GetComponent<Collider>());
            part.transform.SetParent(_root, false);
            part.transform.localPosition = localPosition;
            part.transform.localScale = localScale;
            var renderer = part.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = _material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            return part;
        }

        /// <summary>
        /// Whether <paramref name="ray"/> passes near enough the ball to pick it, and where it does (nearest the ball
        /// along the ray).
        /// </summary>
        public bool TryPick(Ray ray, out Vector3 point)
        {
            point = default;
            if (!_ball.activeInHierarchy) return false;
            Vector3 centre = _root.position;
            float along = Vector3.Dot(centre - ray.origin, ray.direction);
            if (along <= 0f) return false;
            point = ray.origin + ray.direction * along;
            float reach = Mathf.Max(Size * 0.5f, along * PickShareOfDistance);
            return (point - centre).sqrMagnitude <= reach * reach;
        }

        /// <summary>Lets go of the material; the parts go with the root.</summary>
        public void Destroy() => Object.Destroy(_material);
    }
}
