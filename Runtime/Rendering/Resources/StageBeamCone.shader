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
        // Henyey-Greenstein scattering anisotropy. 0 = isotropic (current/legacy look).
        // 0.5-0.7 = forward scattering: beams pointing at the camera flare up, the way real
        // haze reads under stage lighting. Negative values back-scatter.
        _Anisotropy  ("Scattering Anisotropy g", Range(-0.9, 0.9)) = 0.0
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
        _GoboOffset  ("Gobo 1 UV Offset (animation wheel)", Vector) = (0,0,0,0)
        [NoScaleOffset] _GoboArray ("Gobo Array", 2DArray) = "white" {}
        _GoboSlice2   ("Gobo Slice 2 (-1=off)", Float) = -1
        _GoboRotation2("Gobo Rotation 2 (rad)", Float) = 0
        [NoScaleOffset] _GoboArray2 ("Gobo Array 2", 2DArray) = "white" {}
        // (Anti-banding dither lives on the Renderer Feature, not here: this shader writes into an
        // ARGBHalf buffer with mantissa to spare. The banding is born at the COMPOSITE, where the
        // result is blended into the camera's low-mantissa target — see StageBeamUpsample.)
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
            #pragma multi_compile _ _STAGEBEAM_SHADOWS_SCREEN _STAGEBEAM_SHADOWS_VOLUME _STAGEBEAM_SHADOWS_LIGHT
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "StageBeamShadow.hlsl"
            // Shared globals (gobo/haze/master/jitter/RT), IGN/BeamWidthAt/BeamEntryDistance and
            // the whole raymarch (StageBeamRaymarch + BeamParams) live here — see the core header.
            #include "StageBeamConeCore.hlsl"

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
                float  _Anisotropy;
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
                float4 _GoboOffset;
                float  _GoboSlice2;
                float  _GoboRotation2;
            CBUFFER_END

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
                // Clamp the axial position so cap vertices behind the apex (t<0 — the lens-cap
                // centre sits at y=-0.2) don't poke a thin hull out the BACK of the lens. Those
                // fragments still integrate the forward beam along the view ray, so they'd show
                // as a faint cone pointing OPPOSITE the beam. Forward margin (t>1, the reach cap)
                // is kept — it correctly encloses the far end. Mirrors the saturate(t) on r.
                float3 posOS = float3(v.positionOS.x * r, -max(t, 0.0) * _Range, v.positionOS.z * r);
                o.positionWS = TransformObjectToWorld(posOS);
                o.positionHCS = TransformWorldToHClip(o.positionWS);
                return o;
            }

            // Soft Additive (driver Blend mode): the blend state is screen blending
            // (dst + src·(1−dst)), which saturates STACKED beams toward 1 instead of summing
            // to white — but it is only stable for source values below 1, so this flag
            // Reinhard-compresses the beam's own output (c / (1 + c)) before it hits the
            // blender. Raw HDR output would flip the blend factor negative and overlapping
            // fragments would cancel each other out.
            float  _BeamSoft;

            half4 frag (Varyings i) : SV_Target
            {
                // Object space has the beam axis along -Y; remap to a frame whose axis is +Z so the
                // cone test is axis-aligned (object scale 1, so CL distances == world distances).
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
                float3 lightWS = TransformObjectToWorld(float3(0.0, 0.0, 0.0)); // beam apex = light

                BeamParams p;
                p.color           = _BeamColor.rgb;
                p.intensity       = _Intensity;
                p.startRadius     = _StartRadius;
                p.range           = _Range;
                p.edgeSoftness    = _EdgeSoftness;
                p.fieldHalf       = _FieldHalf;
                p.beamHalf        = _BeamHalf;
                p.density         = _Density;
                p.anisotropy      = _Anisotropy;
                p.steps           = _Steps;
                p.depthOcclude    = _DepthOcclude;
                p.surfaceFadeDist = _SurfaceFadeDist;
                p.axialFalloff    = _AxialFalloff;
                p.hotspot         = _Hotspot;
                p.rootBoost       = _RootBoost;
                p.rootBoostFrac   = _RootBoostFrac;
                p.rootWhite       = _RootWhite;
                p.goboSlice       = _GoboSlice;
                p.goboRot         = _GoboRotation;
                p.goboOffset      = _GoboOffset.xy;
                p.goboSlice2      = _GoboSlice2;
                p.goboRot2        = _GoboRotation2;
                p.beamSoft        = _BeamSoft;

                return StageBeamRaymarch(p, camWS, dirWS, camCL, rayCL, exitCL,
                                         lightWS, i.positionHCS.xy);
            }
            ENDHLSL
        }

        // GPU-instanced variant: one DrawMeshInstancedProcedural per gobo batch, per-beam data
        // read from StructuredBuffer<GpuBeam> _StageBeams by SV_InstanceID. Shares this material's
        // blend state and the _SurfaceFadeDist / _BeamSoft tuning (identical UnityPerMaterial
        // CBUFFER, required for SRP-batcher compatibility). Volume/Screen/no-shadow only — the
        // LightShadowMap backend needs a per-beam light index that can't be a global here.
        Pass
        {
            Name "StageBeamInstanced"
            Tags { "LightMode" = "SRPDefaultUnlit" }
            Blend [_BeamSrcBlend] [_BeamDstBlend]
            ZWrite Off
            ZTest Always
            Cull Off

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
            #include "StageBeamConeCore.hlsl"

            // Same CBUFFER layout as the uniform pass (SRP-batcher requires identical
            // UnityPerMaterial across a shader's passes). Only _SurfaceFadeDist is read here;
            // the per-beam fields are ignored — those come from _StageBeams.
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
                float  _Anisotropy;
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
                float4 _GoboOffset;
                float  _GoboSlice2;
                float  _GoboRotation2;
            CBUFFER_END

            float _BeamSoft;

            struct Attributes
            {
                float3 positionOS : POSITION;
                uint   instanceID : SV_InstanceID;
            };

            // The GpuBeam is read ONCE in the vertex shader; the fragment gets everything through
            // flat interpolators (per-beam constants) plus the interpolated object-space hull point
            // — NO per-fragment StructuredBuffer load, which is what made the naive instanced path
            // lose to the cbuffer uniform path in this fill-rate-bound shader. camOS is constant per
            // beam; exitOS == the object-space hull vertex (posOS), linear so exact under interp.
            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float3 positionWS  : TEXCOORD0;   // interpolated world hull point (exit)
                float3 exitOS      : TEXCOORD1;   // interpolated object-space hull point
                nointerpolation float3 camOS   : TEXCOORD2;
                nointerpolation float3 lightWS  : TEXCOORD3;
                nointerpolation float4 c0 : TEXCOORD4;  // color.rgb, intensity
                nointerpolation float4 c1 : TEXCOORD5;  // startRadius, range, edgeSoftness, fieldHalf
                nointerpolation float4 c2 : TEXCOORD6;  // beamHalf, density, anisotropy, steps
                nointerpolation float4 c3 : TEXCOORD7;  // depthOcclude, axialFalloff, hotspot, rootBoost
                nointerpolation float4 c4 : TEXCOORD8;  // rootBoostFrac, rootWhite, goboSlice, goboRot
                nointerpolation float4 c5 : TEXCOORD9;  // goboOffset.xy, goboSlice2, goboRot2
            };

            Varyings vert (Attributes v)
            {
                Varyings o;
                GpuBeam b = StageBeamAt(v.instanceID);
                float startRadius = b.p0.x;
                float range       = b.p0.z;
                float fieldHalf   = b.p1.x;

                float t = v.positionOS.y;
                // Same field-angle hull sizing as the uniform pass.
                float rEndBeam = range * max(tan(fieldHalf), 1e-3) + startRadius;
                float r = lerp(startRadius, rEndBeam, saturate(t)) * 1.2;
                // See the uniform pass: clamp behind-apex cap vertices so no phantom hull sticks
                // out the back of the lens (would show a faint cone opposite the beam).
                float3 posOS = float3(v.positionOS.x * r, -max(t, 0.0) * range, v.positionOS.z * r);

                o.positionWS  = StageBeamObjectToWorld(b, posOS);
                o.positionHCS = TransformWorldToHClip(o.positionWS);
                o.exitOS      = posOS;                                   // worldToObject(posWS) == posOS
                o.camOS       = StageBeamWorldToObject(b, GetCameraPositionWS());
                o.lightWS     = StageBeamObjectToWorld(b, float3(0.0, 0.0, 0.0));

                o.c0 = float4(b.color.rgb, b.p3.w);
                o.c1 = float4(b.p0.x, b.p0.z, b.p0.w, b.p1.x);
                o.c2 = float4(b.p1.y, b.p1.z, b.p1.w, b.p2.x);
                o.c3 = float4(b.p2.y, b.p2.z, b.p2.w, b.p3.x);
                o.c4 = float4(b.p3.y, b.p3.z, b.g0.x, b.g0.y);
                o.c5 = float4(b.g0.z, b.g0.w, b.g1.x, b.g1.y);
                return o;
            }

            half4 frag (Varyings i) : SV_Target
            {
                float3 camWS  = GetCameraPositionWS();
                float3 exitWS = i.positionWS;
                float3 rayWS  = exitWS - camWS;
                float  segLen = length(rayWS);
                if (segLen < 1e-4) return 0;
                float3 dirWS = rayWS / segLen;

                float3 camCL  = float3(i.camOS.x,  i.camOS.z,  -i.camOS.y);
                float3 exitCL = float3(i.exitOS.x, i.exitOS.z, -i.exitOS.y);
                float3 rayCL  = normalize(exitCL - camCL);

                BeamParams p;
                p.color           = i.c0.rgb;
                p.intensity       = i.c0.w;
                p.startRadius     = i.c1.x;
                p.range           = i.c1.y;
                p.edgeSoftness    = i.c1.z;
                p.fieldHalf       = i.c1.w;
                p.beamHalf        = i.c2.x;
                p.density         = i.c2.y;
                p.anisotropy      = i.c2.z;
                p.steps           = i.c2.w;
                p.depthOcclude    = i.c3.x;
                p.surfaceFadeDist = _SurfaceFadeDist;
                p.axialFalloff    = i.c3.y;
                p.hotspot         = i.c3.z;
                p.rootBoost       = i.c3.w;
                p.rootBoostFrac   = i.c4.x;
                p.rootWhite       = i.c4.y;
                p.goboSlice       = i.c4.z;
                p.goboRot         = i.c4.w;
                p.goboOffset      = i.c5.xy;
                p.goboSlice2      = i.c5.z;
                p.goboRot2        = i.c5.w;
                p.beamSoft        = _BeamSoft;

                return StageBeamRaymarch(p, camWS, dirWS, camCL, rayCL, exitCL,
                                         i.lightWS, i.positionHCS.xy);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
