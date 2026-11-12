// MunShot's ink lines, shared by the CelShading shader's Outlines pass (over the Cel shaded picture) and its Paper pass
// (the Ink filter: the lines alone, on white), from the camera's depth (the water's surface over it) and normals. Solid
// lines of two weights: heavier where the depth jumps (an outline, a step, the planet's edge) or the water meets the
// land, at any distance; lighter where the surface turns at a corner. From high above the planet, its coasts are
// faded.
//
// Needs URP's Core, DeclareDepthTexture and DeclareNormalsTexture included first.
#ifndef MUNSHOT_INK_LINES_INCLUDED
#define MUNSHOT_INK_LINES_INCLUDED

// The game draws the ocean's surface into a depth texture of its own each frame (KSP's WaterManager, cleared to
// nothing when there's no water) and sets it for every shader
TEXTURE2D_FLOAT(_GlobalWaterDepthTexture);

// xy: one pixel (of a picture 1080 high), as a fraction of the picture's width and height
float4 _MunShotPixel;

// How far apart neighbouring depths and facings are compared (in pixels)
static const float LinePixels = 1;
// A line where the depth bends by more than DepthBend (for its distance: an outline, a step), or where the surface
// turns by more than about 40 degrees (Crease, as 1 - the cosine: a corner, a fold)
static const float DepthBend = 0.08;
static const float Crease = 0.25;
// Where lines would crowd (more than Busy of the points BusyPixels round a pixel are on an edge too: inside a tree's
// leaves), only an outline against something far behind it (bending more than OutlineBend: a tree against the sky or
// what's well beyond it, not one leaf against the next) is drawn
static const float BusyPixels = 4;
static const int BusyPoints = 8;
static const float Busy = 0.5;
static const float OutlineBend = 1;
static const float3 Ink = float3(0.02, 0.02, 0.025);
// How wide the lines are (in pixels): outlines OutlineWidth, corners InnerWidth, each with a pixel's soft edge. A
// corner line is where the surface turns by more than InnerCrease (as 1 - the cosine: about 50 degrees, a real corner,
// not a gentle bend)
static const float OutlineWidth = 2;
static const float InnerWidth = 1;
static const float InnerCrease = 0.35;
// The planet the camera is near, in the world the camera is in: xyz its centre, w its radius (0 when there's none);
// and where the camera is
float4 _MunShotPlanet;
float4 _MunShotEye;
// The planet as Redux draws it first (its ScaledSpaceViewPass: a scaled-down copy of the universe, from a camera of
// its own, writing no depth): that camera, from the picture back to its world (the inverse of its view and
// projection), and where it is; the copy's centre and radius there, to the top of its highest cloud layer (w 0 when
// there's none); and how many metres one of that world's units is
float4x4 _MunShotFarLook;
float4 _MunShotFarEye;
float4 _MunShotFarPlanet;
float _MunShotFarMetres;
// Where the faraway view sees the planet's surface (g 1), how smooth it is (r: its seas are smoother than Sea), so its
// coasts are drawn there too, past where the camera's own ground and water end (MunShot.Cameras.InkLines' FarSurface
// pass, from the planet's own packed map); _MunShotFarSea 0 when there's none, for a planet with no sea
TEXTURE2D_X(_MunShotFarSurface);
float _MunShotFarSea;
static const float Sea = 0.5;
// From more than HighUp metres above the planet, its own lines (its coasts, the colours on its ground) are drawn
// Faded of the way from the paper to the ink, and fade out over CoastFadePixels before its edge, looked for
// CoastFadeSteps times that far out in each of CoastFadeDirections
static const float HighUp = 20000;
static const float Faded = 0.35;
static const float CoastFadePixels = 48;
static const int CoastFadeSteps = 4;
static const int CoastFadeDirections = 8;

// How much of the ink the planet's own lines get: all of it, unless the camera is high above it
float PlanetInk()
{
    bool highUp = _MunShotPlanet.w > 0 && length(_MunShotEye.xyz - _MunShotPlanet.xyz) - _MunShotPlanet.w > HighUp;
    return highUp ? Faded : 1;
}

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

// 1 / distance from the camera (0 for the sky): across a flat surface this changes evenly over the screen, so it only
// bends at an edge or a step
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

// Whether a pixel is on the shore: a neighbour is water where it isn't, or land where it's water
bool Shore(float2 uv, float2 x, float2 y, bool water)
{
    bool shore = false;
    float2 around[4] = { -x, x, -y, y };
    [unroll]
    for (int i = 0; i < 4; i++)
    {
        bool atWater;
        float raw = RawDepth(uv + around[i], atWater);
        shore = shore || (!IsSky(raw) && atWater != water);
    }
    return shore;
}

