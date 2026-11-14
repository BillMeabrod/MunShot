// MunShot's ink lines, shared by the CelShading shader's Outlines pass (over the Cel shaded picture) and its Paper pass
// (the Ink filter: the lines alone, on white), from the camera's depth (the water's surface over it) and normals. Solid
// lines of two weights: heavier where the depth jumps (an outline, a step, the planet's edge) or the water meets the
// land, at any distance; lighter where the surface turns at a corner, or where the planet's ground turns steep (a
// crater's wall, a ridge). The planet's own lines (its coasts and slopes, and the outlines and corners on its terrain)
// fade, more the higher the camera is. Where the engines' exhaust and smoke cover them, none: the cartoon smoke cloud
// they're drawn as is outlined instead. Faded where the planet's clouds cover them.
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
// How much detail the lines show, as the player sets it (MunShot.Cameras.PhotoLook): 1 all of it; less raises the bar
// for the corner, slope and paint lines (corners must turn up to CornerFade more, slopes be steeper, up to the cosine
// SlopeFadeCos, and paint change up to PaintFade times more), until at 0 only outlines, coasts and the planet's edge
// are left
float _MunShotLineDetail;
static const float CornerFade = 0.65;
static const float SlopeFadeCos = 0.71;
static const float PaintFade = 3;
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
// Where the faraway view sees the planet's surface (g 1), how smooth it is (r: its seas are smoother than Sea), and
// which way its surface leans from the smooth sphere (ba, from its normal map: -1 to 1 as 0 to 1), so its coasts and
// slopes are drawn there too, past where the camera's own ground and water end (MunShot.Cameras.InkLines' FarSurface
// pass, from the planet's own maps); _MunShotHasFarSurface 0 when it wasn't drawn, _MunShotFarSea 0 for a planet with
// no sea
TEXTURE2D_X(_MunShotFarSurface);
float _MunShotHasFarSurface;
float _MunShotFarSea;
static const float Sea = 0.5;
// Where the deferred pass drew the planet's own terrain (MunShot.Cameras.InkCapture's), when it was taken, so its
// slopes are outlined and not a ship's or a building's
TEXTURE2D_X(_MunShotTerrain);
float _MunShotHasTerrain;
// The planet's ground is steep where it leans more than about 20 degrees from level (as the cosine of that), with all
// the detail: a line is drawn where steep ground meets level (a crater's wall, a ridge, a cliff)
static const float SteepCos = 0.94;
// From FadeFrom metres above the planet, its own lines (its coasts, the colours on its ground) fade, to Faded of the
// way from the paper to the ink by HighUp; and then fade out over CoastFadePixels before its edge, looked for
// CoastFadeSteps times that far out in each of CoastFadeDirections
static const float FadeFrom = 2000;
static const float HighUp = 20000;
static const float Faded = 0.35;
static const float CoastFadePixels = 48;
static const int CoastFadeSteps = 4;
static const int CoastFadeDirections = 8;

// How much of the ink the planet's own lines get: all of it near the ground, less the higher the camera is above it
float PlanetInk()
{
    if (_MunShotPlanet.w <= 0) return 1;
    float height = length(_MunShotEye.xyz - _MunShotPlanet.xyz) - _MunShotPlanet.w;
    return lerp(1, Faded, smoothstep(FadeFrom, HighUp, height));
}

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

// How strongly a pixel is on an edge (0 to 1): the depth bending, the surface turning (not on water) or the shore
float Edge(float2 uv, float2 x, float2 y)
{
    bool water;
    float raw = RawDepth(uv, water);
    if (IsSky(raw)) return 0;
    if (Shore(uv, x, y, water)) return 1;
    float bend = Bend(uv, x, y);
    float turn = 0;
    if (!water)
    {
        float3 normal = SampleSceneNormals(uv);
        turn = max(max(Turn(normal, uv - x), Turn(normal, uv + x)), max(Turn(normal, uv - y), Turn(normal, uv + y)));
    }
    return max(smoothstep(DepthBend, DepthBend * 1.5, bend), smoothstep(Crease, Crease * 1.3, turn));
}

// Whether a pixel is on an edge, for where lines crowd (MunShot.Cameras.InkLines' Edges pass draws it for every pixel)
bool OnEdge(float2 uv)
{
    return Edge(uv, float2(_MunShotPixel.x * LinePixels, 0), float2(0, _MunShotPixel.y * LinePixels)) >= 0.5;
}

