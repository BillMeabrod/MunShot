// What the Ink filter's paper knows of the picture, for the CelShading shader's Paper pass, beyond the lines
// (InkLines.hlsl) and the ground lines (InkGround.hlsl): the fisheye, the finished picture, the kerbals' own ink, and
// where a tree's leaves are (MunShot.Cameras.InkLines' Leaves pass, just before the paper).
//
// Needs URP's Core, DeclareDepthTexture and DeclareNormalsTexture, and InkLines.hlsl, included first.
#ifndef MUNSHOT_INK_PAPER_INCLUDED
#define MUNSHOT_INK_PAPER_INCLUDED

#include "InkGround.hlsl"

// The fisheye, as URP's lens distortion draws it (UberPost.shader, DistortUV): x its angle, y its sigma, z how far it
// zooms in; x is 0 with no fisheye
float4 _MunShotLens;

// The ink's colour (the lines and the black) and the paper's, as the player picked them (linear)
float4 _MunShotInkColor;
float4 _MunShotPaperColor;

// Taken earlier in the frame by MunShot.Cameras.InkCapture, with what InkGround.hlsl has: the kerbals' own ink (pupils,
// dark hair), unbent. Without all of it (_MunShotTaken 0), the paper has the outlines alone
TEXTURE2D_X(_MunShotKerbalInk);

// The finished picture, bent by the fisheye as the screen is
TEXTURE2D_X(_MunShotPicture);

// Drawn just before the paper: where lines crowd (a tree's leaves), unbent
TEXTURE2D_X(_MunShotLeaves);

// Among leaves: most of what's within LeafyPixels of a pixel is where lines crowd
static const float LeafyPixels = 2;
// Solid black wherever the finished picture is darker than NearBlack (its brightness as stored), fading out by
// NearBlackFade
static const float NearBlack = 0.02;
static const float NearBlackFade = 0.035;

// Where on the depth and normals a pixel of the bent picture shows: r out from the middle (after zooming in) shows
// what was tan(r x) / (r y) of that out
float2 Unbend(float2 uv)
{
    if (_MunShotLens.x <= 0) return uv;
    float2 fromMiddle = (uv - 0.5) / _MunShotLens.z;
    float r = length(fromMiddle);
    if (r < 1e-6) return uv;
    return 0.5 + fromMiddle * (tan(r * _MunShotLens.x) / (r * _MunShotLens.y));
}

float LeavesAt(float2 uv)
{
    return SAMPLE_TEXTURE2D_X_LOD(_MunShotLeaves, sampler_LinearClamp, uv, 0).r;
}

// Whether a pixel is among a tree's leaves
bool Leafy(float2 uv)
{
    float2 x = float2(_MunShotPixel.x * LeafyPixels, 0);
    float2 y = float2(0, _MunShotPixel.y * LeafyPixels);
    return LeavesAt(uv) + LeavesAt(uv - x) + LeavesAt(uv + x) + LeavesAt(uv - y) + LeavesAt(uv + y) > 2.5;
}

// How much of a pixel (of the finished picture) is filled in black: 1 where it's near black, 0 where it isn't
float Fill(float2 screen)
{
    float brightness = Luminance(SAMPLE_TEXTURE2D_X_LOD(_MunShotPicture, sampler_PointClamp, screen, 0).rgb);
    return 1 - smoothstep(NearBlack, NearBlackFade, brightness);
}

#endif
