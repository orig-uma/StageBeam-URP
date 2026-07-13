Shader "Origuma/StageBeamLens"
{
    Properties
    {
        _LensColor    ("Lens Color", Color) = (1,1,1,1)
        _LensIntensity("Intensity", Float) = 1
        _FresnelPower ("Fresnel Power", Float) = 2
    }

    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" "RenderPipeline"="UniversalPipeline" }

        Pass
        {
            Name "StageBeamLens"
            Tags { "LightMode" = "SRPDefaultUnlit" }
            Blend One One
            ZWrite Off
            Cull Back

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _LensColor;
                float  _LensIntensity;
                float  _FresnelPower;
            CBUFFER_END

            struct Attributes
            {
                float3 positionOS : POSITION;
                float3 normalOS   : NORMAL;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float3 positionWS  : TEXCOORD0;
                float3 normalWS    : TEXCOORD1;
            };

            Varyings vert (Attributes v)
            {
                Varyings o;
                o.positionWS = TransformObjectToWorld(v.positionOS);
                o.positionHCS = TransformWorldToHClip(o.positionWS);
                o.normalWS = TransformObjectToWorldNormal(v.normalOS);
                return o;
            }

            half4 frag (Varyings i) : SV_Target
            {
                float3 normalWS = normalize(i.normalWS);
                float3 viewDirWS = normalize(GetCameraPositionWS() - i.positionWS);
                float fresnel = pow(saturate(dot(normalWS, viewDirWS)), max(_FresnelPower, 1e-3));

                float3 col = _LensColor.rgb * _LensIntensity * fresnel;
                return half4(col, 1.0);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
