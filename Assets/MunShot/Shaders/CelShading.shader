// MunShot's Cel shaded and Ink filters, drawn by MunShot.Cameras.CelShading, CelEffects, InkCapture and InkLines.
//
// Flat pass: right after the game's deferred lighting, every pixel it lit is drawn again as a cartoon from URP's
// G-buffer (the surface's own colour and facing, before any light):
// - Flat colour: the pixel's colour averaged with the pixels around it on the same surface (near in depth, facing the
//   same way) whose colour is close to its own, so a texture's detail evens out while edges between colours stay sharp.
//   On the ground, grass averages only with grass and the rest (dirt, paths, tarmac) only with the rest, so where the
//   ground's texture fades softly from one to the other there's a hard edge, not a blur.
// - Two tones: the surface's facing, smoothed the same way, lit by the sun (with the game's shadows, cloud shadows and
//   the night): lit, or in shade, with a one-pixel edge between.
// The sky is left as it is.
//
// Outlines pass: before the post-processing, solid lines where the depth jumps, the water meets the land or the
// surface turns at a corner (InkLines.hlsl), and where a surface's own colour changes sharply (a path's kerb, a road
// marking, a logo on a wall: InkGround.hlsl), laid over the Cel shaded picture.
//
// Paper pass: the Ink filter. After the post-processing (so the game's exposure, bloom and tonemapping can't grey or
// glow the paper), the same lines on paper, everything else and the sky included, in the ink and paper colours the
// player picked (black on white to start); bent as the fisheye bends the picture, which has already been drawn by then
// (InkPaper.hlsl); among a tree's leaves, only the outlines. With what MunShot.Cameras.InkCapture took earlier in the
// frame, also:
// - Solid black wherever the finished picture was near black (not the sky).
// - Paint lines: where a surface's own colour changes sharply (road markings, a logo on a wall).
// - The kerbals' pupils, from their eyes' own texture, and dark hair.
//
// In the Outlines and Paper passes: where the planet's ground turns steep (a crater's wall, a ridge); and from above
// its clouds, its edge where Redux's faraway view draws it (to the top of its clouds), and its coasts and slopes out
// to the horizon. The coasts and slopes, and the colours on its ground, fade the higher the camera is, and away near
// the edge (InkLines.hlsl).
//
// CelEffects pass: Cel shaded's exhaust and smoke, right after Redux draws them, as a cartoon smoke cloud: in flat
// tones inside its hard edge, the picture as it was before them outside. The Outlines and Paper passes draw nothing of
// what's behind that cloud, and an outline round it; on paper, it's washed lightly with the ink. All from the Effects
// pass. CelEffects also draws the planet's clouds in flat tones, and the clear sky from orbit; both passes fade the
// lines behind the clouds (from the game's own cloud texture).
//
// KeepDepth, KerbalInk and Effects passes: InkCapture's, for the above (Effects: how much what was drawn between two
// copies changed each pixel: Redux's exhaust and smoke on the picture, or the planet's terrain among the surfaces'
// colours). Leaves pass: InkLines', just before the paper (where lines crowd). FarSurface pass: InkLines', the faraway
// planet's surface, for its coasts and slopes. CelEffects pass: MunShot.Cameras.CelEffects'.
Shader "Hidden/MunShot/CelShading"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }
        ZWrite Off ZTest Always Cull Off Blend Off

        Pass
        {
            Name "Flat"

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment FlatFragment
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT

            #define _CLUSTER_LIGHT_LOOP 1
            #include "Packages/ksp2redux.sdk/Assets/Shaders/URP/Include/KSP2Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/GBufferCommon.hlsl"

            TEXTURE2D_X(_MunShotAlbedo);
            TEXTURE2D_X(_MunShotNormals);
            TEXTURE2D_X_FLOAT(_MunShotDepth);

            // xy: one pixel (of a picture 1080 high), as a fraction of the picture's width and height
            float4 _MunShotPixel;

            // Up from the ground, in the world the normals are in (away from the planet's centre)
            float4 _MunShotUp;

            // The ground faces up by more than Ground (the cosine); its colour is grass where its green is more than
            // GrassGreen times its red and its blue
            static const float Ground = 0.8;
            static const float GrassGreen = 1.15;

            // How far round each pixel its colour and facing are evened out (in pixels), in rings of samples
            static const float FlatRadius = 32;
            static const int FlatRings = 4;
            static const int FlatDirections = 16;
            // A neighbour counts as the same surface when it's this near in depth (for its distance) and faces within
            // about 35 degrees the same way; its colour counts when it's within Similar of the pixel's, as each looks (0 to
            // 1 a channel, its square root: so a red ring on a grey wall stays apart, not smeared into it)
            static const float SameDepth = 0.03;
            static const float SameFacing = 0.82;
            static const float Similar = 0.3;

            // The two tones: where the light turns to shade (how much of the sun reaches the surface), each tone's share
            // of the sun (the shade darker and bluer), how much of the sun's colour is kept (its warmth is otherwise
            // balanced by the sky's light, which the cartoon takes little of), and how much of the sky's light there is
            static const float ShadeEdge = 0.15;
            static const float LitShare = 0.75;
            static const float3 Shade = float3(0.32, 0.36, 0.5);
            static const float SunTint = 0.4;
            static const float SkyLight = 0.25;

            // How dark the shade is, as the player sets it (0 to 1, half as above): its share of the sun, from
            // ShadeLightest times it at 0 to ShadeDarkest times it at 1
            float _MunShotShade;
            static const float ShadeLightest = 1.6;
            static const float ShadeDarkest = 0.3;

            float RawDepth(float2 uv)
            {
                return SAMPLE_TEXTURE2D_X_LOD(_MunShotDepth, sampler_PointClamp, uv, 0).r;
            }

            bool IsSky(float raw)
            {
            #if UNITY_REVERSED_Z
                return raw <= 1e-7;
            #else
                return raw >= 1 - 1e-7;
            #endif
            }

            float3 Albedo(float2 uv)
            {
                return SAMPLE_TEXTURE2D_X_LOD(_MunShotAlbedo, sampler_PointClamp, uv, 0).rgb;
            }

            bool Grassy(float3 albedo)
            {
                return albedo.g > max(albedo.r, albedo.b) * GrassGreen;
            }

            float3 Facing(float2 uv)
            {
                return normalize(UnpackGBufferNormal(SAMPLE_TEXTURE2D_X_LOD(_MunShotNormals, sampler_PointClamp, uv, 0).rgb));
            }

            // 1 where value is above edge, 0 below, one pixel between
            float SharpAbove(float value, float edge)
            {
                return saturate((value - edge) / max(fwidth(value), 1e-4) + 0.5);
            }

            float4 FlatFragment(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float2 uv = input.texcoord;
                float raw = RawDepth(uv);
                if (IsSky(raw)) discard;

                // The pixel's colour and facing, evened out over its surface
                float w = 1.0 / max(LinearEyeDepth(raw, _ZBufferParams), 1e-4);
                float3 own = Albedo(uv);
                float3 ownLooks = sqrt(max(own, 0));
                float3 facing = Facing(uv);
                bool ground = dot(facing, _MunShotUp.xyz) > Ground;
                bool grass = Grassy(own);
                float3 colorSum = own;
                float colorWeight = 1;
                float3 facingSum = facing;
                [unroll]
                for (int ring = 1; ring <= FlatRings; ring++)
                {
                    float radius = FlatRadius * ring / FlatRings;
                    [unroll]
                    for (int d = 0; d < FlatDirections; d++)
                    {
                        float angle = (d + 0.5 * (ring & 1)) * (6.2831853 / FlatDirections);
                        float2 at = uv + float2(cos(angle), sin(angle)) * radius * _MunShotPixel.xy;
                        float atRaw = RawDepth(at);
                        float atW = 1.0 / max(LinearEyeDepth(atRaw, _ZBufferParams), 1e-4);
                        float3 atFacing = Facing(at);
                        float same = (IsSky(atRaw) ? 0 : 1)
                                     * step(abs(atW - w), SameDepth * w)
                                     * step(SameFacing, dot(atFacing, facing));
                        float3 c = Albedo(at);
                        float close = same * (1 - smoothstep(Similar, Similar * 1.5, length(sqrt(max(c, 0)) - ownLooks)))
                                      * (ground && Grassy(c) != grass ? 0 : 1);
                        colorSum += c * close;
                        colorWeight += close;
                        facingSum += atFacing * same;
                    }
                }
                float3 albedo = colorSum / colorWeight;
                float3 N = normalize(facingSum);

                // The sun, with the game's shadows (as its own lit shaders take them), the night, and the clouds' shadows
                float3 positionWS = ComputeWorldSpacePosition(uv, raw, UNITY_MATRIX_I_VP);
                float4 positionCS = float4(input.positionCS.xy, raw, 1);
                Light sun = GetMainLight(TransformWorldToShadowCoord(KSP2_MainLightShadowPosition(positionWS, positionCS, uv)));
                KSP2_ApplyLocalSun(sun, positionWS, N);
                float reach = saturate(dot(N, sun.direction)) * sun.shadowAttenuation * KSP2_CloudShadow(uv);
                float lit = SharpAbove(reach, ShadeEdge);

                float3 sunColor = lerp(Luminance(sun.color).xxx, sun.color, SunTint);
                float darkness = saturate(_MunShotShade);
                float shadeScale = darkness < 0.5 ? lerp(ShadeLightest, 1, darkness * 2)
                                                  : lerp(1, ShadeDarkest, darkness * 2 - 1);
                float3 light = sunColor * lerp(Shade * shadeScale, LitShare, lit)
                               + SkyLight * 0.5 * (KSP2_SphericalGaussianAmbient(N) + KSP2_SphericalGaussianAmbient(-N));
                return float4(albedo * light, 1);
            }
            ENDHLSL
        }

        Pass
        {
            Name "Outlines"
            Blend SrcAlpha OneMinusSrcAlpha

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment OutlinesFragment
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareNormalsTexture.hlsl"
            #include "InkLines.hlsl"
            #include "InkGround.hlsl"

            float4 OutlinesFragment(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                bool crowded = Crowded(input.texcoord);
                float ink = max(LinesAt(input.texcoord, crowded), PaintLineAt(input.texcoord, crowded));
                ink *= 1 - CloudsAt(input.texcoord);
                if (InSmoke(input.texcoord)) ink = 0;
                ink = max(ink, SmokeOutlineAt(input.texcoord));
                if (ink <= 0) discard;
                return float4(Ink, ink);
            }
            ENDHLSL
        }

        Pass
        {
            Name "Paper"

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment PaperFragment
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareNormalsTexture.hlsl"
            #include "InkLines.hlsl"
            #include "InkPaper.hlsl"

            // How much of the ink the exhaust and smoke's cloud is washed with
            static const float Wash = 0.15;

            float KerbalInkAt(float2 uv)
            {
                return SAMPLE_TEXTURE2D_X_LOD(_MunShotKerbalInk, sampler_LinearClamp, uv, 0).r;
            }

            float4 PaperFragment(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float2 uv = Unbend(input.texcoord);

                // Among a tree's leaves, only the outlines, and paint lines only on the ground
                bool leafy = Leafy(uv);
                float ink = max(LinesAt(uv, leafy), PaintLineAt(uv, leafy));
                if (_MunShotTaken > 0) ink = max(ink, KerbalInkAt(uv));
                // The planet's clouds fade what's behind them; the exhaust and smoke hide it: inside their cloud, only a
                // light wash
                ink *= 1 - CloudsAt(uv);
                ink = InSmoke(uv) ? Wash : max(ink, Fill(input.texcoord));
                ink = max(ink, SmokeOutlineAt(uv));
                return float4(lerp(_MunShotPaperColor.rgb, _MunShotInkColor.rgb, ink), 1);
            }
            ENDHLSL
        }

        Pass
        {
            Name "KeepDepth"

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment KeepDepthFragment

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            TEXTURE2D_X_FLOAT(_MunShotDepth);

            float4 KeepDepthFragment(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                return SAMPLE_TEXTURE2D_X_LOD(_MunShotDepth, sampler_PointClamp, input.texcoord, 0).r;
            }
            ENDHLSL
        }

        Pass
        {
            Name "KerbalInk"
            ZTest Always
            Cull Back

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex KerbalInkVertex
            #pragma fragment KerbalInkFragment

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Color.hlsl"

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);
            float4 _MainTex_ST;

            // 1: the whole mesh is ink (dark hair); 0: where its own texture is dark (an eye's pupil)
            float _MunShotSolid;

            // The depth once everything solid is drawn: only what's seen there is inked (not behind an eyelid or a hand,
            // nor hair the helmet hides)
            TEXTURE2D_X_FLOAT(_MunShotOpaqueDepth);

            // A pupil is where the eye's own texture is darker than PupilDark, fading out by PupilFade
            static const float PupilDark = 0.15;
            static const float PupilFade = 0.25;
            // How near (for its distance) the drawn depth must be for this to be what's seen
            static const float SeenWithin = 0.002;

            struct KerbalInkAttributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct KerbalInkVaryings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            KerbalInkVaryings KerbalInkVertex(KerbalInkAttributes input)
            {
                KerbalInkVaryings output;
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.uv = TRANSFORM_TEX(input.uv, _MainTex);
                return output;
            }

            float4 KerbalInkFragment(KerbalInkVaryings input) : SV_Target
            {
                float own = LinearEyeDepth(input.positionCS.z, _ZBufferParams);
                float seen = LinearEyeDepth(LOAD_TEXTURE2D_X(_MunShotOpaqueDepth, input.positionCS.xy).r, _ZBufferParams);
                if (abs(own - seen) > SeenWithin * own + SeenWithin) discard;
                if (_MunShotSolid > 0) return 1;
                float brightness = Luminance(SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv).rgb);
                return 1 - smoothstep(PupilDark, PupilFade, brightness);
            }
            ENDHLSL
        }

        Pass
        {
            Name "Leaves"

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment LeavesFragment
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareNormalsTexture.hlsl"
            #include "InkLines.hlsl"

            // 1 where lines crowd (unbent, as the depth)
            float4 LeavesFragment(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                return Crowded(input.texcoord) ? 1 : 0;
            }
            ENDHLSL
        }

        Pass
        {
            Name "FarSurface"
            ZTest Always
            Cull Off

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex FarSurfaceVertex
            #pragma fragment FarSurfaceFragment

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            // The planet's packed map, from its material in Redux's faraway view: its alpha is how smooth the surface
            // is (the seas smooth, the land rough; black for a planet with no sea, which has none); and its normal map,
            // which way its surface leans from the smooth sphere
            TEXTURE2D(_PackedMap);
            SAMPLER(sampler_PackedMap);
            TEXTURE2D(_NormalMap);
            SAMPLER(sampler_NormalMap);
            float4 _NormalMap_ST;

            // That view's camera: its view and projection (as drawn into a texture), and where it is
            float4x4 _MunShotFarViewProjection;
            float4 _MunShotFarEye;

            struct FarSurfaceAttributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct FarSurfaceVaryings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
            };

            FarSurfaceVaryings FarSurfaceVertex(FarSurfaceAttributes input)
            {
                FarSurfaceVaryings output;
                output.positionWS = mul(UNITY_MATRIX_M, float4(input.positionOS.xyz, 1)).xyz;
                output.positionCS = mul(_MunShotFarViewProjection, float4(output.positionWS, 1));
                output.uv = input.uv;
                return output;
            }

            // Where the faraway view sees the planet's surface (g 1): how smooth it is there (r), and which way it
            // leans (ba: its normal map's x and y, -1 to 1 as 0 to 1). Only its near side, as it's drawn without depth
            float4 FarSurfaceFragment(FarSurfaceVaryings input) : SV_Target
            {
                float3 centre = UNITY_MATRIX_M._m03_m13_m23;
                if (dot(input.positionWS - centre, _MunShotFarEye.xyz - input.positionWS) <= 0) discard;
                float smooth = SAMPLE_TEXTURE2D(_PackedMap, sampler_PackedMap, input.uv).a;
                float2 lean = UnpackNormalMapRGorAG(SAMPLE_TEXTURE2D(_NormalMap, sampler_NormalMap,
                                                                     TRANSFORM_TEX(input.uv, _NormalMap))).xy;
                return float4(smooth, 1, lean * 0.5 + 0.5);
            }
            ENDHLSL
        }

        Pass
        {
            Name "Effects"

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment EffectsFragment

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            // Just before and just after something is drawn: the picture, either side of Redux's exhaust and smoke; or
            // the surfaces' colours, either side of the planet's terrain
            TEXTURE2D_X(_MunShotBeforeEffects);
            TEXTURE2D_X(_MunShotAfterEffects);

            // How much a pixel must change, for how bright it is, to be just touched by what was drawn, and wholly
            // covered
            static const float EffectsFaint = 0.05;
            static const float EffectsThick = 0.5;

            // How much what was drawn covers the pixel: how much it changed it, for how bright it is either way
            float4 EffectsFragment(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float3 before = SAMPLE_TEXTURE2D_X_LOD(_MunShotBeforeEffects, sampler_PointClamp, input.texcoord, 0).rgb;
                float3 after = SAMPLE_TEXTURE2D_X_LOD(_MunShotAfterEffects, sampler_PointClamp, input.texcoord, 0).rgb;
                float3 change = abs(after - before);
                float brightest = max(Max3(before.r, before.g, before.b), Max3(after.r, after.g, after.b));
                float covered = Max3(change.r, change.g, change.b) / max(brightest, 1e-4);
                return smoothstep(EffectsFaint, EffectsThick, covered);
            }
            ENDHLSL
        }

        Pass
        {
            Name "CelEffects"

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment CelEffectsFragment
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Color.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareNormalsTexture.hlsl"
            #include "InkLines.hlsl"

            // The picture just before Redux draws the engines' exhaust and smoke, and a copy of it just after
            TEXTURE2D_X(_MunShotBeforeEffects);
            TEXTURE2D_X(_MunShotPicture);

            // How much of the flat tones the clear sky gets (0 to 1): none near the ground, all of them from orbit
            float _MunShotSkyBands;


            // The smoke's colour is evened out over SmokeRings rings of SmokeDirections out to SmokePixels round each
            // pixel (only what's in the cloud), so its grain doesn't show, then split into tones ToneStops apart (each
            // twice as bright as the last: as many for a dim cloud as a bright one)
            static const float SmokePixels = 6;
            static const int SmokeRings = 2;
            static const int SmokeDirections = 8;
            static const float ToneStops = 1;

            float4 Picture(float2 uv)
            {
                return SAMPLE_TEXTURE2D_X_LOD(_MunShotPicture, sampler_PointClamp, uv, 0);
            }

            // A colour in flat tones: its brightness to the middle of its tone, its hue kept
            float3 Toned(float3 color)
            {
                float brightness = max(Luminance(color), 1e-6);
                float tone = exp2((floor(log2(brightness) / ToneStops) + 0.5) * ToneStops);
                return color * (tone / brightness);
            }

            // Inside the smoke cloud, the smoke and fire in flat tones. Outside it: the planet's clouds in flat tones,
            // as they are (not evened out); the clear sky (where nothing has depth: the atmosphere's glow, the planet
            // from far off) as it is near the ground, in flat tones from orbit (not evened out either, so the stars
            // stay); and anything else as it was before the smoke (no thin wisps or shimmer round its cloud)
            float4 CelEffectsFragment(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float2 uv = input.texcoord;
                float4 own = Picture(uv);
                if (!InSmoke(uv))
                {
                    if (CloudsAt(uv) > CloudsEdge) return float4(Toned(own.rgb), own.a);
                    if (IsSky(SampleSceneDepth(uv))) return float4(lerp(own.rgb, Toned(own.rgb), _MunShotSkyBands), own.a);
                    return SAMPLE_TEXTURE2D_X_LOD(_MunShotBeforeEffects, sampler_PointClamp, uv, 0);
                }

                float3 sum = own.rgb;
                float weight = 1;
                [unroll]
                for (int ring = 1; ring <= SmokeRings; ring++)
                {
                    float radius = SmokePixels * ring / SmokeRings;
                    [unroll]
                    for (int d = 0; d < SmokeDirections; d++)
                    {
                        float angle = (d + 0.5 * (ring & 1)) * (6.2831853 / SmokeDirections);
                        float2 at = uv + float2(cos(angle), sin(angle)) * radius * _MunShotPixel.xy;
                        if (!InSmoke(at)) continue;
                        sum += Picture(at).rgb;
                        weight += 1;
                    }
                }
                return float4(Toned(sum / weight), own.a);
            }
            ENDHLSL
        }
    }
}
