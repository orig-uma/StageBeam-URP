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
    /// MVR/DMX/GDTF. Any number of data sources (e.g. <c>MvrBeamSource</c>, <see cref="StageBeamLight"/>)
    /// call <see cref="AddSource"/> at startup to feed it; the driver pulls from every registered
    /// source each frame.
    /// </summary>
    [AddComponentMenu("Stage Beam/Stage Beam Driver")]
    public sealed class StageBeamDriver : MonoBehaviour
    {
        /// <summary>Additive blows out where beams overlap / against bright scenes; Soft Additive
        /// (screen blend) adds less where the background is already bright, so it saturates gently.</summary>
        public enum BeamBlend { Additive, SoftAdditive }

        [Tooltip("Override material (must use Origuma/StageBeamCone). Auto-created if null.")]
        public Material Material;

        [Tooltip("Additive = classic (can blow out on overlap). Soft Additive = screen blend, " +
                 "saturates gently. (Full-resolution path.)")]
        public BeamBlend Blend = BeamBlend.Additive;

        [Tooltip("Radial segments of the shared cone mesh.")]
        [Range(6, 64)] public int Segments = 24;

        /// <summary>All sources currently feeding this driver.</summary>
        public IReadOnlyList<IStageBeamSource> Sources => _sources;

        private static StageBeamDriver _instance;

        private readonly List<IStageBeamSource> _sources = new List<IStageBeamSource>();
        private Mesh _cone;
        private Material _mat;
        private int _builtSegments = -1;
        private BeamBlend _appliedBlend = (BeamBlend)(-1);

        private static readonly int IdSrcBlend = Shader.PropertyToID("_BeamSrcBlend");
        private static readonly int IdDstBlend = Shader.PropertyToID("_BeamDstBlend");

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

            _instance = FindFirstObjectByType<StageBeamDriver>();
            if (_instance == null)
            {
                var go = new GameObject("Stage Beam Driver");
                _instance = go.AddComponent<StageBeamDriver>();
            }
            return _instance;
        }

        private void OnDisable()
        {
            if (_instance == this) _instance = null;

            StageBeamQueue.Begin();
            StageBeamQueue.Mesh = null;
            StageBeamQueue.Material = null;
            if (_cone != null) Destroy(_cone);
            if (_mat != null && _mat != Material) Destroy(_mat);
            _cone = null; _mat = null; _builtSegments = -1;
        }

        private void OnDestroy()
        {
            if (_instance == this) _instance = null;
        }

        private void LateUpdate()
        {
            if (_sources.Count == 0) return;
            EnsureResources();

            _instances.Clear();
            for (var s = 0; s < _sources.Count; s++)
                _sources[s].CollectBeams(_instances);

            Shader.SetGlobalFloat(IdBeamFrameIndex, Time.frameCount);
            StageBeamQueue.Mesh = _cone;
            StageBeamQueue.Material = _mat;
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
                mpb.SetFloat(IdSteps, d.RaymarchSteps);
                mpb.SetFloat(IdDepthOcclude, d.DepthOcclude);
                mpb.SetFloat(IdAxialFalloff, d.AxialFalloff);
                mpb.SetFloat(IdHotspot, d.Hotspot);
                mpb.SetFloat(IdRootBoost, d.RootBoost);
                mpb.SetFloat(IdRootBoostFrac, d.RootBoostFrac);
                mpb.SetFloat(IdRootWhite, d.RootWhite);

                if (d.GoboArray != null) mpb.SetTexture(IdGoboArray, d.GoboArray);
                mpb.SetFloat(IdGoboSlice, d.GoboSlice);
                mpb.SetFloat(IdGoboRot, d.GoboRotationRad);

                if (d.GoboArray2 != null) mpb.SetTexture(IdGoboArray2, d.GoboArray2);
                mpb.SetFloat(IdGoboSlice2, d.GoboSlice2);
                mpb.SetFloat(IdGoboRot2, d.GoboRotationRad2);

                StageBeamQueue.Add(d.Matrix, mpb);
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
                if (_cone != null) Destroy(_cone);
                _cone = BuildUnitCone(Segments);
                _builtSegments = Segments;
            }
            if (_mat == null)
                _mat = Material != null ? Material : new Material(Shader.Find("Origuma/StageBeamCone"));

            if (_appliedBlend != Blend)
            {
                // BlendMode enum values: One = 1, OneMinusDstColor = 6 (screen = soft additive).
                _mat.SetFloat(IdSrcBlend, Blend == BeamBlend.SoftAdditive ? 6f : 1f);
                _mat.SetFloat(IdDstBlend, 1f);
                _appliedBlend = Blend;
            }
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
