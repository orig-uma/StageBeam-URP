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
                "  Each draw call carries its own SetPass, so this is what the Statistics panel " +
                "loses when the Frame Debugger pauses the game.");
        }
    }
}
