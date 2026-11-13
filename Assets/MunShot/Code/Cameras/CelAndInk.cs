using System;
using System.Collections.Generic;
using KSP.Game;
using Redux.Rendering;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace MunShot.Cameras
{
    /// <summary>One of Cel shaded's and Ink's render hooks: the camera it draws for, and letting go of its materials.</summary>
    internal interface IFilterHook : ICameraRenderHook
    {
        Camera Camera { get; set; }
        void Destroy();
    }

    /// <summary>
    /// The Cel shaded and Ink filters: MunShot's own passes, Redux camera render hooks drawing with MunShot's
    /// CelShading shader, which redraw the picture rather than recolouring it (<see cref="CelShading"/>,
    /// <see cref="CelEffects"/>, <see cref="InkCapture"/>, <see cref="InkLines"/>). Made, and their hooks registered,
    /// when photo mode's look is laid over the game's (<see cref="PhotoLook"/>); told each frame which filter is on and
    /// where the camera and planet are; taken away again with the look, Redux's own state as it was.
    /// </summary>
    internal sealed class CelAndInk
    {
        // Cel shaded's clear sky is in flat tones only from orbit: none below this share of the way up the atmosphere,
        // all of them at its top (always, where there's none)
        private const float SkyBandsFrom = 0.6f;

        // A kerbal's eyeballs are "mesh_eye_m_ball_l_original" and the like on the kerbal rig, and its hair
        // "hairstyle_m_01", coloured by its material's _RandomColor (KSP2/KerbalHair2); Ink fills hair darker than
        // DarkHair (its brightness, 0 to 1). Looked for again this often while Ink is on, as kerbals come and go
        private const string EyePrefix = "mesh_eye_";
        private const string EyeballName = "_ball_";
        private const string HairPrefix = "hairstyle_";
        private static readonly int HairColorId = Shader.PropertyToID("_RandomColor");
        private const float DarkHair = 0.4f;
        private const float KerbalSearchSeconds = 1f;

        private readonly CelShading _celShading;
        private readonly InkLines _celOutlines;
        private readonly InkCapture _inkBeforeTerrain;
        private readonly InkCapture _inkScene;
        private readonly InkCapture _inkOpaque;
        private readonly InkCapture _inkBeforeEffects;
        private readonly InkCapture _inkEffects;
        private readonly CelEffects _celEffects;
        private readonly InkLines _ink;

        // Every hook, in the order registered: hooks at one time go in that order (CelEffects after the effects mask)
        private readonly List<IFilterHook> _hooks = new List<IFilterHook>();

        // Redux's exhaust and smoke pass, when it's been moved after _inkBeforeEffects, and what came after it before
        private ICameraRenderHook _movedPlumes;
        private readonly List<ICameraRenderHook> _afterPlumes = new List<ICameraRenderHook>();

        // The planets and moons Redux's faraway view draws, found again every frame a filter needs them
        private readonly List<(Renderer, bool)> _farBodies = new List<(Renderer, bool)>();

        // The kerbals' eyeballs and dark hair, for Ink, and when they're next looked for
        private readonly List<(Renderer, bool)> _kerbalParts = new List<(Renderer, bool)>();
        private readonly MaterialPropertyBlock _hairProperties = new MaterialPropertyBlock();
        private float _nextKerbalSearch;

        /// <summary>Makes the filters' passes with MunShot's CelShading shader and registers their hooks.</summary>
        public CelAndInk(Shader shader, Action<string> logInfo)
        {
            _celShading = new CelShading(shader, logInfo);
            _celOutlines = new InkLines(shader, onPaper: false);
            _inkBeforeTerrain = new InkCapture(shader, InkCapture.Stage.BeforeTerrain);
            _inkScene = new InkCapture(shader, InkCapture.Stage.Scene);
            _inkOpaque = new InkCapture(shader, InkCapture.Stage.Opaque);
            _inkBeforeEffects = new InkCapture(shader, InkCapture.Stage.BeforeEffects);
            _inkEffects = new InkCapture(shader, InkCapture.Stage.Effects);
            _celEffects = new CelEffects(shader);
            _ink = new InkLines(shader, onPaper: true);
            _hooks.AddRange(new IFilterHook[]
            {
                _celShading, _celOutlines, _inkBeforeTerrain, _inkScene, _inkOpaque, _inkBeforeEffects, _inkEffects,
                _celEffects, _ink
            });
            foreach (IFilterHook hook in _hooks) CameraRenderHooks.Register(hook);
        }

        /// <summary>
        /// Called every frame: which filter is on (neither draws anything otherwise), its settings, and where the camera
        /// and the planet are.
        /// </summary>
        /// <param name="shadeDarkness">How dark Cel shaded's shaded tone is: 0 lightest, 1 darkest.</param>
        /// <param name="lineDetail">How much detail the lines show: 0 only outlines, 1 all of it.</param>
        /// <param name="inkColor">Ink's lines and black, and its paper (linear).</param>
        /// <param name="lens">The fisheye as URP's lens distortion draws it (<see cref="InkLines.Lens"/>).</param>
        public void Update(Camera camera, bool cel, bool ink, float shadeDarkness, float lineDetail, Color inkColor,
                           Color paperColor, Vector4 lens)
        {
            if (camera != null && (cel || ink)) FollowCamera(camera);

            _celShading.IsOn = cel;
            _celShading.Shade = Mathf.Clamp01(shadeDarkness);
            _celOutlines.IsOn = cel;
            _celEffects.IsOn = cel;
            _inkBeforeTerrain.IsOn = ink || cel;
            _inkScene.IsOn = ink || cel;
            _inkOpaque.IsOn = ink;
            _inkBeforeEffects.IsOn = ink || cel;
            _inkEffects.IsOn = ink || cel;
            _ink.IsOn = ink;
            if (ink || cel)
            {
                // Either side of Redux's exhaust and smoke, whenever it draws them
                RenderPassEvent plumesAt = DrawPlumesAfter(_inkBeforeEffects)?.Event ?? RenderPassEvent.AfterRenderingTransparents;
                _inkBeforeEffects.EffectsEvent = plumesAt;
                _inkEffects.EffectsEvent = plumesAt;
                _celEffects.EffectsEvent = plumesAt;
            }
            _celOutlines.LineDetail = Mathf.Clamp01(lineDetail);
            _ink.LineDetail = Mathf.Clamp01(lineDetail);
            // Ink covers the whole picture after the post-processing, so it bends its lines as the fisheye has bent the
            // picture under it, and draws into the picture's linear colours
            _ink.Lens = lens;
            _ink.InkColor = inkColor;
            _ink.PaperColor = paperColor;
            if (ink) _inkOpaque.KerbalParts = FindKerbalParts();
        }

        /// <summary>Takes the filters away: unregisters their hooks, puts Redux's own back as it was, and lets go of them.</summary>
        public void Release()
        {
            foreach (IFilterHook hook in _hooks) CameraRenderHooks.Unregister(hook);
            RestorePlumes();
            foreach (IFilterHook hook in _hooks) hook.Destroy();
            _hooks.Clear();
            _kerbalParts.Clear();
        }

        // Where the camera and the planet are, for every pass that draws from them
        private void FollowCamera(Camera camera)
        {
            foreach (IFilterHook hook in _hooks) hook.Camera = camera;
            Vector3 up = Planet.UpAt(camera.transform.position, Vector3.up);
            _celShading.Up = up;
            _celOutlines.Up = up;
            _ink.Up = up;

            // The planet, so its coasts can be faded the higher above it the camera is, and the clear sky put in flat
            // tones from orbit
            bool found = Planet.Find(out Vector3 centre, out double radius, out double atmosphere);
            Vector4 planet = found ? new Vector4(centre.x, centre.y, centre.z, (float)radius) : Vector4.zero;
            _celOutlines.Planet = planet;
            _ink.Planet = planet;
            float height = Vector3.Distance(camera.transform.position, centre) - (float)radius;
            _celEffects.SkyBands = !found ? 0f
                : atmosphere <= 0 ? 1f
                : Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(SkyBandsFrom * (float)atmosphere, (float)atmosphere, height));

            // The game's volume clouds, so the lines behind them fade and Cel shaded puts them in flat tones
            float cloudsShown = Planet.CloudsShown(camera);
            _celOutlines.CloudsShown = cloudsShown;
            _ink.CloudsShown = cloudsShown;
            _celEffects.CloudsShown = cloudsShown;

            // The planet as Redux's faraway view draws it, clouds and all, for its edge; only from above its clouds, as
            // from among them the edge would hang in the sky over the horizon
            Matrix4x4 farLook = Matrix4x4.identity;
            Vector3 farEye = Vector3.zero;
            Vector4 farPlanet = Vector4.zero;
            float farMetres = 0f;
            Renderer farBody = null;
            bool farSea = false;
            Matrix4x4 farViewProjection = Matrix4x4.identity;
            if (Planet.FindDrawn(out Camera farCamera, out Vector3 farCentre, out float farRadius, out farMetres, out Renderer surface, out bool sea)
                && Vector3.Distance(farCamera.transform.position, farCentre) > farRadius)
            {
                farLook = (farCamera.projectionMatrix * farCamera.worldToCameraMatrix).inverse;
                farEye = farCamera.transform.position;
                farPlanet = new Vector4(farCentre.x, farCentre.y, farCentre.z, farRadius);
                // Its own surface, for its coasts (where it has a sea) and its slopes
                farViewProjection = GL.GetGPUProjectionMatrix(farCamera.projectionMatrix, true) * farCamera.worldToCameraMatrix;
                farBody = surface;
                farSea = sea;
            }
            ShowFar(_celOutlines, farLook, farEye, farPlanet, farMetres, farBody, farSea, farViewProjection);
            ShowFar(_ink, farLook, farEye, farPlanet, farMetres, farBody, farSea, farViewProjection);

            // The distant planets and moons that view draws, from any height, for their tones and outlines
            Camera farView = Planet.FarCamera();
            Planet.FindDrawnBodies(_farBodies);
            _inkScene.FarBodies = farView != null ? _farBodies : null;
            _inkScene.FarViewProjection = farView != null
                ? GL.GetGPUProjectionMatrix(farView.projectionMatrix, true) * farView.worldToCameraMatrix
                : Matrix4x4.identity;
        }

        // The planet as Redux's faraway view draws it, for a set of lines (InkLines' Far properties)
        private static void ShowFar(InkLines lines, Matrix4x4 look, Vector3 eye, Vector4 planet, float metres, Renderer body,
                                    bool sea, Matrix4x4 viewProjection)
        {
            lines.FarLook = look;
            lines.FarEye = eye;
            lines.FarPlanet = planet;
            lines.FarMetres = metres;
            lines.FarBody = body;
            lines.FarSea = sea;
            lines.FarViewProjection = viewProjection;
        }

        // Every kerbal's eyeballs and dark hair in the scene (doubles and originals: a hidden one isn't drawn), looked for
        // again every KerbalSearchSeconds
        private IReadOnlyList<(Renderer, bool)> FindKerbalParts()
        {
            if (Time.unscaledTime < _nextKerbalSearch) return _kerbalParts;
            _nextKerbalSearch = Time.unscaledTime + KerbalSearchSeconds;
            _kerbalParts.Clear();
            foreach (Kerbal3DModel model in UnityEngine.Object.FindObjectsByType<Kerbal3DModel>(FindObjectsInactive.Exclude))
                foreach (Renderer renderer in model.GetComponentsInChildren<Renderer>(true))
                {
                    string name = renderer.gameObject.name;
                    if (name.StartsWith(EyePrefix, StringComparison.Ordinal) && name.IndexOf(EyeballName, StringComparison.Ordinal) >= 0)
                        _kerbalParts.Add((renderer, false));
                    else if (name.StartsWith(HairPrefix, StringComparison.Ordinal) && HairColor(renderer).grayscale < DarkHair)
                        _kerbalParts.Add((renderer, true));
                }
            return _kerbalParts;
        }

        // A kerbal's hair colour: set on its renderer, or else its material
        private Color HairColor(Renderer hair)
        {
            if (hair.HasPropertyBlock())
            {
                hair.GetPropertyBlock(_hairProperties);
                if (_hairProperties.HasColor(HairColorId)) return _hairProperties.GetColor(HairColorId);
            }
            Material material = hair.sharedMaterial;
            return material != null && material.HasColor(HairColorId) ? material.GetColor(HairColorId) : Color.white;
        }

        // Redux's exhaust and smoke pass (null while there's none), moved after copy if it's before it: copy is taken at the
        // same time, just before it, and hooks at one time go in the order registered; Redux registers its pass when the
        // first engine appears. What came after it is noted, so RestorePlumes can put it back where it was
        private ICameraRenderHook DrawPlumesAfter(ICameraRenderHook copy)
        {
            IReadOnlyList<ICameraRenderHook> hooks = CameraRenderHooks.Hooks;
            int at = -1;
            bool afterCopy = false;
            for (int i = 0; i < hooks.Count && at < 0; i++)
            {
                if (hooks[i] == copy) afterCopy = true;
                else if (hooks[i].GetType().DeclaringType == typeof(Redux.VFX.Plume.Volumetric.VolumetricPlumeRenderer)) at = i;
            }
            if (at < 0) return null;
            ICameraRenderHook plumes = hooks[at];
            if (afterCopy) return plumes;
            if (_movedPlumes == null)
            {
                _movedPlumes = plumes;
                for (int i = at + 1; i < hooks.Count; i++) _afterPlumes.Add(hooks[i]);
            }
            CameraRenderHooks.Unregister(plumes);
            CameraRenderHooks.Register(plumes);
            return plumes;
        }

        // Redux's exhaust and smoke pass back where it was, once the filters' own hooks are gone: what came after it before
        // it was moved is registered again, in order, after it
        private void RestorePlumes()
        {
            if (_movedPlumes == null) return;
            if (IsRegistered(_movedPlumes))
                foreach (ICameraRenderHook hook in _afterPlumes)
                    if (IsRegistered(hook))
                    {
                        CameraRenderHooks.Unregister(hook);
                        CameraRenderHooks.Register(hook);
                    }
            _movedPlumes = null;
            _afterPlumes.Clear();
        }

        private static bool IsRegistered(ICameraRenderHook hook)
        {
            foreach (ICameraRenderHook registered in CameraRenderHooks.Hooks)
                if (registered == hook) return true;
            return false;
        }
    }
}