// What the Edges pass drew, and where lines crowd as the Leaves pass drew it from that (1, 0 elsewhere): both unbent,
// as the depth
TEXTURE2D_X(_MunShotEdges);
TEXTURE2D_X(_MunShotLeaves);

// Whether lines would crowd round a pixel: more than Busy of the points BusyPixels round it are on an edge too, as
// inside a tree's leaves (the Leaves pass's)
bool Crowded(float2 uv)
{
    float crowd = 0;
    [unroll]
    for (int i = 0; i < BusyPoints; i++)
    {
        float angle = i * (6.2831853 / BusyPoints);
        float2 at = uv + float2(cos(angle), sin(angle)) * BusyPixels * _MunShotPixel.xy;
        crowd += SAMPLE_TEXTURE2D_X_LOD(_MunShotEdges, sampler_PointClamp, at, 0).r;
    }
    return crowd > Busy * BusyPoints;
}

float LeavesAt(float2 uv)
{
    return SAMPLE_TEXTURE2D_X_LOD(_MunShotLeaves, sampler_LinearClamp, uv, 0).r;
}

// How much the engines' exhaust and smoke cover each pixel (MunShot.Cameras.InkCapture's Effects pass), for its Smoke
// pass; and where the cartoon smoke cloud is as that drew it (1 inside), when it was taken
TEXTURE2D_X(_MunShotEffects);
TEXTURE2D_X(_MunShotSmoke);
float _MunShotHasSmoke;

// The exhaust and smoke are drawn as a cartoon smoke cloud with a hard edge: inside it where they cover more than
// SmokeEdge, evened out over SmokeEdgePixels round the pixel so the edge is smooth rather than grainy (the Smoke pass's)
static const float SmokeEdge = 0.5;
static const float SmokeEdgePixels = 2;

float EffectsAt(float2 uv)
{
    return SAMPLE_TEXTURE2D_X_LOD(_MunShotEffects, sampler_LinearClamp, uv, 0).r;
}

bool SmokeCovers(float2 uv)
{
    float2 d = _MunShotPixel.xy * SmokeEdgePixels;
    float cover = (2 * EffectsAt(uv) + EffectsAt(uv + d) + EffectsAt(uv - d)
                   + EffectsAt(uv + float2(d.x, -d.y)) + EffectsAt(uv + float2(-d.x, d.y))) / 6;
    return cover > SmokeEdge;
}

// Whether a pixel is inside the smoke cloud, so the lines behind it don't show through
bool InSmoke(float2 uv)
{
    return _MunShotHasSmoke > 0 && SAMPLE_TEXTURE2D_X_LOD(_MunShotSmoke, sampler_PointClamp, uv, 0).r > 0.5;
}

// How much ink the smoke cloud's outline gives a pixel (0 to 1): every pixel within half an outline's width of its
// edge, as LinesAt draws an outline
float SmokeOutlineAt(float2 uv)
{
    if (_MunShotHasSmoke <= 0) return 0;
    bool inside[5][5];
    [unroll]
    for (int i = 0; i < 5; i++)
    {
        [unroll]
        for (int j = 0; j < 5; j++)
            inside[i][j] = InSmoke(uv + float2(i - 2, j - 2) * _MunShotPixel.xy * LinePixels);
    }
    float ink = 0;
    [unroll]
    for (int a = 1; a <= 3; a++)
    {
        [unroll]
        for (int b = 1; b <= 3; b++)
        {
            bool edge = inside[a][b] && (!inside[a - 1][b] || !inside[a + 1][b] || !inside[a][b - 1] || !inside[a][b + 1]);
            if (edge) ink = max(ink, saturate(OutlineWidth * 0.5 + 0.5 - length(float2(a - 2, b - 2))));
        }
    }
    return ink;
}

// Where Redux's faraway view shows a distant planet or moon (MunShot.Cameras.InkCapture's), when it was taken: Cel
// shaded puts them in flat tones, and both filters outline them where the camera sees nothing nearer (the sky)
TEXTURE2D_X(_MunShotBodies);
float _MunShotHasBodies;

bool BodyAt(float2 uv)
{
    return _MunShotHasBodies > 0 && SAMPLE_TEXTURE2D_X_LOD(_MunShotBodies, sampler_PointClamp, uv, 0).r > 0.5;
}

