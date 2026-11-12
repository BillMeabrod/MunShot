// MunShot's paint lines, shared by the CelShading shader's Outlines pass (Cel shaded) and Paper pass (Ink): where a
// surface's own colour changes sharply, before any light (on the ground a path's kerb, a road marking; on a wall a logo,
// a painted band), from what MunShot.Cameras.InkCapture took earlier in the frame.
//
// Needs URP's Core and DeclareNormalsTexture, and InkLines.hlsl, included first.
#ifndef MUNSHOT_INK_GROUND_INCLUDED
#define MUNSHOT_INK_GROUND_INCLUDED

// Taken by InkCapture: the depth once the deferred pass has drawn the ground, buildings, trees and parts, and the colour
// of each of those surfaces before any light; and the depth once everything solid is drawn. All unbent, as the depth
// and normals are
TEXTURE2D_X_FLOAT(_MunShotSceneDepth);
TEXTURE2D_X(_MunShotAlbedo);
TEXTURE2D_X_FLOAT(_MunShotOpaqueDepth);

// 1 when all of that was taken; 0 otherwise, and there are no paint lines
float _MunShotTaken;

// Up from the ground, in the world the normals are in (away from the planet's centre)
float4 _MunShotUp;

// The ground faces up by more than Ground (the cosine); anything else drawn deferred stands (a wall, a beam, a post). A
// paint line is where a surface's own colour, evened out over a couple of pixels so a texture's grain doesn't count,
// changes by more than Paint (the distance between the two, each channel as how light it looks, its square root),
// fully black there and nothing short of it, so its edges are crisp: on the ground GroundPaint (white paint on grey
// tarmac is about 0.2), on what stands WallPaint, as a wall's own grime and shading would hatch it otherwise (a logo's
// red ring on a grey wall is about 0.3)
static const float Ground = 0.8;
static const float GroundPaint = 0.15;
static const float WallPaint = 0.25;
// Leaves and grass: where a colour's green (as it looks) is more than Green times its red and its blue. A change from
// one green to another (light and dark grass, a tree's leaves) is no line
static const float Green = 1.07;

float Kept(TEXTURE2D_X_FLOAT(depth), float2 uv)
{
    return SAMPLE_TEXTURE2D_X_LOD(depth, sampler_PointClamp, uv, 0).r;
}

// Whether the deferred pass drew what's at a pixel (not something drawn after it, as a kerbal, nor the sky)
bool DrawnDeferred(float2 uv)
{
    float raw = Kept(_MunShotOpaqueDepth, uv);
    return !IsSky(raw) && abs(raw - Kept(_MunShotSceneDepth, uv)) < 1e-7;
}

bool FacingUp(float2 uv)
{
    return dot(SampleSceneNormals(uv), _MunShotUp.xyz) > Ground;
}

bool OnGround(float2 uv)
{
    return DrawnDeferred(uv) && FacingUp(uv);
}

bool OnStanding(float2 uv)
{
    return DrawnDeferred(uv) && !FacingUp(uv);
}

// The ground's own colour round a pixel: four in-between samples, each the mean of the four pixels it falls among
float3 Paint3(float2 uv)
{
    float2 nudge = 0.5 * _MunShotPixel.xy;
    float3 albedo = SAMPLE_TEXTURE2D_X_LOD(_MunShotAlbedo, sampler_LinearClamp, uv + float2(-nudge.x, -nudge.y), 0).rgb
                    + SAMPLE_TEXTURE2D_X_LOD(_MunShotAlbedo, sampler_LinearClamp, uv + float2(nudge.x, -nudge.y), 0).rgb
                    + SAMPLE_TEXTURE2D_X_LOD(_MunShotAlbedo, sampler_LinearClamp, uv + float2(-nudge.x, nudge.y), 0).rgb
                    + SAMPLE_TEXTURE2D_X_LOD(_MunShotAlbedo, sampler_LinearClamp, uv + float2(nudge.x, nudge.y), 0).rgb;
    return sqrt(max(albedo * 0.25, 0));
}

bool IsGreen(float3 looks)
{
    return looks.g > max(looks.r, looks.b) * Green;
}

// How much a surface's own colour changes across a pixel, from one side to the other (step each way): the two sides'
// colours are evened out over patches that don't overlap, so an edge counts in full. Only where both sides are the same
// kind of surface (both ground, or both standing: where a wall meets the ground the outline has it), and not where both
// are green (light and dark grass, a tree's leaves)
float ChangeAcross(float2 uv, float2 step, bool ground)
{
    if (ground ? !OnGround(uv - step) || !OnGround(uv + step) : !OnStanding(uv - step) || !OnStanding(uv + step)) return 0;
    float3 before = Paint3(uv - step);
    float3 after = Paint3(uv + step);
    return IsGreen(before) && IsGreen(after) ? 0 : length(after - before);
}

// How much ink a paint line gives a pixel (0 to 1); none on what stands where lines crowd (a tree's leaves), and on the
// ground as much as the planet's own lines get (faded from high above it, and towards its edge)
float PaintLineAt(float2 uv, bool crowded)
{
    if (_MunShotTaken <= 0 || !DrawnDeferred(uv)) return 0;
    bool ground = FacingUp(uv);
    if (!ground && crowded) return 0;
    if (_MunShotLineDetail <= 0) return 0;
    float2 x = float2(_MunShotPixel.x * LinePixels, 0);
    float2 y = float2(0, _MunShotPixel.y * LinePixels);
    float change = max(ChangeAcross(uv, x, ground), ChangeAcross(uv, y, ground));
    float fade = 1 + (1 - saturate(_MunShotLineDetail)) * PaintFade;
    if (change <= (ground ? GroundPaint : WallPaint) * fade) return 0;
    return ground ? CoastInk(uv) : 1;
}

#endif
