// FilmOut display pipeline.
//
// One full-screen pass renders the frame that will be shown, into a 10-bit texture. In run mode
// that texture is copied to the screen on every refresh of an exposure, so this shader runs once
// per image, not once per refresh, and can afford to be thorough.
//
// Order of operations, per output pixel:
//   screen pixel -> source uv (rotation, flips, fit, offset)  -> decode to linear
//   -> ACEScct -> folder grade -> image grade -> linear "target positive"
//   -> positive: as is | negative: exposure the print stock needs
//   -> flat-field gain -> pixel-defect compensation -> display encoding -> dither

cbuffer Params : register(b0)
{
    float4 UvRow0;       // source u = dot(UvRow0.xyz, float3(px, py, 1))
    float4 UvRow1;       // source v = dot(UvRow1.xyz, float3(px, py, 1))
    float4 Gate;         // x, y, width, height in output pixels
    float4 FolderSlope;
    float4 FolderOffset; // already converted to ACEScct units
    float4 FolderPower;
    float4 FolderMisc;   // x saturation, z contrast
    float4 ImageSlope;
    float4 ImageOffset;
    float4 ImagePower;
    float4 ImageMisc;
    float4 NegGamma;     // xyz print gamma per channel, w density range
    float4 NegPoints;    // xyz printer lights in points
    float4 Field;        // xyz code values for patterns, w defect-hunt ring radius in pixels
    float4 Cursor;       // xy cursor pixel, z ring thickness
    float4 Output;       // x display gamma, y one output step (1/levels), z dither seed
    float4 Reserved;
    float4 Marker[5];    // flat-field target markers: xy centre, z half size (pixels)
    uint4  Modes;        // x content, y negative, z flags, w source encoding
};

Texture2D<float4> SourceTex   : register(t0);
Texture2D<float4> FlatTex     : register(t1);
Texture2D<uint>   DefectMap   : register(t2);   // 0 = none, otherwise defect index + 1
Texture2D<float4> DefectData  : register(t3);   // two texels per defect
SamplerState      LinearClamp : register(s0);

static const uint CONTENT_BLACK = 0;
static const uint CONTENT_IMAGE = 1;
static const uint CONTENT_SOLID = 2;
static const uint CONTENT_FRAMING = 3;
static const uint CONTENT_FLATTARGET = 4;
static const uint CONTENT_DEFECTHUNT = 5;

static const uint FLAG_FLAT = 1;
static const uint FLAG_DEFECTS = 2;

struct VSOut { float4 pos : SV_Position; };

VSOut VSMain(uint id : SV_VertexID)
{
    VSOut o;
    float2 t = float2((id << 1) & 2, id & 2);
    o.pos = float4(t * float2(2, -2) + float2(-1, 1), 0, 1);
    return o;
}

// ---- Encodings -------------------------------------------------------------------------------

float3 SrgbToLinear(float3 c)
{
    return c <= 0.04045 ? c / 12.92 : pow((c + 0.055) / 1.055, 2.4);
}

float3 DecodeSource(float3 c, uint encoding)
{
    c = saturate(c);
    if (encoding == 0) return SrgbToLinear(c);
    if (encoding == 1) return pow(c, 2.2);
    if (encoding == 2) return pow(c, 2.4);
    return c;
}

// ACEScct: log above a small linear toe. One stop is 1/17.52.
static const float CCT_X_BREAK = 0.0078125;
static const float CCT_Y_BREAK = 0.155251141552511;
static const float CCT_A = 10.5402377416545;
static const float CCT_B = 0.0729055341958355;
static const float CCT_PIVOT = 0.4135884; // 18% grey

float3 LinToCct(float3 x)
{
    return x <= CCT_X_BREAK ? CCT_A * x + CCT_B : (log2(max(x, 1e-10)) + 9.72) / 17.52;
}

float3 CctToLin(float3 y)
{
    return y <= CCT_Y_BREAK ? (y - CCT_B) / CCT_A : exp2(y * 17.52 - 9.72);
}