// How much ink a distant planet's or moon's outline gives a pixel (0 to 1): every pixel within half an outline's width
// of its edge, as LinesAt draws an outline, where nothing nearer is in front
float BodyOutlineAt(float2 uv)
{
    if (_MunShotHasBodies <= 0 || !IsSky(SampleSceneDepth(uv))) return 0;
    bool inside[5][5];
    [unroll]
    for (int i = 0; i < 5; i++)
    {
        [unroll]
        for (int j = 0; j < 5; j++)
            inside[i][j] = BodyAt(uv + float2(i - 2, j - 2) * _MunShotPixel.xy * LinePixels);
    }
    float ink = 0;
    [unroll]
    for (int a = 1; a <= 3; a++)
    {
        [unroll]
        for (int b = 1; b <= 3; b++)
        {
            bool edge = inside[a][b] && (!inside[a - 1][b] || !inside[a + 1][b] || !inside[a][b - 1] || !inside[a][b + 1]);
            if (edge) ink = max(ink, saturate(OutlineWidth * 0.5 + 0.5 - length(float2(a - 2, b - 2))));
        }
    }
    return ink;
}

// The game's volume clouds, as it draws them for the picture (KSP.VolumeCloud's, set for every shader): their light,
// and how much they cover each pixel (alpha); and how much they show (0 to 1, as they give way to the faraway view's
// on the way up; 0 when there are none). Lines behind them fade; where they cover more than CloudsEdge, Cel shaded
// draws them in flat tones CloudToneStops apart (by their own light: each twice as bright as the last at 1, finer than
// the smoke's so a cloud deck's gentle shading still shows), and both filters draw a line round them and, as much as
// the detail asks, between their tones and where their light changes by more than CloudChangeStops across
// CloudSmoothPixels either side (a nearer cloud before a brighter one), too gently for a tone of its own, unless a
// tone's line or their edge is within CloudChangeGapPixels across it, as that already draws it. MunShot.Cameras.
// InkCapture's Clouds pass evens them out once a frame (SmoothGameClouds), and the filters' passes read that
// (_MunShotClouds, SmoothClouds; _MunShotHasClouds 0 when there are none). All of it only where the clouds are in front
// of what's there (its CloudDistance pass, from the game's own cloud depth): the game works its clouds out coarser than
// the picture, so they spill over the edges of what's before them
TEXTURE2D(_CloudTex);
float _MunShotCloudsShown;
TEXTURE2D_X(_MunShotClouds);
TEXTURE2D_X_FLOAT(_MunShotCloudDistance);
float _MunShotHasClouds;
static const float CloudsEdge = 0.5;
static const float CloudToneStops = 0.5;
// The clouds are evened out over 3 by 3 points CloudSmoothPixels apart (the middle counting most) before their edge and
// tones are found, as the game draws them grainy: so both follow the clouds' shapes rather than the grain
static const float CloudSmoothPixels = 4;
static const float CloudChangeStops = 0.2;
static const float CloudChangeGapPixels = 5;
// The clouds' lines are lighter than the rest, CloudInk of the way from the paper to the ink, so they don't weigh on
// something as soft
static const float CloudInk = 0.4;

float4 CloudsSample(float2 uv)
{
    return _MunShotCloudsShown > 0 ? SAMPLE_TEXTURE2D_LOD(_CloudTex, sampler_LinearClamp, uv, 0) : 0;
}

bool Clouded(float4 clouds)
{
    return saturate(clouds.a) * _MunShotCloudsShown > CloudsEdge;
}

// The clouds' own light where a sample of them was taken, as bright as where they cover it wholly
float CloudLight(float4 clouds)
{
    return max(Luminance(clouds.rgb) / max(clouds.a, 0.05), 1e-6);
}

// The clouds round a pixel, evened out
struct SmoothedClouds
{
    // How much they cover it, and how much they show
    float Cover;
    // Their own light
    float Light;
    // How their light changes across it, in stops from CloudSmoothPixels one side to as far the other (x left to
    // right, y bottom to top); only from where it's cloud
    float2 Change;
};

SmoothedClouds SmoothGameClouds(float2 uv)
{
    float4 samples[3][3];
    float4 sum = 0;
    [unroll]
    for (int i = 0; i < 3; i++)
    {
        [unroll]
        for (int j = 0; j < 3; j++)
        {
            samples[i][j] = CloudsSample(uv + float2(i - 1, j - 1) * _MunShotPixel.xy * CloudSmoothPixels);
            sum += samples[i][j] * ((2 - abs(i - 1)) * (2 - abs(j - 1)));
        }
    }
    sum /= 16;
    SmoothedClouds clouds;
    clouds.Cover = saturate(sum.a) * _MunShotCloudsShown;
    clouds.Light = CloudLight(sum);
    float stops[3][3];
    [unroll]
    for (int k = 0; k < 3; k++)
    {
        [unroll]
        for (int l = 0; l < 3; l++)
            stops[k][l] = log2(Clouded(samples[k][l]) ? CloudLight(samples[k][l]) : clouds.Light);
    }
    clouds.Change = float2(stops[2][0] + 2 * stops[2][1] + stops[2][2] - stops[0][0] - 2 * stops[0][1] - stops[0][2],
                           stops[0][2] + 2 * stops[1][2] + stops[2][2] - stops[0][0] - 2 * stops[1][0] - stops[2][0]) / 4;
    return clouds;
}

