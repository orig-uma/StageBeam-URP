using UnityEditor;
using UnityEngine;

namespace Origuma.StageBeam.Editor
{
    /// <summary>
    /// Prints what the occupancy build actually costs.
    ///
    /// The build runs through Graphics.ExecuteCommandBuffer, OUTSIDE the render graph, so the
    /// Frame Debugger cannot show any of it — and because enabling the Frame Debugger pauses the
    /// game, the work stops happening and vanishes from the Statistics panel too. (That is the
    /// gap people notice: "Set Pass Calls drops the moment I open the Frame Debugger.") This
    /// reads the counters the builder records while it writes the command buffer, so the cost is
    /// visible without any of that machinery.
    /// </summary>
    internal static class StageBeamOcclusionCostReport
    {
        [MenuItem("Window/Origuma/Stage Beam/Log Occlusion Build Cost", false, 200)]
        private static void Log()
        {
            var b = StageBeamOcclusionBuilder.ActiveDebug;
            if (b == null)
            {
                Debug.LogWarning("[StageBeam] No occlusion builder is active. Enter Play mode with " +
                                 "volumetric shadows enabled on the Stage Beam Renderer Feature.");
                return;
            }

            int draws = b.LastVoxelizeDrawCalls;
            Debug.Log(
                $"<color=#5aa9e6>[StageBeam]</color> Occlusion build — <b>{draws} voxelize draw calls</b>, " +
                $"<b>{b.LastComputeSkinned} skinned via compute (0 draws)</b>\n" +
                $"  skinned compute      : {b.LastSkinnedDispatches} dispatch(es), " +
                $"{b.LastSkinnedTriangles} triangle(s)\n" +
                $"  occluders rasterized : {b.LastVoxelizedOccluders} of {b.OccluderCount} collected " +
                $"(compute-handled ones no longer draw)\n" +
                $"  treated as dynamic   : {b.LastDynamicOccluders} " +
                $"({b.LastDynamicSkinned} skinned, {b.LastMovedMeshCount} moved mesh(es), " +
                $"largest move Δ{b.LastMaxMovedDelta:g3}" +
                $"{(b.LastMaxMovedRenderer != null ? $" on '{b.LastMaxMovedRenderer.name}'" : "")})\n" +
                $"  static volume rebuilt: {(b.LastRebuiltStatic ? "YES (the expensive case)" : "no (cached)")}\n" +
                $"  sphere / box splats  : {b.SphereCount} / {b.BoxCount}\n" +
                $"  occluder hints       : {b.HintCount} found, covering {b.HintCoveredOccluders} renderer(s)\n" +
                $"  movement history     : {b.MovementHistoryCount} renderer(s) tracked" +
                $"{(b.LastMatrixCacheReset ? " (EMPTY at last build — first build only)" : "")}, " +
                $"{b.TotalBuilds} build(s)\n" +
                $"  builder instance     : #{b.GetHashCode():x8}, built at frame {b.LastBuildFrame} (now {Time.frameCount})\n" +
                "  Reading Δ: ~1e-6..1e-4 = micro-jitter (near-invisible at centimetre voxels); " +
                "large Δ = real motion. Draw calls remaining here are non-skinned dynamic meshes " +
                "plus static rebuilds; each carries its own SetPass, which is what the Statistics " +
                "panel loses when the Frame Debugger pauses the game.\n" +
                GpuTimings(b));
        }

        /// <summary>
        /// GPU time per pass, or an invitation to turn it on. Counting stopped being informative
        /// once the draw calls went away: the remaining work is full-volume compute passes whose
        /// invocation count is identical whether or not they accomplish anything, so only time
        /// separates the passes worth cutting from the ones already free.
        /// </summary>
        private static string GpuTimings(StageBeamOcclusionBuilder b)
        {
            if (!StageBeamOcclusionProfiler.Enabled)
                return "  gpu timing           : off — Window > Origuma > Stage Beam > " +
                       "Toggle Occlusion GPU Profiling";

            var p = b.Profiler;
            var sb = new System.Text.StringBuilder();
            sb.Append($"  gpu timing           : {p.TotalMs:0.000} ms total");
            foreach (var name in p.PassOrder)
                sb.Append($"\n      {name,-16} {p.GetMs(name):0.000} ms");
            return sb.ToString();
        }