// How strongly a pixel is on an edge (0 to 1): the depth bending, the surface turning (not on water) or the shore, and
// how much the depth bends there
float Edge(float2 uv, float2 x, float2 y, out float bend)
{
    bool water;
    float raw = RawDepth(uv, water);
    bend = 0;
    if (IsSky(raw)) return 0;
    if (Shore(uv, x, y, water)) return 1;
    bend = Bend(uv, x, y);
    float turn = 0;
    if (!water)
    {
        float3 normal = SampleSceneNormals(uv);
        turn = max(max(Turn(normal, uv - x), Turn(normal, uv + x)), max(Turn(normal, uv - y), Turn(normal, uv + y)));
    }
    return max(smoothstep(DepthBend, DepthBend * 1.5, bend), smoothstep(Crease, Crease * 1.3, turn));
}

// Whether lines would crowd round a pixel: more than Busy of the points BusyPixels round it are on an edge too, as
// inside a tree's leaves
bool Crowded(float2 uv)
{
    float2 x = float2(_MunShotPixel.x * LinePixels, 0);
    float2 y = float2(0, _MunShotPixel.y * LinePixels);
    float crowd = 0;
    [unroll]
    for (int i = 0; i < BusyPoints; i++)
    {
        float angle = i * (6.2831853 / BusyPoints);
        float unused;
        crowd += step(0.5, Edge(uv + float2(cos(angle), sin(angle)) * BusyPixels * _MunShotPixel.xy, x, y, unused));
    }
    return crowd > Busy * BusyPoints;
}

// Solid ink: an edge is drawn fully black, only its last sliver softened against jagged steps
float Solid(float edge)
{
    return saturate((edge - SolidFrom) / (1 - SolidFrom) * SolidSharpness);
}

// Where in the faraway view's world a pixel of the picture shows, somewhere along that view's camera's look through it
// (the picture's bottom is its uv's 0, as the camera's own projection has it)
float3 FarThrough(float2 uv)
{
    float4 world = mul(_MunShotFarLook, float4(uv * 2 - 1, 0.5, 1));
    return world.xyz / world.w;
}

// How far ahead of the camera (in metres, along its view, as the depth is) the planet the faraway view draws is at a
// pixel, or 0 where it isn't. The camera's own depth only has the ground near it; where that has the sky, this stands
// in for it, so the planet's edge is drawn where the picture shows it
float FarDepth(float2 uv)
{
    if (_MunShotFarPlanet.w <= 0) return 0;
    float3 toward = normalize(FarThrough(uv) - _MunShotFarEye.xyz);
    float3 toCentre = _MunShotFarPlanet.xyz - _MunShotFarEye.xyz;
    float along = dot(toCentre, toward);
    float miss = dot(toCentre, toCentre) - along * along;
    float radius = _MunShotFarPlanet.w * _MunShotFarPlanet.w;
    if (along <= 0 || miss >= radius) return 0;
    float ahead = along - sqrt(radius - miss);
    if (ahead <= 0) return 0;
    float3 forward = normalize(FarThrough(0.5) - _MunShotFarEye.xyz);
    return ahead * dot(toward, forward) * _MunShotFarMetres;
}

// How much of the ink the planet's own lines get at a pixel: all of it, unless the camera is high above the planet;
// then Faded, fading out towards its edge (the nearest of them past it)
float CoastInk(float2 uv)
{
    float ink = PlanetInk();
    if (ink >= 1) return 1;
    float clear = 1;
    [unroll]
    for (int step = 1; step <= CoastFadeSteps; step++)
    {
        float reach = step / (float)CoastFadeSteps;
        [unroll]
        for (int d = 0; d < CoastFadeDirections; d++)
        {
            float angle = d * (6.2831853 / CoastFadeDirections);
            bool water;
            float2 at = uv + float2(cos(angle), sin(angle)) * reach * CoastFadePixels * _MunShotPixel.xy;
            if (IsSky(RawDepth(at, water)) && FarDepth(at) <= 0) clear = min(clear, reach - 1.0 / CoastFadeSteps);
        }
    }
    return ink * smoothstep(0, 1, clear);
}

// How much the surface turns towards a neighbour (0 to 2), which is solid ground: not the sky (w 0) nor water
float TurnTo(float3 normal, float3 neighbour, float w, bool water)
{
    return w > 0 && !water ? 1 - dot(normal, neighbour) : 0;
}

