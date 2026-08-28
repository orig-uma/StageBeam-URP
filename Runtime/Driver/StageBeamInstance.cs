using UnityEngine;

namespace Origuma.StageBeam
{
    /// <summary>
    /// One beam's render parameters in renderer-neutral form: a world transform plus every value
    /// the <c>Origuma/StageBeamCone</c> shader needs. Produced by an <see cref="IStageBeamSource"/>
    /// and consumed by <see cref="StageBeamDriver"/>.
    ///
    /// This is the entire contract between a beam data source (e.g. a data-driven lighting rig) and the
    /// renderer. It contains only UnityEngine types, so the StageBeam package never needs to know
    /// what produced the beams.
    /// </summary>
    public struct StageBeamInstance
    {
        /// <summary>World-space transform of the beam (lens at the origin, axis down local -Y).</summary>
        public Matrix4x4 Matrix;

        public Color Color;
        public float  Intensity;

        public float StartRadius;
        public float EndRadius;
        public float Range;
        public float EdgeSoftness;
        public float FieldHalfAngleRad;
        public float BeamHalfAngleRad;

        public float Density;

        /// <summary>Henyey-Greenstein scattering anisotropy g in [-0.9, 0.9]. 0 = isotropic;
        /// 0.5–0.7 = forward scattering (beams aimed at the camera flare up like real haze).</summary>
        public float Anisotropy;

        public float RaymarchSteps;
        /// <summary>1 = occlude against scene depth in the raymarch, 0 = off.</summary>
        public float DepthOcclude;

        public float AxialFalloff;
        public float Hotspot;
        public float RootBoost;
        public float RootBoostFrac;
        public float RootWhite;

        /// <summary>First gobo wheel array (null = none). Slice &lt; 0 disables sampling.</summary>
        public Texture2DArray GoboArray;
        public float          GoboSlice;
        public float          GoboRotationRad;

        /// <summary>Second gobo wheel array (null = none). Slice &lt; 0 disables sampling.</summary>
        public Texture2DArray GoboArray2;
        public float          GoboSlice2;
        public float          GoboRotationRad2;

        /// <summary>UV offset of gobo wheel 1 (drives animation-wheel scroll effects). Applied
        /// after the gobo's own rotation, before the cone edge masking.</summary>
        public Vector2 GoboOffset;

        /// <summary>The real (URP) Light co-located with this fixture, if any. With the
        /// renderer feature's Shadows = LightShadowMap, the beam's raymarch samples this
        /// light's URP shadow map — geometry-exact volumetric shadows. Null = beam renders
        /// unshadowed in that mode.</summary>
        public Light ShadowLight;
    }
}