        [MenuItem("Window/Origuma/Stage Beam/Toggle Occlusion GPU Profiling", false, 202)]
        private static void ToggleProfiling()
        {
            StageBeamOcclusionProfiler.Enabled = !StageBeamOcclusionProfiler.Enabled;
            Debug.Log($"<color=#5aa9e6>[StageBeam]</color> Occlusion GPU profiling " +
                      $"{(StageBeamOcclusionProfiler.Enabled ? "ON — readings appear after a few builds" : "OFF")}.");
        }

        /// <summary>
        /// Who ARE the occluders? Groups the collected list by root object and counts
        /// skinned/static per root. Exists because the aggregate report can say "490 skinned"
        /// while the Statistics panel says "Visible Skinned Meshes: 90" — the difference is
        /// whatever the camera doesn't see or whatever one wouldn't expect to be skinned at all
        /// (e.g. glTF fixture models whose parts import as SkinnedMeshRenderers), and no amount
        /// of aggregate counting identifies it. Names do.
        /// </summary>
        [MenuItem("Window/Origuma/Stage Beam/Dump Occluder List", false, 201)]
        private static void Dump()
        {
            var b = StageBeamOcclusionBuilder.ActiveDebug;
            if (b == null)
            {
                Debug.LogWarning("[StageBeam] No occlusion builder is active. Enter Play mode with " +
                                 "volumetric shadows enabled on the Stage Beam Renderer Feature.");
                return;
            }

            // root name -> (skinned, mesh, inactive)
            var groups = new System.Collections.Generic.Dictionary<string, int[]>();
            var occluders = b.OccludersForDebug;
            for (int i = 0; i < occluders.Count; i++)
            {
                var r = occluders[i];
                if (r == null) continue;
                string root = r.transform.root.name;
                if (!groups.TryGetValue(root, out var c)) groups[root] = c = new int[3];
                bool active = r.enabled && r.gameObject.activeInHierarchy;
                if (!active) c[2]++;
                else if (r is SkinnedMeshRenderer) c[0]++;
                else c[1]++;
            }

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"<color=#5aa9e6>[StageBeam]</color> Occluder roots ({occluders.Count} renderer(s) collected):");
            foreach (var kv in groups)
                sb.AppendLine($"  {kv.Key,-40} skinned {kv.Value[0],4}   mesh {kv.Value[1],4}   inactive {kv.Value[2],4}");

            // Skinned layout probe: when the compute voxelizer writes nothing (shadows vanish),
            // the numbers below are the usual suspects — buffer stride vs mesh claim, position
            // offset, index format, and WHERE the skinning root actually sits versus the SMR
            // node (the space the skinned buffer is relative to).
            var probed = new System.Collections.Generic.HashSet<Mesh>();
            int probes = 0;
            for (int i = 0; i < occluders.Count && probes < 4; i++)
            {
                if (!(occluders[i] is SkinnedMeshRenderer smr) || smr.sharedMesh == null) continue;
                var mesh = smr.sharedMesh;
                if (!probed.Add(mesh)) continue;
                probes++;
                var vb = smr.GetVertexBuffer();
                var root = smr.rootBone != null ? smr.rootBone : smr.transform;
                sb.AppendLine(
                    $"  skin probe '{mesh.name}': vb {(vb != null ? $"stride {vb.stride}" : "NULL")}, " +
                    $"stream0 stride {mesh.GetVertexBufferStride(0)}, " +
                    $"posOffset {mesh.GetVertexAttributeOffset(UnityEngine.Rendering.VertexAttribute.Position)}, " +
                    $"posStream {mesh.GetVertexAttributeStream(UnityEngine.Rendering.VertexAttribute.Position)}, " +
                    $"idx {mesh.indexFormat}, root '{root.name}' @ {root.position}, smr @ {smr.transform.position}");
                vb?.Dispose();
            }

            sb.Append("  'skinned' rows go through the compute voxelizer (zero draws) when their buffers are accessible.");
            Debug.Log(sb.ToString());
        }
    }
}
