#ifndef ORIGUMA_STAGEBEAM_SHADOW_INCLUDED
#define ORIGUMA_STAGEBEAM_SHADOW_INCLUDED

// Volumetric self-shadowing for StageBeam cones.
//
// The whole point of this file: the shadow *data* is SHARED across every beam, so its cost
// is independent of the number of lights. Per beam-pixel-step we only *sample* it. Two
// sampling backends are provided; enable one with a global keyword from the renderer feature:
//
//   _STAGEBEAM_SHADOWS_SCREEN  -> march the camera depth buffer toward the light (free, on-screen only)
//   _STAGEBEAM_SHADOWS_VOLUME  -> march a shared world-space occupancy volume (robust, off-screen ok)
//   _STAGEBEAM_SHADOWS_LIGHT   -> sample the co-located REAL light's URP shadow map (exact
//                                 geometry silhouettes; needs a shadowed spot light per beam)
//
// Include this AFTER Core.hlsl and DeclareDepthTexture.hlsl (the cone shader already pulls those
// in), so SampleSceneDepth / _ZBufferParams / TransformWorldToHClip are available here.

// ----------------------------------------------------------------------------------------------
// Shared knobs (set globally once per frame by StageBeamOcclusionVolume / the renderer feature)
// ----------------------------------------------------------------------------------------------
float  _BeamShadowStrength;   // 0 = no shadow, 1 = fully black in occluded regions
float  _BeamShadowSteps;      // secondary samples along sample->light (keep small: 4..8)
float  _BeamShadowMaxDist;    // clamp the shadow ray length in world units (perf + look)
float  _BeamShadowBias;       // world-space bias to avoid self-occlusion at the sample
float  _BeamShadowDensity;    // amplifies occupancy so thin/blobby occluders block harder (>=1)
float  _BeamShadowLightBias;  // world-space exclusion radius around the light (Volume backend): the
                              // fixture housing itself sits right where the beam apex is, so without
                              // this the occupancy volume self-shadows the beam's own root.

// --- Screen-space backend ---------------------------------------------------------------------
float  _BeamShadowThickness;  // assumed occluder thickness (world units) for the depth test
float  _BeamShadowStep;       // world-space size of each near-range march step (fine: ~0.1..0.3)

// Interleaved gradient noise → [0,1). Screen-space jitter for the shadow march: IGN pushes
// the dither error to the highest screen frequency, which reads as a smooth film grain
// instead of the blotchy white-noise a world-position hash produced (very visible at low
// render scale, where each noisy texel covers a 4×4 pixel block). Callers pass a per-pixel
// jitter into SampleBeamShadow; this helper is for callers without their own noise.
float StageBeamIGN(float2 pixel)
{
    return frac(52.9829189 * frac(dot(pixel, float2(0.06711056, 0.00583715))));
}

// --- Light shadow-map backend -------------------------------------------------------------------
#if defined(_STAGEBEAM_SHADOWS_LIGHT)
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"
#endif

// Per-beam (MaterialPropertyBlock): URP "additional light" index of the beam's co-located real
// light, resolved by the renderer feature each frame. -1 = no visible shadowed light.
float _BeamShadowLightIndex;

// One hardware-filtered shadow-map tap per raymarch sample — URP handles the atlas, culling
// (including off-screen casters), bias and (with _SHADOWS_SOFT) PCF. The silhouette is the
// actual render geometry: limbs, fingers, cloth — no sphere/voxel approximation at all.
// Only called for beams whose light index resolved (see the dispatcher's hybrid fallback).
float BeamShadow_LightMap(float3 posWS, float3 lightWS)
{
#if defined(_STAGEBEAM_SHADOWS_LIGHT)
    if (_BeamShadowStrength <= 1e-4) return 1.0;
    half3 dirL = (half3)normalize(lightWS - posWS);
    half s = AdditionalLightRealtimeShadow((int)_BeamShadowLightIndex, posWS, dirL);
    return lerp(1.0, (float)s, _BeamShadowStrength);
#else
    return 1.0;
#endif
}

