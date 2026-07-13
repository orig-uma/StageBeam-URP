Shader "Origuma/StageBeamUpsample"
{
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" }

        Pass
        {
            Name "BeamUpsample"
            Blend One One
            ZWrite Off
            ZTest Always
            Cull Off

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

            // xy = source texel size (1/halfW, 1/halfH), zw = source size (halfW, halfH)
            float4 _BeamUpsampleTexelSize;

            // Soft Additive ceiling: the composite maps the accumulated beam total through
            // K·(1−exp(−sum/K)) — identity slope at 0 (one beam is unchanged), asymptotic to K
            // for stacks, so overlapping beams approach a chosen thinness instead of piling to
            // white. 0 = off (plain additive composite).
            float _BeamSoftCeiling;

            // Depth-aware (joint bilateral) upsample. A plain tent filter smooths the half-res
            // blocks but ignores depth, so at object silhouettes the beam's depth-clipped edge
            // stays a jagged half-res staircase and bleeds onto foreground geometry. Weighting
            // each low-res texel by how well its depth matches the full-res pixel snaps the
            // composite to the correct side of the edge; a nearest-depth fallback covers pixels
            // where no neighbour matches at all.
            half4 frag (Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float2 uv  = input.texcoord;
                float2 d   = _BeamUpsampleTexelSize.xy;
                float2 res = _BeamUpsampleTexelSize.zw;

                // Reference: scene depth at THIS full-res pixel.
                float z0 = LinearEyeDepth(SampleSceneDepth(uv), _ZBufferParams);

                // 3x3 low-res texels around the pixel, sampled at their CENTERS so each colour
                // tap pairs with one well-defined depth (a bilinear tap would already mix texels
                // from both sides of the edge and defeat the depth test).
                float2 p  = uv * res;                    // pixel position in low-res texel units
                float2 tc = floor(p) + 0.5;              // nearest low-res texel center

                half4 sum = 0;
                float wSum = 0.0;
                half4 nearestC = 0;
                float nearestDiff = 1e30;

                UNITY_UNROLL
                for (int j = -1; j <= 1; j++)
                {
                    UNITY_UNROLL
                    for (int i = -1; i <= 1; i++)
                    {
                        float2 c   = tc + float2(i, j);
                        float2 suv = c * d;
                        half4 col = SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_LinearClamp, suv, 0);
                        float  zi  = LinearEyeDepth(SampleSceneDepth(suv), _ZBufferParams);

                        // Spatial: gaussian-ish tent on the distance to the texel center.
                        float2 o = c - p;
                        float wS = exp(-dot(o, o));
                        // Depth: relative difference, sharp enough to reject the far side of a
                        // silhouette but tolerant of gentle slopes (floors seen at an angle).
                        float rel = abs(zi - z0) / max(z0, 1e-3);
                        float wD = exp(-rel * 32.0);

                        float w = wS * wD;
                        sum  += col * w;
                        wSum += w;

                        float diff = abs(zi - z0);
                        if (diff < nearestDiff) { nearestDiff = diff; nearestC = col; }
                    }
                }

                // No neighbour matched this pixel's depth (thin feature the half-res grid
                // stepped over) → take the single closest-depth texel instead of a bleed.
                half4 outc = wSum > 1e-3 ? sum / wSum : nearestC;
                if (_BeamSoftCeiling > 0.0)
                {
                    // Modulation-preserving ceiling. rgb = full beam total (haze × shadow),
                    // alpha = the PURE beam baseline (neither haze nor shadow). Saturating the
                    // modulated total directly flattens the turbulence AND the shadow carving
                    // (the curve's slope → 0 in a deep stack), so instead: saturate the
                    // baseline, then re-apply the modulation ratio m = total/baseline — the
                    // haze drift and the shadow-carved gaps both survive any overlap depth.
                    // The wide lower clamp lets deep shadows read as deep (down to ~3%); the
                    // upper clamp guards the ratio when the baseline underflows.
                    float lum = dot(outc.rgb, float3(0.2126, 0.7152, 0.0722));
                    float m = clamp(lum / max(outc.a, 1e-5), 0.03, 4.0);
                    float3 baseCol = outc.rgb / m;
                    outc.rgb = _BeamSoftCeiling * (1.0 - exp(-baseCol / _BeamSoftCeiling)) * m;
                }
                return outc;
            }
            ENDHLSL
        }

        Pass
        {
            Name "BeamDenoise"
            ZWrite Off
            ZTest Always
            Cull Off

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment fragDenoise
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

            float4 _BeamUpsampleTexelSize;   // xy = 1/size, zw = size (of the beam buffer)

            // Depth-aware 5×5 smoothing of the accumulated beam buffer, run at the buffer's own
            // resolution BEFORE the upsample composite. Fog is low-frequency, so blurring it is
            // visually near-lossless — but it averages away the static jitter/undersampling
            // pattern that otherwise reads as a screen-fixed "smudge" (worst at low step counts
            // and quarter resolution). Depth weighting keeps beams from bleeding across object
            // silhouettes; alpha (the unmodulated baseline) is filtered identically so the
            // composite's ratio math stays consistent. 5×5 (vs 3×3) reaches the larger-scale
            // low-frequency blotches a 3×3 leaves behind.
            half4 fragDenoise (Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float2 uv = input.texcoord;
                float2 d  = _BeamUpsampleTexelSize.xy;

                float z0 = LinearEyeDepth(SampleSceneDepth(uv), _ZBufferParams);

                half4 sum = 0;
                float wSum = 0.0;

                UNITY_UNROLL
                for (int j = -2; j <= 2; j++)
                {
                    UNITY_UNROLL
                    for (int i = -2; i <= 2; i++)
                    {
                        float2 suv = uv + float2(i, j) * d;
                        half4 c  = SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_LinearClamp, suv, 0);
                        float zi = LinearEyeDepth(SampleSceneDepth(suv), _ZBufferParams);

                        float wS  = exp(-(i * i + j * j) * 0.22);                 // wider gaussian
                        float rel = abs(zi - z0) / max(z0, 1e-3);
                        float wD  = exp(-rel * 16.0);                             // silhouette guard
                        float w = wS * wD;
                        sum  += c * w;
                        wSum += w;
                    }
                }
                return wSum > 1e-4 ? sum / wSum : SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_LinearClamp, uv, 0);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
