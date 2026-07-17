#ifndef STAGEBEAM_CONE_CORE_INCLUDED
#define STAGEBEAM_CONE_CORE_INCLUDED

// Shared raymarch core for the cone beam. Both the uniform-driven shader (Origuma/StageBeamCone)
// and the GPU-instanced shader (Origuma/StageBeamConeInstanced) fill a BeamParams from their own
// source (per-material CBUFFER vs StructuredBuffer<GpuBeam>), compute the camera/ray in the beam's
// object "CL" frame via their own transforms, then call StageBeamRaymarch — so the volumetric math
// lives in exactly one place. Requires StageBeamShadow.hlsl and URP depth to be included first.

// --- Shared globals (renderer feature / driver set these once, not per beam) ------------------
TEXTURE2D_ARRAY(_GoboArray);   SAMPLER(sampler_GoboArray);
TEXTURE2D_ARRAY(_GoboArray2);  SAMPLER(sampler_GoboArray2);

TEXTURE3D(_HazeNoise);         SAMPLER(sampler_HazeNoise);
float  _HazeStrength;   // 0 = off
float  _HazeScale;      // world units -> noise UVW
float4 _HazeScroll;     // xyz = animated world-space offset

float  _StageBeamMaster;     // global master brightness
float  _BeamFrameIndex;
float  _BeamTemporalJitter;  // 1 = animated jitter, 0 = static IGN
float4 _BeamJitterScroll;    // xy = screen px/sec the dither drifts (matches haze)
float4 _BeamRTParams;        // xy = 1 / render-target size (correct depth UV at any resolution)

// --- Per-beam parameters (filled from CBUFFER or StructuredBuffer by the entry shader) --------
struct BeamParams
{
    float3 color;
    float  intensity;
    float  startRadius;
    float  range;
    float  edgeSoftness;
    float  fieldHalf;
    float  beamHalf;
    float  density;
    float  anisotropy;
    float  steps;
    float  depthOcclude;
    float  surfaceFadeDist;
    float  axialFalloff;
    float  hotspot;
    float  rootBoost;
    float  rootBoostFrac;
    float  rootWhite;
    float  goboSlice;
    float  goboRot;
    float2 goboOffset;
    float  goboSlice2;
    float  goboRot2;
    float  beamSoft;
};

float IGN(float2 p)
{
    return frac(52.9829189 * frac(dot(p, float2(0.06711056, 0.00583715))));
}

// Width (radius) of the beam volume at axial distance z.
float BeamWidthAt(float z, float rStart, float rEnd, float zEnd)
{
    return lerp(rStart, rEnd, saturate(z / zEnd));
}

// Nearest forward distance at which the ray enters the truncated beam volume (axis = +Z,
// radius rStart at z=0 growing to rEnd at z=zEnd). Lateral surface = infinite cone about its
// apex solved as a quadratic in t; end disks handled separately. Returns a large sentinel when
// the ray never enters within the z-range.
float BeamEntryDistance(float3 ro, float3 rd, float zEnd, float rStart, float rEnd)
{
    float slope = max((rEnd - rStart) / zEnd, 1e-5);   // dRadius / dz
    float zApex = -rStart / slope;                     // apex sits behind z=0
    float cosSq = 1.0 / (1.0 + slope * slope);         // cos^2 of the half-angle

    float3 ao = ro - float3(0.0, 0.0, zApex);
    float dN  = rd.z;
    float aN  = ao.z;

    float qa = dN * dN - cosSq;
    float qb = 2.0 * (dN * aN - dot(rd, ao) * cosSq);
    float qc = aN * aN - dot(ao, ao) * cosSq;

    float best = 1e9;

    float disc = qb * qb - 4.0 * qa * qc;
    if (disc >= 0.0 && abs(qa) > 1e-6)
    {
        float sq  = sqrt(disc);
        float inv = 0.5 / qa;
        float r0  = (-qb - sq) * inv;
        float r1  = (-qb + sq) * inv;
        [unroll]
        for (int s = 0; s < 2; s++)
        {
            float t = (s == 0) ? r0 : r1;
            float z = ro.z + t * rd.z;
            if (t > 0.0 && z >= 0.0 && z <= zEnd && t < best)
                best = t;
        }
    }

    if (abs(rd.z) > 1e-6)
    {
        float tNear = -ro.z / rd.z;
        float2 nearXY = ro.xy + tNear * rd.xy;
        if (tNear > 0.0 && dot(nearXY, nearXY) <= rStart * rStart && tNear < best)
            best = tNear;

        float tFar = (zEnd - ro.z) / rd.z;
        float2 farXY = ro.xy + tFar * rd.xy;
        if (tFar > 0.0 && dot(farXY, farXY) <= rEnd * rEnd && tFar < best)
            best = tFar;
    }

    return best;
}

