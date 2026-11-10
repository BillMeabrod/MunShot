// MunShot's Cel shaded filter, drawn by MunShot.Cameras.CelShading.
//
// Flat pass: right after the game's deferred lighting, every pixel it lit is drawn again as a cartoon from URP's
// G-buffer (the surface's own colour and facing, before any light):
// - Flat colour: the pixel's colour averaged with the pixels around it on the same surface (near in depth, facing the
//   same way) whose colour is close to its own, so a texture's detail evens out while edges between colours stay sharp.
// - Two tones: the surface's facing, smoothed the same way, lit by the sun (with the game's shadows, cloud shadows and
//   the night): lit, or in shade, with a one-pixel edge between.
// The sky is left as it is.
//
// Outlines pass: before the post-processing, dark lines of an even width where the depth jumps or the surface turns
// sharply, from the camera's depth (the water's surface over it) and normals.
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

            // How far round each pixel its colour and facing are evened out (in pixels), in rings of samples
            static const float FlatRadius = 32;
            static const int FlatRings = 4;
            static const int FlatDirections = 16;
            // A neighbour counts as the same surface when it's this near in depth (for its distance) and faces within
            // about 35 degrees the same way; its colour counts when it's within Similar of the pixel's (0 to 1 a channel)
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
                float3 facing = Facing(uv);
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
                        float close = same * (1 - smoothstep(Similar, Similar * 1.5, length(c - own)));
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
                float3 light = sunColor * lerp(Shade, LitShare, lit)
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

            // The game draws the ocean's surface into a depth texture of its own each frame (KSP's WaterManager,
            // cleared to nothing when there's no water) and sets it for every shader
            TEXTURE2D_FLOAT(_GlobalWaterDepthTexture);

            // xy: one pixel (of a picture 1080 high), as a fraction of the picture's width and height
            float4 _MunShotPixel;

            // How far either side of a pixel its neighbours are compared (in pixels), so a line is about twice as wide
            static const float LinePixels = 1;
            // A line where the depth bends by more than DepthBend (for its distance: an outline, a step), or where the
            // surface turns by more than about 40 degrees (Crease, as 1 - the cosine: a corner, a fold)
            static const float DepthBend = 0.08;
            static const float Crease = 0.25;
            // Where lines would crowd (more than Busy of the points BusyPixels round a pixel are on an edge too: inside a
            // tree's leaves), only an outline against something well behind it (bending more than OutlineBend) is drawn
            static const float BusyPixels = 4;
            static const int BusyPoints = 8;
            static const float Busy = 0.5;
            static const float OutlineBend = 0.3;
            static const float3 Ink = float3(0.02, 0.02, 0.025);
            // How much of an edge counts before it's inked, and how quickly it goes from none to fully black
            static const float SolidFrom = 0.2;
            static const float SolidSharpness = 4;

            // The nearer of the camera's depth and the water's surface, and whether it's the water's
            float RawDepth(float2 uv, out bool water)
            {
                float scene = SampleSceneDepth(uv);
                float waterDepth = SAMPLE_TEXTURE2D_LOD(_GlobalWaterDepthTexture, sampler_PointClamp, uv, 0).r;
            #if UNITY_REVERSED_Z
                water = waterDepth > scene;
                return max(scene, waterDepth);
            #else
                water = waterDepth < scene;
                return min(scene, waterDepth);
            #endif
            }

            bool IsSky(float raw)
            {
            #if UNITY_REVERSED_Z
                return raw <= 1e-7;
            #else
                return raw >= 1 - 1e-7;
            #endif
            }

            // 1 / distance from the camera (0 for the sky): across a flat surface this changes evenly over the screen,
            // so it only bends at an edge or a step
            float InverseDepth(float raw)
            {
                return IsSky(raw) ? 0 : 1.0 / max(LinearEyeDepth(raw, _ZBufferParams), 1e-4);
            }

            // How much the depth bends at a pixel, for its distance: 0 across a flat surface
            float Bend(float2 uv, float2 x, float2 y)
            {
                bool unused;
                float w = InverseDepth(RawDepth(uv, unused));
                if (w <= 0) return 0;
                float across = abs(InverseDepth(RawDepth(uv - x, unused)) + InverseDepth(RawDepth(uv + x, unused)) - 2 * w);
                float down = abs(InverseDepth(RawDepth(uv - y, unused)) + InverseDepth(RawDepth(uv + y, unused)) - 2 * w);
                return max(across, down) / w;
            }

            // How much the surface turns between the pixel and a neighbour (0 to 2), where both are solid ground
            float Turn(float3 normal, float2 at)
            {
                bool water;
                float raw = RawDepth(at, water);
                if (water || IsSky(raw)) return 0;
                return 1 - dot(normal, SampleSceneNormals(at));
            }

            // How strongly a pixel is on an edge (0 to 1): the depth bending, or the surface turning (not on water), and
            // how much the depth bends there
            float Edge(float2 uv, float2 x, float2 y, out float bend)
            {
                bool water;
                float raw = RawDepth(uv, water);
                bend = 0;
                if (IsSky(raw)) return 0;
                bend = Bend(uv, x, y);
                float turn = 0;
                if (!water)
                {
                    float3 normal = SampleSceneNormals(uv);
                    turn = max(max(Turn(normal, uv - x), Turn(normal, uv + x)), max(Turn(normal, uv - y), Turn(normal, uv + y)));
                }
                return max(smoothstep(DepthBend, DepthBend * 1.5, bend), smoothstep(Crease, Crease * 1.3, turn));
            }

            float4 OutlinesFragment(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float2 uv = input.texcoord;
                float2 x = float2(_MunShotPixel.x * LinePixels, 0);
                float2 y = float2(0, _MunShotPixel.y * LinePixels);
                float bend;
                float ink = Edge(uv, x, y, bend);
                if (ink <= 0) discard;

                // Crowded (a tree's leaves, each a little apart in depth and facing its own way): only the outline
                // where it stands clear of what's behind
                float crowd = 0;
                [unroll]
                for (int i = 0; i < BusyPoints; i++)
                {
                    float angle = i * (6.2831853 / BusyPoints);
                    float unused;
                    crowd += step(0.5, Edge(uv + float2(cos(angle), sin(angle)) * BusyPixels * _MunShotPixel.xy, x, y, unused));
                }
                if (crowd > Busy * BusyPoints) ink = smoothstep(OutlineBend, OutlineBend * 1.5, bend);
                // Solid ink: an edge is drawn fully black, only its last sliver softened against jagged steps
                ink = saturate((ink - SolidFrom) / (1 - SolidFrom) * SolidSharpness);
                if (ink <= 0) discard;
                return float4(Ink, ink);
            }
            ENDHLSL
        }
    }
}
