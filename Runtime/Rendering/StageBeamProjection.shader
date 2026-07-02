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
                float  _AxialFalloff;
                float  _GoboSlice;
                float  _GoboRotation;
                float  _GoboSlice2;
                float  _GoboRotation2;
            CBUFFER_END

            // Projection-wide knobs (set globally by the renderer feature).
            float _ProjSurfaceBoost;   // brightness multiplier for the projected pool
            float _ProjNormalCull;     // 0..1 cosine band: surfaces steeper than this fade out
            // TODO layer-ignore: sample _CameraRenderingLayersTexture and mask here once URP
            // "Rendering Layers" is enabled on the renderer.

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

                // Normal culling: geometric normal from depth; fade out steep / back faces.
                float3 nWS = normalize(cross(ddy(surfWS), ddx(surfWS)));
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
                    float2 g = (pOS.xz / axis) / tanField;   // [-1,1] across the field
                    if (useGobo)
                    {
                        float cs = cos(_GoboRotation), sn = sin(_GoboRotation);
                        float2 guv = float2(g.x * cs - g.y * sn, g.x * sn + g.y * cs) * 0.5 + 0.5;
                        gobo *= SAMPLE_TEXTURE2D_ARRAY(_GoboArray, sampler_GoboArray, guv, _GoboSlice).r;
                    }
                    if (useGobo2)
                    {
                        float cs2 = cos(_GoboRotation2), sn2 = sin(_GoboRotation2);
                        float2 guv2 = float2(g.x * cs2 - g.y * sn2, g.x * sn2 + g.y * cs2) * 0.5 + 0.5;
                        gobo *= SAMPLE_TEXTURE2D_ARRAY(_GoboArray2, sampler_GoboArray2, guv2, _GoboSlice2).r;
                    }
                }

                float intensity = _Intensity * _ProjSurfaceBoost * side * axialAtt * normalFade * gobo * nearFade;

            #if defined(_STAGEBEAM_SHADOWS_SCREEN) || defined(_STAGEBEAM_SHADOWS_VOLUME)
                // Shadow the light pool where the occluder blocks the beam from the surface point.
                intensity *= SampleBeamShadow(surfWS, TransformObjectToWorld(float3(0.0, 0.0, 0.0)));
            #endif
                return half4(_BeamColor.rgb * intensity, 1.0);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
