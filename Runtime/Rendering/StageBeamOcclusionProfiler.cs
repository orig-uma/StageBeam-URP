using System.Collections.Generic;
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
        // A ring of recent readings per pass, not a single value. Three consecutive "optimizations"
        // were each judged a regression off one frame apiece (0.188 / 0.239 / 0.247 ms) before
        // anyone asked what a SECOND reading of the unchanged build would have said. One sample
        // cannot separate a 30% regression from 30% frame-to-frame spread, and every conclusion
        // drawn from one is a guess wearing three decimal places.
        private const int Window = 64;
        private readonly Dictionary<string, double[]> _ring = new Dictionary<string, double[]>(16);
        private readonly Dictionary<string, int> _ringCount = new Dictionary<string, int>(16);
        private readonly Dictionary<string, int> _ringHead = new Dictionary<string, int>(16);
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
            // ProfilingScope takes the SAMPLER OBJECT, which is what ties the command buffer's GPU
            // timing to that sampler's recorder — the thing Collect() reads. (Opening by name
            // instead goes through a separately-owned sampler this object could never read back,
            // and ProfilingSampler.sampler is not public to bridge the two by hand.)
            return new Scope(cmd, sampler);
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
                // A recorder with no completed sample reports 0 — skip it rather than poisoning
                // the window with a zero a pass never actually achieved.
                if (ms <= 0f) continue;
                if (!_ring.TryGetValue(kv.Key, out var ring))
                {
                    _ring[kv.Key] = ring = new double[Window];
                    _ringCount[kv.Key] = 0;
                    _ringHead[kv.Key] = 0;
                }
                int head = _ringHead[kv.Key];
                ring[head] = ms;
                _ringHead[kv.Key] = (head + 1) % Window;
                _ringCount[kv.Key] = Mathf.Min(Window, _ringCount[kv.Key] + 1);
            }
        }

        /// <summary>Pass timings in the order they were first recorded, in milliseconds.</summary>
        public IReadOnlyList<string> PassOrder => _order;

        /// <summary>
        /// Distribution of a pass's recent GPU times in milliseconds. MEDIAN is the figure to
        /// compare across builds and MIN is the cleanest read of the pass's own cost (the one least
        /// contaminated by whatever else the GPU was doing); the SPREAD between them says whether a
        /// difference between two builds means anything at all.
        /// </summary>
        public bool TryGetStats(string passName, out double min, out double median, out double max,
                                out int samples)
        {
            min = median = max = 0.0; samples = 0;
            if (!_ring.TryGetValue(passName, out var ring)) return false;
            samples = _ringCount[passName];
            if (samples == 0) return false;

            var sorted = new double[samples];
            System.Array.Copy(ring, sorted, samples);
            System.Array.Sort(sorted);
            min = sorted[0];
            max = sorted[samples - 1];
            median = (samples & 1) == 1
                ? sorted[samples / 2]
                : (sorted[samples / 2 - 1] + sorted[samples / 2]) * 0.5;
            return true;
        }

        private double MedianMs(string passName)
            => TryGetStats(passName, out _, out var med, out _, out _) ? med : 0.0;

        /// <summary>Sum of the per-pass medians.</summary>
        public double TotalMs
        {
            get
            {
                double t = 0.0;
                for (int i = 0; i < _order.Count; i++) t += MedianMs(_order[i]);
                return t;
            }
        }

        /// <summary>Drop every reading. Call when switching between two builds being compared, so
        /// the window holds one configuration rather than a blend of both.</summary>
        public void ResetWindow()
        {
            _ring.Clear();
            _ringCount.Clear();
            _ringHead.Clear();
        }

        /// <summary>Wraps a ProfilingScope so the disabled case can be a genuine no-op — a
        /// default-constructed instance opens nothing and closes nothing.</summary>
        public struct Scope : System.IDisposable
        {
            private ProfilingScope _inner;
            private bool _active;
            internal Scope(CommandBuffer cmd, ProfilingSampler sampler)
            {
                _inner = new ProfilingScope(cmd, sampler);
                _active = true;
            }
            public void Dispose()
            {
                if (!_active) return;
                _active = false;
                _inner.Dispose();
            }
        }
    }
}