// The full cone raymarch. camCL/rayCL/exitCL are the camera origin, ray direction and hull exit
// in the beam's CL frame (axis = +Z, object scale 1 so distances == world). lightWS is the beam
// apex in world space (shadow origin). screenPix = fragment pixel coords (for depth UV + jitter).
half4 StageBeamRaymarch(BeamParams p, float3 camWS, float3 dirWS,
                        float3 camCL, float3 rayCL, float3 exitCL,
                        float3 lightWS, float2 screenPix)
{
    float tanField    = max(tan(p.fieldHalf), 1e-3);
    float radiusStart = p.startRadius;
    float radiusEnd   = p.range * tanField + p.startRadius;

#if defined(_STAGEBEAM_SHADOWS_VOLUME)
    float dbgOcc = 0.0;      // red: max occupancy the view ray passes through
    float dbgShadow = 0.0;   // green: max shadowing of the samples (shadow ray hit)
#endif

    // Start the march where the ray actually enters the volume, independent of the bounding mesh.
    float camWidth = BeamWidthAt(camCL.z, radiusStart, radiusEnd, p.range);
    bool camInside = (camCL.z >= 0.0 && camCL.z <= p.range &&
                      length(camCL.xy) <= camWidth);

    float tIn;
    if (camInside)
    {
        tIn = 0.0;
    }
    else
    {
        float tEnter = BeamEntryDistance(camCL, rayCL, p.range, radiusStart, radiusEnd);
        if (tEnter > 1e8) return 0;            // ray never enters the volume
        tIn = tEnter;
    }

    float tOut = length(exitCL - camCL);       // far hull face
    bool  depthClipped = false;                // tOut cut short by real scene geometry

    if (p.depthOcclude > 0.5)
    {
        float2 suv = screenPix * _BeamRTParams.xy;
        float sceneEye = LinearEyeDepth(SampleSceneDepth(suv), _ZBufferParams);
        float3 fwd = GetViewForwardDir();
        float cosToRay = max(dot(dirWS, fwd), 1e-3);
        float sceneDist = sceneEye / cosToRay;
        if (sceneDist < tOut) { tOut = sceneDist; depthClipped = true; }
    }

    if (tOut <= tIn) return 0;                 // nothing in front of the exit

    int   steps   = max((int)p.steps, 1);
    float chord   = tOut - tIn;

    // Per-pixel jitter (full step). When animated, the dither SAMPLING POSITION drifts at a
    // screen-space velocity matched to the haze scroll, so the grain reads as the fog drifting.
    float2 jpix = screenPix;
    if (_BeamTemporalJitter > 0.5) jpix += _BeamJitterScroll.xy * _Time.y;
    float jitterNoise  = IGN(jpix);
    float shadowJitter = jitterNoise;
    float stepSize     = chord / steps;

    bool  useGobo  = p.goboSlice  >= 0.0;
    bool  useGobo2 = p.goboSlice2 >= 0.0;
    bool  goboScrolls = abs(p.goboOffset.x) + abs(p.goboOffset.y) > 1e-5;
    float cs  = cos(p.goboRot),  sn  = sin(p.goboRot);
    float cs2 = cos(p.goboRot2), sn2 = sin(p.goboRot2);
    float goboSlope = (radiusEnd - radiusStart) / max(p.range, 1e-5);   // dWidth/dz, for the gobo mip
    float sideSoftness = max(p.edgeSoftness, 0.01);
    float tanBeam = max(tan(p.beamHalf), 1e-4);
    float rootLen = max(p.rootBoostFrac * p.range, 1e-3);

    bool  usePhase = abs(p.anisotropy) > 1e-3;
    float phaseG   = p.anisotropy;
    float phaseG2  = phaseG * phaseG;

    float sum = 0.0;
    float whiteSum = 0.0;
    float sumRaw = 0.0;   // debug: sum WITHOUT shadow
    float sumNH = 0.0;      // haze-unmodulated sums (composite ratio re-apply)
    float whiteSumNH = 0.0;
    float shadowCache = 1.0;
    float hazeCache   = 1.0;
    [loop]
    for (int k = 0; k < steps; k++)
    {
        float t = tIn + (k + jitterNoise) * stepSize;
        float3 p3 = camCL + rayCL * t;
        float3 pWS = camWS + dirWS * t;

        float z = p3.z;
        float inAxis = step(0.0, z) * step(z, p.range);

        float hazeF = 1.0;
        if (_HazeStrength > 0.0001)
        {
            if ((k & 1) == 0)
            {
                float n = SAMPLE_TEXTURE3D_LOD(_HazeNoise, sampler_HazeNoise,
                            pWS * _HazeScale + _HazeScroll.xyz, 0).r;
                hazeCache = 1.0 + _HazeStrength * (n - 0.5) * 2.0;
            }
            hazeF = hazeCache;
        }

        float widthAtZ = BeamWidthAt(z, radiusStart, radiusEnd, p.range);
        float radial   = length(p3.xy);

        float side = saturate((widthAtZ - radial) / (widthAtZ * sideSoftness));

        float widthBeam = z * tanBeam + radiusStart;
        float rNorm = radial / max(widthAtZ, 1e-5);
        float beamFrac = saturate(widthBeam / max(widthAtZ, 1e-5));
        float hot = 1.0 - smoothstep(0.0, beamFrac, rNorm);
        float hotFactor = 1.0 + p.hotspot * hot;

        float rootBoost = 1.0 + p.rootBoost * exp(-z / rootLen);

        float axNorm   = z / p.range;
        float axialAtt = saturate(1.0 - axNorm * axNorm * p.axialFalloff);

        float gobo = 1.0;
        if (useGobo || useGobo2)
        {
            float2 g = p3.xy / max(widthAtZ, 1e-5);     // [-1,1] across the field

            // --- Gobo mip footprint (prefilter, not more samples) -------------------------
            // A gobo carves the volume into thin light shafts. The hardware's automatic mip only
            // sees how the UV changes ACROSS THE SCREEN; it cannot see that the march also jumps
            // `stepSize` ALONG the ray between samples. Shafts finer than that jump get stepped
            // straight over — one pixel lands in a shaft, its neighbour misses — and that variance
            // is the dappling/moire. So widen the filter to whichever footprint is coarser:
            // the shaft is then blurred BELOW the sampling rate instead of aliasing against it.
            //
            // g = p3.xy / w(z), so  dg/dt = (rayCL.xy - g * dw/dz * rayCL.z) / w   (quotient rule).
            // Gradients come from g, NOT the frac()'d UV: frac's wrap discontinuity would spike the
            // derivative and slam the sampler to the coarsest mip in a line along the seam.
            float2 dgdt    = (rayCL.xy - g * (goboSlope * rayCL.z)) / max(widthAtZ, 1e-5);
            float  rayFoot = length(dgdt) * stepSize * 0.5;                 // 0.5: guv = g*0.5+0.5
            float  scrFoot = max(length(ddx(g)), length(ddy(g))) * 0.5;
            float  foot    = max(rayFoot, scrFoot);
            // Isotropic on purpose — we want the shafts filtered, not anisotropically preserved.
            float2 footX = float2(foot, 0.0);
            float2 footY = float2(0.0, foot);
            // Rotation is rigid, so both wheels share this footprint.

            if (useGobo)
            {
                float2 guv = float2(g.x * cs - g.y * sn, g.x * sn + g.y * cs) * 0.5 + 0.5;
                if (goboScrolls) guv = frac(guv + p.goboOffset.xy);
                half4 g1 = SAMPLE_TEXTURE2D_ARRAY_GRAD(_GoboArray, sampler_GoboArray, guv,
                                                       p.goboSlice, footX, footY);
                gobo *= g1.r * g1.a;
            }
            if (useGobo2)
            {
                float2 guv2 = float2(g.x * cs2 - g.y * sn2, g.x * sn2 + g.y * cs2) * 0.5 + 0.5;
                half4 g2 = SAMPLE_TEXTURE2D_ARRAY_GRAD(_GoboArray2, sampler_GoboArray2, guv2,
                                                       p.goboSlice2, footX, footY);
                gobo *= g2.r * g2.a;
            }
        }

        float phase = 1.0;
        if (usePhase)
        {
            float pLen = max(length(p3), 1e-4);
            float cosT = dot(p3 / pLen, -rayCL);
            // pow(x, 1.5) = x * sqrt(x); the latter is a mul + rsqrt vs pow's log/exp.
            float d = abs(1.0 + phaseG2 - 2.0 * phaseG * cosT);
            phase = (1.0 - phaseG2) / (d * sqrt(d));
        }

        float base  = inAxis * side * hotFactor * axialAtt * gobo * hazeF * phase;

        if (depthClipped)
        {
            float distToExit = tOut - t;
            base *= saturate(distToExit / max(p.surfaceFadeDist, 1e-4));
        }
        float baseRaw = base;
    #if defined(_STAGEBEAM_SHADOWS_SCREEN) || defined(_STAGEBEAM_SHADOWS_VOLUME) || defined(_STAGEBEAM_SHADOWS_LIGHT)
        if (base > 1e-4)
        {
            if ((k & 1) == 0)
                shadowCache = SampleBeamShadow(pWS, lightWS, shadowJitter);
            base *= shadowCache;
        }
    #endif
    #if defined(_STAGEBEAM_SHADOWS_VOLUME)
        if (_BeamShadowDebug > 0.5)
        {
            dbgOcc = max(dbgOcc, StageBeamSampleOccupancy(pWS));
            dbgShadow = max(dbgShadow, 1.0 - SampleBeamShadow(pWS, lightWS, shadowJitter));
        }
    #endif
        float boost = rootBoost - 1.0;
        float bC = 1.0 + boost * (1.0 - p.rootWhite);   // colour weight (was recomputed ×3)
        float bW = boost * p.rootWhite;                  // white weight (was recomputed ×2)
        sum        += base * bC;
        whiteSum   += base * bW;
        float baseNM = baseRaw / hazeF;
        sumNH      += baseNM * bC;
        whiteSumNH += baseNM * bW;
    #if defined(_STAGEBEAM_SHADOWS_VOLUME)
        // sumRaw feeds ONLY the shadow-debug view — accumulate it only when that's on, not every
        // sample of every production frame.
        if (_BeamShadowDebug > 0.5) sumRaw += baseRaw * bC;
    #endif
    }

    float scale = (1.0 / steps) * chord * p.density * p.intensity * _StageBeamMaster;
    float3 col = p.color * (sum * scale) + (whiteSum * scale).xxx;
    float beamLum = dot(p.color, float3(0.2126, 0.7152, 0.0722));
    float alphaNH = beamLum * (sumNH * scale) + whiteSumNH * scale;
#if defined(_STAGEBEAM_SHADOWS_VOLUME)
    if (_BeamShadowDebug > 0.5)
    {
        col = (sum / max(sumRaw, 1e-5)).xxx;
    }
#endif
    col = lerp(col, col / (1.0 + col), p.beamSoft);
    return half4(col, alphaNH);
}

#endif // STAGEBEAM_CONE_CORE_INCLUDED
