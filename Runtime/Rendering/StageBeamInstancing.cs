using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

namespace Origuma.StageBeam
{
    /// <summary>
    /// One beam's parameters in the exact layout the instanced cone shader reads from its
    /// <c>StructuredBuffer&lt;GpuBeam&gt;</c>. The field order and packing here MUST match the
    /// <c>GpuBeam</c> struct in <c>StageBeamInstanced.hlsl</c> byte-for-byte.
    ///
    /// Scalars are packed into float4 groups so both sides agree on alignment without padding
    /// surprises. Matrices are stored as Unity <see cref="Matrix4x4"/> (column-major); the shader
    /// compensates for the storage convention (see the hlsl include).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct GpuBeam
    {
        public Matrix4x4 ObjectToWorld;   // vertex object->world, and world-space sample points
        public Matrix4x4 WorldToObject;   // camera/world -> object (CL frame is derived from this)

        public Vector4 Color;             // rgb = beam colour, w = unused
        public Vector4 P0;                // startRadius, endRadius, range, edgeSoftness
        public Vector4 P1;                // fieldHalf, beamHalf, density, anisotropy
        public Vector4 P2;                // steps, depthOcclude, axialFalloff, hotspot
        public Vector4 P3;                // rootBoost, rootBoostFrac, rootWhite, intensity
        public Vector4 G0;                // goboSlice, goboRot, goboOffset.x, goboOffset.y
        public Vector4 G1;                // goboSlice2, goboRot2, shadowLightIndex, pad

        /// <summary>Byte stride of this struct — passed to the <see cref="GraphicsBuffer"/>. Must
        /// match the hlsl GpuBeam (2 float4x4 + 7 float4 = 240 B). Computed via Marshal so a field
        /// change here can't silently desync the buffer stride from the data.</summary>
        public static readonly int Stride = Marshal.SizeOf<GpuBeam>();

        /// <summary>
        /// Fills everything except the shadow-light index (P3? no — G1.z), which is resolved later
        /// by the renderer feature from culling results and patched in via <see cref="SetShadowIndex"/>.
        /// </summary>
        public static GpuBeam Pack(in StageBeamInstance d, float steps)
        {
            var m = d.Matrix;
            return new GpuBeam
            {
                ObjectToWorld = m,
                WorldToObject = m.inverse,
                Color = new Vector4(d.Color.r, d.Color.g, d.Color.b, 0f),
                P0 = new Vector4(d.StartRadius, d.EndRadius, d.Range, d.EdgeSoftness),
                P1 = new Vector4(d.FieldHalfAngleRad, d.BeamHalfAngleRad, d.Density, d.Anisotropy),
                P2 = new Vector4(steps, d.DepthOcclude, d.AxialFalloff, d.Hotspot),
                P3 = new Vector4(d.RootBoost, d.RootBoostFrac, d.RootWhite, d.Intensity),
                G0 = new Vector4(d.GoboSlice, d.GoboRotationRad, d.GoboOffset.x, d.GoboOffset.y),
                G1 = new Vector4(d.GoboSlice2, d.GoboRotationRad2, -1f, 0f),
            };
        }

        /// <summary>Patches the resolved additional-light index (LightShadowMap mode).</summary>
        public void SetShadowIndex(float index) => G1.z = index;
    }

    /// <summary>
    /// Collects per-beam <see cref="GpuBeam"/> records and groups them into draw batches keyed by
    /// gobo array pair — beams sharing the same gobo Texture2DArray(s) (or none) instance together
    /// in one <c>DrawMeshInstancedProcedural</c> call. The buffer is filled contiguously per batch
    /// so each draw indexes a slice <c>[Start, Start+Count)</c> of the shared <see cref="Buffer"/>.
    ///
    /// This is the CPU half of the GPU-instanced beam path; the shader half lives in
    /// <c>StageBeamInstanced.hlsl</c>. The legacy per-beam <c>DrawMesh</c> path stays intact and is
    /// used whenever instancing is disabled, so this can be toggled off with zero behaviour change.
    /// </summary>
    public sealed class StageBeamInstanceBatcher
    {
        public struct Batch
        {
            public Texture2DArray GoboA;   // null = no gobo wheel 1 (shader: slice < 0 disables)
            public Texture2DArray GoboB;   // null = no gobo wheel 2
            public int Start;              // first instance index in Buffer
            public int Count;              // instance count in this batch
        }

        // The feature Adds only the beams that survive its per-camera cull, with the shadow-light
        // index already patched into each record. Gobo textures are the batch key only.
        private readonly List<GpuBeam> _records = new List<GpuBeam>(256);
        private readonly List<Texture2DArray> _goboA = new List<Texture2DArray>(256);
        private readonly List<Texture2DArray> _goboB = new List<Texture2DArray>(256);

        private GpuBeam[] _sorted = new GpuBeam[256];
        private bool[] _claimed = new bool[256];
        private readonly List<Batch> _batches = new List<Batch>(16);
        private GraphicsBuffer _buffer;

        public GraphicsBuffer Buffer => _buffer;
        public IReadOnlyList<Batch> Batches => _batches;
        public int Count => _records.Count;

        public void Begin()
        {
            _records.Clear();
            _goboA.Clear();
            _goboB.Clear();
            _batches.Clear();
        }

        /// <summary>Add one visible beam. <paramref name="rec"/> must already carry its resolved
        /// shadow-light index (see <see cref="GpuBeam.SetShadowIndex"/>).</summary>
        public void Add(in GpuBeam rec, Texture2DArray goboA, Texture2DArray goboB)
        {
            _records.Add(rec);
            _goboA.Add(goboA);
            _goboB.Add(goboB);
        }

        /// <summary>
        /// Groups records by gobo pair, writes them contiguously into the shared buffer in batch
        /// order, and emits the batch spans. O(n·b) where b = distinct gobo pairs (tiny).
        /// </summary>
        public void BuildBatches()
        {
            int n = _records.Count;
            if (n == 0) return;
            if (_sorted.Length < n) { _sorted = new GpuBeam[Mathf.NextPowerOfTwo(n)]; _claimed = new bool[_sorted.Length]; }
            System.Array.Clear(_claimed, 0, n);

            int cursor = 0;
            for (int i = 0; i < n; i++)
            {
                if (_claimed[i]) continue;
                var ga = _goboA[i];
                var gb = _goboB[i];
                int start = cursor;
                for (int j = i; j < n; j++)
                {
                    if (_claimed[j] || _goboA[j] != ga || _goboB[j] != gb) continue;
                    _sorted[cursor++] = _records[j];
                    _claimed[j] = true;
                }
                _batches.Add(new Batch { GoboA = ga, GoboB = gb, Start = start, Count = cursor - start });
            }

            EnsureBuffer(n);
            _buffer.SetData(_sorted, 0, 0, n);
        }

        private void EnsureBuffer(int count)
        {
            int cap = Mathf.Max(1, Mathf.NextPowerOfTwo(count));
            if (_buffer != null && _buffer.count >= cap) return;
            _buffer?.Dispose();
            _buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, cap, GpuBeam.Stride);
        }

        public void Dispose()
        {
            _buffer?.Dispose();
            _buffer = null;
        }
    }
}