// --- Volume backend ---------------------------------------------------------------------------
TEXTURE3D(_StageBeamOcc);
SAMPLER(sampler_StageBeamOcc);
float3 _StageBeamOccMin;      // world-space min corner of the volume
float3 _StageBeamOccInvSize;  // 1 / (world-space size of the volume)

float  _BeamShadowDebug;      // >0.5 : visualise the occupancy volume inside the beam (diagnostic)

// Raw occupancy at a world point (0 outside the volume / when the Volume backend is off).
// Also live under the LIGHT backend: beams without a shadow-mapped light there fall back to
// the volume march, so ONE mode mixes per-light quality (hero lights) with scalable volume
// shadows (the other 190 fixtures).
float StageBeamSampleOccupancy(float3 posWS)
{
#if defined(_STAGEBEAM_SHADOWS_VOLUME) || defined(_STAGEBEAM_SHADOWS_LIGHT)
    float3 uvw = (posWS - _StageBeamOccMin) * _StageBeamOccInvSize;
    if (any(uvw < 0.0) || any(uvw > 1.0)) return 0.0;
    return SAMPLE_TEXTURE3D_LOD(_StageBeamOcc, sampler_StageBeamOcc, uvw, 0).r;
#else
    return 0.0;
#endif
}

// ==============================================================================================
// Screen-space: march from the sample toward the light, testing each step against scene depth.
// Pixel-accurate for anything the camera can see (limbs, props, performers). Misses off-screen
// occluders and shadowing from geometry thickness the camera can't see. Costs K depth taps.
//
// The march covers a short NEAR-RANGE at a fine, fixed world step (not the full sample->light
// distance): the occluder standing in the beam sits within a few metres of the sample, while the
// fixture may be a ceiling mount 10-20 m away. A fixed step COUNT over that whole distance made
// each step metres long and stepped clean over a dancer — the old "unusable" behaviour. Fine
// fixed steps (_BeamShadowStep) with a jittered start give a soft, stable shadow instead.
// ==============================================================================================
float BeamShadow_ScreenSpace(float3 posWS, float3 lightWS, float jitter)
{
    if (_BeamShadowStrength <= 1e-4) return 1.0;      // nothing to shadow → skip the march
    float3 toLight = lightWS - posWS;
    float  distL   = length(toLight);
    if (distL < 1e-4) return 1.0;
    float3 dirL = toLight / distL;

    int   steps    = max((int)_BeamShadowSteps, 1);
    float stepSize = max(_BeamShadowStep, 0.02);
    float reach    = min(distL, _BeamShadowMaxDist);
    // Jittered start (screen-space IGN from the caller) so the coarse march dithers
    // smoothly rather than banding.

    float occluded = 0.0;
    UNITY_LOOP
    for (int j = 0; j < steps; j++)
    {
        float s = _BeamShadowBias + stepSize * (j + jitter);
        if (s >= reach) break;
        float3 sp = posWS + dirL * s;

        float4 hcs = TransformWorldToHClip(sp);
        if (hcs.w <= 0.0) continue;                       // behind the camera
        float2 uv = (hcs.xy / hcs.w) * 0.5 + 0.5;
        #if UNITY_UV_STARTS_AT_TOP
            uv.y = 1.0 - uv.y;
        #endif
        if (any(uv < 0.0) || any(uv > 1.0)) continue;     // off-screen: assume lit

        float sceneEye  = LinearEyeDepth(SampleSceneDepth(uv), _ZBufferParams);
        float sampleEye = LinearEyeDepth(hcs.z / hcs.w, _ZBufferParams);

        // delta > 0 => sp is behind a surface (something is between it and the light). Fade the
        // occlusion IN just past the bias (soft contact edge) and back OUT beyond the assumed
        // occluder thickness, so a far background wall the ray happens to cross doesn't shadow.
        float delta = sampleEye - sceneEye;
        float nearW = smoothstep(_BeamShadowBias, _BeamShadowBias + stepSize, delta);
        float farW  = 1.0 - smoothstep(_BeamShadowThickness, _BeamShadowThickness + stepSize, delta);
        occluded = max(occluded, nearW * farW);           // soft first-hit: strongest occluder wins
    }
    return 1.0 - occluded * _BeamShadowStrength;
}

