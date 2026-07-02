using System.Collections.Generic;
using UnityEngine;

namespace Origuma.StageBeam
{
    /// <summary>
    /// Standalone, per-GameObject volumetric beam. Zero MVR/DMX knowledge required: add this
    /// component to any GameObject and it registers itself with a <see cref="StageBeamDriver"/>
    /// (auto-created if the scene doesn't have one yet) and renders one beam every frame.
    ///
    /// Fixture convention: the object's local +Y is "up" and the beam exits along local -Y,
    /// matching <see cref="StageBeamInstance"/>'s documented axis. Set <see cref="BeamAxis"/> to
    /// <see cref="Axis.PositiveZ"/> if you'd rather aim it like a Unity spotlight (down local +Z /
    /// transform.forward).
    /// </summary>
    [AddComponentMenu("Stage Beam/Stage Beam Light")]
    public sealed class StageBeamLight : MonoBehaviour, IStageBeamSource
    {
        /// <summary>Which local axis the beam exits along.</summary>
        public enum Axis
        {
            /// <summary>Beam exits along local -Y (fixture convention: +Y is "up").</summary>
            NegativeY,

            /// <summary>Beam exits along local +Z / transform.forward, like a Unity spotlight.</summary>
            PositiveZ
        }

        [Header("Orientation")]
        [Tooltip("NegativeY = fixture convention (+Y up, beam exits -Y). PositiveZ = aim like a " +
                 "Unity spotlight, down transform.forward.")]
        public Axis BeamAxis = Axis.NegativeY;

        [Header("Light")]
        [Tooltip("Beam colour.")]
        public Color Color = Color.white;

        [Tooltip("Overall brightness multiplier.")]
        public float Intensity = 1.2f;

        [Tooltip("Maximum throw distance in metres.")]
        public float Range = 15f;

        [Tooltip("Full field (outer) cone angle in degrees — the total angular width of the beam.")]
        [Range(1f, 120f)] public float SpotAngle = 32f;

        [Tooltip("Full beam (hotspot) cone angle in degrees — the brighter core inside the field " +
                 "cone. Clamped to at most Spot Angle.")]
        [Range(1f, 120f)] public float BeamAngle = 15f;

        [Tooltip("Lens radius in metres — the beam's width at the source.")]
        public float StartRadius = 0.05f;

        [Header("Look")]
        [Tooltip("Softness of the beam's outer edge (side falloff).")]
        [Range(0.01f, 1f)] public float EdgeSoftness = 0.35f;

        [Tooltip("Overall volume density.")]
        [Range(0f, 4f)] public float Density = 1f;

        [Tooltip("How strongly the beam fades along its length (0 = no fade, 2 = fades fast).")]
        [Range(0f, 2f)] public float AxialFalloff = 1f;

        [Tooltip("Strength of the bright core inside the beam (hotspot) angle.")]
        [Range(0f, 4f)] public float Hotspot = 1f;

        [Tooltip("Extra brightness boost near the lens (source flare/glare). 0 = off.")]
        [Range(0f, 6f)] public float RootBoost;

        [Tooltip("Length of the root glare as a fraction of Range.")]
        [Range(0.01f, 0.5f)] public float RootBoostFrac = 0.1f;

        [Tooltip("How much of the root glare desaturates toward white vs. tints with Color.")]
        [Range(0f, 1f)] public float RootWhite = 0.6f;

        [Header("Quality")]
        [Tooltip("Raymarch sample count. Higher = smoother gradients, more cost.")]
        [Range(1, 128)] public int RaymarchSteps = 24;

        [Tooltip("Clip the beam against scene depth (so it stops at walls/floors) instead of " +
                 "passing through solid geometry.")]
        public bool DepthOcclude = true;

        [Header("Gobo")]
        [Tooltip("Optional single gobo texture projected through the beam. None = plain cone.")]
        public Texture2D Gobo;

        [Tooltip("Static rotation offset of the gobo, in degrees.")]
        public float GoboRotationDeg;

        [Tooltip("Continuous gobo spin speed, in degrees per second. 0 = static.")]
        public float GoboRotationSpeedDegPerSec;

        /// <summary>Beam colour (scripting access).</summary>
        public Color BeamColor { get => Color; set => Color = value; }

        /// <summary>Overall brightness multiplier (scripting access).</summary>
        public float BeamIntensity { get => Intensity; set => Intensity = value; }

        /// <summary>Maximum throw distance in metres (scripting access).</summary>
        public float BeamRange { get => Range; set => Range = value; }

        /// <summary>Full field (outer) cone angle in degrees (scripting access).</summary>
        public float BeamSpotAngle { get => SpotAngle; set => SpotAngle = value; }

        private static readonly Dictionary<Texture2D, Texture2DArray> GoboArrayCache =
            new Dictionary<Texture2D, Texture2DArray>();
        private static readonly HashSet<Texture2D> GoboWarned = new HashSet<Texture2D>();

        private StageBeamDriver _driver;
        private float _goboRotationRuntimeDeg;

        private void OnEnable()
        {
            _driver = StageBeamDriver.EnsureInstance();
            _driver.AddSource(this);
        }

        private void OnDisable()
        {
            if (_driver != null) _driver.RemoveSource(this);
        }

