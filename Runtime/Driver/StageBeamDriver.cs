using System.Collections.Generic;
using UnityEngine;

namespace Origuma.StageBeam
{
    /// <summary>
    /// Renders the beams supplied by an injected <see cref="IStageBeamSource"/>. Owns the shared
    /// cone mesh, the beam material and a pooled set of <see cref="MaterialPropertyBlock"/>s, and
    /// pushes each beam into <see cref="StageBeamQueue"/> for the renderer feature to raymarch.
    ///
    /// This is the generic, source-agnostic half of the beam pipeline: it knows nothing about
    /// MVR/DMX/GDTF. Any number of data sources (e.g. <c>StageBeamSource</c>, <see cref="StageBeamLight"/>)
    /// call <see cref="AddSource"/> at startup to feed it; the driver pulls from every registered
    /// source each frame.
    /// </summary>
    [AddComponentMenu("Stage Beam/Stage Beam Driver")]
    [ExecuteAlways]   // authoring without Play mode; see StageEditModePreview
    public sealed class StageBeamDriver : MonoBehaviour
    {
        /// <summary>
        /// Draw beams outside Play mode. Its own flag rather than a reference to the rig's: this
        /// package is the generic renderer and knows nothing about rigs, shows or DMX — the
        /// dependency runs one way, and an editor toggle sets both sides.
        /// </summary>
        public static bool EditModePreview;

        /// <summary>Additive blows out where beams overlap / against bright scenes; Soft Additive
        /// (screen blend) adds less where the background is already bright, so it saturates gently.</summary>
        public enum BeamBlend { Additive, SoftAdditive }

        [Tooltip("Override material (must use Origuma/StageBeamCone). Leave empty: a runtime " +
                 "instance is created automatically on first use and shown here.")]
        public Material Material;

        [Tooltip("Additive = classic (can blow out on overlap). Soft Additive = screen blend, " +
                 "saturates gently. (Full-resolution path.)")]
        // Soft Additive by default: real beams saturate toward the source colour instead of
        // stacking to pure white.
        public BeamBlend Blend = BeamBlend.SoftAdditive;

        // Composite curve is K*(1 - exp(-total/K)): identity slope at 0, asymptotic to K. The knee
        // therefore sits near K, so the value has to be chosen against the totals the rig actually
        // reaches — it is not an absolute look setting, and it moves whenever Master Intensity or
        // the scattering does.
        [Tooltip("Soft Additive: the brightness stacked beams saturate toward. Set it just above " +
                 "what a single beam reaches, so one beam is untouched and only overlaps compress " +
                 "— around 1.5-2 for a typical rig. Too low and every beam looks washed out and " +
                 "thin; too high and overlaps pile up to white and swallow anyone standing in " +
                 "them. Ignored unless Blend is Soft Additive.")]
        [Range(0.05f, 5f)] public float SoftMaxBrightness = 5f;

        // Softens the aliased line where the volumetric meets geometry. Kept near zero: the fade
        // takes the shaft to zero at contact while the surface projection pass draws the light pool
        // at full brightness, so any real width of it shows up as a gap between a beam and its own
        // gobo pattern. The aliasing it was introduced to hide shrinks as Resolution Scale rises.
        [Tooltip("Metres over which a beam fades out where it lands on a surface. Raise it if the " +
                 "contact line crawls or flickers — worst when a beam rakes across the floor at a " +
                 "grazing angle. Lower it if the beam looks like it hovers above its light pool. " +
                 "0 = hard edge.")]
        [Range(0f, 1f)] public float SurfaceContactFade = 0.01f;

        // A wide cone covers far more pixels but is dimmer and softer per flux conservation, so it
        // tolerates fewer samples; without this the fill-rate cost of a wide zoom explodes.
        [Tooltip("Use fewer raymarch steps on beams zoomed wider than 20°, down to a floor of 8 " +
                 "(half the steps at 40° and above). Keeps wide zooms cheap. Turn it off if wide " +
                 "beams look banded or grainy next to narrow ones.")]
        public bool AdaptiveSteps = true;