// ==============================================================================================
// Volume: march the shared world-space occupancy field from the sample toward the light and
// accumulate transmittance. Works for occluders off-screen or behind other geometry. Fidelity
// is bounded by the volume resolution and how occluders are voxelized (see the .compute).
//
// The sample->light segment is CLIPPED to the occupancy box and the fixed step budget is spread
// across just that span. All occluder data lives inside the box, so the march reaches the light
// no matter how far away it is — the previous fixed "near-range" march (steps x ~0.25 m from
// the sample) silently ended ~3 m out, so any beam sample further than that below an occluder
// got no shadow at all and shafts faded out short instead of running the beam's full length.
// ==============================================================================================
float _StageBeamOccVoxel;    // smallest world voxel edge — paces the volume march below

// Clips [tMin, tMax] along posWS + t*dirL to the occupancy box. Returns false when the segment
// misses the box entirely (no occluder can shadow this sample).
bool StageBeamClipToOccBox(float3 posWS, float3 dirL, inout float tMin, inout float tMax)
{
    float3 boxMin = _StageBeamOccMin;
    float3 boxMax = _StageBeamOccMin + 1.0 / max(_StageBeamOccInvSize, 1e-6);
    // Keep the slab test finite for axis-aligned rays.
    float3 d = float3(abs(dirL.x) < 1e-6 ? 1e-6 : dirL.x,
                      abs(dirL.y) < 1e-6 ? 1e-6 : dirL.y,
                      abs(dirL.z) < 1e-6 ? 1e-6 : dirL.z);
    float3 ta = (boxMin - posWS) / d;
    float3 tb = (boxMax - posWS) / d;
    float3 tNear = min(ta, tb);
    float3 tFar  = max(ta, tb);
    tMin = max(tMin, max(max(tNear.x, tNear.y), tNear.z));
    tMax = min(tMax, min(min(tFar.x, tFar.y), tFar.z));
    return tMax > tMin;
}

// DEBUG: max occupancy the sample->light ray passes through, independent of _BeamShadowStrength.
// >0 means the shadow ray IS hitting the occluder (so any "no shadow" is a strength/look issue).
float StageBeamShadowRayMax(float3 posWS, float3 lightWS)
{
#if defined(_STAGEBEAM_SHADOWS_VOLUME)
    float3 toLight = lightWS - posWS;
    float  distL   = length(toLight);
    if (distL < 1e-4) return 0.0;
    float3 dirL = toLight / distL;
    float tMin = _BeamShadowBias;
    float tMax = min(distL, _BeamShadowMaxDist) - _BeamShadowLightBias;
    if (tMax <= tMin || !StageBeamClipToOccBox(posWS, dirL, tMin, tMax)) return 0.0;
    int   steps = max((int)_BeamShadowSteps, 1);
    float dt    = (tMax - tMin) / steps;
    float m = 0.0;
    UNITY_LOOP
    for (int j = 0; j < steps; j++)
    {
        float3 uvw = (posWS + dirL * (tMin + (j + 0.5) * dt) - _StageBeamOccMin)
                     * _StageBeamOccInvSize;
        m = max(m, SAMPLE_TEXTURE3D_LOD(_StageBeamOcc, sampler_StageBeamOcc, uvw, 0).r);
    }
    return m;
#else
    return 0.0;
#endif
}

