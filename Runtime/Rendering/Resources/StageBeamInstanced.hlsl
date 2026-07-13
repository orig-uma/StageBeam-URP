#ifndef STAGEBEAM_INSTANCED_INCLUDED
#define STAGEBEAM_INSTANCED_INCLUDED

// Per-beam record read by the GPU-instanced cone path. Layout MUST match the C# `GpuBeam`
// struct in StageBeamInstancing.cs byte-for-byte (2 float4x4 + 6 float4 = 240 bytes).
//
// MATRIX CONVENTION: Unity's shader compiler packs float4x4 COLUMN-major, which matches the
// column-major memory layout of a C# Matrix4x4 uploaded via SetData — so the buffer matrix reads
// back as the SAME logical matrix, and the standard mul(M, v) applies (identical to Unity's own
// TransformObjectToWorld). Use the helpers below.
struct GpuBeam
{
    float4x4 objectToWorld;
    float4x4 worldToObject;
    float4 color;   // rgb = beam colour, w unused
    float4 p0;      // startRadius, endRadius, range, edgeSoftness
    float4 p1;      // fieldHalf, beamHalf, density, anisotropy
    float4 p2;      // steps, depthOcclude, axialFalloff, hotspot
    float4 p3;      // rootBoost, rootBoostFrac, rootWhite, intensity
    float4 g0;      // goboSlice, goboRot, goboOffset.x, goboOffset.y
    float4 g1;      // goboSlice2, goboRot2, shadowLightIndex, pad
};

StructuredBuffer<GpuBeam> _StageBeams;
int _StageBeamBase;   // index of this batch's first instance within _StageBeams

GpuBeam StageBeamAt(uint instanceID) { return _StageBeams[_StageBeamBase + instanceID]; }

// Object->World for a point (standard column-major transform, same as TransformObjectToWorld).
float3 StageBeamObjectToWorld(GpuBeam b, float3 posOS)
{
    return mul(b.objectToWorld, float4(posOS, 1.0)).xyz;
}

// World->Object for a point.
float3 StageBeamWorldToObject(GpuBeam b, float3 posWS)
{
    return mul(b.worldToObject, float4(posWS, 1.0)).xyz;
}

#endif // STAGEBEAM_INSTANCED_INCLUDED
