using System.Collections.Generic;
using UnityEngine;

namespace Origuma.StageBeam
{
    /// <summary>
    /// Standalone stress test for the stage-beam renderer. Bypasses the MVR/DMX rig
    /// entirely: it builds a cone mesh + material and pushes a configurable number of
    /// animated beams straight into <see cref="StageBeamQueue"/> every frame, arranged
    /// so the cones blanket the screen for worst-case fill-rate measurement.
    ///
    /// Add to any GameObject in a scene that has a camera and a URP renderer with the
    /// StageBeamRendererFeature enabled. Tweak <see cref="BeamCount"/> at runtime.
    /// </summary>
    [AddComponentMenu("Stage Beam/Stage Beam Load Test")]
    public sealed class StageBeamLoadTest : MonoBehaviour
    {
        [Header("Load")]
        [Tooltip("Number of beams pushed every frame.")]
        [Range(1, 4096)] public int BeamCount = 200;

        [Tooltip("Camera the beams aim toward (defaults to Camera.main).")]
        public Camera TargetCamera;

        [Header("Placement")]
        [Tooltip("Beams are scattered on a sphere of this radius around the focus point.")]
        public float SpawnRadius = 18f;

        [Tooltip("Point the beams converge on / aim through (world space).")]
        public Vector3 FocusPoint = Vector3.zero;

        [Header("Beam")]
        public float Range = 25f;
        public float StartRadius = 0.05f;
        [Tooltip("Field (outer) half angle in degrees.")]
        [Range(1f, 60f)] public float FieldHalfAngleDeg = 16f;
        [Range(0.01f, 1f)] public float SideSoftness = 0.35f;
        public float Density = 1f;
        public float Intensity = 1.2f;
        [Range(1, 128)] public int RaymarchSteps = 24;
        public bool SceneDepthOcclusion = false;

        [Header("Animation")]
        public bool Animate = true;
        [Range(0f, 4f)] public float SweepSpeed = 0.6f;
        [Range(0f, 1f)] public float SweepAmount = 0.5f;
        public bool RandomColors = true;

        [Header("Mesh")]
        [Range(6, 48)] public int Segments = 24;

        [Header("Shadow Occluders (test)")]
        [Tooltip("Spawn moving spheres in the beam field so volumetric shadows have something to " +
                 "cast. Needs a Stage Beam Occlusion Volume in the scene + Shadows = Volume on the feature.")]
        public bool SpawnOccluders = false;
        [Range(0, 64)] public int OccluderCount = 8;
        public float OccluderRadius = 1.2f;
        [Tooltip("Layer for the spawned occluders — match the Occlusion Volume's OccluderMask.")]
        [Range(0, 31)] public int OccluderLayer = 0;

        private readonly List<Transform> _occluders = new List<Transform>();

        private Mesh _cone;
        private Material _mat;
        private readonly List<MaterialPropertyBlock> _mpbPool = new List<MaterialPropertyBlock>();
        private int _mpbUsed;
        private int _builtSegments = -1;
        private int _lastLoggedCount = -1;

        private static readonly int IdColor       = Shader.PropertyToID("_BeamColor");
        private static readonly int IdIntensity   = Shader.PropertyToID("_Intensity");
        private static readonly int IdStartRadius = Shader.PropertyToID("_StartRadius");
        private static readonly int IdEndRadius   = Shader.PropertyToID("_EndRadius");
        private static readonly int IdRange       = Shader.PropertyToID("_Range");
        private static readonly int IdSoftness    = Shader.PropertyToID("_EdgeSoftness");
        private static readonly int IdFieldHalf   = Shader.PropertyToID("_FieldHalf");
        private static readonly int IdBeamHalf    = Shader.PropertyToID("_BeamHalf");
        private static readonly int IdDensity     = Shader.PropertyToID("_Density");
        private static readonly int IdSteps       = Shader.PropertyToID("_Steps");
        private static readonly int IdDepthOcclude = Shader.PropertyToID("_DepthOcclude");
        private static readonly int IdAxialFalloff = Shader.PropertyToID("_AxialFalloff");
        private static readonly int IdGoboSlice   = Shader.PropertyToID("_GoboSlice");
        private static readonly int IdBeamFrameIndex = Shader.PropertyToID("_BeamFrameIndex");

        private void OnDisable()
        {
            StageBeamQueue.Begin();
            StageBeamQueue.Mesh = null;
            StageBeamQueue.Material = null;
            if (_cone != null) Destroy(_cone);
            if (_mat != null) Destroy(_mat);
            _cone = null; _mat = null; _builtSegments = -1;
            DespawnOccluders();
        }

