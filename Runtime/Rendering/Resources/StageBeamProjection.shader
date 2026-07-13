Shader "Origuma/StageBeamProjection"
{
    // Projects each beam's gobo × colour onto the opaque surfaces it hits (light pools /
    // gobo projection), as a screen-space decal. Drawn with the same per-beam mesh + MPB
    // as the cone, so per-fixture gobo arrays and colours come through unchanged.
    Properties
    {
        _BeamColor   ("Beam Color", Color) = (1,1,1,1)
        _Intensity   ("Intensity", Float) = 1
        _StartRadius ("Start Radius", Float) = 0.05
        _EndRadius   ("End Radius", Float) = 2
        _Range       ("Range", Float) = 15
        _EdgeSoftness("Side Softness", Range(0.01,1)) = 0.35
        _FieldHalf   ("Field Half Angle (rad)", Float) = 0.22
        _AxialFalloff  ("Axial Falloff", Range(0, 2)) = 1.0
        _GoboSlice   ("Gobo Slice (-1=off)", Float) = -1
        _GoboRotation("Gobo Rotation (rad)", Float) = 0
        _GoboOffset  ("Gobo 1 UV Offset (animation wheel)", Vector) = (0,0,0,0)
        [NoScaleOffset] _GoboArray ("Gobo Array", 2DArray) = "white" {}
        _GoboSlice2   ("Gobo Slice 2 (-1=off)", Float) = -1
        _GoboRotation2("Gobo Rotation 2 (rad)", Float) = 0
        [NoScaleOffset] _GoboArray2 ("Gobo Array 2", 2DArray) = "white" {}
    }

    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" "RenderPipeline"="UniversalPipeline" }

        Pass
        {
            Name "StageBeamProjection"
            Tags { "LightMode" = "SRPDefaultUnlit" }
            Blend One One
            ZWrite Off
            ZTest Always
            Cull Front          // draw back faces so the proxy still covers when seen from inside
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile _ _STAGEBEAM_SHADOWS_SCREEN _STAGEBEAM_SHADOWS_VOLUME _STAGEBEAM_SHADOWS_LIGHT
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
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
                float  _AxialFalloff;
                float  _GoboSlice;
                float  _GoboRotation;
                float4 _GoboOffset;
                float  _GoboSlice2;
                float  _GoboRotation2;
            CBUFFER_END

            // Projection-wide knobs (set globally by the renderer feature).
            float _ProjSurfaceBoost;   // brightness multiplier for the projected pool
            float _ProjNormalCull;     // 0..1 cosine band: surfaces steeper than this fade out
            float _ProjShadowHardness; // 0 = raw soft shadow, higher = occluded pool → fully black

            // Receiver layer mask: a depth-only pre-pass of ONLY the receiver layers. A pixel
            // receives projection only when the visible surface IS that receiver surface
            // (depths match) — a character (excluded layer) standing in the pool stays clean.
            float _ProjReceiverMaskOn;
            TEXTURE2D_X(_StageBeamReceiverDepth);
            SAMPLER(sampler_StageBeamReceiverDepth);

            TEXTURE2D_ARRAY(_GoboArray);  SAMPLER(sampler_GoboArray);
            TEXTURE2D_ARRAY(_GoboArray2); SAMPLER(sampler_GoboArray2);

            struct Attributes { float3 positionOS : POSITION; };
            struct Varyings   { float4 positionHCS : SV_POSITION; };

            // Same proxy volume as the cone, 1.2× padded, so its screen footprint covers
            // every surface the beam can light.
            Varyings vert (Attributes v)
            {
                Varyings o;
                float t = v.positionOS.y;
                float r = lerp(_StartRadius, _EndRadius, saturate(t)) * 1.2;
                float3 posOS = float3(v.positionOS.x * r, -t * _Range, v.positionOS.z * r);
                o.positionHCS = TransformWorldToHClip(TransformObjectToWorld(posOS));
                return o;
            }

            half4 frag (Varyings i) : SV_Target
            {
                float2 uv = GetNormalizedScreenSpaceUV(i.positionHCS);

                // Reconstruct the lit surface in world space from the camera depth.
                float deviceDepth = SampleSceneDepth(uv);

                // Receiver mask: only paint pixels whose visible surface is on a receiver
                // layer (its depth matches the receiver-only depth pre-pass; anything in
                // front of the receivers — e.g. a character — mismatches and is skipped).
                if (_ProjReceiverMaskOn > 0.5)
                {
                    float recvDevice = SAMPLE_TEXTURE2D_X(_StageBeamReceiverDepth,
                        sampler_StageBeamReceiverDepth, uv).r;
                    float sceneEye = LinearEyeDepth(deviceDepth, _ZBufferParams);
                    float recvEye  = LinearEyeDepth(recvDevice, _ZBufferParams);
                    if (abs(sceneEye - recvEye) > 0.02 * sceneEye + 0.02) return 0;
                }

                float3 surfWS = ComputeWorldSpacePosition(uv, deviceDepth, UNITY_MATRIX_I_VP);

                // Into the beam's object space (axis = -Y, radius grows with axis). Sky /
                // far-plane points fall outside the axial range and are rejected here.
                float3 pOS = TransformWorldToObject(surfWS);
                float axis = -pOS.y;
                if (axis <= 0.0 || axis > _Range) return 0;
                float tanField = max(tan(_FieldHalf), 1e-3);
                float coneR = axis * tanField + _StartRadius;
                float rad = length(pOS.xz);
                if (rad > coneR) return 0;

                // Fixture housing / anything sitting right at the lens reconstructs as a
                // "surface" too (unlike the volumetric cone, this decal has no raymarch to fade
                // through) — without this it paints a hard, full-strength pool right at the
                // beam's own root. Reuses the Volume-shadow near-light exclusion radius so one
                // knob (StageBeamOcclusionVolume.LightBias) governs both. 0 = feature unused, no-op.
                float nearFade = _BeamShadowLightBias > 0.0 ? smoothstep(0.0, _BeamShadowLightBias, axis) : 1.0;
                if (nearFade <= 0.0) return 0;

                // Soft cone edge + axial falloff.
                float sideSoftness = max(_EdgeSoftness, 0.01);
                float side = saturate((coneR - rad) / (coneR * sideSoftness));
                float axNorm = axis / _Range;
                float axialAtt = saturate(1.0 - axNorm * axNorm * _AxialFalloff);

                // Normal culling: geometric normal from depth. Plain ddx/ddy quad derivatives
                // get noisy on curved surfaces, at depth edges and at grazing angles — pick the
                // horizontal/vertical neighbour with the SMALLER position jump instead (min-diff
                // reconstruction), which stays stable across curvature and silhouettes.
                float2 px = float2(1.0 / _ScreenParams.x, 0.0);
                float2 py = float2(0.0, 1.0 / _ScreenParams.y);
                float3 posR = ComputeWorldSpacePosition(uv + px, SampleSceneDepth(uv + px), UNITY_MATRIX_I_VP);
                float3 posL = ComputeWorldSpacePosition(uv - px, SampleSceneDepth(uv - px), UNITY_MATRIX_I_VP);
                float3 posU = ComputeWorldSpacePosition(uv + py, SampleSceneDepth(uv + py), UNITY_MATRIX_I_VP);
                float3 posD = ComputeWorldSpacePosition(uv - py, SampleSceneDepth(uv - py), UNITY_MATRIX_I_VP);
                float3 dX = length(posR - surfWS) < length(surfWS - posL) ? posR - surfWS : surfWS - posL;
                float3 dY = length(posU - surfWS) < length(surfWS - posD) ? posU - surfWS : surfWS - posD;
                float3 nWS = normalize(cross(dY, dX));
                // Orientation-independent: a visible surface's normal faces the camera.
                if (dot(nWS, GetCameraPositionWS() - surfWS) < 0.0) nWS = -nWS;
                float3 beamDownWS = normalize(TransformObjectToWorldDir(float3(0, -1, 0)));
                float facing = dot(nWS, -beamDownWS);       // 1 = facing the lens, <0 = away
                float normalFade = smoothstep(0.0, max(_ProjNormalCull, 1e-3), facing);
                if (normalFade <= 0.0) return 0;

                // Gobo projection (wheel 1 × wheel 2), same field coords as the cone.
                float gobo = 1.0;
                bool useGobo  = _GoboSlice  >= 0.0;
                bool useGobo2 = _GoboSlice2 >= 0.0;
                if (useGobo || useGobo2)
                {
                    // [-1,1] across the field. Normalised by the SAME cone width as the
                    // volumetric shader (axis·tan + StartRadius) so the projected pattern's
                    // size matches the beam exactly, including close to the fixture.
                    float2 g = pOS.xz / max(coneR, 1e-5);
                    if (useGobo)
                    {
                        float cs = cos(_GoboRotation), sn = sin(_GoboRotation);
                        float2 guv = float2(g.x * cs - g.y * sn, g.x * sn + g.y * cs) * 0.5 + 0.5;
                        // Animation wheel scroll (matches the cone shader): tile via frac() ONLY
                        // while scrolling — a static gobo must respect the sampler's Clamp
                        // (frac() wraps uv 1.0 to 0.0 and bilinear bleeds the opposite edge).
                        if (abs(_GoboOffset.x) + abs(_GoboOffset.y) > 1e-5)
                            guv = frac(guv + _GoboOffset.xy);
                        half4 g1 = SAMPLE_TEXTURE2D_ARRAY(_GoboArray, sampler_GoboArray, guv, _GoboSlice);
                        // Alpha = coverage (transparent outside the aperture blocks light).
                        gobo *= g1.r * g1.a;
                    }
                    if (useGobo2)
                    {
                        float cs2 = cos(_GoboRotation2), sn2 = sin(_GoboRotation2);
                        float2 guv2 = float2(g.x * cs2 - g.y * sn2, g.x * sn2 + g.y * cs2) * 0.5 + 0.5;
                        half4 g2 = SAMPLE_TEXTURE2D_ARRAY(_GoboArray2, sampler_GoboArray2, guv2, _GoboSlice2);
                        gobo *= g2.r * g2.a;
                    }
                }

                float intensity = _Intensity * _ProjSurfaceBoost * side * axialAtt * normalFade * gobo * nearFade;

            #if defined(_STAGEBEAM_SHADOWS_SCREEN) || defined(_STAGEBEAM_SHADOWS_VOLUME) || defined(_STAGEBEAM_SHADOWS_LIGHT)
                // Shadow the light pool where the occluder blocks the beam from the surface point.
                float shadow = SampleBeamShadow(surfWS,
                    TransformObjectToWorld(float3(0.0, 0.0, 0.0)),
                    StageBeamIGN(i.positionHCS.xy));
                // The volumetric shadow is a soft transmittance (exp falloff) that never quite
                // reaches 0, so a sharp gobo pool keeps a faint residual even when fully blocked.
                // Remap so transmittance below the hardness threshold clamps to fully black — a
                // crisp hard shadow on the floor, with complete occlusion possible.
                shadow = saturate((shadow - _ProjShadowHardness) / max(1.0 - _ProjShadowHardness, 1e-3));
                intensity *= shadow;
            #endif
                float3 col = _BeamColor.rgb * intensity;
                // Soft Additive accumulates pools raw into an offscreen buffer and the composite
                // saturates the SUMMED total toward the ceiling — so pools stop at the same thin
                // ceiling as the beams instead of piling to white (which bloom/ACES then wreck).
                // Alpha carries this pool's luminance so the composite's ratio math is a no-op
                // for decals (m ≈ 1) and the ceiling applies straight to the summed colour.
                float lum = dot(col, float3(0.2126, 0.7152, 0.0722));
                return half4(col, lum);
            }
            ENDHLSL
        }

        // GPU-instanced variant: one DrawMeshInstancedProcedural per gobo batch, reading the SAME
        // per-beam StructuredBuffer<GpuBeam> the cone pass uses. Collapses the projection pass from
        // one DrawMesh per beam (the dominant CPU draw-call cost with gobos on) to one per batch.
        // The decal is fill-light, not a raymarch, so it just reads the GpuBeam per fragment.
        Pass
        {
            Name "StageBeamProjectionInstanced"
            Tags { "LightMode" = "SRPDefaultUnlit" }
            Blend One One
            ZWrite Off
            ZTest Always
            Cull Front
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile _ _STAGEBEAM_SHADOWS_SCREEN _STAGEBEAM_SHADOWS_VOLUME _STAGEBEAM_SHADOWS_LIGHT
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "StageBeamShadow.hlsl"
            #include "StageBeamInstanced.hlsl"

            float _ProjSurfaceBoost;
            float _ProjNormalCull;
            float _ProjShadowHardness;
            float _ProjReceiverMaskOn;
            TEXTURE2D_X(_StageBeamReceiverDepth);
            SAMPLER(sampler_StageBeamReceiverDepth);
            TEXTURE2D_ARRAY(_GoboArray);  SAMPLER(sampler_GoboArray);
            TEXTURE2D_ARRAY(_GoboArray2); SAMPLER(sampler_GoboArray2);

            struct Attributes { float3 positionOS : POSITION; uint instanceID : SV_InstanceID; };
            struct Varyings   { float4 positionHCS : SV_POSITION; nointerpolation uint instanceID : TEXCOORD0; };

            Varyings vert (Attributes v)
            {
                Varyings o;
                GpuBeam b = StageBeamAt(v.instanceID);
                float t = v.positionOS.y;
                float r = lerp(b.p0.x, b.p0.y, saturate(t)) * 1.2;   // startRadius, endRadius
                float3 posOS = float3(v.positionOS.x * r, -t * b.p0.z, v.positionOS.z * r); // range = p0.z
                o.positionHCS = TransformWorldToHClip(StageBeamObjectToWorld(b, posOS));
                o.instanceID = v.instanceID;
                return o;
            }

            half4 frag (Varyings i) : SV_Target
            {
                GpuBeam b = StageBeamAt(i.instanceID);
                float startRadius = b.p0.x, range = b.p0.z, edgeSoftness = b.p0.w;
                float fieldHalf = b.p1.x, axialFalloff = b.p2.z, intensityP = b.p3.w;

                float2 uv = GetNormalizedScreenSpaceUV(i.positionHCS);
                float deviceDepth = SampleSceneDepth(uv);

                if (_ProjReceiverMaskOn > 0.5)
                {
                    float recvDevice = SAMPLE_TEXTURE2D_X(_StageBeamReceiverDepth,
                        sampler_StageBeamReceiverDepth, uv).r;
                    float sceneEye = LinearEyeDepth(deviceDepth, _ZBufferParams);
                    float recvEye  = LinearEyeDepth(recvDevice, _ZBufferParams);
                    if (abs(sceneEye - recvEye) > 0.02 * sceneEye + 0.02) return 0;
                }

                float3 surfWS = ComputeWorldSpacePosition(uv, deviceDepth, UNITY_MATRIX_I_VP);
                float3 pOS = StageBeamWorldToObject(b, surfWS);
                float axis = -pOS.y;
                if (axis <= 0.0 || axis > range) return 0;
                float tanField = max(tan(fieldHalf), 1e-3);
                float coneR = axis * tanField + startRadius;
                float rad = length(pOS.xz);
                if (rad > coneR) return 0;

                float nearFade = _BeamShadowLightBias > 0.0 ? smoothstep(0.0, _BeamShadowLightBias, axis) : 1.0;
                if (nearFade <= 0.0) return 0;

                float sideSoftness = max(edgeSoftness, 0.01);
                float side = saturate((coneR - rad) / (coneR * sideSoftness));
                float axNorm = axis / range;
                float axialAtt = saturate(1.0 - axNorm * axNorm * axialFalloff);

                float2 px = float2(1.0 / _ScreenParams.x, 0.0);
                float2 py = float2(0.0, 1.0 / _ScreenParams.y);
                float3 posR = ComputeWorldSpacePosition(uv + px, SampleSceneDepth(uv + px), UNITY_MATRIX_I_VP);
                float3 posL = ComputeWorldSpacePosition(uv - px, SampleSceneDepth(uv - px), UNITY_MATRIX_I_VP);
                float3 posU = ComputeWorldSpacePosition(uv + py, SampleSceneDepth(uv + py), UNITY_MATRIX_I_VP);
                float3 posD = ComputeWorldSpacePosition(uv - py, SampleSceneDepth(uv - py), UNITY_MATRIX_I_VP);
                float3 dX = length(posR - surfWS) < length(surfWS - posL) ? posR - surfWS : surfWS - posL;
                float3 dY = length(posU - surfWS) < length(surfWS - posD) ? posU - surfWS : surfWS - posD;
                float3 nWS = normalize(cross(dY, dX));
                if (dot(nWS, GetCameraPositionWS() - surfWS) < 0.0) nWS = -nWS;
                float3 beamDownWS = normalize(StageBeamObjectToWorldDir(b, float3(0, -1, 0)));
                float facing = dot(nWS, -beamDownWS);
                float normalFade = smoothstep(0.0, max(_ProjNormalCull, 1e-3), facing);
                if (normalFade <= 0.0) return 0;

                float gobo = 1.0;
                bool useGobo  = b.g0.x >= 0.0;   // goboSlice
                bool useGobo2 = b.g1.x >= 0.0;   // goboSlice2
                if (useGobo || useGobo2)
                {
                    float2 g = pOS.xz / max(coneR, 1e-5);
                    if (useGobo)
                    {
                        float cs = cos(b.g0.y), sn = sin(b.g0.y);   // goboRot
                        float2 guv = float2(g.x * cs - g.y * sn, g.x * sn + g.y * cs) * 0.5 + 0.5;
                        if (abs(b.g0.z) + abs(b.g0.w) > 1e-5) guv = frac(guv + b.g0.zw); // goboOffset
                        half4 g1 = SAMPLE_TEXTURE2D_ARRAY(_GoboArray, sampler_GoboArray, guv, b.g0.x);
                        gobo *= g1.r * g1.a;
                    }
                    if (useGobo2)
                    {
                        float cs2 = cos(b.g1.y), sn2 = sin(b.g1.y);  // goboRot2
                        float2 guv2 = float2(g.x * cs2 - g.y * sn2, g.x * sn2 + g.y * cs2) * 0.5 + 0.5;
                        half4 g2 = SAMPLE_TEXTURE2D_ARRAY(_GoboArray2, sampler_GoboArray2, guv2, b.g1.x);
                        gobo *= g2.r * g2.a;
                    }
                }

                float intensity = intensityP * _ProjSurfaceBoost * side * axialAtt * normalFade * gobo * nearFade;

            #if defined(_STAGEBEAM_SHADOWS_SCREEN) || defined(_STAGEBEAM_SHADOWS_VOLUME) || defined(_STAGEBEAM_SHADOWS_LIGHT)
                float shadow = SampleBeamShadow(surfWS,
                    StageBeamObjectToWorld(b, float3(0.0, 0.0, 0.0)),
                    StageBeamIGN(i.positionHCS.xy));
                shadow = saturate((shadow - _ProjShadowHardness) / max(1.0 - _ProjShadowHardness, 1e-3));
                intensity *= shadow;
            #endif
                float3 col = b.color.rgb * intensity;
                float lum = dot(col, float3(0.2126, 0.7152, 0.0722));
                return half4(col, lum);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