float BeamShadow_Volume(float3 posWS, float3 lightWS, float jitter)
{
    if (_BeamShadowStrength <= 1e-4) return 1.0;      // no occluders → skip the whole march (free)
    float3 toLight = lightWS - posWS;
    float  distL   = length(toLight);
    if (distL < 1e-4) return 1.0;
    float3 dirL = toLight / distL;

    // Segment of interest: from just past the sample (Bias) to just short of the light
    // (LightBias keeps the fixture housing from shadowing the beam's own root), clipped to the
    // occupancy box so the whole step budget lands where occluders can actually be.
    float tMin = _BeamShadowBias;
    float tMax = min(distL, _BeamShadowMaxDist) - _BeamShadowLightBias;
    if (tMax <= tMin) return 1.0;
    if (!StageBeamClipToOccBox(posWS, dirL, tMin, tMax)) return 1.0;

    // The step pitch is anchored to the DATA, not to a dial: the volume cannot represent
    // anything finer than a voxel, so ~2 voxels per step resolves everything it holds — while
    // a march coarser than the occluder turns the shadow into a hit-PROBABILITY cloud (each
    // pixel's jittered comb hits or misses a limb at random), which reads as a noisy round
    // blob no matter how finely the body was voxelized. That was the "union of balls" look:
    // a fixed step count spread over the whole box span (metres per step) undersampling
    // 30 cm limbs. _BeamShadowSteps is now the COST CAP only — when the clipped span needs
    // more samples than the cap allows, the pitch grows again (graceful degradation), so the
    // cure for blobby shadows is a tighter box or more voxels, both of which shrink the pitch
    // here automatically.
    float pitch = max(2.0 * _StageBeamOccVoxel, 1e-3);
    int   steps = clamp((int)ceil((tMax - tMin) / pitch), 1, max((int)_BeamShadowSteps, 1));
    float dt    = (tMax - tMin) / steps;
    // Jittered start (screen-space IGN from the caller) so coarse steps dither smoothly
    // instead of banding along the shaft.

    float trans = 1.0;
    UNITY_LOOP
    for (int j = 0; j < steps; j++)
    {
        float3 uvw = (posWS + dirL * (tMin + (j + jitter) * dt) - _StageBeamOccMin)
                     * _StageBeamOccInvSize;
        float occ = SAMPLE_TEXTURE3D_LOD(_StageBeamOcc, sampler_StageBeamOcc, uvw, 0).r;
        // Per-metre extinction (Beer-Lambert): the look stays the same whatever the step size
        // or box size — denser sampling only smooths it. _BeamShadowDensity is opacity/metre.
        trans *= exp(-occ * max(_BeamShadowDensity, 0.0) * dt);
        if (trans < 0.01) { trans = 0.0; break; }
    }
    return lerp(1.0, trans, _BeamShadowStrength);
}

// ----------------------------------------------------------------------------------------------
// Dispatcher used by the cone shader. Compiles to a no-op (return 1) when no backend is enabled,
// so the shadow feature has zero cost when switched off.
// ----------------------------------------------------------------------------------------------
float SampleBeamShadow(float3 posWS, float3 lightWS, float jitter)
{
#if defined(_STAGEBEAM_SHADOWS_SCREEN)
    return BeamShadow_ScreenSpace(posWS, lightWS, jitter);
#elif defined(_STAGEBEAM_SHADOWS_VOLUME)
    return BeamShadow_Volume(posWS, lightWS, jitter);
#elif defined(_STAGEBEAM_SHADOWS_LIGHT)
    // HYBRID per light: beams with a resolved shadow-mapped light get the geometry-exact
    // map; every other beam falls back to the shared volume — assign a real Light to a
    // fixture and just that beam upgrades.
    return _BeamShadowLightIndex >= 0.0
        ? BeamShadow_LightMap(posWS, lightWS)
        : BeamShadow_Volume(posWS, lightWS, jitter);
#else
    return 1.0;
#endif
}

#endif // ORIGUMA_STAGEBEAM_SHADOW_INCLUDED
