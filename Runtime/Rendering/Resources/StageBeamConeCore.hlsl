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
float  _StageBeamEdgeAA;     // cone-rim softening in pixel footprints; 0 = off
float  _StageBeamExtinction; // haze extinction per metre along the view ray; 0 = pure additive
// ONE physical-model dial (0 = legacy, 1 = physical), driving the candela bell, the
// lens-anchored exponential falloff AND the distance-diffused rim below. They were three
// dials briefly; every combination other than "all legacy" or "all physical" was an
// incoherent look (a bell profile dimming over a normalised ramp, say), so the split bought
// confusion, not control. Per-fixture character still comes from the Look: AxialFalloff sets
// the decay, Hotspot/angles shape the bell, EdgeSoftness the rim.
float  _StageBeamPhysical;
float  _StageBeamNearFade;   // metres a beam eases back in over, right in front of the camera
float  _StageBeamMultiScatter; // 0 = single scattering only, 1 = fully isotropic (see the phase term)
float  _BeamFrameIndex;
float  _BeamTemporalJitter;  // 1 = animated jitter, 0 = static IGN
float4 _BeamJitterScroll;    // xy = screen px/sec the dither drifts (matches haze)
float4 _BeamRTParams;        // xy = 1 / render-target size (correct depth UV at any resolution)
// Gobo prefilter widening, derived from the resolution scale by the renderer feature (1 at
// Full). The mip footprint below filters shafts to exactly the sampling rate; that is Nyquist-
// BORDERLINE, and once the buffer is rendered small and magnified back up, the residual
// near-Nyquist energy is exactly what shows as moire. Filtering slightly below the rate at
// reduced resolutions trades a little shaft sharpness for a stable image — no dial, it follows
// the one resolution choice the user already made.
float  _StageBeamGoboFilterWiden;

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

// --- Transmittance MRT ------------------------------------------------------------------------
// With _STAGEBEAM_TRANSMITTANCE on, every beam pass writes a SECOND target carrying the optical
// depth its haze puts in front of the pixel, so the composite can darken the background the beam
// stands in front of. Optical depth is the quantity that makes this work with additive blending:
// transmittance MULTIPLIES between beams (T = T1·T2), which One One cannot express, but the depths
// it comes from ADD (τ = τ1+τ2, T = exp(-τ)) — so the same accumulation buffer that sums scattered
// light also sums occlusion, exactly, with no ordering and no extra pass.
//
// Behind a keyword because the alternative is worse than a branch: target 1 has to be BOUND, so a
// permanently-MRT shader would force the offscreen route (and its buffer) on every project whether
// or not it uses extinction. Keyword off = byte-identical to a single-target beam pass.
#if defined(_STAGEBEAM_TRANSMITTANCE)
    #define STAGEBEAM_TARGETS   out half4 outColor : SV_Target0, out float outTau : SV_Target1
    #define STAGEBEAM_WRITE(c, t)  { outColor = (c); outTau = (t); }
#else
    #define STAGEBEAM_TARGETS   out half4 outColor : SV_Target0
    #define STAGEBEAM_WRITE(c, t)  { outColor = (c); }
#endif