// How much ink the lines give a pixel (0 to 1); where they'd crowd (a tree's leaves), only the outlines against what's
// far behind (bending more than OutlineBend). Every pixel within half a line's width of an edge is inked, so a line
// stays whole where a slanted edge steps from one row of pixels to the next; and the sky beside something is inked as
// the far side of a step is, so an outline against the sky (the planet's edge) is as heavy as any other
float LinesAt(float2 uv, bool crowded)
{
    // The depth (as 1 / distance, 0 for the sky), whether it's water, whether it's the faraway view's (no corner is told
    // there, and its shore is from its own sea, where its surface is drawn), and the facing, 5 by 5 round the pixel
    float w[5][5];
    bool wet[5][5];
    bool far[5][5];
    bool farSurface[5][5];
    bool farSea[5][5];
    float3 facing[5][5];
    [unroll]
    for (int i = 0; i < 5; i++)
    {
        [unroll]
        for (int j = 0; j < 5; j++)
        {
            float2 at = uv + float2(i - 2, j - 2) * _MunShotPixel.xy * LinePixels;
            bool water;
            w[i][j] = InverseDepth(RawDepth(at, water));
            far[i][j] = false;
            if (w[i][j] <= 0)
            {
                float faraway = FarDepth(at);
                far[i][j] = faraway > 0;
                if (far[i][j]) w[i][j] = 1.0 / faraway;
            }
            wet[i][j] = water || far[i][j];
            float2 surface = far[i][j] && _MunShotFarSea > 0
                ? SAMPLE_TEXTURE2D_X_LOD(_MunShotFarSurface, sampler_PointClamp, at, 0).rg
                : 0;
            farSurface[i][j] = surface.g > 0.5;
            farSea[i][j] = surface.r > Sea;
            facing[i][j] = SampleSceneNormals(at);
        }
    }

    // Corner lines, not where lines crowd
    float inner = w[2][2] > 0 && !crowded ? 1 : 0;

    // Each of the 3 by 3 round the pixel: what edge it's on, and how much of this pixel that edge's line covers (a
    // coast's, as much as the planet's own lines get there)
    float ink = 0;
    float coast = 0;
    [unroll]
    for (int a = 1; a <= 3; a++)
    {
        [unroll]
        for (int b = 1; b <= 3; b++)
        {
            float here = w[a][b];
            float distance = length(float2(a - 2, b - 2));
            if (here <= 0)
            {
                if (w[a - 1][b] > 0 || w[a + 1][b] > 0 || w[a][b - 1] > 0 || w[a][b + 1] > 0)
                    ink = max(ink, saturate(OutlineWidth * 0.5 + 0.5 - distance));
                continue;
            }
            float bend = max(abs(w[a - 1][b] + w[a + 1][b] - 2 * here), abs(w[a][b - 1] + w[a][b + 1] - 2 * here)) / here;
            bool shore = !far[a][b]
                         ? (w[a - 1][b] > 0 && !far[a - 1][b] && wet[a - 1][b] != wet[a][b])
                           || (w[a + 1][b] > 0 && !far[a + 1][b] && wet[a + 1][b] != wet[a][b])
                           || (w[a][b - 1] > 0 && !far[a][b - 1] && wet[a][b - 1] != wet[a][b])
                           || (w[a][b + 1] > 0 && !far[a][b + 1] && wet[a][b + 1] != wet[a][b])
                         : farSurface[a][b]
                           && ((farSurface[a - 1][b] && farSea[a - 1][b] != farSea[a][b])
                               || (farSurface[a + 1][b] && farSea[a + 1][b] != farSea[a][b])
                               || (farSurface[a][b - 1] && farSea[a][b - 1] != farSea[a][b])
                               || (farSurface[a][b + 1] && farSea[a][b + 1] != farSea[a][b]));
            float turn = 0;
            if (!wet[a][b])
            {
                float3 n = facing[a][b];
                turn = max(max(TurnTo(n, facing[a - 1][b], w[a - 1][b], wet[a - 1][b]),
                               TurnTo(n, facing[a + 1][b], w[a + 1][b], wet[a + 1][b])),
                           max(TurnTo(n, facing[a][b - 1], w[a][b - 1], wet[a][b - 1]),
                               TurnTo(n, facing[a][b + 1], w[a][b + 1], wet[a][b + 1])));
            }
            // Against the sky, always an outline. Where the camera's own ground meets the faraway view's planet, the two
            // don't quite match: only an outline against something far behind (a ship over the planet) is drawn there
            bool toSky = w[a - 1][b] <= 0 || w[a + 1][b] <= 0 || w[a][b - 1] <= 0 || w[a][b + 1] <= 0;
            bool toFar = far[a][b] || far[a - 1][b] || far[a + 1][b] || far[a][b - 1] || far[a][b + 1];
            if (toSky || bend > (crowded || toFar ? OutlineBend : DepthBend))
                ink = max(ink, saturate(OutlineWidth * 0.5 + 0.5 - distance));
            else if (shore)
                coast = max(coast, saturate(OutlineWidth * 0.5 + 0.5 - distance));
            else if (turn > InnerCrease)
                ink = max(ink, inner * saturate(InnerWidth * 0.5 + 0.5 - distance));
        }
    }
    return coast > 0 ? max(ink, coast * CoastInk(uv)) : ink;
}

#endif