float3 DisplayToLinear(float3 code) { return pow(saturate(code), Output.x); }

// ---- Grade -----------------------------------------------------------------------------------

float3 ApplyGrade(float3 v, float3 slope, float3 offset, float3 power, float4 misc)
{
    v = v * slope + offset;
    v = pow(max(v, 0.0), power);
    float luma = dot(v, float3(0.2126, 0.7152, 0.0722));
    v = luma + misc.x * (v - luma);
    v = CCT_PIVOT + (v - CCT_PIVOT) * misc.z;
    return v;
}

// Placeholder negative model, to be replaced by a fitted calibration LUT.
// Print stock is negative-working: density = gamma * log10(E) + c. For a print transmission T,
// the exposure needed is E ~ T^(-1/gamma). Normalised so the deepest black uses full screen light.
float3 ToDisplayLinear(float3 target)
{
    if (Modes.y == 0) return saturate(target);
    float tmin = pow(10.0, -NegGamma.w);
    float3 t = clamp(target, tmin, 1.0);
    float3 e = pow(t / tmin, -1.0 / NegGamma.xyz);
    e *= pow(10.0, 0.025 * NegPoints.xyz);
    return saturate(e);
}

// ---- Content ---------------------------------------------------------------------------------

bool InGate(float2 p)
{
    return all(p >= Gate.xy) && all(p < Gate.xy + Gate.zw);
}

float3 ImageLinear(float2 p)
{
    if (!InGate(p)) return 0;
    float3 h = float3(p, 1);
    float2 uv = float2(dot(UvRow0.xyz, h), dot(UvRow1.xyz, h));
    if (any(uv < 0.0) || any(uv > 1.0)) return 0;

    float2 ddxUv = float2(UvRow0.x, UvRow1.x);
    float2 ddyUv = float2(UvRow0.y, UvRow1.y);
    float3 src = SourceTex.SampleGrad(LinearClamp, uv, ddxUv, ddyUv).rgb;

    float3 v = LinToCct(DecodeSource(src, Modes.w));
    v = ApplyGrade(v, FolderSlope.xyz, FolderOffset.xyz, FolderPower.xyz, FolderMisc);
    v = ApplyGrade(v, ImageSlope.xyz, ImageOffset.xyz, ImagePower.xyz, ImageMisc);
    return ToDisplayLinear(max(CctToLin(v), 0.0));
}

float3 FramingLinear(float2 p)
{
    if (!InGate(p)) return 0;
    float2 rel = p - Gate.xy;
    float thick = max(2.0, round(Gate.w / 540.0));
    float3 c = DisplayToLinear(0.12);

    bool edge = rel.x < thick || rel.y < thick || rel.x >= Gate.z - thick || rel.y >= Gate.w - thick;
    float2 inset = Gate.zw * 0.05;
    bool safe = (abs(rel.x - inset.x) < thick * 0.5 || abs(rel.x - (Gate.z - inset.x)) < thick * 0.5) && rel.y > inset.y && rel.y < Gate.w - inset.y
             || (abs(rel.y - inset.y) < thick * 0.5 || abs(rel.y - (Gate.w - inset.y)) < thick * 0.5) && rel.x > inset.x && rel.x < Gate.z - inset.x;
    float2 centre = Gate.zw * 0.5;
    float arm = min(Gate.z, Gate.w) * 0.06;
    bool centreMark = (abs(rel.x - centre.x) < thick * 0.5 && abs(rel.y - centre.y) < arm)
              || (abs(rel.y - centre.y) < thick * 0.5 && abs(rel.x - centre.x) < arm);

    if (edge || centreMark) c = 1.0;
    else if (safe) c = DisplayToLinear(0.45);
    return c;
}

float3 FlatTargetLinear(float2 p)
{
    if (!InGate(p)) return 0;
    [unroll] for (int i = 0; i < 5; i++)
    {
        float2 d = abs(p - Marker[i].xy);
        if (all(d <= Marker[i].z)) return 0;
    }
    return DisplayToLinear(Field.xyz);
}