// The clouds round a pixel as the Clouds pass evened them out (xy its Cover and Light, zw its Change)
SmoothedClouds SmoothClouds(float2 uv)
{
    float4 kept = _MunShotHasClouds > 0 ? SAMPLE_TEXTURE2D_X_LOD(_MunShotClouds, sampler_LinearClamp, uv, 0) : 0;
    SmoothedClouds clouds;
    clouds.Cover = kept.x;
    clouds.Light = max(kept.y, 1e-6);
    clouds.Change = kept.zw;
    return clouds;
}

// Whether the clouds at a pixel are in front of what's there, given as 1 / its distance (0 for the sky)
bool CloudsBefore(float2 uv, float w)
{
    float cloud = _MunShotHasClouds > 0 ? SAMPLE_TEXTURE2D_X_LOD(_MunShotCloudDistance, sampler_PointClamp, uv, 0).r : 0;
    return cloud > 0 && cloud * w < 1;
}

// How much the clouds cover a pixel, so the lines behind them fade: none where they're behind the nearest thing within a
// line's reach (an outline against the sky is its object's, and stays whole in front of a cloud)
float CloudsAt(float2 uv)
{
    float2 x = float2(_MunShotPixel.x * LinePixels, 0);
    float2 y = float2(0, _MunShotPixel.y * LinePixels);
    bool water;
    float w = max(InverseDepth(RawDepth(uv, water)),
                  max(max(InverseDepth(RawDepth(uv - x, water)), InverseDepth(RawDepth(uv + x, water))),
                      max(InverseDepth(RawDepth(uv - y, water)), InverseDepth(RawDepth(uv + y, water)))));
    return CloudsBefore(uv, w) ? SmoothClouds(uv).Cover : 0;
}

bool Clouded(SmoothedClouds clouds)
{
    return clouds.Cover > CloudsEdge;
}

// Which of the clouds' tones a light is in, and the middle of that tone
float CloudTone(float light)
{
    return floor(log2(light) / CloudToneStops);
}

float CloudToneLight(float light)
{
    return exp2((CloudTone(light) + 0.5) * CloudToneStops);
}

