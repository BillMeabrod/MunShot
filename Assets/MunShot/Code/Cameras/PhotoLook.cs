using System;
using Redux.Rendering.PostProcessing;
using SpaceWarp2.API;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace MunShot.Cameras
{
    /// <summary>The filter laid over the picture: one at a time, as a camera has one filter on its lens.</summary>
    public enum PhotoFilter
    {
        None,
        OldFilm,
        SpaceAge,
        CelShaded,
        Ink
    }

    /// <summary>The Ink filter's ink and paper as a set: black on white, white on blue, brown on cream, white on black.</summary>
    public enum InkLook
    {
        Classic,
        Blueprint,
        Sepia,
        Chalk
    }

    /// <summary>
    /// Photo mode's own look, laid over the game's while photo mode is open: depth of field, fisheye, vignette, the
    /// picture's exposure, contrast, saturation and warmth, and a filter (<see cref="PhotoFilter"/>).
    ///
    /// Redux renders the game's post-processing from URP volumes (it mirrors the game's own effects into them every
    /// frame, see Redux's PostProcessingBridge): depth of field with its own effect, <see cref="ReduxDepthOfField"/>,
    /// and the rest, lens distortion, vignette and colour adjustments among them, with URP's. This adds one more volume:
    /// global, ranked above the game's, on a layer the game's camera reads volumes from. Only what photo mode turns on is
    /// overridden; everything else stays the game's. The volume is made on opening and destroyed on closing, so the
    /// game's look comes back untouched, and every setting starts afresh the next time.
    ///
    /// Cel shaded and Ink redraw the picture rather than recolouring it, with MunShot's own passes (<see cref="CelAndInk"/>),
    /// made for the same time.
    ///
    /// As the <see cref="IScreenLens"/>, it tells where the fisheye moves things on screen, so the mouse picks what's
    /// seen under it.
    /// </summary>
    public sealed class PhotoLook : IScreenLens
    {
        public const float MinFocusDistance = 0.3f;

        /// <summary>The furthest focus short of infinity (<see cref="float.PositiveInfinity"/>), as on a lens's ring.</summary>
        public const float MaxFocusDistance = 5000f;

        /// <summary>The widest and narrowest apertures, as f-numbers: the lower, the more blur.</summary>
        public const float MinAperture = 1.4f;
        public const float MaxAperture = 22f;

        // URP's lens distortion at full fisheye: its strongest bulge
        private const float MaxFisheyeIntensity = 1f;

        // URP's vignette at full: darker than this, the corners go black and the frame looks like a keyhole
        private const float MaxVignetteIntensity = 0.7f;

        // How gradually the vignette fades in from the middle (URP's 0.01 hard to 1 soft): a soft, photo-like fade
        private const float VignetteSmoothness = 0.4f;

        /// <summary>
        /// The colour settings' ranges: exposure in stops (each doubles or halves the light), the rest as URP's own
        /// -100 to 100. 0 is the game's own picture for each.
        /// </summary>
        public const float MaxExposure = 3f;
        public const float MaxColorAdjustment = 100f;

        // Old film, as an aged sepia print: the brightness alone, tinted so white comes out cream-yellow (a colour mix for
        // URP's channel mixer, each row how much of the red, green and blue going in make that colour coming out); blacks
        // lifted to a soft brown and whites dimmed, as a faded print's; flatter; and darker towards the edges
        private static readonly float[,] SepiaMix = Tinted(new Vector3(0.3f, 0.59f, 0.11f), new Vector3(1.1f, 0.93f, 0.68f));
        private static readonly Vector4 OldFilmLift = new Vector4(1f, 0.94f, 0.86f, 0.08f);
        private static readonly Vector4 OldFilmGain = new Vector4(1f, 1f, 1f, -0.06f);

        private const float OldFilmContrast = -15f;
        private const float OldFilmVignette = 0.5f;

        // Space age, as 16 mm colour film from the first trips to the Moon: washed-out colour, a little warmer, and
        // flatter; the blacks lifted to a milky green-grey and the whites dimmed to ivory
        private const float SpaceAgeSaturation = -30f;
        private const float SpaceAgeWarmth = 8f;
        private const float SpaceAgeContrast = -15f;
        private static readonly Vector4 SpaceAgeLift = new Vector4(0.93f, 1f, 0.94f, 0.06f);
        private static readonly Vector4 SpaceAgeGain = new Vector4(1f, 0.97f, 0.85f, -0.05f);


        private const float DefaultFocusDistance = 5f;
        private const float DefaultAperture = 4f;

        // What Redux's depth of field is given for infinity: its maths needs a number, and nothing in KSP is far enough
        // past this for the difference to show (10,000 km)
        private const float InfiniteFocusDistance = 1e7f;

        // Above the game's own volumes, which Redux mirrors at the priorities the game gave them
        private const float Priority = 1000f;

        // Redux's depth of field works out the blur for a 24 mm high film, as a 35 mm still camera has
        private const float FilmHeightMillimetres = 24f;

        private readonly Action<string> _logInfo;
        private readonly Action<string> _logWarning;

        private GameObject _holder;
        private VolumeProfile _profile;
        private ReduxDepthOfField _depthOfField;
        private LensDistortion _lensDistortion;
        private UnityEngine.Rendering.Universal.Vignette _vignette;
        private ColorAdjustments _colorAdjustments;
        private WhiteBalance _whiteBalance;
        private ChannelMixer _channelMixer;
        private LiftGammaGain _liftGammaGain;
        private ReduxAmbientOcclusion _occlusion;
        // Cel shaded and Ink, while engaged with MunShot's shader loaded
        private CelAndInk _celAndInk;

        // The camera's volume layers last seen, so a change is only looked at (and logged) once
        private int _volumeLayers = -1;

        public bool IsEngaged => _holder != null;

        /// <summary>Whether things nearer and further than <see cref="FocusDistance"/> are blurred.</summary>
        public bool DepthOfField { get; set; }

        /// <summary>How far from the camera things are sharp, in metres; infinity for everything far off.</summary>
        public float FocusDistance { get; set; } = DefaultFocusDistance;

        /// <summary>The aperture, as an f-number: lower blurs what's out of focus more.</summary>
        public float Aperture { get; set; } = DefaultAperture;

        /// <summary>How much the picture bulges out from its middle, as through a fisheye lens: 0 (flat) to 1.</summary>
        public float Fisheye { get; set; }

        /// <summary>How much the picture darkens towards its edges and corners: 0 (none) to 1.</summary>
        public float Vignette { get; set; }

        /// <summary>Brighter or darker, in stops, ±<see cref="MaxExposure"/>.</summary>
        public float Exposure { get; set; }

        /// <summary>More or less difference between light and dark, ±<see cref="MaxColorAdjustment"/>.</summary>
        public float Contrast { get; set; }

        /// <summary>More or less colourful, ±<see cref="MaxColorAdjustment"/>: the lowest is black and white.</summary>
        public float Saturation { get; set; }

        /// <summary>Warmer (orange) or cooler (blue), ±<see cref="MaxColorAdjustment"/>.</summary>
        public float Warmth { get; set; }

        /// <summary>Whether any colour setting is off the game's own picture.</summary>
        public bool ColorChanged => Exposure != 0f || Contrast != 0f || Saturation != 0f || Warmth != 0f;

        /// <summary>Puts exposure, contrast, saturation and warmth back to the game's own picture.</summary>
        public void ResetColor()
        {
            Exposure = 0f;
            Contrast = 0f;
            Saturation = 0f;
            Warmth = 0f;
        }

        /// <summary>The filter over the picture; <see cref="PhotoFilter.None"/> for none.</summary>
        public PhotoFilter Filter { get; set; }

        /// <summary>
        /// How much detail Cel shaded's and Ink's lines show: 0 only outlines, coasts and the planet's edge, to 1, all of
        /// it (corners, slopes and paint lines too).
        /// </summary>
        public float LineDetail { get; set; } = 1f;

        /// <summary>How dark Cel shaded's shaded tone is: 0 lightest, 1 darkest, half as it's drawn by default.</summary>
        public float ShadeDarkness { get; set; } = DefaultShadeDarkness;
        public const float DefaultShadeDarkness = 0.5f;

        /// <summary>The Ink filter's lines and black, and its paper, as they'd be picked (sRGB).</summary>
        public Color InkColor { get; set; }
        public Color PaperColor { get; set; }

        /// <summary>The ink and paper colours of one of the Ink filter's looks.</summary>
        public static (Color ink, Color paper) InkLookColors(InkLook look)
        {
            switch (look)
            {
                case InkLook.Blueprint: return (new Color(0.93f, 0.96f, 1f), new Color(0.11f, 0.29f, 0.56f));
                case InkLook.Sepia: return (new Color(0.33f, 0.2f, 0.11f), new Color(0.95f, 0.9f, 0.78f));
                case InkLook.Chalk: return (new Color(0.94f, 0.94f, 0.91f), new Color(0.14f, 0.16f, 0.15f));
                default: return (new Color(0.15f, 0.15f, 0.16f), Color.white);
            }
        }

        /// <summary>MunShot's cel shading shader, once loaded with the mod's assets; without it, Cel shaded and Ink leave the picture as it is.</summary>
        public Shader CelShader { get; set; }

        public PhotoLook(Action<string> logInfo, Action<string> logWarning)
        {
            _logInfo = logInfo ?? throw new ArgumentNullException(nameof(logInfo));
            _logWarning = logWarning ?? throw new ArgumentNullException(nameof(logWarning));
        }

        /// <summary>Lays photo mode's look over the game's, starting from the defaults. Safe to call when engaged.</summary>
        public void Engage()
        {
            if (IsEngaged) return;
            DepthOfField = false;
            FocusDistance = DefaultFocusDistance;
            Aperture = DefaultAperture;
            Fisheye = 0f;
            Vignette = 0f;
            ResetColor();
            Filter = PhotoFilter.None;
            (InkColor, PaperColor) = InkLookColors(InkLook.Classic);
            LineDetail = 1f;
            ShadeDarkness = DefaultShadeDarkness;
            _volumeLayers = -1;

            _profile = ScriptableObject.CreateInstance<VolumeProfile>();
            _profile.name = "MunShot photo look";
            _profile.hideFlags = HideFlags.DontSave;
            _depthOfField = _profile.Add<ReduxDepthOfField>(overrides: false);
            _lensDistortion = _profile.Add<LensDistortion>(overrides: false);
            _vignette = _profile.Add<UnityEngine.Rendering.Universal.Vignette>(overrides: false);
            _colorAdjustments = _profile.Add<ColorAdjustments>(overrides: false);
            _whiteBalance = _profile.Add<WhiteBalance>(overrides: false);
            _channelMixer = _profile.Add<ChannelMixer>(overrides: false);
            _liftGammaGain = _profile.Add<LiftGammaGain>(overrides: false);
            _occlusion = _profile.Add<ReduxAmbientOcclusion>(overrides: false);

            // On the layer the game keeps its post-processing volumes on, which its cameras read
            _holder = new GameObject("MunShot photo look") { hideFlags = HideFlags.DontSave, layer = Layers.LayerRenderPostProcessing };
            var volume = _holder.AddComponent<Volume>();
            volume.isGlobal = true;
            volume.priority = Priority;
            volume.weight = 1f;
            volume.sharedProfile = _profile;
            if (CelShader != null)
            {
                _celAndInk = new CelAndInk(CelShader, _logInfo);
            }
            else
            {
                _logWarning("Photo mode: MunShot's cel shading shader isn't loaded, so the Cel shaded and Ink filters won't change the picture.");
            }
            Apply(null);
            _logInfo("Photo mode: photo look laid over the game's.");
        }

        /// <summary>
        /// Called every frame while engaged: passes the settings on, with the lens worked out from
        /// <paramref name="camera"/>'s field of view, and keeps the volume on a layer the camera reads.
        /// </summary>
        public void Tick(Camera camera)
        {
            if (!IsEngaged) return;
            if (camera != null) FollowVolumeLayers(camera);
            Apply(camera);
        }

        /// <summary>Takes photo mode's look away again. Safe to call when not engaged.</summary>
        public void Release()
        {
            if (!IsEngaged) return;
            _celAndInk?.Release();
            _celAndInk = null;
            UnityEngine.Object.Destroy(_holder);
            UnityEngine.Object.Destroy(_depthOfField);
            UnityEngine.Object.Destroy(_lensDistortion);
            UnityEngine.Object.Destroy(_vignette);
            UnityEngine.Object.Destroy(_colorAdjustments);
            UnityEngine.Object.Destroy(_whiteBalance);
            UnityEngine.Object.Destroy(_channelMixer);
            UnityEngine.Object.Destroy(_liftGammaGain);
            UnityEngine.Object.Destroy(_occlusion);
            UnityEngine.Object.Destroy(_profile);
            _holder = null;
            _depthOfField = null;
            _lensDistortion = null;
            _vignette = null;
            _colorAdjustments = null;
            _whiteBalance = null;
            _channelMixer = null;
            _liftGammaGain = null;
            _occlusion = null;
            _profile = null;
            _logInfo("Photo mode: photo look taken away.");
        }

        // Each effect overrides the game's only while it's on, so whatever the game has there itself shows otherwise
        private void Apply(Camera camera)
        {
            bool blur = DepthOfField;
            Set(_depthOfField.Enabled, blur, true);
            Set(_depthOfField.FocusDistance, blur, float.IsPositiveInfinity(FocusDistance)
                ? InfiniteFocusDistance
                : Mathf.Clamp(FocusDistance, MinFocusDistance, MaxFocusDistance));
            Set(_depthOfField.Aperture, blur, Mathf.Clamp(Aperture, MinAperture, MaxAperture));
            // A narrower field of view is a longer lens, which blurs more, as a real telephoto does
            if (camera != null) Set(_depthOfField.FocalLength, blur, FocalLength(camera.fieldOfView));
            Set(_depthOfField.KernelSize, blur, BokehKernelSize.Large);

            float intensity = FisheyeIntensity;
            bool bulge = intensity > 0f;
            Set(_lensDistortion.intensity, bulge, intensity);
            Set(_lensDistortion.scale, bulge, FillingScale(intensity));
            Set(_lensDistortion.center, bulge, new Vector2(0.5f, 0.5f));
            Set(_lensDistortion.xMultiplier, bulge, 1f);
            Set(_lensDistortion.yMultiplier, bulge, 1f);

            // Black, from the middle of the picture, following its shape (a wide picture darkens in a wide oval); old
            // film darkens the edges too, the vignette slider's or its own, whichever is darker
            bool oldFilm = Filter == PhotoFilter.OldFilm;
            float vignette = Mathf.Max(Mathf.Clamp01(Vignette) * MaxVignetteIntensity, oldFilm ? OldFilmVignette : 0f);
            bool darken = vignette > 0f;
            Set(_vignette.intensity, darken, vignette);
            Set(_vignette.smoothness, darken, VignetteSmoothness);
            Set(_vignette.color, darken, Color.black);
            Set(_vignette.center, darken, new Vector2(0.5f, 0.5f));
            Set(_vignette.rounded, darken, false);

            // Each colour setting at 0 leaves the game's own value (the game grades some scenes itself). Old film adds to
            // the contrast slider, Space age to the contrast, saturation and warmth sliders
            bool spaceAge = Filter == PhotoFilter.SpaceAge;
            float exposure = Mathf.Clamp(Exposure, -MaxExposure, MaxExposure);
            float filmContrast = oldFilm ? OldFilmContrast : spaceAge ? SpaceAgeContrast : 0f;
            float contrast = Mathf.Clamp(Contrast + filmContrast, -MaxColorAdjustment, MaxColorAdjustment);
            float saturation = Mathf.Clamp(Saturation + (spaceAge ? SpaceAgeSaturation : 0f), -MaxColorAdjustment, MaxColorAdjustment);
            float warmth = Mathf.Clamp(Warmth + (spaceAge ? SpaceAgeWarmth : 0f), -MaxColorAdjustment, MaxColorAdjustment);
            Set(_colorAdjustments.postExposure, exposure != 0f, exposure);
            Set(_colorAdjustments.contrast, contrast != 0f, contrast);
            Set(_colorAdjustments.saturation, saturation != 0f, saturation);
            Set(_whiteBalance.temperature, warmth != 0f, warmth);

            ApplyChannelMixer(oldFilm ? SepiaMix : null);
            Set(_liftGammaGain.lift, oldFilm || spaceAge, oldFilm ? OldFilmLift : SpaceAgeLift);
            Set(_liftGammaGain.gain, oldFilm || spaceAge, oldFilm ? OldFilmGain : SpaceAgeGain);

            // Cel shaded has no soft shadows in creases: Redux's ambient occlusion, drawn over the picture after the cel
            // pass, would put grey smudges on its flat colours
            bool cel = Filter == PhotoFilter.CelShaded;
            Set(_occlusion.Enabled, cel, false);
            // Cel shaded and Ink redraw the picture with MunShot's own passes; Ink's paper is drawn into the picture's
            // linear colours
            _celAndInk?.Update(camera, cel, Filter == PhotoFilter.Ink, ShadeDarkness, LineDetail, InkColor.linear,
                               PaperColor.linear, LensParameters(intensity));
        }

        // The fisheye as URP's lens distortion draws it, for a shader to do the same: its angle, its sigma and how far it
        // zooms in; all 0 with no fisheye
        private static Vector4 LensParameters(float intensity)
        {
            if (intensity <= 0f) return Vector4.zero;
            LensAngles(intensity, out float theta, out float sigma);
            return new Vector4(theta, sigma, FillingScale(intensity), 0f);
        }

        // A filter's colour mix, or the game's own colours for null. URP's channel mixer takes percentages
        private void ApplyChannelMixer(float[,] mix)
        {
            bool on = mix != null;
            Set(_channelMixer.redOutRedIn, on, on ? mix[0, 0] * 100f : 0f);
            Set(_channelMixer.redOutGreenIn, on, on ? mix[0, 1] * 100f : 0f);
            Set(_channelMixer.redOutBlueIn, on, on ? mix[0, 2] * 100f : 0f);
            Set(_channelMixer.greenOutRedIn, on, on ? mix[1, 0] * 100f : 0f);
            Set(_channelMixer.greenOutGreenIn, on, on ? mix[1, 1] * 100f : 0f);
            Set(_channelMixer.greenOutBlueIn, on, on ? mix[1, 2] * 100f : 0f);
            Set(_channelMixer.blueOutRedIn, on, on ? mix[2, 0] * 100f : 0f);
            Set(_channelMixer.blueOutGreenIn, on, on ? mix[2, 1] * 100f : 0f);
            Set(_channelMixer.blueOutBlueIn, on, on ? mix[2, 2] * 100f : 0f);
        }

        // A mix giving every colour the brightness (from the weights) in one tint
        private static float[,] Tinted(Vector3 weights, Vector3 tint)
        {
            var mix = new float[3, 3];
            for (int row = 0; row < 3; row++)
                for (int column = 0; column < 3; column++)
                    mix[row, column] = tint[row] * weights[column];
            return mix;
        }

        private static void Set<T>(VolumeParameter<T> parameter, bool on, T value)
        {
            parameter.overrideState = on;
            if (on) parameter.value = value;
        }

        private float FisheyeIntensity => IsEngaged ? Mathf.Clamp01(Fisheye) * MaxFisheyeIntensity : 0f;

        // ---- Where the fisheye moves things ----
        // URP's lens distortion (UberPost.shader, DistortUV) gives each screen pixel the picture from further out the
        // further it is from the middle: in screen coordinates (0 to 1) from the middle, after zooming in by the scale,
        // a pixel r out shows what was tan(r θ) / σ out. ToScene does the same; ToScreen undoes it (r = atan(s σ) / θ)

        public bool Bends => FisheyeIntensity > 0f;

        public Vector2 ToScene(Vector2 screen)
        {
            float intensity = FisheyeIntensity;
            if (intensity <= 0f || Screen.width <= 0 || Screen.height <= 0) return screen;
            LensAngles(intensity, out float theta, out float sigma);
            Vector2 size = new Vector2(Screen.width, Screen.height);
            Vector2 fromMiddle = (screen / size - Middle) / FillingScale(intensity);
            float r = fromMiddle.magnitude;
            if (r < 1e-6f) return screen;
            return (Middle + fromMiddle * (Mathf.Tan(r * theta) / (r * sigma))) * size;
        }

        public Vector2 ToScreen(Vector2 scene)
        {
            float intensity = FisheyeIntensity;
            if (intensity <= 0f || Screen.width <= 0 || Screen.height <= 0) return scene;
            LensAngles(intensity, out float theta, out float sigma);
            Vector2 size = new Vector2(Screen.width, Screen.height);
            Vector2 fromMiddle = scene / size - Middle;
            float s = fromMiddle.magnitude;
            if (s < 1e-6f) return scene;
            float r = Mathf.Atan(s * sigma) / theta;
            return (Middle + fromMiddle * (r / s) * FillingScale(intensity)) * size;
        }

        private static readonly Vector2 Middle = new Vector2(0.5f, 0.5f);

        // URP's angles for an intensity (UberPostProcessPass.CalcLensDistortionParams)
        private static void LensAngles(float intensity, out float theta, out float sigma)
        {
            theta = Mathf.Deg2Rad * Mathf.Min(160f, 1.6f * Mathf.Max(intensity * 100f, 1f));
            sigma = 2f * Mathf.Tan(theta * 0.5f);
        }

        // How far URP's lens distortion has to zoom in so a bulging picture still fills the frame: a corner shows what
        // was at the corner, not past it (where the picture runs out and smears). Solved for the corner, half a
        // diagonal (0.707) from the middle in screen coordinates
        private static float FillingScale(float intensity)
        {
            if (intensity <= 0f) return 1f;
            const float corner = 0.70710678f;
            LensAngles(intensity, out float theta, out float sigma);
            return Mathf.Clamp(corner * theta / Mathf.Atan(corner * sigma), 0.01f, 5f);
        }

        // The focal length (mm) that sees the camera's vertical field of view on Redux's film
        private static float FocalLength(float verticalFieldOfView) =>
            FilmHeightMillimetres * 0.5f / Mathf.Tan(Mathf.Clamp(verticalFieldOfView, 1f, 179f) * 0.5f * Mathf.Deg2Rad);

        // A camera only blends volumes on its volume layers, which Redux copies from the game's post-processing each
        // frame. The volume starts on the game's post-processing layer; if a camera ever reads others, it moves to the
        // first of them, and says so
        private void FollowVolumeLayers(Camera camera)
        {
            int layers = camera.GetUniversalAdditionalCameraData().volumeLayerMask.value;
            if (layers == _volumeLayers) return;
            _volumeLayers = layers;
            if (layers == 0)
            {
                _logWarning($"Photo mode: the camera '{camera.name}' reads volumes from no layer, so the photo look can't show.");
                return;
            }
            if ((layers & (1 << _holder.layer)) != 0) return;
            int layer = 0;
            while ((layers & (1 << layer)) == 0) layer++;
            _holder.layer = layer;
            _logInfo($"Photo mode: photo look on layer {layer} ({LayerMask.LayerToName(layer)}), read by the camera '{camera.name}' (volume layers 0x{layers:X}).");
        }
    }
}
