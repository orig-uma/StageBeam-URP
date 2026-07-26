using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Origuma.StageBeam
{
    /// <summary>
    /// Builds the single shared world-space occupancy volume every StageBeam cone self-shadows
    /// against: occluders are discovered by layer mask, written into the volume once per frame,
    /// and bound as global shader state. Cost is independent of how many beams are drawn.
    ///
    /// Two ways in, chosen by <see cref="MeshVoxelize"/>: rasterizing the occluders' real meshes
    /// (the default — exact silhouettes, but a draw call per renderer per axis), or splatting
    /// approximating shapes with a compute pass (oriented boxes from renderer bounds, per-bone
    /// spheres for skinned meshes, or an authored <see cref="StageBeamOccluderHint"/> — no draw
    /// calls at all). Hints apply in both modes.
    ///
    /// Boxes come from <see cref="Renderer.localBounds"/> + the transform — no per-object setup
    /// needed — so walls, risers, panels and cases occlude with their actual silhouette. When a
    /// collider sits on the renderer's GameObject it is used as a better shape hint (sphere /
    /// capsule / box), since bounds alone can't tell a sphere mesh from a cube.
    ///
    /// Plain class (not a component) so it can be owned by either:
    /// - <see cref="StageBeamRendererFeature"/> — the automatic, zero-scene-setup path used
    ///   whenever Shadows = Volume and no override component exists, or
    /// - <see cref="StageBeamOcclusionVolume"/> — an optional scene component for manual control
    ///   of box placement, resolution and discovery.
    /// </summary>
    public sealed class StageBeamOcclusionBuilder
    {
        // --- occluder discovery ---------------------------------------------------------------
        public LayerMask OccluderMask = ~0;
        // Occluder DISCOVERY interval (FindObjectsByType allocates a full scene renderer array, so
        // this is deliberately infrequent). Enabling an already-discovered occluder is instant (the
        // gather gates on activeInHierarchy), so this only bounds how fast a NEWLY SPAWNED occluder
        // starts casting — 1s is plenty for a stage scene whose cast rarely changes at runtime.
        public float RescanInterval = 1.0f;

        /// <summary>Voxelize the occluders' REAL meshes (3-axis rasterization into the volume)
        /// instead of approximating them with spheres/boxes. Silhouettes become the actual
        /// render geometry — skinned poses and cloth deformation included — while the shared
        /// volume keeps the cost independent of the light count. Off = shape splatting
        /// (bone spheres / bounds boxes).
        ///
        /// A <see cref="StageBeamOccluderHint"/> still wins in EITHER mode: a hinted subtree is
        /// splatted as its authored shape and taken out of the raster set. That is what makes the
        /// hint a cost lever (a character's renderers stop costing a draw call each) as well as
        /// what makes its Ignore option mean Ignore here.</summary>
        public bool MeshVoxelize = true;

        /// <summary>
        /// Voxelize SkinnedMeshRenderers by compute instead of rasterization. Same geometry,
        /// same volume, ZERO draw calls: the skinned (deformed) vertices already live on the GPU
        /// when GPU skinning is on, so a dispatch per renderer reads them directly — where the
        /// raster path pays one DrawRenderer (with its own SetPass) per renderer PER AXIS. A cast
        /// of performers goes from hundreds of draws per build to none.
        /// Falls back to rasterization per renderer whenever the skinned vertex buffer is
        /// unavailable (GPU skinning disabled, or the first frame after raw access is enabled).
        /// </summary>
        public bool ComputeSkinnedVoxelize = true;

        public bool ArticulateSkinnedMeshes = true;
        public int BonesPerOccluder = 16;
        public float BoneRadiusScale = 0.12f;
        public float MaxOccluderSize = 8f;
        public int MaxOccluders = 128;

        // --- volume box -----------------------------------------------------------------------
        public bool AutoFit = true;
        // Small padding on purpose: every metre of padding is voxels NOT spent on the subject.
        // Shadows march the ray inside the box, so the box only needs to wrap the occluders.
        public float AutoFitPadding = 0.75f;
        /// <summary>Manual box centre (used when <see cref="AutoFit"/> is off or nothing found).</summary>
        public Vector3 ManualCenter;
        public Vector3 ManualSize = new Vector3(20f, 12f, 20f);
        // At the default fit around one performer this is a ~3-4 cm voxel — fine enough that
        // limbs read as limbs instead of aliased blocks.
        public Vector3Int Resolution = new Vector3Int(96, 72, 96);
        public float EdgeSoftness = 0.5f;
        // Mesh-voxelization orthographic passes. 3 (X/Y/Z) is fully conservative; 2 drops the
        // top-down pass (cheapest CPU cut — recorded DrawRenderer count scales with this) and is
        // usually enough for upright performers since the two horizontal passes + the GrowMax fill
        // already close the body. Lower this if the occlusion build's CPU cost matters more than
        // catching perfectly horizontal surfaces (out-held arms' top faces).
        public int VoxelizeAxisCount = 3;
        /// <summary>
        /// Static/dynamic occluder split (opt-in perf). Voxelizes NON-moving rigid occluders into a
        /// cached volume that's rebuilt only when that set changes, and re-voxelizes only the DYNAMIC
        /// ones (SkinnedMeshRenderers — mesh deforms in place — and anything whose transform moved)
        /// every build, combining them with a max pass. Kills the per-build DrawRenderer spike from
        /// static set geometry. Fully automatic (no layers/flags): classification is by renderer type
        /// + transform movement. MeshVoxelize only.
        /// </summary>
        public bool StaticDynamicSplit;
        /// <summary>Temporal smoothing of the occupancy (0 = off/instant, 1 = heavy). Absorbs the
        /// frame-to-frame voxel chatter a moving occluder causes; higher = smoother but the
        /// shadow lags moving occluders more.</summary>
        public float TemporalSmoothing = 0.6f;

        // --- shadow look (published as globals) -----------------------------------------------
        public float Strength = 0.85f;
        public float ShadowDensity = 8f;
        public int ShadowSteps = 16;
        public float MaxShadowDistance = 25f;
        public float Bias = 0.05f;
        public float LightBias = 0.5f;
        public float ScreenSpaceThickness = 1.5f;

        /// <summary>Compute shader; left null it auto-loads the bundled StageBeamOcclusion.compute.</summary>
        public ComputeShader Occlusion;

        // Read-only state for gizmos / debug logging.
        public int SphereCount => _sphereCount;
        public int BoxCount => _boxCount;
        public int OccluderCount => _occluders.Count;
        public Vector3 BoxCenter => _wc;
        public Vector3 BoxSize => _ws;

        /// <summary>
        /// Draw calls recorded by the LAST GPU build — the mesh-voxelize cost, which is what
        /// actually scales with the scene.
        ///
        /// Exists because the build runs through Graphics.ExecuteCommandBuffer, OUTSIDE the render
        /// graph, so the Frame Debugger cannot see any of it. Without a counter there is no way to
        /// tell whether a change made this cheaper or more expensive. Counted while RECORDING, so
        /// it costs an increment per call and nothing else.
        /// </summary>
        public int LastVoxelizeDrawCalls => _statDraws;
        /// <summary>Occluders that actually rasterized last build (active and within MaxOccluderSize).</summary>
        public int LastVoxelizedOccluders => _statOccluders;
        /// <summary>Of those, how many were treated as dynamic (skinned, or moved since last build).</summary>
        public int LastDynamicOccluders => _statDynamic;
        /// <summary>True when the cached static volume was re-voxelized last build (the expensive case).</summary>
        public bool LastRebuiltStatic => _statRebuiltStatic;
        /// <summary>Distinct StageBeamOccluderHint components found above the collected occluders.
        /// Zero means no hint is in play — so no subtree is being represented by a cheap shape.</summary>
        public int HintCount => _statHints;
        /// <summary>Occluders taken out of the raster set because a hint represents them.</summary>
        public int HintCoveredOccluders => _statHintCovered;

        /// <summary>
        /// True when the movement history was EMPTY at the last build — expected exactly once,
        /// on this builder's first build. History is keyed by renderer identity and survives
        /// rescans, so TRUE recurring across samples means the builder itself is being recreated.
        /// </summary>
        public bool LastMatrixCacheReset => _statMatrixReset;
        /// <summary>Time.frameCount of the last recorded build (staleness check for the report).</summary>
        public int LastBuildFrame => _statBuildFrame;

        /// <summary>
        /// Of the dynamic occluders, how many are NON-skinned meshes that changed their matrix
        /// since the previous build — and how big the largest change was. This separates the two
        /// situations "everything is dynamic" collapses together: matrices moving by visible
        /// amounts (fixtures actually panning — the cost is real; the lever is instancing) versus
        /// micro-jitter far below a voxel (easing/animation rewriting near-identical values every
        /// frame — an epsilon in the comparison would return those to the static cache with no
        /// visible change, since a voxel is centimetres).
        /// </summary>
        public int LastMovedMeshCount => _statMovedMeshes;
        /// <summary>Largest matrix-element change among those movers (world units).</summary>
        public float LastMaxMovedDelta => _statMaxMovedDelta;
        /// <summary>The renderer with that largest change (name resolved by the report, not here —
        /// Renderer.name allocates).</summary>
        public Renderer LastMaxMovedRenderer => _statMaxMovedRenderer;

        /// <summary>Dynamic occluders that are skinned — MEASURED in the loop, not derived by
        /// subtraction (a derived "skinned" figure once mislabelled 400 fixture meshes).</summary>
        public int LastDynamicSkinned => _statDynSkinned;
        /// <summary>Classification runs since this builder was created.</summary>
        public int TotalBuilds => _statBuilds;
        /// <summary>Renderers currently tracked by the identity-keyed movement history.</summary>
        public int MovementHistoryCount => _moveHistory.Count;
        /// <summary>Skinned renderers voxelized by the compute path last build — each of these
        /// would otherwise have cost one draw call (its own SetPass) per axis.</summary>
        public int LastComputeSkinned => _statComputeSkinned;
        /// <summary>Compute dispatches and triangles the skinned voxelizer issued last build.
        /// Time divided by dispatches tells you whether the pass is overhead-bound (a roughly
        /// fixed microsecond cost per call, unmoved by triangle count) or work-bound.</summary>
        public int LastSkinnedDispatches => _statSkinnedDispatches;
        public int LastSkinnedTriangles => _statSkinnedTris;

        /// <summary>Per-pass GPU timing (opt-in via StageBeamOcclusionProfiler.Enabled). With the
        /// draw calls gone, time is the only way to tell which of the full-volume compute passes
        /// actually costs anything.</summary>
        public StageBeamOcclusionProfiler Profiler => _profiler;
        private readonly StageBeamOcclusionProfiler _profiler = new StageBeamOcclusionProfiler();

        private int _statDraws, _statOccluders, _statDynamic, _statDynSkinned;
        private int _statHints, _statHintCovered;
        private int _statBuildFrame = -1;
        private int _statMovedMeshes;
        private int _statBuilds;
        private float _statMaxMovedDelta;
        private Renderer _statMaxMovedRenderer;
        private bool _statMatrixReset;
        private bool _statRebuiltStatic;
        private bool _warnedSphereOverflow;

        private static float MaxAbsElementDelta(in Matrix4x4 a, in Matrix4x4 b)
        {
            float max = 0f;
            for (int e = 0; e < 16; e++)
            {
                float d = Mathf.Abs(a[e] - b[e]);
                if (d > max) max = d;
            }
            return max;
        }
        public Vector4 GetSphere(int i) => _spheres != null && i < _spheres.Length ? _spheres[i] : default;
        public void GetBox(int i, out Vector3 center, out Vector3 halfExtents, out Quaternion rotation)
        {
            var c = _boxes[i * 3];
            var e = _boxes[i * 3 + 1];
            var q = _boxes[i * 3 + 2];
            center = new Vector3(c.x, c.y, c.z);
            halfExtents = new Vector3(e.x, e.y, e.z);
            rotation = new Quaternion(q.x, q.y, q.z, q.w);
        }

        private RenderTexture _volume;
        private ComputeBuffer _sphereBuffer;
        private ComputeBuffer _boxBuffer;
        private Material _voxelizeMat;
        private int[] _occluderSubMeshes;   // parallel to _occluders, cached at rescan
        private readonly List<Renderer> _occluders = new List<Renderer>(128);

        /// <summary>The collected occluder list, for editor diagnostics (the cost report's dump).
        /// Read-only view; do not mutate through casts.</summary>
        public IReadOnlyList<Renderer> OccludersForDebug => _occluders;
        // Per-occluder explicit shape override (StageBeamOccluderHint on a parent), resolved at
        // rescan time — parallel to _occluders. One hint covers many renderers; the emit pass
        // dedupes so a hinted character casts exactly one capsule/box.
        private readonly List<StageBeamOccluderHint> _occluderHints = new List<StageBeamOccluderHint>(128);
        private readonly HashSet<StageBeamOccluderHint> _emittedHints = new HashSet<StageBeamOccluderHint>();
        // Reused for Renderer.GetSharedMaterials so the rescan submesh-count read allocates nothing
        // (the `sharedMaterials` PROPERTY returns a fresh array on every access).
        private readonly List<Material> _tempMaterials = new List<Material>(8);
        // Per-occluder "rasterize this in the voxelize passes" flag, computed ONCE per build
        // (active + not-too-big) so the per-axis draw loops don't recompute r.bounds ×3.
        private bool[] _voxelizeFlags;
        private Vector4[] _spheres;          // xyz = centre, w = radius
        private Vector4[] _boxes;            // 3 per box: [centre, minHalfExt], [halfExt, 0], [rot quat]
        private int _sphereCount, _boxCount;
        private Bounds _fitBounds;
        private bool _hasFit;
        private int _clearKernel = -1, _splatKernel = -1, _growKernel = -1, _dilateKernel = -1, _temporalKernel = -1;
        private int _combineKernel = -1;
        private int _triKernel = -1;            // VoxelizeTriangles (compute path for skinned)

        // GraphicsBuffer wrappers from GetVertexBuffer/GetIndexBuffer. Each call returns a NEW
        // wrapper that must be disposed — but not while the command buffer that references it
        // may still be executing on the GPU. Wrappers therefore retire on a two-build delay:
        // used this build → survive the next → disposed at the start of the one after.
        private List<GraphicsBuffer> _buffersThisBuild = new List<GraphicsBuffer>();
        private List<GraphicsBuffer> _buffersInFlight  = new List<GraphicsBuffer>();
        // Occluders handled by the compute path this build (parallel to _occluders); the raster
        // axes skip these.
        private bool[] _computeHandled;
        private int _statComputeSkinned, _statSkinnedDispatches, _statSkinnedTris;
        private bool _warnedNoSkinBuffer;
        private RenderTexture _volumeTemp;      // ping-pong for the dilate passes
        private RenderTexture _volumeHistory;   // temporal EMA of the occupancy (bound for shadowing)
        private RenderTexture _volumeStatic;    // cached static occupancy (StaticDynamicSplit)
        // Static/dynamic classification state.
        private bool[] _dynamicFlags;           // this build, parallel to _occluders: skinned OR moved

        // Movement history, keyed by RENDERER IDENTITY — deliberately not by list index. The
        // occluder list is rebuilt by every rescan and FindObjectsByType's order is not stable,
        // so index-keyed history compared renderer A against renderer B's old matrix after each
        // rescan; the old code "solved" that by wiping the whole cache instead (Rescan used to
        // null it), which reclassified every occluder as moved and re-voxelized the entire static
        // set — the exact cost the static/dynamic split exists to avoid.
        private struct MoveEntry { public Matrix4x4 Matrix; public bool WasDynamic; }
        private readonly Dictionary<Renderer, MoveEntry> _moveHistory =
            new Dictionary<Renderer, MoveEntry>(512);
        private readonly HashSet<Renderer> _rescanSeen = new HashSet<Renderer>();
        private readonly List<Renderer> _rescanDead = new List<Renderer>();
        private bool _occluderSetShrunk;        // a tracked occluder vanished → static voxels linger
        private bool _staticDirty;              // static set changed → rebuild the static volume
        private bool _staticVolumeValid;
        private bool _historyValid;             // false right after (re)allocation → skip the blend once
        private ComputeShader _kernelSource;
        private float _nextRescan;
        private Vector3 _wc, _ws = new Vector3(20f, 12f, 20f);
        private Vector3 _prevWc, _prevWs;   // last frame's box, to detect re-anchoring for temporal reuse

        private static readonly int IdOcc        = Shader.PropertyToID("_StageBeamOcc");
        private static readonly int IdOccMin     = Shader.PropertyToID("_StageBeamOccMin");
        private static readonly int IdOccInvSize = Shader.PropertyToID("_StageBeamOccInvSize");
        private static readonly int IdStrength   = Shader.PropertyToID("_BeamShadowStrength");
        private static readonly int IdSteps      = Shader.PropertyToID("_BeamShadowSteps");
        private static readonly int IdMaxDist    = Shader.PropertyToID("_BeamShadowMaxDist");
        private static readonly int IdBias       = Shader.PropertyToID("_BeamShadowBias");
        private static readonly int IdLightBias  = Shader.PropertyToID("_BeamShadowLightBias");
        private static readonly int IdThickness  = Shader.PropertyToID("_BeamShadowThickness");
        private static readonly int IdVoxel      = Shader.PropertyToID("_StageBeamOccVoxel");
        private static readonly int IdDensity    = Shader.PropertyToID("_BeamShadowDensity");
        // compute-side
        private static readonly int IdCOcc      = Shader.PropertyToID("_Occ");
        private static readonly int IdCCombineOther = Shader.PropertyToID("_CombineOther");
        private static readonly int IdCSpheres  = Shader.PropertyToID("_Spheres");
        private static readonly int IdCCount    = Shader.PropertyToID("_SphereCount");
        private static readonly int IdCBoxes    = Shader.PropertyToID("_Boxes");
        private static readonly int IdCBoxCount = Shader.PropertyToID("_BoxCount");
        private static readonly int IdCVolMin   = Shader.PropertyToID("_VolMin");
        private static readonly int IdCVolSize  = Shader.PropertyToID("_VolSize");
        private static readonly int IdCRes      = Shader.PropertyToID("_Res");
        private static readonly int IdCSoft     = Shader.PropertyToID("_EdgeSoftness");
        // triangle-voxelize kernel
        private static readonly int IdTriVerts        = Shader.PropertyToID("_TriVerts");
        private static readonly int IdTriIndices      = Shader.PropertyToID("_TriIndices");
        private static readonly int IdTriIndexStart   = Shader.PropertyToID("_TriIndexStart");
        private static readonly int IdTriIndexCount   = Shader.PropertyToID("_TriIndexCount");
        private static readonly int IdTriBaseVertex   = Shader.PropertyToID("_TriBaseVertex");
        private static readonly int IdTriIndex16      = Shader.PropertyToID("_TriIndex16");
        private static readonly int IdTriVertStride   = Shader.PropertyToID("_TriVertStride");
        private static readonly int IdTriPosOffset    = Shader.PropertyToID("_TriPosOffset");
        private static readonly int IdTriLocalToWorld = Shader.PropertyToID("_TriLocalToWorld");

        /// <summary>The most recently built builder (auto or override) — lets
        /// <see cref="StageBeamOcclusionDebugView"/> visualize the shapes regardless of which
        /// path owns the build this frame.</summary>
        public static StageBeamOcclusionBuilder ActiveDebug { get; private set; }

        /// <summary>
        /// CPU half of the per-frame pipeline: (re)discover occluders, gather shapes, fit the
        /// box, size the GPU resources. Returns false when the compute shader is missing.
        /// Followed by <see cref="RecordGpuBuild"/> (both wrapped by <see cref="BuildAndBind"/>).
        /// </summary>
        public bool PrepareFrame()
        {
            ActiveDebug = this;
            if (!EnsureCompute()) return false;
            EnsureVolume();

            var want = Mathf.Max(1, MaxOccluders);
            if (_spheres == null || _spheres.Length != want) _spheres = new Vector4[want];
            if (_boxes == null || _boxes.Length != want * 3) _boxes = new Vector4[want * 3];

            // realtimeSinceStartup so re-scanning also works in edit mode (Time.time stalls there).
            float now = Time.realtimeSinceStartup;
            if (now >= _nextRescan)
            {
                Rescan();
                _nextRescan = now + Mathf.Max(0.1f, RescanInterval);
            }

            GatherOccluders();
            ComputeBox();
            return true;
        }

        /// <summary>
        /// GPU half: clear + shape splat, mesh voxelization, dilation and the global binds —
        /// all recorded into <paramref name="cmd"/>. <see cref="BuildAndBind"/> executes it via
        /// <c>Graphics.ExecuteCommandBuffer</c> during the renderer feature's AddRenderPasses,
        /// which is BEFORE the RenderGraph beam passes run — so the volume is built in time for
        /// every camera. (It is NOT a RenderGraph pass: mixing legacy temp-RT allocation with
        /// graph textures aliased the voxelize target onto the beam buffer and corrupted it.)
        /// </summary>
        public void RecordGpuBuild(CommandBuffer cmd)
        {
            if (_volume == null || _spheres == null) return;
            _statDraws = 0;
            _statRebuiltStatic = false;
            _statBuildFrame = Time.frameCount;

            // Retire the buffer wrappers from two builds ago (safely past GPU execution) and
            // rotate last build's into the in-flight slot.
            for (int i = 0; i < _buffersInFlight.Count; i++) _buffersInFlight[i]?.Dispose();
            _buffersInFlight.Clear();
            (_buffersInFlight, _buffersThisBuild) = (_buffersThisBuild, _buffersInFlight);
            _profiler.Collect();   // read back what finished on the GPU since the last build
            RecordUploadAndDispatch(cmd);
            if (MeshVoxelize)
            {
                RecordVoxelizeMeshes(cmd);
                RecordDilate(cmd);   // thicken the 1-voxel raster shells so the march can't miss them
            }
            var bound = RecordTemporal(cmd);   // EMA smoothing → returns the texture to bind
            RecordBindGlobals(cmd, bound);
        }

        /// <summary>Legacy immediate build (prepare + execute on the spot). Prefer the
        /// Prepare/Record pair driven by the renderer feature.</summary>
        public void BuildAndBind()
        {
            if (!PrepareFrame()) return;
            var cmd = CommandBufferPool.Get("StageBeam Occlusion Build");
            RecordGpuBuild(cmd);
            Graphics.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }

        /// <summary>Free GPU resources and (by default) neutralise the shadow globals. Pass
        /// <paramref name="neutraliseGlobals"/> = false when another builder is taking over the
        /// binding this same frame, so its already-published strength isn't stomped.</summary>
        public void Release(bool neutraliseGlobals = true)
        {
            if (_volume != null) { _volume.Release(); CoreUtils.Destroy(_volume); _volume = null; }
            if (_volumeTemp != null) { _volumeTemp.Release(); CoreUtils.Destroy(_volumeTemp); _volumeTemp = null; }
            if (_volumeHistory != null) { _volumeHistory.Release(); CoreUtils.Destroy(_volumeHistory); _volumeHistory = null; }
            if (_volumeStatic != null) { _volumeStatic.Release(); CoreUtils.Destroy(_volumeStatic); _volumeStatic = null; }
            for (int i = 0; i < _buffersInFlight.Count; i++) _buffersInFlight[i]?.Dispose();
            _buffersInFlight.Clear();
            for (int i = 0; i < _buffersThisBuild.Count; i++) _buffersThisBuild[i]?.Dispose();
            _buffersThisBuild.Clear();
            _staticVolumeValid = false;
            _historyValid = false;
            _sphereBuffer?.Dispose(); _sphereBuffer = null;
            _boxBuffer?.Dispose(); _boxBuffer = null;
            if (_voxelizeMat != null) { CoreUtils.Destroy(_voxelizeMat); _voxelizeMat = null; }
            _occluders.Clear();
            _sphereCount = 0;
            _boxCount = 0;
            _nextRescan = 0f;
            if (ActiveDebug == this) ActiveDebug = null;
            if (neutraliseGlobals) Shader.SetGlobalFloat(IdStrength, 0f);
        }

        private bool EnsureCompute()
        {
            if (Occlusion == null) Occlusion = Resources.Load<ComputeShader>("StageBeamOcclusion");
            if (Occlusion == null) return false;
            if (_kernelSource != Occlusion)
            {
                _kernelSource = Occlusion;
                _clearKernel = Occlusion.FindKernel("Clear");
                _splatKernel = Occlusion.FindKernel("Splat");
                _growKernel = Occlusion.HasKernel("GrowMax") ? Occlusion.FindKernel("GrowMax") : -1;
                _dilateKernel = Occlusion.HasKernel("Dilate") ? Occlusion.FindKernel("Dilate") : -1;
                _temporalKernel = Occlusion.HasKernel("TemporalBlend") ? Occlusion.FindKernel("TemporalBlend") : -1;
                _combineKernel = Occlusion.HasKernel("CombineMax") ? Occlusion.FindKernel("CombineMax") : -1;
                _triKernel = Occlusion.HasKernel("VoxelizeTriangles") ? Occlusion.FindKernel("VoxelizeTriangles") : -1;
            }
            return _clearKernel >= 0;
        }

        // --- occluder discovery ---------------------------------------------------------------

        private void Rescan()
        {
            _occluders.Clear();
            _occluderHints.Clear();
            // INCLUDE inactive: occluders that start disabled (e.g. a prop enabled mid-show) are
            // discovered and their submesh counts cached now, so the moment their GameObject is
            // enabled they occlude on the very next frame — no wait for the next rescan. The
            // per-frame gather/voxelize passes gate on activeInHierarchy, so an inactive occluder
            // contributes nothing to the volume until it's actually enabled.
            var all = Object.FindObjectsByType<Renderer>(FindObjectsInactive.Include);
            for (var i = 0; i < all.Length; i++)
            {
                var r = all[i];
                if (r == null || !r.enabled) continue;
                if ((OccluderMask.value & (1 << r.gameObject.layer)) == 0) continue;
                // Skip the beams themselves and anything without real geometry.
                if (r is ParticleSystemRenderer) continue;

                // Explicit shape override anywhere above the renderer (one per character root).
                var hint = r.GetComponentInParent<StageBeamOccluderHint>();
                if (hint != null && hint.Shape == StageBeamOccluderHint.OccluderShape.Ignore)
                    continue;

                _occluders.Add(r);
                _occluderHints.Add(hint);
            }

            // Submesh counts for the mesh-voxelize draw loop, cached here so the per-frame
            // path never touches Renderer.sharedMaterials (it allocates).
            if (_occluderSubMeshes == null || _occluderSubMeshes.Length < _occluders.Count)
                _occluderSubMeshes = new int[_occluders.Count];
            for (var i = 0; i < _occluders.Count; i++)
            {
                _occluders[i].GetSharedMaterials(_tempMaterials);   // non-allocating
                _occluderSubMeshes[i] = Mathf.Max(1, _tempMaterials.Count);
            }

            // Prune history for renderers that left the scene. A REMOVED occluder must also force
            // a static rebuild — its voxels linger in the cached static volume otherwise. That is
            // the only rescan outcome that needs one: unchanged membership keeps every cache warm
            // (movement history is keyed by renderer, so the list being rebuilt in a different
            // order no longer matters), and ADDED occluders force the rebuild by themselves when
            // they classify dynamic on first sight and flip static a build later.
            _rescanSeen.Clear();
            for (var i = 0; i < _occluders.Count; i++) _rescanSeen.Add(_occluders[i]);
            _rescanDead.Clear();
            foreach (var kv in _moveHistory)
                if (!_rescanSeen.Contains(kv.Key)) _rescanDead.Add(kv.Key);
            for (var i = 0; i < _rescanDead.Count; i++) _moveHistory.Remove(_rescanDead[i]);
            if (_rescanDead.Count > 0) _occluderSetShrunk = true;
        }

        // Occluders represented by a StageBeamOccluderHint this build, so the mesh voxelizer skips
        // them. Reset every gather because a hint can be added, removed or disabled at any time.
        private bool[] _hintCovered;

        private void MarkHintCovered(int index)
        {
            if (_hintCovered == null || _hintCovered.Length < _occluders.Count)
                _hintCovered = new bool[Mathf.Max(_occluders.Count, 8)];
            if (!_hintCovered[index]) _statHintCovered++;
            _hintCovered[index] = true;
        }

        private void GatherOccluders()
        {
            _sphereCount = 0;
            _boxCount = 0;
            _hasFit = false;
            _emittedHints.Clear();
            if (_hintCovered != null) System.Array.Clear(_hintCovered, 0, _hintCovered.Length);
            _statHintCovered = 0;
            _statHints = 0;
            for (var h = 0; h < _occluderHints.Count; h++)
                if (_occluderHints[h] != null) _statHints++;

            for (var i = 0; i < _occluders.Count; i++)
            {
                var r = _occluders[i];
                // Gate on activeInHierarchy: inactive occluders stay cached (see Rescan) but
                // contribute nothing to the volume until their GameObject is enabled.
                if (r == null || !r.enabled || !r.gameObject.activeInHierarchy) continue;

                var hint = i < _occluderHints.Count ? _occluderHints[i] : null;

                // Mesh voxelization: real geometry goes straight into the volume (see
                // VoxelizeMeshes), so no approximation shapes are emitted here — this pass
                // only accumulates the AutoFit bounds. Skinned fit still comes from bone
                // positions so inflated culling bounds don't balloon the box.
                if (MeshVoxelize)
                {
                    // A hint still WINS in mesh mode. Rasterizing a hinted subtree would both
                    // duplicate the shape it already declares and pay a draw call per renderer —
                    // the expensive half of a character (body, hair, clothes, cloth proxies) for a
                    // silhouette the voxel grid quantises away anyway. So the shape is splatted by
                    // the compute pass (no draw calls) and the renderers are taken out of the
                    // raster set. This is also what makes Ignore mean Ignore here: without it a
                    // subtree marked "casts no volumetric shadow" still voxelized.
                    if (hint != null)
                    {
                        MarkHintCovered(i);
                        if (_emittedHints.Add(hint)) EmitHint(hint);
                        continue;
                    }

                    if (r is SkinnedMeshRenderer skinned &&
                        TryComputeSkeletonBounds(skinned, out var skelFit))
                    {
                        skelFit.Expand(0.3f);            // limbs reach past the bone points
                        Grow(skelFit);
                    }
                    else
                    {
                        var wb = r.bounds;
                        if (MaxOccluderSize <= 0f || wb.extents.magnitude * 2f <= MaxOccluderSize)
                            Grow(wb);
                    }
                    continue;
                }

                // Explicit hint: the whole subtree occludes as ONE authored shape — stable and
                // visible in the Scene view, immune to cloth-sim bones / inflated bounds.
                if (hint != null)
                {
                    if (_emittedHints.Add(hint)) EmitHint(hint);
                    continue;
                }

                // Dancers: scatter spheres over the skeleton so the shadow shows limbs, not a
                // box. Size, the too-big filter and the AutoFit box all come from the BONE
                // positions, never renderer bounds — skinned bounds are routinely inflated on
                // purpose (culling workarounds) and would fatten the spheres, balloon the
                // AutoFit box, or get the character rejected as "floor-sized" entirely.
                if (ArticulateSkinnedMeshes && r is SkinnedMeshRenderer smr &&
                    TryComputeSkeletonBounds(smr, out var skel))
                {
                    float len = Mathf.Max(skel.size.x, Mathf.Max(skel.size.y, skel.size.z));
                    float radius = Mathf.Max(len, 0.5f) * BoneRadiusScale;
                    skel.Expand(radius * 2f);           // include the spheres' own girth
                    if (MaxOccluderSize > 0f && skel.extents.magnitude * 2f > MaxOccluderSize) continue;

                    if (_hasFit) _fitBounds.Encapsulate(skel);
                    else { _fitBounds = skel; _hasFit = true; }

                    EmitBoneSpheres(smr, radius);
                    continue;
                }

                var b = r.bounds;                       // world-space, updates for skinned meshes
                if (MaxOccluderSize > 0f && b.extents.magnitude * 2f > MaxOccluderSize) continue;

                if (_hasFit) _fitBounds.Encapsulate(b);
                else { _fitBounds = b; _hasFit = true; }

                // Colliders, when present, are shape HINTS: local bounds can't tell a sphere
                // mesh from a cube (both have box bounds), but a SphereCollider can — without
                // this a sphere prop occludes as a box.
                if (!TryEmitColliderShape(r)) EmitBox(r);
            }
        }

        // Grown to whatever the hint needs: one hint can represent a whole cast, and a fixed
        // 24-segment buffer would quietly keep only the first character's capsules.
        private static StageBeamOccluderHint.Segment[] HintSegments =
            new StageBeamOccluderHint.Segment[StageBeamOccluderHint.MaxSegments];

        // One authored shape for a whole hinted subtree. Contributes to AutoFit like any other
        // occluder so the volume still wraps it.
        private void EmitHint(StageBeamOccluderHint hint)
        {
            if (hint.Shape == StageBeamOccluderHint.OccluderShape.Box)
            {
                EmitOrientedBox(hint.transform, hint.Center, hint.BoxSize * 0.5f);
                var t = hint.transform;
                var half = Vector3.Scale(hint.BoxSize * 0.5f, Abs(t.lossyScale));
                Grow(new Bounds(t.TransformPoint(hint.Center), half * 2f));
                return;
            }

            if (hint.Shape == StageBeamOccluderHint.OccluderShape.HumanoidCapsules)
            {
                int need = hint.HumanoidSegmentCapacity;
                if (HintSegments.Length < need)
                    HintSegments = new StageBeamOccluderHint.Segment[need];
                int n = hint.FillHumanoidSegments(HintSegments);
                if (n > 0)
                {
                    for (int i = 0; i < n; i++)
                        EmitSegment(in HintSegments[i]);
                    return;
                }
                // No humanoid avatar below the hint: fall through to the single capsule.
            }

            hint.GetWorldCapsule(out var p0, out var p1, out var radius);
            EmitSphere(p0, radius);
            EmitSphere((p0 + p1) * 0.5f, radius);
            EmitSphere(p1, radius);

            var b = new Bounds(p0, Vector3.one * (radius * 2f));
            b.Encapsulate(new Bounds(p1, Vector3.one * (radius * 2f)));
            Grow(b);
        }

        // Sphere-swept segment: spheres spaced ~one radius apart so the chain reads as a
        // solid capsule in the voxel volume (no gaps for light to leak through).
        private void EmitSegment(in StageBeamOccluderHint.Segment s)
        {
            float len = Vector3.Distance(s.A, s.B);
            int count = Mathf.Clamp(Mathf.CeilToInt(len / Mathf.Max(s.Radius, 0.01f)) + 1, 1, 8);
            for (int k = 0; k < count; k++)
            {
                float t = count <= 1 ? 0.5f : (float)k / (count - 1);
                EmitSphere(Vector3.Lerp(s.A, s.B, t), s.Radius);
            }

            var b = new Bounds(s.A, Vector3.one * (s.Radius * 2f));
            b.Encapsulate(new Bounds(s.B, Vector3.one * (s.Radius * 2f)));
            Grow(b);
        }

        private void Grow(Bounds b)
        {
            if (_hasFit) _fitBounds.Encapsulate(b);
            else { _fitBounds = b; _hasFit = true; }
        }

        private static Vector3 Abs(Vector3 v) =>
            new Vector3(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));

        // World AABB of the skeleton's bone positions (sampled with a stride so huge rigs stay
        // cheap). False when the rig has no usable bones — caller falls back to renderer bounds.
        private static bool TryComputeSkeletonBounds(SkinnedMeshRenderer smr, out Bounds bounds)
        {
            bounds = default;
            var bones = smr.bones;
            if (bones == null || bones.Length == 0) return false;

            int stride = Mathf.Max(1, bones.Length / 32);
            bool has = false;
            for (int i = 0; i < bones.Length; i += stride)
            {
                var t = bones[i];
                if (t == null) continue;
                var p = t.position;
                if (!has) { bounds = new Bounds(p, Vector3.zero); has = true; }
                else bounds.Encapsulate(p);
            }
            return has;
        }

        // Shape hint from a collider on the renderer's own GameObject. Colliders are authored to
        // hug the object, so when one exists it beats the box-bounds guess: a sphere occludes as
        // a sphere, a capsule as a chain of spheres, a box with the collider's tighter extents.
        // Enabled state is ignored — the collider is used purely as geometry data.
        private bool TryEmitColliderShape(Renderer r)
        {
            if (r.TryGetComponent(out SphereCollider sphere))
            {
                var t = sphere.transform;
                var ls = t.lossyScale;
                float maxScale = Mathf.Max(Mathf.Abs(ls.x), Mathf.Max(Mathf.Abs(ls.y), Mathf.Abs(ls.z)));
                EmitSphere(t.TransformPoint(sphere.center), sphere.radius * maxScale);
                return true;
            }

            if (r.TryGetComponent(out CapsuleCollider capsule))
            {
                var t = capsule.transform;
                var ls = new Vector3(Mathf.Abs(t.lossyScale.x), Mathf.Abs(t.lossyScale.y),
                                     Mathf.Abs(t.lossyScale.z));
                // Local axis of the capsule (0 = X, 1 = Y, 2 = Z) and the scales that apply to
                // its radius (the two perpendicular axes, per Unity physics) and height.
                Vector3 axis = capsule.direction == 0 ? Vector3.right
                             : capsule.direction == 2 ? Vector3.forward : Vector3.up;
                float axisScale = capsule.direction == 0 ? ls.x : capsule.direction == 2 ? ls.z : ls.y;
                float radScale  = capsule.direction == 0 ? Mathf.Max(ls.y, ls.z)
                                : capsule.direction == 2 ? Mathf.Max(ls.x, ls.y)
                                : Mathf.Max(ls.x, ls.z);
                float radius = capsule.radius * radScale;
                float half   = Mathf.Max(capsule.height * 0.5f * axisScale - radius, 0f);

                var center = t.TransformPoint(capsule.center);
                var dir = (t.rotation * axis).normalized;
                if (half <= 1e-4f)
                {
                    EmitSphere(center, radius);           // degenerate capsule = sphere
                }
                else
                {
                    EmitSphere(center - dir * half, radius);
                    EmitSphere(center, radius);
                    EmitSphere(center + dir * half, radius);
                }
                return true;
            }

            if (r.TryGetComponent(out BoxCollider box))
            {
                EmitOrientedBox(box.transform, box.center, box.size * 0.5f);
                return true;
            }

            return false;
        }

        private void EmitSphere(Vector3 center, float radius)
        {
            if (_sphereCount >= _spheres.Length)
            {
                // Silent truncation reads as "that character casts no shadow" with nothing to go
                // on. An articulated humanoid costs ~17 spheres, so a cast of eight overruns the
                // default 128 — say so once instead of letting shadows quietly disappear.
                if (!_warnedSphereOverflow)
                {
                    _warnedSphereOverflow = true;
                    Debug.LogWarning($"[StageBeam] Occluder shape budget full ({_spheres.Length}) — " +
                        "further shapes are dropped and will cast no volumetric shadow. Raise " +
                        "Max Occluders (an articulated humanoid costs ~17 shapes).");
                }
                return;
            }
            _spheres[_sphereCount++] = new Vector4(center.x, center.y, center.z,
                                                   Mathf.Max(radius, 1e-3f));
        }

        // Oriented box straight from the renderer's local bounds: exact for anything box-shaped
        // (walls, risers, panels, cases) and a tight hull for everything else.
        private void EmitBox(Renderer r)
        {
            var lb = r.localBounds;
            EmitOrientedBox(r.transform, lb.center, lb.extents);
        }

        // Non-uniform scale is folded into the half extents so the rotation stays orthonormal.
        private void EmitOrientedBox(Transform t, Vector3 localCenter, Vector3 localExtents)
        {
            if (_boxCount >= MaxOccluders) return;

            var center = t.TransformPoint(localCenter);
            var ls = t.lossyScale;
            var he = Vector3.Max(
                Vector3.Scale(localExtents, new Vector3(Mathf.Abs(ls.x), Mathf.Abs(ls.y), Mathf.Abs(ls.z))),
                Vector3.one * 0.02f);   // paper-thin quads still get a sliver of occupancy
            var q = t.rotation;

            var i = _boxCount * 3;
            // w of the first row = reference length for the soft shell (smallest half extent,
            // floored so thin panels keep a usable falloff band).
            _boxes[i]     = new Vector4(center.x, center.y, center.z,
                                        Mathf.Max(Mathf.Min(he.x, Mathf.Min(he.y, he.z)), 0.1f));
            _boxes[i + 1] = new Vector4(he.x, he.y, he.z, 0f);
            _boxes[i + 2] = new Vector4(q.x, q.y, q.z, q.w);
            _boxCount++;
        }

        // Sample bones evenly across the skeleton (no naming conventions needed) and drop a
        // sphere at each PLUS one at the midpoint toward its parent — the midpoints CONNECT the
        // chain, so the silhouette reads as joined limbs instead of separated blobs with false
        // light slivers between arm and torso. Budget stays BonesPerOccluder spheres total.
        // Radius is precomputed by the caller from the SKELETON's real span (not renderer bounds).
        private void EmitBoneSpheres(SkinnedMeshRenderer smr, float radius)
        {
            var bones = smr.bones;
            int want  = Mathf.Min(Mathf.Max(BonesPerOccluder / 2, 1), bones.Length);
            int denom = Mathf.Max(want - 1, 1);

            for (int k = 0; k < want && _sphereCount < _spheres.Length; k++)
            {
                int idx = (want <= 1) ? 0
                        : Mathf.RoundToInt((float)k / denom * (bones.Length - 1));
                var t = bones[idx];
                if (t == null) continue;
                var p = t.position;
                _spheres[_sphereCount++] = new Vector4(p.x, p.y, p.z, radius);

                var parent = t.parent;
                if (parent == null || _sphereCount >= _spheres.Length) continue;
                var pp = parent.position;
                // Connector midpoint — skipped for abnormally long links (retargeting helpers,
                // detached props) that would draw occupancy across empty space.
                if ((pp - p).sqrMagnitude < 0.6f * 0.6f)
                {
                    var mid = (p + pp) * 0.5f;
                    _spheres[_sphereCount++] = new Vector4(mid.x, mid.y, mid.z, radius);
                }
            }
        }

        // Decide the world-space box used this frame: wrap the occluders (AutoFit) or use the
        // manual centre/size.
        private void ComputeBox()
        {
            if (AutoFit && _hasFit)
            {
                var size = _fitBounds.size + Vector3.one * (2f * Mathf.Max(0f, AutoFitPadding));
                size = Vector3.Max(size, Vector3.one * 0.5f);

                // WORLD-ANCHOR the voxel grid, or animated occluders shimmer: a box that
                // follows the subject re-quantizes the whole volume every frame (each voxel
                // covers a slightly different piece of the world). Quantize the SIZE to coarse
                // steps (so the voxel size is piecewise constant) and snap the min corner to
                // whole voxels — the grid then stays fixed in world space while the subject
                // moves through it, and only re-anchors on the rare size step.
                const float SizeStep = 1f;
                size = new Vector3(
                    Mathf.Ceil(size.x / SizeStep) * SizeStep,
                    Mathf.Ceil(size.y / SizeStep) * SizeStep,
                    Mathf.Ceil(size.z / SizeStep) * SizeStep);

                var voxel = new Vector3(
                    size.x / Mathf.Max(1, Resolution.x),
                    size.y / Mathf.Max(1, Resolution.y),
                    size.z / Mathf.Max(1, Resolution.z));
                var rawMin = _fitBounds.center - size * 0.5f;
                var snappedMin = new Vector3(
                    Mathf.Floor(rawMin.x / voxel.x) * voxel.x,
                    Mathf.Floor(rawMin.y / voxel.y) * voxel.y,
                    Mathf.Floor(rawMin.z / voxel.z) * voxel.z);

                _ws = size;
                _wc = snappedMin + size * 0.5f;
            }
            else
            {
                _wc = ManualCenter;
                _ws = Vector3.Max(ManualSize, Vector3.one * 0.5f);
            }
        }

        // --- gpu build ------------------------------------------------------------------------

        private void RecordUploadAndDispatch(CommandBuffer cmd)
        {
            var res = Resolution;
            int gx = Mathf.CeilToInt(res.x / 4f);
            int gy = Mathf.CeilToInt(res.y / 4f);
            int gz = Mathf.CeilToInt(res.z / 4f);

            // The volume's world placement, bound UNCONDITIONALLY. It describes the grid itself,
            // not the shape splat, and the triangle voxelizer reads it to map world positions into
            // voxels. It used to live inside the Splat block, which was harmless only because
            // Splat always ran; once Splat became conditional (skipped when there are no shapes —
            // the normal case) the values went stale, the voxelizer kept writing against an older
            // box, and every shadow sat at a fixed offset from its caster.
            cmd.SetComputeIntParams(Occlusion, IdCRes, res.x, res.y, res.z);
            cmd.SetComputeVectorParam(Occlusion, IdCVolMin, _wc - _ws * 0.5f);
            cmd.SetComputeVectorParam(Occlusion, IdCVolSize, _ws);

            // Splat computes each voxel's coverage from scratch and writes it unconditionally, so
            // it IS the clear when it runs — a preceding Clear only pays a second full-volume pass
            // to write zeroes that Splat immediately overwrites. And with no shapes at all (the
            // normal case once mesh voxelization handles everything) Splat itself is 663k threads
            // whose entire job is to store 0, which Clear already does more cheaply. So: exactly
            // one of the two runs, never both.
            bool hasShapes = _sphereCount > 0 || _boxCount > 0;

            if (!hasShapes)
            {
                using (_profiler.Sample(cmd, "Clear"))
                {
                    cmd.SetComputeTextureParam(Occlusion, _clearKernel, IdCOcc, _volume);
                    cmd.DispatchCompute(Occlusion, _clearKernel, gx, gy, gz);
                }
                return;
            }

            if (_sphereBuffer == null || _sphereBuffer.count != _spheres.Length)
            {
                _sphereBuffer?.Dispose();
                _sphereBuffer = new ComputeBuffer(_spheres.Length, sizeof(float) * 4);
            }
            cmd.SetBufferData(_sphereBuffer, _spheres);

            if (_boxBuffer == null || _boxBuffer.count != _boxes.Length)
            {
                _boxBuffer?.Dispose();
                _boxBuffer = new ComputeBuffer(_boxes.Length, sizeof(float) * 4);
            }
            cmd.SetBufferData(_boxBuffer, _boxes);

            using (_profiler.Sample(cmd, "Splat"))
            {
                // _VolMin / _VolSize are bound above, for every path.
                cmd.SetComputeTextureParam(Occlusion, _splatKernel, IdCOcc, _volume);
                cmd.SetComputeBufferParam(Occlusion, _splatKernel, IdCSpheres, _sphereBuffer);
                cmd.SetComputeIntParam(Occlusion, IdCCount, _sphereCount);
                cmd.SetComputeBufferParam(Occlusion, _splatKernel, IdCBoxes, _boxBuffer);
                cmd.SetComputeIntParam(Occlusion, IdCBoxCount, _boxCount);
                cmd.SetComputeFloatParam(Occlusion, IdCSoft, EdgeSoftness);
                cmd.DispatchCompute(Occlusion, _splatKernel, gx, gy, gz);
            }
        }

        private static readonly int IdVoxTargetRT   = Shader.PropertyToID("_StageBeamVoxelizeRT");
        private static readonly int IdVoxVolMin     = Shader.PropertyToID("_VoxVolMin");
        private static readonly int IdVoxVolInvSize = Shader.PropertyToID("_VoxVolInvSize");
        private static readonly int IdVoxRes        = Shader.PropertyToID("_VoxRes");

        /// <summary>
        /// Rasterizes the occluders' real meshes into the occupancy volume: three orthographic
        /// passes (one per axis, so faces of any orientation land at least once), fragments
        /// writing 1 into their voxel via UAV. The silhouette becomes the actual render
        /// geometry — skinned poses and cloth deformation included — while the shared volume
        /// keeps the cost independent of the light count.
        /// </summary>
        private enum VoxClass { All, Static, Dynamic }

        /// <summary>
        /// Records compute-shader voxelization for every rasterizable SkinnedMeshRenderer whose
        /// deformed vertex buffer is available on the GPU, and marks them so the raster axes skip
        /// them. This is the draw-call eliminator: the raster path pays one DrawRenderer (its own
        /// SetPass each) per renderer per axis; a dispatch pays none, and covers all axes at once.
        /// </summary>
        private void RecordComputeSkinned(CommandBuffer cmd, RenderTexture target)
        {
            if (_computeHandled == null || _computeHandled.Length < _occluders.Count)
                _computeHandled = new bool[Mathf.Max(_occluders.Count, 8)];
            System.Array.Clear(_computeHandled, 0, _computeHandled.Length);
            _statComputeSkinned = 0;
            _statSkinnedDispatches = 0;
            _statSkinnedTris = 0;

            if (!ComputeSkinnedVoxelize || _triKernel < 0 || target == null) return;

            using var _sk = _profiler.Sample(cmd, "SkinnedCompute");
            bool boundVolume = false;
            for (int i = 0; i < _occluders.Count; i++)
            {
                if (_voxelizeFlags == null || i >= _voxelizeFlags.Length || !_voxelizeFlags[i]) continue;
                if (!(_occluders[i] is SkinnedMeshRenderer smr)) continue;
                var mesh = smr.sharedMesh;
                if (mesh == null) continue;
                // Exotic layout (position outside stream 0 — the stream skinning deforms):
                // leave the renderer on the raster path rather than read garbage.
                if (mesh.GetVertexAttributeStream(UnityEngine.Rendering.VertexAttribute.Position) != 0)
                    continue;

                // The skinned output and the index buffer need raw (ByteAddressBuffer) access.
                // Enabling the target can take a frame to materialise, during which
                // GetVertexBuffer returns null — the renderer simply rasterizes as before and
                // joins this path on the next build.
                bool hadRaw = (smr.vertexBufferTarget & GraphicsBuffer.Target.Raw) != 0;
                if (!hadRaw) smr.vertexBufferTarget |= GraphicsBuffer.Target.Raw;
                if ((mesh.indexBufferTarget & GraphicsBuffer.Target.Raw) == 0)
                    mesh.indexBufferTarget |= GraphicsBuffer.Target.Raw;

                var vb = smr.GetVertexBuffer();
                if (vb == null)
                {
                    // Null on the very first attempt is the expected warm-up; null while raw
                    // access was already on means GPU skinning itself is unavailable.
                    if (hadRaw && !_warnedNoSkinBuffer)
                    {
                        _warnedNoSkinBuffer = true;
                        Debug.LogWarning("[StageBeam] SkinnedMeshRenderer.GetVertexBuffer() " +
                            "returned null — GPU skinning appears unavailable (Project Settings > " +
                            "Player > GPU Skinning). Skinned occluders fall back to rasterized " +
                            "voxelization: one draw call per renderer per axis.");
                    }
                    continue;
                }
                var ib = mesh.GetIndexBuffer();
                if (ib == null) { vb.Dispose(); continue; }

                _buffersThisBuild.Add(vb);
                _buffersThisBuild.Add(ib);

                if (!boundVolume)
                {
                    cmd.SetComputeTextureParam(Occlusion, _triKernel, IdCOcc, target);
                    boundVolume = true;
                }

                // Layout comes from the DATA, not assumptions. The skin output shares stream 0's
                // layout (skinning deforms stream 0 and copies anything else there through), so
                // the mesh's stream-0 stride and position offset are authoritative — and the
                // buffer's own stride wins outright when available: it describes the actual
                // allocation. A hand-derived pos+normal+tangent guess sat here once and read
                // garbage on any mesh whose stream 0 carried more than that.
                int stride = vb.stride >= 12 ? vb.stride : mesh.GetVertexBufferStride(0);
                int posOffset = Mathf.Max(0, mesh.GetVertexAttributeOffset(
                    UnityEngine.Rendering.VertexAttribute.Position));

                cmd.SetComputeBufferParam(Occlusion, _triKernel, IdTriVerts, vb);
                cmd.SetComputeBufferParam(Occlusion, _triKernel, IdTriIndices, ib);
                cmd.SetComputeIntParam(Occlusion, IdTriVertStride, stride);
                cmd.SetComputeIntParam(Occlusion, IdTriPosOffset, posOffset);
                cmd.SetComputeIntParam(Occlusion, IdTriIndex16,
                    mesh.indexFormat == UnityEngine.Rendering.IndexFormat.UInt16 ? 1 : 0);
                // GPU skinning outputs vertices relative to the renderer's skinning ROOT — the
                // root bone when one is assigned, else the renderer's own transform. This is the
                // same matrix VFX Graph's "Get Skinned Mesh World Root Transform" supplies for
                // exactly this reconstruction; multiplying by smr.transform instead put every
                // vertex in the wrong place (the classic tell: moving an SMR's own transform
                // doesn't move the rendered mesh — bones, not the SMR node, define its space).
                var skinRoot = smr.rootBone != null ? smr.rootBone : smr.transform;
                cmd.SetComputeMatrixParam(Occlusion, IdTriLocalToWorld,
                    skinRoot.localToWorldMatrix);

                // Submeshes are contiguous ranges of one index buffer, so when they all share a
                // baseVertex (the overwhelmingly common case) the whole renderer is ONE range and
                // one dispatch. Occupancy does not care which material a triangle belongs to, and
                // dispatch overhead is per-call — with ~90 skinned renderers, dispatching per
                // submesh instead multiplies a fixed cost by the material count for no benefit.
                int spanStart = int.MaxValue, spanEnd = 0, spanBase = 0, spanTris = 0;
                bool uniformBase = true, anyTris = false;
                for (int sm = 0; sm < mesh.subMeshCount; sm++)
                {
                    var d = mesh.GetSubMesh(sm);
                    if (d.topology != MeshTopology.Triangles || d.indexCount < 3) continue;
                    if (!anyTris) { spanBase = d.baseVertex; anyTris = true; }
                    else if (d.baseVertex != spanBase) { uniformBase = false; }
                    spanStart = Mathf.Min(spanStart, d.indexStart);
                    spanEnd = Mathf.Max(spanEnd, d.indexStart + d.indexCount);
                    spanTris += d.indexCount / 3;
                }
                if (!anyTris) continue;

                if (uniformBase)
                {
                    Dispatch(cmd, spanStart, spanEnd - spanStart, spanBase);
                }
                else
                {
                    // Mixed baseVertex: each range needs its own offset, so fall back to per-submesh.
                    for (int sm = 0; sm < mesh.subMeshCount; sm++)
                    {
                        var d = mesh.GetSubMesh(sm);
                        if (d.topology != MeshTopology.Triangles || d.indexCount < 3) continue;
                        Dispatch(cmd, d.indexStart, d.indexCount, d.baseVertex);
                    }
                }

                _computeHandled[i] = true;
                _statComputeSkinned++;
                _statSkinnedTris += spanTris;
            }
        }

        // One triangle-voxelize dispatch over an index range. Counted so the report can tell
        // dispatch-overhead-bound from triangle-work-bound: those need opposite fixes (fewer
        // dispatches vs. fewer samples per triangle), and the totals alone cannot distinguish them.
        private void Dispatch(CommandBuffer cmd, int indexStart, int indexCount, int baseVertex)
        {
            cmd.SetComputeIntParam(Occlusion, IdTriIndexStart, indexStart);
            cmd.SetComputeIntParam(Occlusion, IdTriIndexCount, indexCount);
            cmd.SetComputeIntParam(Occlusion, IdTriBaseVertex, baseVertex);
            cmd.DispatchCompute(Occlusion, _triKernel,
                Mathf.CeilToInt(indexCount / 3f / 64f), 1, 1);
            _statSkinnedDispatches++;
        }

        private void RecordVoxelizeMeshes(CommandBuffer cmd)
        {
            if (_occluders.Count == 0 || _volume == null) return;
            if (_voxelizeMat == null)
            {
                var shader = Resources.Load<Shader>("StageBeamVoxelize");
                if (shader == null) return;
                _voxelizeMat = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            }

            var res = Resolution;
            // 2× supersampled raster target: fragments denser than voxels, so thin features
            // between voxel centres still get written (cheap conservative-ish coverage).
            int rtSize = Mathf.Clamp(Mathf.Max(res.x, Mathf.Max(res.y, res.z)) * 2, 64, 1024);
            // The colour target is a DUMMY (the shader writes ColorMask 0; all output goes to
            // the UAV volume) — use plain ARGB32, which every GPU accepts as a render target.
            // R8 was rejected on some GPUs ("R8 sRGB unsupported").
            var dummyDesc = new RenderTextureDescriptor(rtSize, rtSize,
                RenderTextureFormat.ARGB32, 0)
            {
                sRGB = false,
                msaaSamples = 1,
            };
            cmd.GetTemporaryRT(IdVoxTargetRT, dummyDesc, FilterMode.Point);
            cmd.SetRenderTarget(IdVoxTargetRT);

            cmd.SetGlobalVector(IdVoxVolMin, _wc - _ws * 0.5f);
            cmd.SetGlobalVector(IdVoxVolInvSize, new Vector3(
                1f / Mathf.Max(_ws.x, 1e-3f), 1f / Mathf.Max(_ws.y, 1e-3f),
                1f / Mathf.Max(_ws.z, 1e-3f)));
            cmd.SetGlobalVector(IdVoxRes, new Vector3(res.x, res.y, res.z));

            ClassifyAndFlag();

            // Skinned renderers go through the compute path first (zero draw calls); whatever it
            // marks as handled, the raster axes below skip. Skinned occupancy is always dynamic,
            // so it writes _volume — cleared by RecordUploadAndDispatch — in both split modes.
            RecordComputeSkinned(cmd, _volume);

            // Timed separately from SkinnedCompute, not around it: a scope that CONTAINS another
            // double-counts in the report's total and hides which of the two costs anything.
            using var _raster = _profiler.Sample(cmd, "RasterVoxelize");

            bool split = StaticDynamicSplit && _combineKernel >= 0 && _clearKernel >= 0 && EnsureStaticVolume();
            if (split)
            {
                // Static occupancy is voxelized ONLY when the static set changed (rare) and cached;
                // the dynamic occupancy (skinned performers + anything that moved) is voxelized every
                // build. max() OR's the cached static into the fresh dynamic volume before dilation.
                if (_staticDirty || !_staticVolumeValid)
                {
                    DispatchClear(cmd, _volumeStatic);
                    VoxelizeAxes(cmd, _volumeStatic, VoxClass.Static);
                    _staticVolumeValid = true;
                    _statRebuiltStatic = true;
                }
                // _volume was already cleared by RecordUploadAndDispatch (Clear + empty Splat).
                VoxelizeAxes(cmd, _volume, VoxClass.Dynamic);
                DispatchCombine(cmd, _volume, _volumeStatic);
            }
            else
            {
                VoxelizeAxes(cmd, _volume, VoxClass.All);
            }

            cmd.ReleaseTemporaryRT(IdVoxTargetRT);
        }

        // Per-build classification: which occluders rasterize at all (active + not too big), and
        // which are DYNAMIC (SkinnedMeshRenderer — mesh deforms in place — or the transform moved
        // since last build). A static↔dynamic flip means the cached static set changed → rebuild it.
        private void ClassifyAndFlag()
        {
            int n = _occluders.Count;
            if (_voxelizeFlags == null || _voxelizeFlags.Length < n) _voxelizeFlags = new bool[Mathf.Max(n, 8)];

            _statBuilds++;
            // Stats are reset BEFORE the classification below computes them. An earlier revision
            // reset _statMatrixReset here, AFTER the doSplit block had already recorded it — so
            // the report unconditionally printed "reset: no" and hid the very condition it was
            // added to reveal.
            _staticDirty = false;
            _statOccluders = 0;
            _statDynamic = 0;
            _statDynSkinned = 0;
            _statMatrixReset = false;
            _statMovedMeshes = 0;
            _statMaxMovedDelta = 0f;
            _statMaxMovedRenderer = null;

            bool doSplit = StaticDynamicSplit;
            if (doSplit)
            {
                if (_dynamicFlags == null || _dynamicFlags.Length < n)
                    _dynamicFlags = new bool[Mathf.Max(n, 8)];
                // Empty history = the first build of this builder: nothing to compare against,
                // so everything classifies dynamic exactly once. Rescans no longer empty it —
                // history is keyed by renderer identity and survives list rebuilds (see Rescan).
                _statMatrixReset = _moveHistory.Count == 0;
                if (_occluderSetShrunk) { _staticDirty = true; _occluderSetShrunk = false; }
            }
            for (var i = 0; i < n; i++)
            {
                var r = _occluders[i];
                bool active = r != null && r.enabled && r.gameObject.activeInHierarchy;
                // A hinted subtree already contributed its authored shape via the compute splat —
                // rasterizing it too would double the occupancy and cost a draw call per renderer.
                bool hinted = _hintCovered != null && i < _hintCovered.Length && _hintCovered[i];
                _voxelizeFlags[i] = active && !hinted &&
                    (MaxOccluderSize <= 0f || r.bounds.extents.magnitude * 2f <= MaxOccluderSize);

                if (_voxelizeFlags[i]) _statOccluders++;

                if (!doSplit) continue;   // dynamic classification only needed for the split
                Matrix4x4 m = r != null ? r.transform.localToWorldMatrix : Matrix4x4.identity;
                MoveEntry prev = default;
                bool hasHistory = r != null && _moveHistory.TryGetValue(r, out prev);
                bool moved = !hasHistory || m != prev.Matrix;
                // Measure the movers. History is identity-keyed, so this is a real comparison
                // even on the build right after a rescan.
                if (moved && hasHistory && active && _voxelizeFlags[i] &&
                    !(r is SkinnedMeshRenderer))
                {
                    _statMovedMeshes++;
                    float d = MaxAbsElementDelta(in m, in prev.Matrix);
                    if (d > _statMaxMovedDelta) { _statMaxMovedDelta = d; _statMaxMovedRenderer = r; }
                }
                bool dyn = active && ((r is SkinnedMeshRenderer) || moved);
                if (dyn != (hasHistory && prev.WasDynamic)) _staticDirty = true;
                _dynamicFlags[i] = dyn;
                if (r != null) _moveHistory[r] = new MoveEntry { Matrix = m, WasDynamic = dyn };
                if (dyn && _voxelizeFlags[i])
                {
                    _statDynamic++;
                    if (r is SkinnedMeshRenderer) _statDynSkinned++;
                }
            }
        }

        private bool EnsureStaticVolume()
        {
            if (_volume == null) return false;
            if (_volumeStatic == null || _volumeStatic.width != _volume.width ||
                _volumeStatic.height != _volume.height || _volumeStatic.volumeDepth != _volume.volumeDepth)
            {
                if (_volumeStatic != null) { _volumeStatic.Release(); CoreUtils.Destroy(_volumeStatic); }
                _volumeStatic = new RenderTexture(_volume.descriptor) { name = "StageBeamOccStatic" };
                _volumeStatic.Create();
                _staticVolumeValid = false;
            }
            return true;
        }

        private void DispatchClear(CommandBuffer cmd, RenderTexture vol)
        {
            using var _s = _profiler.Sample(cmd, "ClearStatic");
            var res = Resolution;
            cmd.SetComputeIntParams(Occlusion, IdCRes, res.x, res.y, res.z);
            cmd.SetComputeTextureParam(Occlusion, _clearKernel, IdCOcc, vol);
            cmd.DispatchCompute(Occlusion, _clearKernel,
                Mathf.CeilToInt(res.x / 4f), Mathf.CeilToInt(res.y / 4f), Mathf.CeilToInt(res.z / 4f));
        }

        private void DispatchCombine(CommandBuffer cmd, RenderTexture target, RenderTexture other)
        {
            using var _s = _profiler.Sample(cmd, "CombineMax");
            var res = Resolution;
            cmd.SetComputeIntParams(Occlusion, IdCRes, res.x, res.y, res.z);
            cmd.SetComputeTextureParam(Occlusion, _combineKernel, IdCOcc, target);
            cmd.SetComputeTextureParam(Occlusion, _combineKernel, IdCCombineOther, other);
            cmd.DispatchCompute(Occlusion, _combineKernel,
                Mathf.CeilToInt(res.x / 4f), Mathf.CeilToInt(res.y / 4f), Mathf.CeilToInt(res.z / 4f));
        }

        // Rasterize a class of occluders into <paramref name="target"/> over the axis passes.
        private void VoxelizeAxes(CommandBuffer cmd, RenderTexture target, VoxClass cls)
        {
            cmd.SetRandomWriteTarget(1, target);
            // 3 axes = fully conservative; 2 drops the top-down pass; 1 keeps only the side pass.
            int axes = Mathf.Clamp(VoxelizeAxisCount, 1, 3);
            DrawAxis(cmd, Vector3.right,   Vector3.up,      new Vector2(_ws.z, _ws.y), _ws.x, cls);
            if (axes >= 3) DrawAxis(cmd, Vector3.up, Vector3.forward, new Vector2(_ws.x, _ws.z), _ws.y, cls);
            if (axes >= 2) DrawAxis(cmd, Vector3.forward, Vector3.up, new Vector2(_ws.x, _ws.y), _ws.z, cls);
            cmd.ClearRandomWriteTargets();
        }

        // One orthographic pass over the box along +axis: camera sits outside the min face
        // looking down the axis, frustum exactly covering the box's cross-section.
        private void DrawAxis(CommandBuffer cmd, Vector3 axis, Vector3 up, Vector2 extent, float depth, VoxClass cls)
        {
            var camPos = _wc - axis * (depth * 0.5f + 0.5f);
            var rot = Quaternion.LookRotation(axis, up);
            // Unity view matrix convention: camera looks down -Z, so flip Z of the pose inverse.
            var view = Matrix4x4.Scale(new Vector3(1f, 1f, -1f)) *
                       Matrix4x4.TRS(camPos, rot, Vector3.one).inverse;
            var proj = GL.GetGPUProjectionMatrix(
                Matrix4x4.Ortho(-extent.x * 0.5f, extent.x * 0.5f,
                                -extent.y * 0.5f, extent.y * 0.5f,
                                0.1f, depth + 1f), true);
            cmd.SetViewProjectionMatrices(view, proj);

            for (var i = 0; i < _occluders.Count; i++)
            {
                // Active + not-too-big (from ClassifyAndFlag), then the requested static/dynamic class.
                if (_voxelizeFlags == null || i >= _voxelizeFlags.Length || !_voxelizeFlags[i]) continue;
                // Already written by the compute path — a raster draw here would be pure waste.
                if (_computeHandled != null && i < _computeHandled.Length && _computeHandled[i]) continue;
                if (cls == VoxClass.Static  &&  _dynamicFlags[i]) continue;
                if (cls == VoxClass.Dynamic && !_dynamicFlags[i]) continue;
                var r = _occluders[i];
                int subs = _occluderSubMeshes != null && i < _occluderSubMeshes.Length
                    ? _occluderSubMeshes[i] : 1;
                for (int sm = 0; sm < subs; sm++)
                {
                    cmd.DrawRenderer(r, _voxelizeMat, sm, 0);
                    _statDraws++;
                }
            }
        }

        private static readonly int IdCDilateSrc = Shader.PropertyToID("_DilateSrc");

        // Two GrowMax passes (FILL the hollow voxelized shell + thicken it at full strength, so
        // the shadow is actually opaque) followed by two blur passes (feather the edge into a
        // smooth gradient — no voxel terracing). Ping-pong volume↔temp; 4 passes end back in
        // _volume. The mesh voxelizer only writes surface voxels, so without the fill the body
        // would be a thin translucent shell and the shadow far too weak (worst on the long
        // vertical path of an overhead light).
        private void RecordDilate(CommandBuffer cmd)
        {
            if (_dilateKernel < 0 || _volume == null) return;

            if (_volumeTemp == null || _volumeTemp.width != _volume.width ||
                _volumeTemp.height != _volume.height || _volumeTemp.volumeDepth != _volume.volumeDepth)
            {
                if (_volumeTemp != null) { _volumeTemp.Release(); CoreUtils.Destroy(_volumeTemp); }
                _volumeTemp = new RenderTexture(_volume.descriptor) { name = "StageBeamOccDilate" };
                _volumeTemp.Create();
            }

            var res = Resolution;
            int gx = Mathf.CeilToInt(res.x / 4f);
            int gy = Mathf.CeilToInt(res.y / 4f);
            int gz = Mathf.CeilToInt(res.z / 4f);
            cmd.SetComputeIntParams(Occlusion, IdCRes, res.x, res.y, res.z);

            // 2× GrowMax (fill/thicken) then 2× blur (smooth) — ping-pong, ends in _volume.
            // Timed separately: they are four full-volume passes and the likeliest place left to
            // find real time, so "grow" and "blur" must be distinguishable in the report.
            using (_profiler.Sample(cmd, "GrowMax x2"))
            {
                Pass(cmd, _growKernel >= 0 ? _growKernel : _dilateKernel, _volume, _volumeTemp, gx, gy, gz);
                Pass(cmd, _growKernel >= 0 ? _growKernel : _dilateKernel, _volumeTemp, _volume, gx, gy, gz);
            }
            using (_profiler.Sample(cmd, "Blur x2"))
            {
                Pass(cmd, _dilateKernel, _volume, _volumeTemp, gx, gy, gz);
                Pass(cmd, _dilateKernel, _volumeTemp, _volume, gx, gy, gz);
            }
        }

        private void Pass(CommandBuffer cmd, int kernel, RenderTexture src, RenderTexture dst,
            int gx, int gy, int gz)
        {
            cmd.SetComputeTextureParam(Occlusion, kernel, IdCDilateSrc, src);
            cmd.SetComputeTextureParam(Occlusion, kernel, IdCOcc, dst);
            cmd.DispatchCompute(Occlusion, kernel, gx, gy, gz);
        }

        private static readonly int IdCCurrent = Shader.PropertyToID("_Current");
        private static readonly int IdCTemporalAlpha = Shader.PropertyToID("_TemporalAlpha");

        // Temporal EMA of the occupancy. Returns the texture to bind for shadowing: the smoothed
        // history when enabled, else the freshly built volume. The world-anchored grid makes this
        // reprojection-free — but when the box RE-ANCHORS (its snapped min or quantized size
        // changes) the history's voxels no longer map to the same world points, so it's reset to
        // this frame's data (one un-smoothed frame) instead of smearing.
        private RenderTexture RecordTemporal(CommandBuffer cmd)
        {
            if (_temporalKernel < 0 || TemporalSmoothing <= 1e-3f || _volume == null)
                return _volume;

            if (_volumeHistory == null || _volumeHistory.width != _volume.width ||
                _volumeHistory.height != _volume.height || _volumeHistory.volumeDepth != _volume.volumeDepth)
            {
                if (_volumeHistory != null) { _volumeHistory.Release(); CoreUtils.Destroy(_volumeHistory); }
                _volumeHistory = new RenderTexture(_volume.descriptor) { name = "StageBeamOccHistory" };
                _volumeHistory.Create();
                _historyValid = false;
            }

            // Re-anchor (or first frame) → mix in 100% this frame, i.e. history = current, so
            // stale voxels that now map to different world points are replaced rather than
            // smeared. Otherwise the EMA. Done entirely with the compute kernel — no
            // CopyTexture, which does NOT reliably copy 3D RenderTextures via a command buffer
            // (that failure left the bound history empty → shadows vanished).
            bool reAnchored = !_historyValid || _wc != _prevWc || _ws != _prevWs;
            _prevWc = _wc; _prevWs = _ws;
            _historyValid = true;

            float alpha = reAnchored ? 1f : Mathf.Clamp01(1f - TemporalSmoothing);
            var res = Resolution;
            int gx = Mathf.CeilToInt(res.x / 4f), gy = Mathf.CeilToInt(res.y / 4f), gz = Mathf.CeilToInt(res.z / 4f);
            using (_profiler.Sample(cmd, "Temporal"))
            {
                cmd.SetComputeIntParams(Occlusion, IdCRes, res.x, res.y, res.z);
                cmd.SetComputeFloatParam(Occlusion, IdCTemporalAlpha, alpha);
                cmd.SetComputeTextureParam(Occlusion, _temporalKernel, IdCCurrent, _volume);
                cmd.SetComputeTextureParam(Occlusion, _temporalKernel, IdCOcc, _volumeHistory);
                cmd.DispatchCompute(Occlusion, _temporalKernel, gx, gy, gz);
            }
            return _volumeHistory;
        }

        private void RecordBindGlobals(CommandBuffer cmd, RenderTexture bound)
        {
            var min = _wc - _ws * 0.5f;
            cmd.SetGlobalTexture(IdOcc, bound);
            cmd.SetGlobalVector(IdOccMin, min);
            cmd.SetGlobalVector(IdOccInvSize,
                new Vector4(1f / Mathf.Max(_ws.x, 1e-3f),
                            1f / Mathf.Max(_ws.y, 1e-3f),
                            1f / Mathf.Max(_ws.z, 1e-3f), 0f));

            // Smallest world voxel edge (kept bound for tooling/diagnostics).
            float voxel = Mathf.Min(_ws.x / Mathf.Max(1, Resolution.x),
                          Mathf.Min(_ws.y / Mathf.Max(1, Resolution.y),
                                    _ws.z / Mathf.Max(1, Resolution.z)));
            cmd.SetGlobalFloat(IdVoxel, Mathf.Max(voxel, 1e-3f));

            // No occluders this frame → strength 0 so the shader skips the shadow march entirely.
            var any = _sphereCount + _boxCount > 0 || (MeshVoxelize && _occluders.Count > 0);
            cmd.SetGlobalFloat(IdStrength, any ? Strength : 0f);
            cmd.SetGlobalFloat(IdDensity, ShadowDensity);
            cmd.SetGlobalFloat(IdSteps, ShadowSteps);
            cmd.SetGlobalFloat(IdMaxDist, MaxShadowDistance);
            cmd.SetGlobalFloat(IdBias, Bias);
            cmd.SetGlobalFloat(IdLightBias, LightBias);
            cmd.SetGlobalFloat(IdThickness, ScreenSpaceThickness);
        }

        private void EnsureVolume()
        {
            var res = new Vector3Int(Mathf.Max(1, Resolution.x), Mathf.Max(1, Resolution.y),
                                     Mathf.Max(1, Resolution.z));
            if (_volume != null &&
                _volume.width == res.x && _volume.height == res.y && _volume.volumeDepth == res.z)
                return;

            if (_volume != null) { _volume.Release(); CoreUtils.Destroy(_volume); }
            _volume = new RenderTexture(res.x, res.y, 0, RenderTextureFormat.RHalf)
            {
                dimension         = TextureDimension.Tex3D,
                volumeDepth       = res.z,
                enableRandomWrite = true,
                wrapMode          = TextureWrapMode.Clamp,
                filterMode        = FilterMode.Bilinear,
                name              = "StageBeamOcclusionVolume",
            };
            _volume.Create();
        }
    }
}