        [Tooltip("Radial segments of the shared cone mesh.")]
        [Range(6, 64)] public int Segments = 24;

        // One DrawMeshInstancedProcedural per gobo group instead of one DrawMesh per beam. Fill
        // rate is unchanged — per-beam data reaches the fragment through interpolators, not a
        // per-pixel buffer read — so the win is purely CPU/draw-call side.
        [Tooltip("Draw all beams sharing a gobo in one call instead of one call each. Saves CPU " +
                 "when the rig is large; makes little difference with few beams, and none to " +
                 "GPU cost. Ignored when Shadows is LightShadowMap.")]
        public bool GpuInstancing;

        /// <summary>All sources currently feeding this driver.</summary>
        public IReadOnlyList<IStageBeamSource> Sources => _sources;

        private static StageBeamDriver _instance;

        private readonly List<IStageBeamSource> _sources = new List<IStageBeamSource>();
        private Mesh _cone;
        private Material _mat;
        private bool _ownsMat;
        private int _builtSegments = -1;
        private BeamBlend _appliedBlend = (BeamBlend)(-1);

        private static readonly int IdSrcBlend = Shader.PropertyToID("_BeamSrcBlend");
        private static readonly int IdDstBlend = Shader.PropertyToID("_BeamDstBlend");
        private static readonly int IdBeamSoft = Shader.PropertyToID("_BeamSoft");
        private static readonly int IdSurfaceFade = Shader.PropertyToID("_SurfaceFadeDist");
        private float _appliedFade = -1f;

        private readonly List<StageBeamInstance> _instances = new List<StageBeamInstance>(64);
        private readonly List<MaterialPropertyBlock> _mpbPool = new List<MaterialPropertyBlock>();
        private int _mpbUsed;

        private static readonly int IdColor          = Shader.PropertyToID("_BeamColor");
        private static readonly int IdIntensity      = Shader.PropertyToID("_Intensity");
        private static readonly int IdStartRadius    = Shader.PropertyToID("_StartRadius");
        private static readonly int IdEndRadius      = Shader.PropertyToID("_EndRadius");
        private static readonly int IdRange          = Shader.PropertyToID("_Range");
        private static readonly int IdSoftness       = Shader.PropertyToID("_EdgeSoftness");
        private static readonly int IdFieldHalf      = Shader.PropertyToID("_FieldHalf");
        private static readonly int IdBeamHalf       = Shader.PropertyToID("_BeamHalf");
        private static readonly int IdDensity        = Shader.PropertyToID("_Density");
        private static readonly int IdAnisotropy     = Shader.PropertyToID("_Anisotropy");
        private static readonly int IdSteps          = Shader.PropertyToID("_Steps");
        private static readonly int IdDepthOcclude   = Shader.PropertyToID("_DepthOcclude");
        private static readonly int IdAxialFalloff   = Shader.PropertyToID("_AxialFalloff");
        private static readonly int IdHotspot        = Shader.PropertyToID("_Hotspot");
        private static readonly int IdRootBoost      = Shader.PropertyToID("_RootBoost");
        private static readonly int IdRootBoostFrac  = Shader.PropertyToID("_RootBoostFrac");
        private static readonly int IdRootWhite      = Shader.PropertyToID("_RootWhite");
        private static readonly int IdGoboSlice      = Shader.PropertyToID("_GoboSlice");
        private static readonly int IdGoboRot        = Shader.PropertyToID("_GoboRotation");
        private static readonly int IdGoboArray      = Shader.PropertyToID("_GoboArray");
        private static readonly int IdGoboSlice2     = Shader.PropertyToID("_GoboSlice2");
        private static readonly int IdGoboRot2       = Shader.PropertyToID("_GoboRotation2");
        private static readonly int IdGoboArray2     = Shader.PropertyToID("_GoboArray2");
        private static readonly int IdGoboOffset     = Shader.PropertyToID("_GoboOffset");
        private static readonly int IdBeamFrameIndex = Shader.PropertyToID("_BeamFrameIndex");