        private void Update()
        {
            if (GoboRotationSpeedDegPerSec != 0f)
                _goboRotationRuntimeDeg += GoboRotationSpeedDegPerSec * Time.deltaTime;
        }

        /// <inheritdoc />
        public void CollectBeams(List<StageBeamInstance> beams)
        {
            var fieldHalfRad = SpotAngle * 0.5f * Mathf.Deg2Rad;
            var beamAngleClamped = Mathf.Min(BeamAngle, SpotAngle);
            var beamHalfRad = beamAngleClamped * 0.5f * Mathf.Deg2Rad;
            var endRadius = Range * Mathf.Tan(fieldHalfRad) + StartRadius;

            ResolveGobo(out var goboArray, out var goboSlice);

            beams.Add(new StageBeamInstance
            {
                Matrix = BuildMatrix(),
                Color = Color,
                Intensity = Intensity,
                StartRadius = StartRadius,
                EndRadius = endRadius,
                Range = Range,
                EdgeSoftness = EdgeSoftness,
                FieldHalfAngleRad = fieldHalfRad,
                BeamHalfAngleRad = beamHalfRad,
                Density = Density,
                RaymarchSteps = RaymarchSteps,
                DepthOcclude = DepthOcclude ? 1f : 0f,
                AxialFalloff = AxialFalloff,
                Hotspot = Hotspot,
                RootBoost = RootBoost,
                RootBoostFrac = RootBoostFrac,
                RootWhite = RootWhite,
                GoboArray = goboArray,
                GoboSlice = goboSlice,
                GoboRotationRad = (GoboRotationDeg + _goboRotationRuntimeDeg) * Mathf.Deg2Rad,
                GoboArray2 = null,
                GoboSlice2 = -1f,
                GoboRotationRad2 = 0f
            });
        }

        /// <summary>
        /// World matrix for this beam. NegativeY uses the transform as-is (fixture convention).
        /// PositiveZ pre-multiplies a rotation so local -Y maps onto local +Z / forward, so the
        /// beam exits like a Unity spotlight while <see cref="StageBeamInstance"/> keeps its
        /// documented -Y-axis contract.
        /// </summary>
        private Matrix4x4 BuildMatrix()
        {
            if (BeamAxis == Axis.NegativeY) return transform.localToWorldMatrix;

            var rot = Quaternion.FromToRotation(Vector3.down, Vector3.forward);
            return transform.localToWorldMatrix * Matrix4x4.Rotate(rot);
        }

        private void ResolveGobo(out Texture2DArray array, out float slice)
        {
            array = null;
            slice = -1f;
            if (Gobo == null) return;

            if (GoboArrayCache.TryGetValue(Gobo, out var cached))
            {
                if (cached == null) return; // previously failed, stay disabled
                array = cached;
                slice = 0f;
                return;
            }

            try
            {
                var texArray = new Texture2DArray(
                    Gobo.width, Gobo.height, 1, Gobo.format, Gobo.mipmapCount > 1)
                {
                    name = $"StageBeamGobo_{Gobo.name}",
                    wrapMode = Gobo.wrapMode,
                    filterMode = Gobo.filterMode
                };
                Graphics.CopyTexture(Gobo, 0, texArray, 0);
                GoboArrayCache[Gobo] = texArray;
                array = texArray;
                slice = 0f;
            }
            catch (System.Exception e)
            {
                GoboArrayCache[Gobo] = null;
                if (GoboWarned.Add(Gobo))
                    Debug.LogWarning($"[StageBeam] Failed to wrap gobo '{Gobo.name}' into a " +
                                      $"Texture2DArray ({e.Message}). Falling back to no gobo.", Gobo);
            }
        }

        private void OnDrawGizmosSelected()
        {
            var fieldHalfRad = SpotAngle * 0.5f * Mathf.Deg2Rad;
            var reachRadius = Range * Mathf.Tan(fieldHalfRad) + StartRadius;

            var m = BuildMatrix();
            var prevMatrix = Gizmos.matrix;
            var prevColor = Gizmos.color;
            Gizmos.matrix = m;

            var c = Color;
            c.a = 0.5f;
            Gizmos.color = c;

            const int segments = 24;
            Vector3 PointOnCircle(float radius, float y, int i) =>
                new Vector3(Mathf.Cos(i * Mathf.PI * 2f / segments) * radius,
                            y,
                            Mathf.Sin(i * Mathf.PI * 2f / segments) * radius);

            // Lens circle at the origin, reach circle at -Range (beam exits along local -Y).
            for (var i = 0; i < segments; i++)
            {
                var a0 = PointOnCircle(StartRadius, 0f, i);
                var a1 = PointOnCircle(StartRadius, 0f, i + 1);
                Gizmos.DrawLine(a0, a1);

                var b0 = PointOnCircle(reachRadius, -Range, i);
                var b1 = PointOnCircle(reachRadius, -Range, i + 1);
                Gizmos.DrawLine(b0, b1);
            }

            // 4 connecting lines from lens to reach.
            for (var i = 0; i < 4; i++)
            {
                var idx = i * segments / 4;
                var a = PointOnCircle(StartRadius, 0f, idx);
                var b = PointOnCircle(reachRadius, -Range, idx);
                Gizmos.DrawLine(a, b);
            }

            Gizmos.matrix = prevMatrix;
            Gizmos.color = prevColor;
        }
    }
}