        private void LateUpdate()
        {
            EnsureResources();
            EnsureOccluders();

            var cam = TargetCamera != null ? TargetCamera : Camera.main;
            var aim = cam != null ? cam.transform.position : FocusPoint + Vector3.up * 5f;

            Shader.SetGlobalFloat(IdBeamFrameIndex, Time.frameCount);

            StageBeamQueue.Mesh = _cone;
            StageBeamQueue.Material = _mat;
            StageBeamQueue.Begin();
            _mpbUsed = 0;

            var fieldHalfRad = FieldHalfAngleDeg * Mathf.Deg2Rad;
            var endRadius = Range * Mathf.Tan(fieldHalfRad) + StartRadius;
            var t = Animate ? Time.time * SweepSpeed : 0f;

            for (var i = 0; i < BeamCount; i++)
            {
                // Deterministic scatter on a sphere around the focus point.
                var u = Hash(i * 2 + 1);
                var v = Hash(i * 2 + 2);
                var theta = u * Mathf.PI * 2f;
                var phi = Mathf.Acos(1f - v);             // upper-biased hemisphere
                var dirOnSphere = new Vector3(
                    Mathf.Sin(phi) * Mathf.Cos(theta),
                    Mathf.Cos(phi),
                    Mathf.Sin(phi) * Mathf.Sin(theta));
                var pos = FocusPoint + dirOnSphere * SpawnRadius;

                // Aim toward the camera so cones blanket the view, with an animated wobble.
                var toAim = (aim - pos).normalized;
                if (Animate && SweepAmount > 0f)
                {
                    var w = t + i * 0.6180339887f * Mathf.PI * 2f;
                    var wob = new Vector3(Mathf.Sin(w), Mathf.Sin(w * 1.3f), Mathf.Cos(w)) * (SweepAmount * 0.4f);
                    toAim = (toAim + wob).normalized;
                }

                // Beam axis is local -Y; orient that onto the aim direction.
                var rot = Quaternion.FromToRotation(Vector3.down, toAim);

                var mpb = NextMpb();
                var c = RandomColors ? Color.HSVToRGB(Hash(i + 7), 0.7f, 1f) : Color.white;
                mpb.SetColor(IdColor, c);
                mpb.SetFloat(IdIntensity, Intensity);
                mpb.SetFloat(IdStartRadius, StartRadius);
                mpb.SetFloat(IdEndRadius, endRadius);
                mpb.SetFloat(IdRange, Range);
                mpb.SetFloat(IdSoftness, SideSoftness);
                mpb.SetFloat(IdFieldHalf, fieldHalfRad);
                mpb.SetFloat(IdBeamHalf, fieldHalfRad * 0.6f);
                mpb.SetFloat(IdDensity, Density);
                mpb.SetFloat(IdSteps, RaymarchSteps);
                mpb.SetFloat(IdDepthOcclude, SceneDepthOcclusion ? 1f : 0f);
                mpb.SetFloat(IdAxialFalloff, 1f);
                mpb.SetFloat(IdGoboSlice, -1f);

                StageBeamQueue.Add(Matrix4x4.TRS(pos, rot, Vector3.one), mpb);
            }

            if (BeamCount != _lastLoggedCount)
            {
                Debug.Log($"[StageBeam] Load test drawing {BeamCount} beams.", this);
                _lastLoggedCount = BeamCount;
            }
        }

        private MaterialPropertyBlock NextMpb()
        {
            if (_mpbUsed >= _mpbPool.Count) _mpbPool.Add(new MaterialPropertyBlock());
            return _mpbPool[_mpbUsed++];
        }

        // Spawn / animate moving occluder spheres so volumetric shadows have casters at scale.
        private void EnsureOccluders()
        {
            if (!SpawnOccluders) { DespawnOccluders(); return; }

            while (_occluders.Count < OccluderCount)
            {
                var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                go.name = "LoadTestOccluder";
                go.hideFlags = HideFlags.DontSave;
                _occluders.Add(go.transform);
            }
            while (_occluders.Count > OccluderCount)
            {
                var last = _occluders[_occluders.Count - 1];
                _occluders.RemoveAt(_occluders.Count - 1);
                if (last != null) Destroy(last.gameObject);
            }

            // Place each occluder mid-way along a real beam path (fixture sphere → camera) so it
            // is guaranteed to sit inside that beam and cast a visible shadow.
            var cam = TargetCamera != null ? TargetCamera : Camera.main;
            var camPos = cam != null ? cam.transform.position : FocusPoint + Vector3.back * 20f;
            for (var i = 0; i < _occluders.Count; i++)
            {
                var tr = _occluders[i];
                if (tr == null) continue;
                if (tr.gameObject.layer != OccluderLayer) tr.gameObject.layer = OccluderLayer;

                var u = Hash(i * 7 + 3);
                var v = Hash(i * 7 + 5);
                var theta = u * Mathf.PI * 2f;
                var phi = Mathf.Acos(1f - v);
                var dir = new Vector3(Mathf.Sin(phi) * Mathf.Cos(theta), Mathf.Cos(phi), Mathf.Sin(phi) * Mathf.Sin(theta));
                var beamStart = FocusPoint + dir * SpawnRadius;
                var frac = 0.5f + 0.12f * Mathf.Sin(Time.time * 0.5f + i);   // slide along the beam
                tr.position = Vector3.Lerp(beamStart, camPos, frac);
                tr.localScale = Vector3.one * (OccluderRadius * 2f);
            }
        }

        private void DespawnOccluders()
        {
            for (var i = 0; i < _occluders.Count; i++)
                if (_occluders[i] != null) Destroy(_occluders[i].gameObject);
            _occluders.Clear();
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
                _mat = new Material(Shader.Find("Origuma/StageBeamCone"));
        }

        // Cheap deterministic [0,1) hash so the layout is stable frame to frame.
        private static float Hash(int n)
        {
            var x = (uint)(n * 747796405 + 2891336453);
            x = ((x >> ((int)(x >> 28) + 4)) ^ x) * 277803737u;
            x = (x >> 22) ^ x;
            return (x & 0xFFFFFF) / 16777216f;
        }

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
                // Sides wound outward (normals point OUT), matching the caps below — see the
                // matching comment in StageBeamDriver.BuildUnitCone for why consistent winding
                // is required by StageBeamProjection.shader's Cull Front.
                tris[ti++] = a0; tris[ti++] = a1; tris[ti++] = b0;
                tris[ti++] = a1; tris[ti++] = b1; tris[ti++] = b0;
                tris[ti++] = lensCenterIdx; tris[ti++] = a1; tris[ti++] = a0;
                tris[ti++] = reachCenterIdx; tris[ti++] = b0; tris[ti++] = b1;
            }

            var mesh = new Mesh { name = "StageBeamLoadTestCone" };
            mesh.vertices = verts;
            mesh.triangles = tris;
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 1000f);
            return mesh;
        }
    }
}