        /// <summary>Add a beam data source (no-op if already added).</summary>
        public void AddSource(IStageBeamSource source)
        {
            if (source == null || _sources.Contains(source)) return;
            _sources.Add(source);
        }

        /// <summary>Remove a beam data source (no-op if not present).</summary>
        public void RemoveSource(IStageBeamSource source) => _sources.Remove(source);

        /// <summary>Legacy alias for <see cref="AddSource"/>. Does not clear other sources.</summary>
        public void SetSource(IStageBeamSource source) => AddSource(source);

        /// <summary>Legacy alias for <see cref="RemoveSource"/>.</summary>
        public void ClearSource(IStageBeamSource source) => RemoveSource(source);

        /// <summary>
        /// Returns the first enabled <see cref="StageBeamDriver"/> in the scene, or creates a new
        /// "Stage Beam Driver" GameObject with one if none exists yet. Result is cached until the
        /// driver is disabled/destroyed.
        /// </summary>
        public static StageBeamDriver EnsureInstance()
        {
            if (_instance != null) return _instance;

            _instance = FindAnyObjectByType<StageBeamDriver>();
            if (_instance == null)
            {
                var go = new GameObject("Stage Beam Driver");
                _instance = go.AddComponent<StageBeamDriver>();
            }
            return _instance;
        }

        /// <summary>
        /// Destroys a generated asset from either mode. Plain Destroy is a no-op-with-an-error
        /// outside Play, and these objects (the shared cone mesh, the runtime material) are created
        /// per session and must actually go — otherwise every domain reload leaks another one.
        /// </summary>
        private static void DestroyGenerated(Object o)
        {
            if (o == null) return;
            if (Application.isPlaying) Destroy(o);
            else DestroyImmediate(o);
        }

        private void OnDisable()
        {
            if (_instance == this) _instance = null;

            StageBeamQueue.Begin();
            StageBeamQueue.Mesh = null;
            StageBeamQueue.Material = null;
            DestroyGenerated(_cone);
            if (_ownsMat && _mat != null)
            {
                if (Material == _mat) Material = null;   // don't leave a destroyed ref visible
                DestroyGenerated(_mat);
            }
            _ownsMat = false;
            _cone = null; _mat = null; _builtSegments = -1;
            _appliedBlend = (BeamBlend)(-1);             // re-apply blend to the next material
        }

        private void OnDestroy()
        {
            if (_instance == this) _instance = null;
        }

