Shader "Origuma/StageBeamCone"
{
    Properties
    {
        _BeamColor   ("Beam Color", Color) = (1,1,1,1)
        _Intensity   ("Intensity", Float) = 1
        _StartRadius ("Start Radius", Float) = 0.05
        _EndRadius   ("End Radius", Float) = 2
        _Range       ("Range", Float) = 15
        _EdgeSoftness("Side Softness", Range(0.01,1)) = 0.35
        _FieldHalf   ("Field Half Angle (rad)", Float) = 0.22
        _BeamHalf    ("Beam Half Angle (rad)", Float) = 0.13
        _Density     ("Density", Float) = 1.0
        _Steps       ("Raymarch Steps", Float) = 24
        _DepthOcclude("Scene Depth Occlusion", Float) = 1
        _SurfaceFadeDist("Surface Contact Fade Distance", Float) = 0.35
        _AxialFalloff  ("Axial Falloff", Range(0, 2)) = 1.0
        _Hotspot       ("Hotspot Strength (beam-angle core)", Range(0, 4)) = 1.0
        _RootBoost     ("Root Glare Boost", Range(0, 6)) = 0.0
        _RootBoostFrac ("Root Glare Length (frac of range)", Range(0.01, 0.5)) = 0.1
        _RootWhite     ("Root Glare White Bias", Range(0, 1)) = 0.6
        _GoboSlice   ("Gobo Slice (-1=off)", Float) = -1
        _GoboRotation("Gobo Rotation (rad)", Float) = 0
        [NoScaleOffset] _GoboArray ("Gobo Array", 2DArray) = "white" {}
        _GoboSlice2   ("Gobo Slice 2 (-1=off)", Float) = -1
        _GoboRotation2("Gobo Rotation 2 (rad)", Float) = 0
        [NoScaleOffset] _GoboArray2 ("Gobo Array 2", 2DArray) = "white" {}
        // Anti-banding: value-relative dither hides the quantization contours the
        // smooth gradient picks up when stored in a low-mantissa target (R11G11B10).
        _Dither      ("Dither (anti-banding)", Range(0, 0.1)) = 0.02
        // Blend factors: Additive = One One, Soft Additive (screen) = OneMinusDstColor One.
        [HideInInspector] _BeamSrcBlend ("Src Blend", Float) = 1
        [HideInInspector] _BeamDstBlend ("Dst Blend", Float) = 1
    }

    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" "RenderPipeline"="UniversalPipeline" }

        Pass
        {
            Name "StageBeam"
            Tags { "LightMode" = "SRPDefaultUnlit" }
            Blend [_BeamSrcBlend] [_BeamDstBlend]
            ZWrite Off
            ZTest Always
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile _ _STAGEBEAM_SHADOWS_SCREEN _STAGEBEAM_SHADOWS_VOLUME
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "StageBeamShadow.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _BeamColor;
                float  _Intensity;
                float  _StartRadius;
                float  _EndRadius;
                float  _Range;
                float  _EdgeSoftness;
                float  _FieldHalf;
                float  _BeamHalf;
                float  _Density;
                float  _Steps;
                float  _DepthOcclude;
                float  _SurfaceFadeDist;
                float  _AxialFalloff;
                float  _Hotspot;
                float  _RootBoost;
                float  _RootBoostFrac;
                float  _RootWhite;
                float  _GoboSlice;
                float  _GoboRotation;
                float  _GoboSlice2;
                float  _GoboRotation2;
                float  _Dither;
            CBUFFER_END

            TEXTURE2D_ARRAY(_GoboArray);
            SAMPLER(sampler_GoboArray);
            TEXTURE2D_ARRAY(_GoboArray2);
            SAMPLER(sampler_GoboArray2);

            struct Attributes { float3 positionOS : POSITION; };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float3 positionWS  : TEXCOORD0;
            };

            Varyings vert (Attributes v)
            {
                Varyings o;
                float t = v.positionOS.y;
                // Size the bounding hull from the SAME field-angle beam width the
                // fragment march uses, so the hull always encloses the analytic cone.
                // (Deriving it from _EndRadius let the beam grow wider than the mesh,
                // clipping its soft edge to the cone's polygon facets — visible "segments".)
                float rEndBeam = _Range * max(tan(_FieldHalf), 1e-3) + _StartRadius;
                float r = lerp(_StartRadius, rEndBeam, saturate(t)) * 1.2;
                float3 posOS = float3(v.positionOS.x * r, -t * _Range, v.positionOS.z * r);
                o.positionWS = TransformObjectToWorld(posOS);
                o.positionHCS = TransformWorldToHClip(o.positionWS);
                return o;
            }

            float _BeamFrameIndex;
            // xy = 1 / render-target size. Lets depth sampling use the correct screen UV
            // whether drawing at full resolution or into a smaller off-screen target.
            float4 _BeamRTParams;

            // Optional world-space 3D haze noise (set globally by the renderer feature).
            TEXTURE3D(_HazeNoise);
            SAMPLER(sampler_HazeNoise);
            float  _HazeStrength;   // 0 = off
            float  _HazeScale;      // world units → noise UVW
            float4 _HazeScroll;     // xyz = animated world-space offset

            float IGN(float2 p)
            {
                return frac(52.9829189 * frac(dot(p, float2(0.06711056, 0.00583715))));
            }

            // Width (radius) of the beam volume at axial distance z.
            float BeamWidthAt(float z, float rStart, float rEnd, float zEnd)
            {
                return lerp(rStart, rEnd, saturate(z / zEnd));
            }

            // Nearest forward distance at which the ray enters the truncated beam
            // volume (axis = +Z, radius rStart at z=0 growing to rEnd at z=zEnd).
            // The lateral surface is treated as an infinite cone about its apex and
            // solved as a quadratic in t; the near end disk is handled separately.
            // Returns a large sentinel when the ray never enters within the z-range.
            float BeamEntryDistance(float3 ro, float3 rd, float zEnd, float rStart, float rEnd)
            {
                float slope = max((rEnd - rStart) / zEnd, 1e-5);   // dRadius / dz
                float zApex = -rStart / slope;                     // apex sits behind z=0
                float cosSq = 1.0 / (1.0 + slope * slope);         // cos^2 of the half-angle

                float3 ao = ro - float3(0.0, 0.0, zApex);
                float dN  = rd.z;            // ray dir projected on the axis
                float aN  = ao.z;            // apex->origin projected on the axis

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

                // Entry through either end disk: the small near end (z = 0, radius
                // rStart) or the open far end (z = zEnd, radius rEnd). For a convex
                // volume the smallest valid boundary distance is always the entry.
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

            half4 frag (Varyings i) : SV_Target
            {
                // Object space has the beam axis along -Y; remap to a frame whose axis
                // is +Z so the cone test below is axis-aligned. Object scale is 1, so
                // distances in this frame match world-space distances.
                float3 camWS  = GetCameraPositionWS();
                float3 exitWS = i.positionWS;
                float3 rayWS  = exitWS - camWS;
                float  segLen = length(rayWS);
                if (segLen < 1e-4) return 0;
                float3 dirWS = rayWS / segLen;

                float3 camOS  = TransformWorldToObject(camWS);
                float3 exitOS = TransformWorldToObject(exitWS);
                float3 camCL  = float3(camOS.x,  camOS.z,  -camOS.y);
                float3 exitCL = float3(exitOS.x, exitOS.z, -exitOS.y);
                float3 rayCL  = normalize(exitCL - camCL);

                float tanField   = max(tan(_FieldHalf), 1e-3);
                float radiusStart = _StartRadius;
                float radiusEnd   = _Range * tanField + _StartRadius;

            #if defined(_STAGEBEAM_SHADOWS_SCREEN) || defined(_STAGEBEAM_SHADOWS_VOLUME)
                float3 lightWS = TransformObjectToWorld(float3(0.0, 0.0, 0.0)); // beam apex = light
            #endif
            #if defined(_STAGEBEAM_SHADOWS_VOLUME)
                float dbgOcc = 0.0;      // red: max occupancy the view ray passes through
                float dbgShadow = 0.0;   // green: max shadowing of the samples (shadow ray hit)
            #endif

                // Start the march where the ray actually enters the volume, independent
                // of the bounding mesh. If the camera already sits inside, start at it.
                float camWidth = BeamWidthAt(camCL.z, radiusStart, radiusEnd, _Range);
                bool camInside = (camCL.z >= 0.0 && camCL.z <= _Range &&
                                  length(camCL.xy) <= camWidth);

                float tIn;
                if (camInside)
                {
                    tIn = 0.0;
                }
                else
                {
                    float tEnter = BeamEntryDistance(camCL, rayCL, _Range, radiusStart, radiusEnd);
                    if (tEnter > 1e8) return 0;            // ray never enters the volume
                    tIn = tEnter;
                }

                float tOut = length(exitCL - camCL);       // far hull face (= segLen)
                bool  depthClipped = false;                // tOut cut short by real scene geometry

                // Clamp the exit to the opaque scene depth (occlusion).
                if (_DepthOcclude > 0.5)
                {
                    float2 suv = i.positionHCS.xy * _BeamRTParams.xy;
                    float sceneEye = LinearEyeDepth(SampleSceneDepth(suv), _ZBufferParams);
                    float3 fwd = GetViewForwardDir();
                    float cosToRay = max(dot(dirWS, fwd), 1e-3);
                    float sceneDist = sceneEye / cosToRay;
                    if (sceneDist < tOut) { tOut = sceneDist; depthClipped = true; }
                }

                if (tOut <= tIn) return 0;                 // nothing in front of the exit

                int   steps   = max((int)_Steps, 1);
                float chord   = tOut - tIn;

                // Per-pixel, per-frame jitter offset (full step) to break up the
                // visible sample planes across the beam cross-section.
                float spatialNoise   = IGN(i.positionHCS.xy);
                float temporalOffset = frac(_BeamFrameIndex * 0.6180339887);
                float jitterNoise    = frac(spatialNoise + temporalOffset);
                float stepSize       = chord / steps;

                bool  useGobo  = _GoboSlice  >= 0.0;
                bool  useGobo2 = _GoboSlice2 >= 0.0;
                float cs  = cos(_GoboRotation),  sn  = sin(_GoboRotation);
                float cs2 = cos(_GoboRotation2), sn2 = sin(_GoboRotation2);
                float sideSoftness = max(_EdgeSoftness, 0.01);
                float tanBeam = max(tan(_BeamHalf), 1e-4);
                float rootLen = max(_RootBoostFrac * _Range, 1e-3);

                // March a fixed number of samples across the cone chord [tIn, tOut].
                // Spreading the samples over the chord (rather than camera→mesh) keeps
                // the per-pixel sample density uniform, and averaging then scaling by the
                // chord length gives a stable line integral with a clean cone edge.
                float sum = 0.0;
                float whiteSum = 0.0;
                float sumRaw = 0.0;   // debug: sum WITHOUT shadow, to prove the shadow reduces it
                [loop]
                for (int k = 0; k < steps; k++)
                {
                    float t = tIn + (k + jitterNoise) * stepSize;
                    float3 p = camCL + rayCL * t;

                    float z = p.z;
                    float inAxis = step(0.0, z) * step(z, _Range);

                    // Haze turbulence: modulate density with world-space 3D noise so the
                    // volume looks like drifting atmosphere rather than a solid cone.
                    float hazeF = 1.0;
                    if (_HazeStrength > 0.0001)
                    {
                        float3 pWS = camWS + dirWS * t;
                        float n = SAMPLE_TEXTURE3D_LOD(_HazeNoise, sampler_HazeNoise,
                                    pWS * _HazeScale + _HazeScroll.xyz, 0).r;
                        hazeF = 1.0 + _HazeStrength * (n - 0.5) * 2.0;
                    }

                    float widthAtZ = BeamWidthAt(z, radiusStart, radiusEnd, _Range);
                    float radial   = length(p.xy);

                    // Smooth side falloff: 1 in the core, reaching 0 at the wall.
                    float side = saturate((widthAtZ - radial) / (widthAtZ * sideSoftness));

                    // Hotspot: brighter core inside the beam (inner) angle, fading toward
                    // the field (outer) edge — the real bright-centre / soft-penumbra look.
                    float widthBeam = z * tanBeam + radiusStart;
                    float rNorm = radial / max(widthAtZ, 1e-5);
                    float beamFrac = saturate(widthBeam / max(widthAtZ, 1e-5));
                    float hot = 1.0 - smoothstep(0.0, beamFrac, rNorm);
                    float hotFactor = 1.0 + _Hotspot * hot;

                    // Root glare: extra brightness near the lens (the source flare).
                    float rootBoost = 1.0 + _RootBoost * exp(-z / rootLen);

                    float axNorm   = z / _Range;
                    float axialAtt = saturate(1.0 - axNorm * axNorm * _AxialFalloff);

                    float gobo = 1.0;
                    if (useGobo || useGobo2)
                    {
                        float2 g = p.xy / max(widthAtZ, 1e-5);     // [-1,1] across the field
                        if (useGobo)
                        {
                            float2 guv = float2(g.x * cs - g.y * sn, g.x * sn + g.y * cs) * 0.5 + 0.5;
                            gobo *= SAMPLE_TEXTURE2D_ARRAY(_GoboArray, sampler_GoboArray, guv, _GoboSlice).r;
                        }
                        if (useGobo2)
                        {
                            float2 guv2 = float2(g.x * cs2 - g.y * sn2, g.x * sn2 + g.y * cs2) * 0.5 + 0.5;
                            gobo *= SAMPLE_TEXTURE2D_ARRAY(_GoboArray2, sampler_GoboArray2, guv2, _GoboSlice2).r;
                        }
                    }

                    // Split the root-glare excess into a white part (highlight desaturation:
                    // an intense source core reads white) and a tinted part.
                    float base  = inAxis * side * hotFactor * axialAtt * gobo * hazeF;

                    // Contact fade: when a real occluder cuts the march short (depthClipped),
                    // taper the density toward zero as we approach its surface instead of
                    // handing it the full un-attenuated column sum. Without this, any solid
                    // poking into the beam shows a hard, flat-lit disc (no surface shading is
                    // computed here) rather than blending into the beam like the rest of the volume.
                    if (depthClipped)
                    {
                        float distToExit = tOut - t;
                        base *= saturate(distToExit / max(_SurfaceFadeDist, 1e-4));
                    }
                    float baseRaw = base;
                #if defined(_STAGEBEAM_SHADOWS_SCREEN) || defined(_STAGEBEAM_SHADOWS_VOLUME)
                    if (base > 1e-4) base *= SampleBeamShadow(camWS + dirWS * t, lightWS);
                #endif
                #if defined(_STAGEBEAM_SHADOWS_VOLUME)
                    if (_BeamShadowDebug > 0.5)
                    {
                        float3 pWSdbg = camWS + dirWS * t;
                        dbgOcc = max(dbgOcc, StageBeamSampleOccupancy(pWSdbg));
                        // ACTUAL shadow applied to base (includes _BeamShadowStrength).
                        // Bright green = strong shadow IS multiplied into base (look/brightness issue).
                        // Dark green    = SampleBeamShadow≈1 despite the hit (strength not reaching shader).
                        dbgShadow = max(dbgShadow, 1.0 - SampleBeamShadow(pWSdbg, lightWS));
                    }
                #endif
                    float boost = rootBoost - 1.0;
                    sum      += base * (1.0 + boost * (1.0 - _RootWhite));
                    whiteSum += base * boost * _RootWhite;
                    sumRaw   += baseRaw * (1.0 + boost * (1.0 - _RootWhite));
                }

                float scale = (1.0 / steps) * chord * _Density * _Intensity;
                float3 col = _BeamColor.rgb * (sum * scale) + (whiteSum * scale).xxx;
            #if defined(_STAGEBEAM_SHADOWS_VOLUME)
                // Debug: RED = occupancy the view ray passes through (volume bound & populated).
                //        GREEN = samples the shadow ray found occluded (shadow IS being computed).
                // Red but no green anywhere = shadow ray never hits occupancy (direction/range).
                // Green present but beam not darkening (debug off) = magnitude / bloom issue.
                if (_BeamShadowDebug > 0.5)
                {
                    // Grayscale = sum/sumRaw = the shadow ratio ACTUALLY applied to this pixel.
                    // White (=1) everywhere below the sphere => sum is NOT being reduced (real bug).
                    // A dark shaft below the sphere => sum IS reduced (shadow works; was perceptual).
                    col = (sum / max(sumRaw, 1e-5)).xxx;
                }
            #endif
                return half4(col, 1.0);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
