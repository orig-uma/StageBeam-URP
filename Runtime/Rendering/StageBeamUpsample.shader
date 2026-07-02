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

            // xy = source texel size (1/halfW, 1/halfH)
            float4 _BeamUpsampleTexelSize;

            half4 frag (Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float2 uv = input.texcoord;
                float2 d = _BeamUpsampleTexelSize.xy;

                // 3x3 tent filter to smooth the low-resolution block boundaries.
                half4 c  = SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_LinearClamp, uv, 0) * 0.25;
                c += SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_LinearClamp, uv + float2( d.x, 0), 0) * 0.125;
                c += SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_LinearClamp, uv + float2(-d.x, 0), 0) * 0.125;
                c += SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_LinearClamp, uv + float2(0,  d.y), 0) * 0.125;
                c += SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_LinearClamp, uv + float2(0, -d.y), 0) * 0.125;
                c += SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_LinearClamp, uv + float2( d.x,  d.y), 0) * 0.0625;
                c += SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_LinearClamp, uv + float2(-d.x,  d.y), 0) * 0.0625;
                c += SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_LinearClamp, uv + float2( d.x, -d.y), 0) * 0.0625;
                c += SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_LinearClamp, uv + float2(-d.x, -d.y), 0) * 0.0625;
                return c;
            }
            ENDHLSL
        }
    }
    Fallback Off
}