        private void LateUpdate()
        {
            if (!Application.isPlaying && !EditModePreview) return;
            if (_sources.Count == 0) return;
            EnsureResources();

            _instances.Clear();
            for (var s = 0; s < _sources.Count; s++)
                _sources[s].CollectBeams(_instances);

            Shader.SetGlobalFloat(IdBeamFrameIndex, Time.frameCount);
            StageBeamQueue.Mesh = _cone;
            StageBeamQueue.Material = _mat;
            StageBeamQueue.Instancing = GpuInstancing;
            StageBeamQueue.Begin();
            _mpbUsed = 0;

            for (var i = 0; i < _instances.Count; i++)
            {
                var d = _instances[i];
                var mpb = NextMpb();

                mpb.SetColor(IdColor, d.Color);
                mpb.SetFloat(IdIntensity, d.Intensity);
                mpb.SetFloat(IdStartRadius, d.StartRadius);
                mpb.SetFloat(IdEndRadius, d.EndRadius);
                mpb.SetFloat(IdRange, d.Range);
                mpb.SetFloat(IdSoftness, d.EdgeSoftness);
                mpb.SetFloat(IdFieldHalf, d.FieldHalfAngleRad);
                mpb.SetFloat(IdBeamHalf, d.BeamHalfAngleRad);
                mpb.SetFloat(IdDensity, d.Density);
                mpb.SetFloat(IdAnisotropy, d.Anisotropy);
                float steps = d.RaymarchSteps;
                if (AdaptiveSteps)
                {
                    // Wide zoom covers ~(angle ratio)² more pixels; spend proportionally fewer
                    // samples per pixel (the wide beam is dimmer + softer, so it can afford it).
                    const float RefHalfRad = 10f * Mathf.Deg2Rad;   // 20° field reference
                    float scale = Mathf.Clamp(RefHalfRad / Mathf.Max(d.FieldHalfAngleRad, 1e-3f),
                                              0.5f, 1f);
                    steps = Mathf.Max(8f, Mathf.Round(steps * scale));
                }
                mpb.SetFloat(IdSteps, steps);
                mpb.SetFloat(IdDepthOcclude, d.DepthOcclude);
                mpb.SetFloat(IdAxialFalloff, d.AxialFalloff);
                mpb.SetFloat(IdHotspot, d.Hotspot);
                mpb.SetFloat(IdRootBoost, d.RootBoost);
                mpb.SetFloat(IdRootBoostFrac, d.RootBoostFrac);
                mpb.SetFloat(IdRootWhite, d.RootWhite);

                if (d.GoboArray != null) mpb.SetTexture(IdGoboArray, d.GoboArray);
                mpb.SetFloat(IdGoboSlice, d.GoboSlice);
                mpb.SetFloat(IdGoboRot, d.GoboRotationRad);
                mpb.SetVector(IdGoboOffset, d.GoboOffset);

                if (d.GoboArray2 != null) mpb.SetTexture(IdGoboArray2, d.GoboArray2);
                mpb.SetFloat(IdGoboSlice2, d.GoboSlice2);
                mpb.SetFloat(IdGoboRot2, d.GoboRotationRad2);

                // World-space bounding sphere for frustum / screen-area culling. The cone runs
                // from the lens (local origin) down local -Y for Range, widening to EndRadius;
                // the tightest enclosing sphere is centred at the mid-axis.
                float half = d.Range * 0.5f;
                var localCenter = new Vector3(0f, -half, 0f);
                var worldCenter = d.Matrix.MultiplyPoint3x4(localCenter);
                var ls = d.Matrix.lossyScale;
                float maxScale = Mathf.Max(Mathf.Abs(ls.x), Mathf.Max(Mathf.Abs(ls.y), Mathf.Abs(ls.z)));
                float worldRadius = Mathf.Sqrt(half * half + d.EndRadius * d.EndRadius) * maxScale;

                // GPU-instanced path: also pack the same values as a GpuBeam record (gobo arrays
                // carried alongside as the batch key). Cheap; only consumed when instancing is on.
                GpuBeam raw = default;
                if (GpuInstancing) raw = GpuBeam.Pack(in d, steps);

                StageBeamQueue.Add(d.Matrix, mpb, d.ShadowLight, worldCenter, worldRadius,
                    raw, GpuInstancing ? d.GoboArray : null, GpuInstancing ? d.GoboArray2 : null);
            }
        }

        private MaterialPropertyBlock NextMpb()
        {
            if (_mpbUsed >= _mpbPool.Count) _mpbPool.Add(new MaterialPropertyBlock());
            return _mpbPool[_mpbUsed++];
        }