// The full cone raymarch. camCL/rayCL/exitCL are the camera origin, ray direction and hull exit
// in the beam's CL frame (axis = +Z, object scale 1 so distances == world). lightWS is the beam
// apex in world space (shadow origin). screenPix = fragment pixel coords (for depth UV + jitter).
// opticalDepth returns the beam's own τ over the marched span, for the transmittance target above.
half4 StageBeamRaymarch(BeamParams p, float3 camWS, float3 dirWS,
                        float3 camCL, float3 rayCL, float3 exitCL,
                        float3 lightWS, float2 screenPix, out float opticalDepth)
{
    opticalDepth = 0.0;   // set before every early-out below: an unwritten out is undefined
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
    // Decorrelate the sampling phase BETWEEN beams. Every march at a pixel otherwise uses the
    // same IGN value, so overlapping cones — prism facets above all, which share an apex and
    // differ only by a small rotation — place their sample combs in lockstep, and two aligned
    // combs through fine gobo shafts BEAT: a structured moire the denoiser must preserve
    // because it does not look like noise. camCL is per-beam (it rotates with the facet's
    // frame) and already an input, so the hash costs one dot+frac and no new plumbing;
    // decorrelated, the interference turns into incoherent grain that dither, the denoise
    // pass and temporal integration already know how to eat.
    jpix += frac(dot(camCL, float3(12.9898, 78.233, 37.719))) * 64.0;
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
    // The far end eases out instead of ending on a step. The axial cut used to be step(z, Range),
    // which drops whatever value the falloff still had there straight to zero — and for a narrow
    // beam, whose knee is metres out, that is a lot: a throw ending in mid-air showed a flat disc
    // where its volume stopped. A fixed fraction of Range rather than another dial, because the
    // fade is only ever visible on a beam that hits nothing, and nobody tunes the length of
    // something they are trying not to notice.
    float invTipFade = 1.0 / max(0.12 * p.range, 1e-4);

    // Virtual apex: the point the lens optics appear to emit from, zApexLen metres BEHIND the
    // lens (the same construction BeamEntryDistance solves its cone with, since the lateral
    // slope is tanField). The physical falloff takes its knee from this length, and the phase
    // direction is measured from here — finite right at the lens, so it never divides by ~0
    // at the root.
    float zApexLen = radiusStart / tanField;
    float invNearFade = 1.0 / max(_StageBeamNearFade, 1e-4);

    bool  usePhase = abs(p.anisotropy) > 1e-3;
    float phaseG   = p.anisotropy;
    float phaseG2  = phaseG * phaseG;
    // Henyey-Greenstein normalised so a beam seen SIDE-ON is 1.0 whatever g is. Unnormalised, the
    // side-on value is (1-g²)/(1+g²)^1.5 — 0.40 at g = 0.6 — so raising Anisotropy on one fixture
    // dimmed that fixture by 2.5x, and "which way does it scatter" doubled as an exposure control.
    // Master cannot fix that: it is global, and Anisotropy is per-fixture. Dividing the HG by its
    // own side-on value cancels the (1-g²) numerator outright, so this is also one op cheaper.
    float phaseNorm = pow(1.0 + phaseG2, 1.5);

    float sum = 0.0;
    float whiteSum = 0.0;
    float sumRaw = 0.0;   // debug: sum WITHOUT shadow
    float sumNH = 0.0;      // haze-unmodulated sums (composite ratio re-apply)
    float whiteSumNH = 0.0;
    float shadowCache = 1.0;
    float hazeCache   = 1.0;
    // Transmittance from the camera to the sample being shaded. Without it the march is a plain
    // SUM: every sample counts the same whether it is the near face of the beam or the far one, so
    // a dense beam just grows brighter and whiter instead of gaining a front and a back. Real haze
    // scatters and absorbs on the way to the eye, so the far side arrives attenuated — that is
    // what reads as volume rather than as a lit fog.
    //
    // This is the VIEW-RAY half of Beer-Lambert only. The other half, the beam occluding what is
    // behind it, cannot be done here: the pass blends additively (One One), and making a beam
    // darken its background needs coverage in the alpha channel and an alpha-blended composite,
    // which is a change to the whole compositing chain rather than to this loop.
    float trans = 1.0;
    [loop]
    for (int k = 0; k < steps; k++)
    {
        float t = tIn + (k + jitterNoise) * stepSize;
        float3 p3 = camCL + rayCL * t;
        float3 pWS = camWS + dirWS * t;

        float z = p3.z;
        float inAxis = step(0.0, z) * saturate((p.range - z) * invTipFade);
        float axNorm = z / p.range;
        float3 relApex = float3(p3.xy, z + zApexLen);   // sample position from the virtual apex

        float hazeF = 1.0;
        if (_HazeStrength > 0.0001)
        {
            if ((k & 1) == 0)
            {
                float n = SAMPLE_TEXTURE3D_LOD(_HazeNoise, sampler_HazeNoise,
                            pWS * _HazeScale + _HazeScroll.xyz, 0).r;
                // Floored, because the unmodulated baseline below divides BY this. At full
                // Haze Strength the noise reaches 0 and so does this factor, and 0/0 is a NaN
                // that the denoise pass then spreads over its whole 5x5 neighbourhood.
                hazeCache = max(1.0 + _HazeStrength * (n - 0.5) * 2.0, 0.02);
            }
            hazeF = hazeCache;
        }

        float widthAtZ = BeamWidthAt(z, radiusStart, radiusEnd, p.range);
        float radial   = length(p3.xy);

        // Edge antialiasing, on the same principle the gobo filter already uses: a feature
        // thinner than the sampling grid must be widened until it is resolvable, or it aliases
        // against it. `edge` is the normalised distance in from the cone's rim, and its screen
        // gradient says how much of that field one pixel spans; softening by at least that much
        // guarantees the rim always crosses a full pixel of gradient rather than snapping between
        // in and out. A Spot's authored softness is 0.15 of the beam radius, which is under one
        // pixel once a narrow beam is far away or the buffer is at Third — hence the staircase.
        //
        // Costs two derivatives and never sharpens: a beam wide enough on screen keeps exactly the
        // softness it was authored with, since the footprint is then the smaller of the two.
        float edge = (widthAtZ - radial) / max(widthAtZ, 1e-5);
        // Haze diffuses a throw as it travels: the rim a metre from the lens keeps the authored
        // sharpness, the far end reads visibly blurrier. Linear in z — multiple scattering blur
        // grows roughly with path length — and one madd per sample is the whole cost. The 0.25
        // gain (a quarter of full softness added by the far end) is fixed: per-fixture rim width
        // is already EdgeSoftness's job, and a separate growth dial proved indistinguishable
        // from it in practice.
        float sideSoftEff = sideSoftness + 0.25 * _StageBeamPhysical * axNorm;
        float edgeSoft = max(sideSoftEff, fwidth(edge) * _StageBeamEdgeAA);
        // Linear on purpose. This was briefly x(2-x), to meet the flat interior with zero slope
        // and remove the crease a linear ramp leaves at the INNER end of the rim. The crease is
        // real, but it had never been reported — it was inferred from reading the profile — and
        // the replacement doubled the slope at the RIM (f'(0) = 2 against 1), which visibly
        // hardened every beam edge. A speculative fix is not worth an observed regression, and
        // softness here is EdgeSoftness's job, not the ramp shape's.
        float side = saturate(edge / edgeSoft);

        float widthBeam = z * tanBeam + radiusStart;
        float rNorm = radial / max(widthAtZ, 1e-5);
        float beamFrac = saturate(widthBeam / max(widthAtZ, 1e-5));
        float hot = 1.0 - smoothstep(0.0, beamFrac, rNorm);
        float hotFactor = 1.0 + p.hotspot * hot;

        // --- Photometric profile ------------------------------------------------------------
        // A fixture is specified by two angles with agreed meanings: the BEAM angle is where its
        // intensity has fallen to 50% of peak, the FIELD angle where it reaches 10%. That is a
        // smooth bell, falling the whole way out. The bump above is not that shape — it decays to
        // 1.0 at the beam angle and then sits FLAT until `side` cuts the field edge off, so the
        // core is a raised plateau on a slab rather than a peak, which is why the centre never
        // reads as a core no matter how far Hotspot is pushed.
        //
        // exp(-k*r^n) hits both anchors exactly: k = ln(10) puts 10% at the field edge, and n
        // solves exp(-k*beamFrac^n) = 0.5. Nothing new to author — both angles already come from
        // GDTF, so a fixture whose beam angle is near its field angle stays broad and one whose
        // beam angle is half of it gets a genuinely sharp core, straight from its own data.
        //
        // Matched at the CENTRE, not by area: the peak stays where the old model put it and the
        // surroundings fall away, so this DIMS a beam overall. That is the honest consequence of
        // replacing a plateau with a bell; compensate with Intensity or Master, not by flattening
        // the profile back out.
        //
        // Behind a branch on a UNIFORM: a log, a pow and an exp per sample is real money at 48
        // steps, and without the guard every frame pays it to multiply the result by zero.
        //
        // Hotspot scales the DEPTH of the bell, inside the exponent, rather than multiplying its
        // height. As a height multiplier — which is what (1 + Hotspot) x bell was — it did not
        // touch the shape at all: `bell` does not depend on it, so the dial that claims to set
        // "how much brighter the core is than the outer field" was a plain gain, and turning it
        // to 0 dimmed the beam to nothing instead of flattening it. In the exponent it does what
        // it says: 0 = flat across the field, 1 = the fixture's own data (50% at the beam angle,
        // 10% at the field angle), 2 = 1% at the field angle and a much harder core. Free — it
        // folds into a multiply that was already there.
        //
        // This DIMS a beam by (1 + Hotspot) versus the height-multiplier form; Master's default
        // is rebalanced for it alongside the phase normalisation (see StageBeamRendererFeature).
        if (_StageBeamPhysical > 1e-3)
        {
            float bf   = clamp(beamFrac, 0.05, 0.95);
            float nExp = log(0.30103) / log(bf);        // ln(ln2/ln10) / ln(beamFrac)
            float bell = exp(-2.302585 * p.hotspot * pow(max(rNorm, 1e-5), nExp));
            hotFactor  = lerp(hotFactor, bell, _StageBeamPhysical);
        }

        // Same reasoning as the bell above: RootBoost is off on most fixtures, and the exp is
        // pure waste on those. Uniform per beam, so the branch is coherent across the wave.
        float rootBoost = 1.0;
        if (p.rootBoost > 1e-4) rootBoost = 1.0 + p.rootBoost * exp(-z / rootLen);

        // --- Distance falloff ---------------------------------------------------------------
        // Legacy: an inverted parabola in z/range — flat near the root with all the dimming
        // shoved to the far end. Physical mode: irradiance from the virtual apex, normalised
        // AT THE LENS — att = (zA/(z+zA))^AxialFalloff, exactly 1 at z = 0 (the root is never
        // brightened; source glare is RootGlare/lens-emissive's job) and 2 = true inverse
        // square. zA is derived from each beam's own optics, and that is the point: it is the
        // knee of the curve, so a NARROW zoom (zA metres to tens of metres) reads as a solid
        // stick reaching far, while a WIDE wash (zA well under a metre) glows under the
        // fixture and dies — the zoom-dependent character real fixtures have, with nothing
        // per-fixture to author and no dependence on Range at all.
        //
        // NOT the exponential exp(-k·z) tried before this: a constant relative decay is
        // cancelled by the cone's own linear width growth, so a wide cone's side-on brightness
        // (chord × att ∝ (z+zA)·att) RISES until z ≈ 1/k and the top half reads as a flat
        // CG slab. This curve makes chord × att = (z+zA)^(1-falloff), monotone falling from
        // the lens for any falloff > 1 — the beam visibly starts dying the moment it leaves
        // the glass. With global extinction on, the light's own Beer-Lambert path through the
        // haze (∝ density) still multiplies in, so dense beams die sooner than clean ones.
        // One pow (+ exp under extinction) per sample, uniform-branched like the bell above.
        float axialAtt = saturate(1.0 - axNorm * axNorm * p.axialFalloff);
        if (_StageBeamPhysical > 1e-3)
        {
            float attPhys = pow(zApexLen / (max(z, 0.0) + zApexLen), p.axialFalloff);
            if (_StageBeamExtinction > 1e-5)
                attPhys *= exp(-_StageBeamExtinction * p.density * max(z, 0.0));
            axialAtt = lerp(axialAtt, attPhys, _StageBeamPhysical);
        }

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
            // Full-step ray footprint, restored after an attempt at half-step: the sharper
            // shafts it bought were then lost anyway inside the half-res buffer (whose own
            // grid became the limit), while the removed margin let borderline moire through
            // at the distances where the two footprints cross over. Shaft peak brightness is
            // capped by the BUFFER resolution before it is capped by this filter — the honest
            // lever is the Resolution Scale, not under-filtering the pattern.
            float  rayFoot = length(dgdt) * stepSize * 0.5;                 // 0.5: guv = g*0.5+0.5
            // 1.25 floor: filtering exactly at the screen Nyquist is borderline by definition;
            // a quarter over costs almost nothing through trilinear mip blending. The reduced-
            // resolution widening stacks on top via the global.
            float  scrFoot = max(length(ddx(g)), length(ddy(g))) * 0.5
                             * max(_StageBeamGoboFilterWiden, 1.25);
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
            // Direction the light actually travels: from the VIRTUAL apex, not the lens centre.
            // normalize(p3) degenerates at the root (p3 → 0), which made the phase term flicker
            // exactly where the camera gets close to the fixture; relApex never collapses.
            float pLen = max(length(relApex), 1e-4);
            float cosT = dot(relApex / pLen, -rayCL);
            // pow(x, 1.5) = x * sqrt(x); the latter is a mul + rsqrt vs pow's log/exp.
            float d = abs(1.0 + phaseG2 - 2.0 * phaseG * cosT);
            phase = phaseNorm / (d * sqrt(d));
            // Multiple scattering. One HG lobe is what light does on a SINGLE bounce; in haze
            // thick enough to show beams at all, a good share of what reaches the eye has bounced
            // more than once, and every bounce randomises the direction further. The limit of
            // that is isotropic, so mixing the single lobe toward 1.0 stands in for it — cheaply.
            // It buys the soft halo dense haze carries around a beam, and stops a beam pointed
            // away from the camera collapsing to nothing. The isotropic end is exactly 1.0 only
            // because of the side-on normalisation above; without it this would also be a
            // brightness change.
            phase = lerp(phase, 1.0, _StageBeamMultiScatter);
        }

        float base  = inAxis * side * hotFactor * axialAtt * gobo * hazeF * phase;

        // Camera-side twin of the surface contact fade below: the first stretch in front of the
        // eye eases in, so flying the camera into a beam meets fog it enters rather than a lit
        // wall it clips through. At NearFade 0 the multiplier saturates to 1 — a no-op.
        base *= saturate(t * invNearFade);

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
        // --- Extinction over this step, and the segment integral it implies ------------------
        // Proportional to the haze actually present: the beam's own density, the noise field's
        // local thickening, and nothing at all outside the cone, so a ray crossing empty stage
        // between two beams is not attenuated by either.
        //
        // Thickness follows the beam's own SHAPE, not just its bounding cone. A pure `inAxis`
        // weight — the volume is either fully hazed or empty — makes the throw opaque all the way
        // to Range and out to the hull at FULL strength, including the faded tail and the soft rim
        // where the beam is no longer visible at all. The fixture then reads as a black cone with
        // a bright core inside it, widest and darkest exactly where it should have disappeared.
        //
        // Physically a real haze column WOULD occlude that evenly — but only because the rest of
        // the room is hazed too, and here it isn't: this model puts haze inside beams and nowhere
        // else (see `trans`'s note above), so an evenly-opaque cone is a beam-shaped hole in clean
        // air rather than a thicker patch of a hazy room.
        //
        // Shape only — `side`, the throw, the gobo. Intensity, Master and the phase function stay
        // out: how bright a beam is told to be, and which way it is being looked at, must not
        // change how much it blocks.
        float sigma = 0.0;
        float stepT = 1.0;     // transmittance across THIS step
        // How much this sample is worth. A plain Riemann sum weights every step by stepSize, which
        // over-counts as soon as the medium attenuates appreciably WITHIN one step — and by an
        // amount that depends on the step count, so raising Raymarch Steps quietly changed
        // exposure on a dial whose tooltip tells people to raise it. (1-exp(-σΔ))/σ is the exact
        // integral of the step, equals Δ in the limit σ→0, and is right at any thickness.
        float seg = stepSize;
        if (_StageBeamExtinction > 1e-5)
        {
            float shape = inAxis * side * axialAtt * gobo;
            sigma = _StageBeamExtinction * p.density * hazeF * shape;
            if (sigma > 1e-6)
            {
                stepT = exp(-sigma * stepSize);
                seg   = (1.0 - stepT) / sigma;
            }
        }

        float boost = rootBoost - 1.0;
        float bC = 1.0 + boost * (1.0 - p.rootWhite);   // colour weight (was recomputed ×3)
        float bW = boost * p.rootWhite;                  // white weight (was recomputed ×2)
        float w  = seg * trans;                          // segment integral × transmittance to here
        sum        += base * bC * w;
        whiteSum   += base * bW * w;
        float baseNM = baseRaw / hazeF;
        sumNH      += baseNM * bC * w;
        whiteSumNH += baseNM * bW * w;

        if (sigma > 1e-6)
        {
            // One depth for both halves of Beer-Lambert: `trans` dims the samples BEHIND this one
            // on the way to the eye, `opticalDepth` accumulates the same thickness for the
            // composite to dim the BACKGROUND with. Sharing the term keeps them consistent by
            // construction — a beam can never look thick from the front and thin from behind.
            opticalDepth += sigma * stepSize;
            trans *= stepT;
            // Bails at τ ≈ 6.2, where the background is already 99.8% extinguished, so the depth
            // this stops accumulating cannot change the composite.
            if (trans < 0.002) break;   // nothing behind this can still register
        }
    #if defined(_STAGEBEAM_SHADOWS_VOLUME)
        // sumRaw feeds ONLY the shadow-debug view — accumulate it only when that's on, not every
        // sample of every production frame.
        if (_BeamShadowDebug > 0.5) sumRaw += baseRaw * bC * seg;
    #endif
    }

    // stepSize is no longer folded in here — it moved into `seg`, per sample, where the analytic
    // segment integral takes its place (and equals it wherever the medium is thin).
    float scale = p.density * p.intensity * _StageBeamMaster;
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
