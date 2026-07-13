using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Origuma.StageBeam
{
    /// <summary>
    /// Builds the single shared world-space occupancy volume every StageBeam cone self-shadows
    /// against: occluders are discovered by layer mask, approximated as oriented boxes from
    /// their renderer's local bounds (per-bone spheres for skinned meshes, so dancers read as
    /// limbs), voxelized by a compute shader once per frame, and bound as global shader state.
    /// Cost is independent of how many beams are drawn.
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
        public float RescanInterval = 0.25f;

        /// <summary>Voxelize the occluders' REAL meshes (3-axis rasterization into the volume)
        /// instead of approximating them with spheres/boxes. Silhouettes become the actual
        /// render geometry — skinned poses and cloth deformation included — while the shared
        /// volume keeps the cost independent of the light count. Off = legacy shape splatting
        /// (bone spheres / bounds boxes / hint shapes).</summary>
        public bool MeshVoxelize = true;

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
        // Per-occluder explicit shape override (StageBeamOccluderHint on a parent), resolved at
        // rescan time — parallel to _occluders. One hint covers many renderers; the emit pass
        // dedupes so a hinted character casts exactly one capsule/box.
        private readonly List<StageBeamOccluderHint> _occluderHints = new List<StageBeamOccluderHint>(128);
        private readonly HashSet<StageBeamOccluderHint> _emittedHints = new HashSet<StageBeamOccluderHint>();
        private Vector4[] _spheres;          // xyz = centre, w = radius
        private Vector4[] _boxes;            // 3 per box: [centre, minHalfExt], [halfExt, 0], [rot quat]
        private int _sphereCount, _boxCount;
        private Bounds _fitBounds;
        private bool _hasFit;
        private int _clearKernel = -1, _splatKernel = -1, _growKernel = -1, _dilateKernel = -1, _temporalKernel = -1;
        private RenderTexture _volumeTemp;      // ping-pong for the dilate passes
        private RenderTexture _volumeHistory;   // temporal EMA of the occupancy (bound for shadowing)
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
        private static readonly int IdCSpheres  = Shader.PropertyToID("_Spheres");
        private static readonly int IdCCount    = Shader.PropertyToID("_SphereCount");
        private static readonly int IdCBoxes    = Shader.PropertyToID("_Boxes");
        private static readonly int IdCBoxCount = Shader.PropertyToID("_BoxCount");
        private static readonly int IdCVolMin   = Shader.PropertyToID("_VolMin");
        private static readonly int IdCVolSize  = Shader.PropertyToID("_VolSize");
        private static readonly int IdCRes      = Shader.PropertyToID("_Res");
        private static readonly int IdCSoft     = Shader.PropertyToID("_EdgeSoftness");

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
                _occluderSubMeshes[i] = Mathf.Max(1, _occluders[i].sharedMaterials.Length);
        }

        private void GatherOccluders()
        {
            _sphereCount = 0;
            _boxCount = 0;
            _hasFit = false;
            _emittedHints.Clear();

            for (var i = 0; i < _occluders.Count; i++)
            {
                var r = _occluders[i];
                // Gate on activeInHierarchy: inactive occluders stay cached (see Rescan) but
                // contribute nothing to the volume until their GameObject is enabled.
                if (r == null || !r.enabled || !r.gameObject.activeInHierarchy) continue;

                // Mesh voxelization: real geometry goes straight into the volume (see
                // VoxelizeMeshes), so no approximation shapes are emitted here — this pass
                // only accumulates the AutoFit bounds. Skinned fit still comes from bone
                // positions so inflated culling bounds don't balloon the box.
                if (MeshVoxelize)
                {
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
                var hint = i < _occluderHints.Count ? _occluderHints[i] : null;
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

        private static readonly StageBeamOccluderHint.Segment[] HintSegments =
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
            if (_sphereCount >= _spheres.Length) return;
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

            var res = Resolution;
            int gx = Mathf.CeilToInt(res.x / 4f);
            int gy = Mathf.CeilToInt(res.y / 4f);
            int gz = Mathf.CeilToInt(res.z / 4f);

            // Clear
            cmd.SetComputeTextureParam(Occlusion, _clearKernel, IdCOcc, _volume);
            cmd.SetComputeIntParams(Occlusion, IdCRes, res.x, res.y, res.z);
            cmd.DispatchCompute(Occlusion, _clearKernel, gx, gy, gz);

            // Splat
            var min = _wc - _ws * 0.5f;
            cmd.SetComputeTextureParam(Occlusion, _splatKernel, IdCOcc, _volume);
            cmd.SetComputeBufferParam(Occlusion, _splatKernel, IdCSpheres, _sphereBuffer);
            cmd.SetComputeIntParam(Occlusion, IdCCount, _sphereCount);
            cmd.SetComputeBufferParam(Occlusion, _splatKernel, IdCBoxes, _boxBuffer);
            cmd.SetComputeIntParam(Occlusion, IdCBoxCount, _boxCount);
            cmd.SetComputeVectorParam(Occlusion, IdCVolMin, min);
            cmd.SetComputeVectorParam(Occlusion, IdCVolSize, _ws);
            cmd.SetComputeFloatParam(Occlusion, IdCSoft, EdgeSoftness);
            cmd.DispatchCompute(Occlusion, _splatKernel, gx, gy, gz);
        }

        private static readonly int IdVoxVolMin     = Shader.PropertyToID("_VoxVolMin");
        private static readonly int IdVoxVolInvSize = Shader.PropertyToID("_VoxVolInvSize");
        private static readonly int IdVoxRes        = Shader.PropertyToID("_VoxRes");
        private static readonly int IdVoxTargetRT   = Shader.PropertyToID("_StageBeamVoxelizeRT");

        /// <summary>
        /// Rasterizes the occluders' real meshes into the occupancy volume: three orthographic
        /// passes (one per axis, so faces of any orientation land at least once), fragments
        /// writing 1 into their voxel via UAV. The silhouette becomes the actual render
        /// geometry — skinned poses and cloth deformation included — while the shared volume
        /// keeps the cost independent of the light count.
        /// </summary>
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

            cmd.SetRandomWriteTarget(1, _volume);
            DrawAxis(cmd, Vector3.right,   Vector3.up,      new Vector2(_ws.z, _ws.y), _ws.x);
            DrawAxis(cmd, Vector3.up,      Vector3.forward, new Vector2(_ws.x, _ws.z), _ws.y);
            DrawAxis(cmd, Vector3.forward, Vector3.up,      new Vector2(_ws.x, _ws.y), _ws.z);
            cmd.ClearRandomWriteTargets();
            cmd.ReleaseTemporaryRT(IdVoxTargetRT);
        }

        // One orthographic pass over the box along +axis: camera sits outside the min face
        // looking down the axis, frustum exactly covering the box's cross-section.
        private void DrawAxis(CommandBuffer cmd, Vector3 axis, Vector3 up, Vector2 extent, float depth)
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
                var r = _occluders[i];
                // Gate on activeInHierarchy: inactive occluders stay cached (see Rescan) but
                // contribute nothing to the volume until their GameObject is enabled.
                if (r == null || !r.enabled || !r.gameObject.activeInHierarchy) continue;
                // Same "too big" filter as the shape path (floors/walls don't shadow beams).
                if (MaxOccluderSize > 0f && r.bounds.extents.magnitude * 2f > MaxOccluderSize)
                    continue;
                int subs = _occluderSubMeshes != null && i < _occluderSubMeshes.Length
                    ? _occluderSubMeshes[i] : 1;
                for (int sm = 0; sm < subs; sm++)
                    cmd.DrawRenderer(r, _voxelizeMat, sm, 0);
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
            Pass(cmd, _growKernel >= 0 ? _growKernel : _dilateKernel, _volume, _volumeTemp, gx, gy, gz);
            Pass(cmd, _growKernel >= 0 ? _growKernel : _dilateKernel, _volumeTemp, _volume, gx, gy, gz);
            Pass(cmd, _dilateKernel, _volume, _volumeTemp, gx, gy, gz);
            Pass(cmd, _dilateKernel, _volumeTemp, _volume, gx, gy, gz);
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
            cmd.SetComputeIntParams(Occlusion, IdCRes, res.x, res.y, res.z);
            cmd.SetComputeFloatParam(Occlusion, IdCTemporalAlpha, alpha);
            cmd.SetComputeTextureParam(Occlusion, _temporalKernel, IdCCurrent, _volume);
            cmd.SetComputeTextureParam(Occlusion, _temporalKernel, IdCOcc, _volumeHistory);
            cmd.DispatchCompute(Occlusion, _temporalKernel, gx, gy, gz);
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
