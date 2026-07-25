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
                $"<color=#5aa9e6>[StageBeam]</color> Occlusion build — <b>{draws} voxelize draw calls</b>\n" +
                $"  occluders rasterized : {b.LastVoxelizedOccluders} of {b.OccluderCount} collected\n" +
                $"  treated as dynamic   : {b.LastDynamicOccluders}\n" +
                $"  static volume rebuilt: {(b.LastRebuiltStatic ? "YES (the expensive case)" : "no (cached)")}\n" +
                $"  sphere / box splats  : {b.SphereCount} / {b.BoxCount}\n" +
                $"  occluder hints       : {b.HintCount} found, covering {b.HintCoveredOccluders} renderer(s)\n" +
                "  Each draw call carries its own SetPass, so this is what the Statistics panel " +
                "loses when the Frame Debugger pauses the game.");
        }
    }
}