float3 DefectHuntLinear(float2 p)
{
    float3 field = DisplayToLinear(Field.xyz);
    float r = length(p - (Cursor.xy + 0.5));
    if (abs(r - Field.w) < Cursor.z * 0.5)
        return dot(field, 1.0 / 3.0) > 0.25 ? 0.0 : 1.0;
    return field;
}

float3 ContentLinear(float2 p)
{
    uint content = Modes.x;
    if (content == CONTENT_IMAGE) return ImageLinear(p);
    if (content == CONTENT_SOLID) return DisplayToLinear(Field.xyz);
    if (content == CONTENT_FRAMING) return FramingLinear(p);
    if (content == CONTENT_FLATTARGET) return FlatTargetLinear(p);
    if (content == CONTENT_DEFECTHUNT) return DefectHuntLinear(p);
    return 0;
}

// The light we intend this pixel to emit, before working around defective pixels.
float3 IntendedLinear(float2 p)
{
    float3 v = ContentLinear(p);
    if ((Modes.z & FLAG_FLAT) != 0 && InGate(p))
        v *= FlatTex.SampleLevel(LinearClamp, (p - Gate.xy) / Gate.zw, 0).rgb;
    return v;
}

// ---- Output ----------------------------------------------------------------------------------

float Hash(uint2 p, uint seed)
{
    uint h = p.x * 1664525u + p.y * 1013904223u + seed * 2654435761u;
    h ^= h >> 16; h *= 2246822519u;
    h ^= h >> 13; h *= 3266489917u;
    h ^= h >> 16;
    return (h & 0x00FFFFFFu) / 16777216.0;
}

float4 PSMain(VSOut input) : SV_Target
{
    float2 p = input.pos.xy;   // pixel centre
    int2 ip = int2(p);
    float3 v = IntendedLinear(p);

    // A defective pixel can't show what it should, but film can't resolve single 8K pixels either.
    // Neighbours make up the missing light (or hold back the excess of a stuck pixel) so that the
    // total over the small area the film integrates comes out right.
    if ((Modes.z & FLAG_DEFECTS) != 0)
    {
        uint id = DefectMap.Load(int3(ip, 0));
        if (id != 0)
        {
            int k = (int)id - 1;
            float4 d0 = DefectData.Load(int3(k * 2, 0, 0));     // x, y, channel mask, kind
            float4 d1 = DefectData.Load(int3(k * 2 + 1, 0, 0)); // level
            int2 dp = int2(d0.xy);
            uint mask = (uint)d0.z;
            uint kind = (uint)d0.w;
            float3 m = float3((mask & 1u) != 0, (mask & 2u) != 0, (mask & 4u) != 0);

            bool self = all(dp == ip);
            float3 intended = saturate(self ? v : IntendedLinear(float2(dp) + 0.5));
            float3 driven = intended;
            float3 emitted;
            if (kind == 0) emitted = 0;                  // dead
            else if (kind == 1) emitted = d1.x;          // stuck at a level
            else                                         // weak: drive it harder first
            {
                driven = min(intended / max(d1.x, 1e-3), 1.0);
                emitted = driven * d1.x;
            }
            float3 deficit = (intended - emitted) * m;

            if (self)
            {
                v = lerp(v, driven, m);
            }
            else
            {
                int2 o = ip - dp;
                float w = (abs(o.x) + abs(o.y) == 1) ? 0.15 : 0.10;   // 4 x 0.15 + 4 x 0.10 = 1
                v += deficit * w;
            }
        }
    }

    v = saturate(v);
    float3 code = pow(v, 1.0 / Output.x);

    // Triangular dither of one output step, so smooth gradients don't band on high-contrast stock.
    // Exactly black stays exactly black.
    uint seed = (uint)Output.z;
    float n = Hash(uint2(ip), seed) + Hash(uint2(ip) + uint2(7919u, 104729u), seed) - 1.0;
    code = v > 0.0 ? saturate(code + n * Output.y) : 0.0;
    return float4(code, 1);
}
