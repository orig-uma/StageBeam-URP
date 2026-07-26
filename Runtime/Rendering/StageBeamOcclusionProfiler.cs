using System.Collections.Generic;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Rendering;

namespace Origuma.StageBeam
{
    /// <summary>
    /// GPU timing for the occupancy build, broken down by pass.
    ///
    /// Needed because the build's cost moved. While it rasterized meshes the cost was countable —
    /// draw calls, one SetPass each — and the Frame Debugger would have shown them if the build
    /// lived in the render graph. Now the draws are gone and the work is compute dispatches over
    /// the whole voxel grid, where COUNTING tells you nothing: eight passes over 96x72x96 is five
    /// million invocations whether or not any of them accomplish anything. Only time distinguishes
    /// "this pass matters" from "this pass is a full-volume no-op", which is exactly the question
    /// when deciding what to cut.
    ///
    /// Samples are recorded into the command buffer, so they measure GPU work, not the CPU cost of
    /// recording it. Results lag by a frame or two (the recorder reads back asynchronously) —
    /// irrelevant for a steady-state stage scene.
    ///
    /// Zero cost when disabled: no samplers are begun and no recorders are created.
    /// </summary>
    public sealed class StageBeamOcclusionProfiler
    {
        /// <summary>Per-pass GPU timing. Off by default — enable from the Renderer Feature (or the
        /// cost report menu) while investigating.</summary>
        public static bool Enabled;

        // ProfilingSampler carries the GPU recorder Unity fills in; one per pass name, created on
        // demand and kept for the session (they are cheap and must outlive the frames they time).
        private readonly Dictionary<string, ProfilingSampler> _samplers =
            new Dictionary<string, ProfilingSampler>(16);
        private readonly Dictionary<string, double> _lastMs = new Dictionary<string, double>(16);
        private readonly List<string> _order = new List<string>(16);

        /// <summary>
        /// Brackets a pass. Use with `using` around the dispatch(es) that make up one logical step:
        ///
        ///     using (_profiler.Sample(cmd, "Dilate")) { ...record dispatches... }
        ///
        /// Returns a disposable that closes the sample; a no-op struct when profiling is off.
        /// </summary>
        public Scope Sample(CommandBuffer cmd, string passName)
        {
            if (!Enabled || cmd == null) return default;

            if (!_samplers.TryGetValue(passName, out var sampler))
            {
                sampler = new ProfilingSampler("StageBeamOcc." + passName);
                _samplers[passName] = sampler;
                _order.Add(passName);
            }
            cmd.BeginSample(sampler.name);
            return new Scope(cmd, sampler.name);
        }

        /// <summary>Reads back whatever the recorders have and refreshes the reported times.
        /// Call once per build, before reporting.</summary>
        public void Collect()
        {
            if (!Enabled) return;
            foreach (var kv in _samplers)
            {
                var rec = kv.Value.gpuElapsedTime;
                // A recorder with no completed sample reports 0 — keep the previous reading so a
                // pass that ran a frame ago doesn't flicker to zero in the report.
                if (rec > 0.0) _lastMs[kv.Key] = rec * 1000.0;
            }
        }

        /// <summary>Pass timings in the order they were first recorded, in milliseconds.</summary>
        public IReadOnlyList<string> PassOrder => _order;
        public double GetMs(string passName) => _lastMs.TryGetValue(passName, out var v) ? v : 0.0;
        public double TotalMs
        {
            get
            {
                double t = 0.0;
                foreach (var kv in _lastMs) t += kv.Value;
                return t;
            }
        }

        public readonly struct Scope : System.IDisposable
        {
            private readonly CommandBuffer _cmd;
            private readonly string _name;
            internal Scope(CommandBuffer cmd, string name) { _cmd = cmd; _name = name; }
            public void Dispose() { if (_cmd != null) _cmd.EndSample(_name); }
        }
    }
}
