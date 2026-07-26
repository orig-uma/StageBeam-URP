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
            // The last two lines discriminate look-alike root causes behind "everything dynamic,
            // static rebuilt every build":
            //   matrix cache reset = yes on every sample -> the movement cache is wiped between
            //     builds (builder recreated / arrays resized): an accounting bug, fixable for free.
            //   matrix cache reset = no                  -> the occluders genuinely move; the
            //     classification is right and the cost is real (levers: instancing, fewer axes).
            //   builder instance changing between samples -> something recreates the builder.
            Debug.Log(
                $"<color=#5aa9e6>[StageBeam]</color> Occlusion build — <b>{draws} voxelize draw calls</b>\n" +
                $"  occluders rasterized : {b.LastVoxelizedOccluders} of {b.OccluderCount} collected\n" +
                $"  treated as dynamic   : {b.LastDynamicOccluders}\n" +
                $"  static volume rebuilt: {(b.LastRebuiltStatic ? "YES (the expensive case)" : "no (cached)")}\n" +
                $"  sphere / box splats  : {b.SphereCount} / {b.BoxCount}\n" +
                $"  occluder hints       : {b.HintCount} found, covering {b.HintCoveredOccluders} renderer(s)\n" +
                $"  matrix cache reset   : {(b.LastMatrixCacheReset ? "YES (movement compare had no history)" : "no")}\n" +
                $"  builder instance     : #{b.GetHashCode():x8}, built at frame {b.LastBuildFrame} (now {Time.frameCount})\n" +
                $"  dynamic breakdown    : {b.LastDynamicOccluders - b.LastMovedMeshCount} skinned, " +
                $"{b.LastMovedMeshCount} moved mesh(es), largest move Δ{b.LastMaxMovedDelta:g3}" +
                $"{(b.LastMaxMovedRenderer != null ? $" on '{b.LastMaxMovedRenderer.name}'" : "")}\n" +
                "  Reading Δ: ~1e-6..1e-4 is micro-jitter (easing rewriting near-identical values — " +
                "an epsilon would return these to the static cache; a voxel is centimetres). " +
                "Large Δ = real motion; the lever is instancing, not cache repair.\n" +
                "  Each draw call carries its own SetPass, so this is what the Statistics panel " +
                "loses when the Frame Debugger pauses the game.");
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
            sb.Append("  'skinned' rows are what the voxelizer redraws every build (skinned = always dynamic).");
            Debug.Log(sb.ToString());
        }
    }
}
