Shader "Hidden/Origuma/StageBeamVoxelize"
{
    // Voxelizes real occluder geometry into the shared occupancy volume: the mesh is
    // rasterized with an orthographic view over the volume box (three passes, one per axis,
    // so faces of any orientation land at least once) and each fragment writes 1 into the
    // voxel its world position falls in (UAV, bound at u1 by the builder).
    //
    // This is what replaced the sphere/capsule approximation: the shadow silhouette is the
    // actual render mesh — skinned poses and cloth deformation included — at voxel resolution,
    // while the shared-volume architecture keeps the cost independent of the light count.
    SubShader
    {
        Pass
        {
            Name "StageBeamVoxelize"
            Cull Off
            ZWrite Off
            ZTest Always
            ColorMask 0

            HLSLPROGRAM
            #pragma target 5.0
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            RWTexture3D<float> _StageBeamOccRW : register(u1);

            float3 _VoxVolMin;      // world-space min corner of the volume box
            float3 _VoxVolInvSize;  // 1 / world size
            float3 _VoxRes;         // voxel resolution per axis

            struct Attributes { float4 positionOS : POSITION; };
            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float3 positionWS  : TEXCOORD0;
            };

            Varyings vert (Attributes v)
            {
                Varyings o;
                o.positionWS  = TransformObjectToWorld(v.positionOS.xyz);
                o.positionHCS = TransformWorldToHClip(o.positionWS);
                return o;
            }

            float4 frag (Varyings i) : SV_Target
            {
                float3 uvw = (i.positionWS - _VoxVolMin) * _VoxVolInvSize;
                if (all(uvw >= 0.0) && all(uvw <= 1.0))
                {
                    uint3 coord = (uint3)min(uvw * _VoxRes, _VoxRes - 1.0);
                    _StageBeamOccRW[coord] = 1.0;
                }
                return 0;
            }
            ENDHLSL
        }
    }
    Fallback Off
}
