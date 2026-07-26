using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Profiling;   // CustomSampler (ProfilingSampler.sampler's type)
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
        /// cost report menu) while investigating. Toggling ON arms the recorders of every sampler
        /// created so far, so a session that profiled, stopped and resumed keeps working.</summary>
        public static bool Enabled
        {
            get => s_enabled;
            set
            {
                if (s_enabled == value) return;
                s_enabled = value;
                for (int i = 0; i < s_all.Count; i++) s_all[i].ArmRecorders(value);
            }
        }
        private static bool s_enabled;
        private static readonly List<StageBeamOcclusionProfiler> s_all =
            new List<StageBeamOcclusionProfiler>(4);

        public StageBeamOcclusionProfiler() => s_all.Add(this);

        private void ArmRecorders(bool on)
        {
            foreach (var kv in _samplers) kv.Value.enableRecording = on;
        }

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
                // Without this the sampler still shows up in the Profiler window but its recorder
                // stays disabled, and gpuElapsedTime reads a flat 0.00 — which looks exactly like
                // "this pass is free" rather than "nothing was measured".
                sampler.enableRecording = true;
                _samplers[passName] = sampler;
                _order.Add(passName);
            }
            // Begin with the SAMPLER, not its name: that is what ties the command buffer's GPU
            // timing to this sampler's recorder. A name-based BeginSample opens an unrelated
            // sampler whose timings this object can never read back.
            cmd.BeginSample(sampler.sampler);
            return new Scope(cmd, sampler.sampler);
        }

        /// <summary>Reads back whatever the recorders have and refreshes the reported times.
        /// Call once per build, before reporting.</summary>
        public void Collect()
        {
            if (!Enabled) return;
            foreach (var kv in _samplers)
            {
                // Already MILLISECONDS — ProfilingSampler.gpuElapsedTime does the nanosecond
                // conversion itself. Scaling it again reported microseconds as milliseconds.
                float ms = kv.Value.gpuElapsedTime;
                // A recorder with no completed sample reports 0 — keep the previous reading so a
                // pass that ran a frame ago doesn't flicker to zero in the report.
                if (ms > 0f) _lastMs[kv.Key] = ms;
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
            private readonly CustomSampler _sampler;
            internal Scope(CommandBuffer cmd, CustomSampler sampler)
            {
                _cmd = cmd;
                _sampler = sampler;
            }
            public void Dispose()
            {
                if (_cmd != null && _sampler != null) _cmd.EndSample(_sampler);
            }
        }
    }
}