        private void EnsureResources()
        {
            if (_cone == null || _builtSegments != Segments)
            {
                DestroyGenerated(_cone);
                _cone = BuildUnitCone(Segments);
                _builtSegments = Segments;
            }
            if (_mat == null)
            {
                if (Material == null)
                {
                    // Auto-create and surface the instance in the Inspector's Material field so
                    // an auto-spawned driver doesn't look broken ("Material: None").
                    Material = new Material(Shader.Find("Origuma/StageBeamCone"))
                        { name = "StageBeamCone (Runtime Instance)" };
                    _ownsMat = true;
                }
                _mat = Material;
            }

            if (_appliedBlend != Blend)
            {
                // Both modes accumulate plain additive (One One). Soft Additive's saturation
                // happens at COMPOSITE time: the renderer feature routes beams through an
                // offscreen buffer and maps the TOTAL through K·(1−exp(−sum/K)) — identity
                // for one thin beam, asymptotic to SoftMaxBrightness for a stack. Per-beam
                // blend tricks can't do this: the ceiling has to see the summed value.
                _mat.SetFloat(IdSrcBlend, 1f);
                _mat.SetFloat(IdDstBlend, 1f);
                _mat.SetFloat(IdBeamSoft, 0f);
                _appliedBlend = Blend;
            }

            // Not inside the blend block: this is independent of blend mode and has to follow the
            // field whenever it is edited or animated. A material float lives in UnityPerMaterial,
            // so a global of the same name would be ignored — it has to be set on the material.
            if (_appliedFade != SurfaceContactFade)
            {
                _appliedFade = SurfaceContactFade;
                _mat.SetFloat(IdSurfaceFade, Mathf.Max(SurfaceContactFade, 1e-4f));
            }

            // Published every frame (cheap statics) so the renderer feature routes/saturates
            // accordingly even when these fields are animated.
            StageBeamQueue.SoftComposite = Blend == BeamBlend.SoftAdditive;
            StageBeamQueue.SoftCeiling = Mathf.Max(SoftMaxBrightness, 0.01f);
        }

        /// <summary>
        /// Unit cone frustum: xz on the unit circle, y carries t in {0,1} (0 = lens, 1 = reach).
        /// The vertex shader expands it to real metres using _StartRadius/_EndRadius/_Range.
        /// </summary>
        private static Mesh BuildUnitCone(int segments)
        {
            var ring = segments + 1;
            var verts = new Vector3[ring * 2 + 2];
            var tris = new int[segments * 12];

            for (var s = 0; s <= segments; s++)
            {
                var a = (float)s / segments * Mathf.PI * 2f;
                float cx = Mathf.Cos(a), cz = Mathf.Sin(a);
                verts[s] = new Vector3(cx, 0f, cz);
                verts[ring + s] = new Vector3(cx, 1f, cz);
            }

            var lensCenterIdx = ring * 2;
            var reachCenterIdx = ring * 2 + 1;
            verts[lensCenterIdx] = new Vector3(0f, -0.2f, 0f);
            verts[reachCenterIdx] = new Vector3(0f, 1.2f, 0f);

            var ti = 0;
            for (var s = 0; s < segments; s++)
            {
                int a0 = s, a1 = s + 1, b0 = ring + s, b1 = ring + s + 1;
                // Sides wound outward (normals point OUT), matching the caps below. Consistent
                // winding across the whole mesh is what lets StageBeamProjection.shader's
                // Cull Front render exactly ONE fragment per screen pixel (the mesh's far side
                // from the camera). Previously the sides were wound inward and Cull Front kept
                // the near-camera side surface + the far-camera cap surface both, giving a
                // double-additive "cone tip disc" over the pool where their footprints overlap.
                tris[ti++] = a0; tris[ti++] = a1; tris[ti++] = b0;
                tris[ti++] = a1; tris[ti++] = b1; tris[ti++] = b0;
                tris[ti++] = lensCenterIdx; tris[ti++] = a1; tris[ti++] = a0;
                tris[ti++] = reachCenterIdx; tris[ti++] = b0; tris[ti++] = b1;
            }

            var mesh = new Mesh { name = "StageBeamUnitCone" };
            mesh.vertices = verts;
            mesh.triangles = tris;
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 4f);
            return mesh;
        }
    }
}