// How much ink the clouds' lines give a pixel (0 to 1): round their edge, where a neighbour is clear; and, as much as
// the detail asks, between their tones, on the brighter side, and along the sharpest of a change in their light; at
// CloudInk, and as much as the planet's own lines get from up high
float CloudLinesAt(float2 uv)
{
    bool water;
    if (!CloudsBefore(uv, InverseDepth(RawDepth(uv, water)))) return 0;
    SmoothedClouds here = SmoothClouds(uv);
    if (!Clouded(here)) return 0;
    float tone = CloudTone(here.Light);
    float2 around[4] =
    {
        float2(-_MunShotPixel.x, 0), float2(_MunShotPixel.x, 0), float2(0, -_MunShotPixel.y), float2(0, _MunShotPixel.y)
    };
    float edge = 0;
    float between = 0;
    float changes[4];
    [unroll]
    for (int i = 0; i < 4; i++)
    {
        SmoothedClouds next = SmoothClouds(uv + around[i] * LinePixels);
        changes[i] = length(next.Change);
        if (!Clouded(next)) edge = 1;
        else if (CloudTone(next.Light) < tone) between = 1;
    }
    // A change is drawn only where it's sharper than at its neighbours either side across it, so its line stays thin
    float change = length(here.Change);
    bool sideways = abs(here.Change.x) >= abs(here.Change.y);
    float sides = sideways ? max(changes[0], changes[1]) : max(changes[2], changes[3]);
    float sharp = 0;
    if (change > CloudChangeStops && change >= sides)
    {
        float2 across = normalize(here.Change) * _MunShotPixel.xy * CloudChangeGapPixels;
        SmoothedClouds before = SmoothClouds(uv - across);
        SmoothedClouds after = SmoothClouds(uv + across);
        bool lined = !Clouded(before) || !Clouded(after) || CloudTone(before.Light) != CloudTone(after.Light);
        sharp = lined ? 0 : 1;
    }
    return max(edge, max(between, sharp) * saturate(_MunShotLineDetail)) * CloudInk * PlanetInk();
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

// How much of the ink the planet's own lines get at a pixel: all of it near the ground; higher up, less (PlanetInk),
// fading out towards the planet's edge (the nearest of them past it)
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
    // there; its shore is from its own sea, where its surface is drawn), the facing, and whether it's the planet's ground
    // (the camera's own terrain, or the faraway view's surface) and steep there, 5 by 5 round the pixel
    float w[5][5];
    bool wet[5][5];
    bool far[5][5];
    bool farSurface[5][5];
    bool farSea[5][5];
    float3 facing[5][5];
    bool terrain[5][5];
    bool ground[5][5];
    bool steep[5][5];

    // How much detail the lines show: how far a corner must turn, and how steep a slope must be (as the cosine and sine
    // of its lean), for a line; none of either at 0
    float detail = saturate(_MunShotLineDetail);
    float cornerTurn = detail > 0 ? InnerCrease + (1 - detail) * CornerFade : 3;
    float steepCos = lerp(SlopeFadeCos, SteepCos, detail);
    float steepSin = sqrt(1 - steepCos * steepCos);

    // Up, away from the planet's centre, where the pixel is (for the camera's own terrain)
    float3 up = 0;
    if (_MunShotPlanet.w > 0)
        up = normalize(ComputeWorldSpacePosition(uv, SampleSceneDepth(uv), UNITY_MATRIX_I_VP) - _MunShotPlanet.xyz);
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
            float4 surface = far[i][j] && _MunShotHasFarSurface > 0
                ? SAMPLE_TEXTURE2D_X_LOD(_MunShotFarSurface, sampler_PointClamp, at, 0)
                : 0;
            farSurface[i][j] = surface.g > 0.5;
            farSea[i][j] = _MunShotFarSea > 0 && surface.r > Sea;
            facing[i][j] = SampleSceneNormals(at);
            terrain[i][j] = !wet[i][j] && w[i][j] > 0 && _MunShotHasTerrain > 0 && _MunShotPlanet.w > 0
                            && SAMPLE_TEXTURE2D_X_LOD(_MunShotTerrain, sampler_PointClamp, at, 0).r > 0.5;
            ground[i][j] = terrain[i][j] || (farSurface[i][j] && !farSea[i][j]);
            steep[i][j] = detail > 0 && (terrain[i][j] ? dot(facing[i][j], up) < steepCos
                                         : ground[i][j] && length(surface.ba * 2 - 1) > steepSin);
        }
    }

    // Corner lines, not where lines crowd
    float inner = w[2][2] > 0 && !crowded ? 1 : 0;

    // Each of the 3 by 3 round the pixel: what edge it's on, and how much of this pixel that edge's line covers (a
    // coast's, a slope's, or an outline's or corner's wholly on the camera's own terrain, as much as the planet's own
    // lines get there)
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
            // Wholly on the camera's own terrain (no ship, building or sky beside it): the planet's own line
            bool onTerrain = terrain[a][b] && terrain[a - 1][b] && terrain[a + 1][b]
                             && terrain[a][b - 1] && terrain[a][b + 1];
            // Steep ground beside level ground (a crater's wall, a ridge, a cliff)
            bool slope = steep[a][b]
                         && ((ground[a - 1][b] && !steep[a - 1][b]) || (ground[a + 1][b] && !steep[a + 1][b])
                             || (ground[a][b - 1] && !steep[a][b - 1]) || (ground[a][b + 1] && !steep[a][b + 1]));
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
            {
                float outline = saturate(OutlineWidth * 0.5 + 0.5 - distance);
                if (onTerrain) coast = max(coast, outline);
                else ink = max(ink, outline);
            }
            else if (shore)
                coast = max(coast, saturate(OutlineWidth * 0.5 + 0.5 - distance));
            else if (slope)
                coast = max(coast, saturate(InnerWidth * 0.5 + 0.5 - distance));
            else if (turn > cornerTurn)
            {
                float corner = inner * saturate(InnerWidth * 0.5 + 0.5 - distance);
                if (onTerrain) coast = max(coast, corner);
                else ink = max(ink, corner);
            }
        }
    }
    return coast > 0 ? max(ink, coast * CoastInk(uv)) : ink;
}

#endif
