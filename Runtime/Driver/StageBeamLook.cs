using System;
using UnityEngine;

namespace Origuma.StageBeam
{
    /// <summary>
    /// How a beam READS, as opposed to what it is aimed at or how bright it is told to be.
    ///
    /// It exists because these same nine values had grown up twice — once on
    /// <see cref="StageBeamLight"/> and once on the rig's beam source — with drifting names
    /// (RootBoostFrac vs RootBoostLength, RootWhite vs RootBoostWhite, DepthOcclude vs
    /// SceneDepthOcclusion) and no way to tell which one an inspector field belonged to. Worse, one
    /// of them, Anisotropy, existed on only one of the two, so rigs could never reach the cheapest
    /// realism control there is. One definition means adding the next dial is one edit, and a value
    /// cannot exist on one path and quietly not the other.
    ///
    /// WHAT BELONGS HERE is decided by who varies it. These are the values a designer wants to
    /// differ BETWEEN FIXTURES — a wash and a beam fixture scatter differently, and often it is the
    /// whole fixture TYPE that wants its own settings. Values describing the room rather than a
    /// fixture (haze noise, master balance) are not per-fixture. Values describing the renderer
    /// (resolution, dither, culling) live on the renderer feature, which is an ASSET and therefore
    /// cannot be animated from Timeline at all — which is exactly why nothing a show might move
    /// during a cue should end up there.
    ///
    /// EdgeSoftness is deliberately absent. For GDTF fixtures it is derived from the beam type plus
    /// the live Frost attribute, so it is not something anyone authors; a hand-placed light authors
    /// it directly. Including it would create a field that silently loses to derivation on one of
    /// the two paths, which is the very confusion this struct exists to end.
    /// </summary>
    [Serializable]
    public struct StageBeamLook
    {
        [Tooltip("How thick the haze inside this beam looks. Raise for a heavier, smokier beam.")]
        [Range(0f, 4f)] public float Density;

        [Tooltip("Scattering direction (Henyey-Greenstein g). 0 = same brightness from every " +
                 "viewing angle. 0.5-0.7 = forward scattering: beams aimed toward the camera " +
                 "flare, beams aimed away sit back. Negative scatters backward. Raising this dims " +
                 "the ordinary side-on view, so raise Master Intensity to match.")]
        [Range(-0.9f, 0.9f)] public float Anisotropy;

        [Tooltip("How steeply the beam dims along its throw. 0 = even along its whole length; " +
                 "with the physical model on (renderer feature), 2 = true inverse-square. The " +
                 "fade length follows each beam's own zoom — narrow beams carry far, wide " +
                 "washes die near the fixture — and the source end never changes, only the tail.")]
        [Range(0f, 5f)] public float AxialFalloff;

        [Tooltip("How much brighter the core is than the outer field. 0 = flat across the beam.")]
        [Range(0f, 4f)] public float Hotspot;

        // One dial where there were three (strength / reach / white bias). This is the ONLY
        // source of root glare — the physical falloff deliberately leaves the source end
        // untouched (it is anchored to 1 at the lens) — and a garnish does not earn three
        // dials: reach stays at 0.1 of Range and the white blow-out at 0.6, the values every
        // tuned look had converged on anyway. The wire format (StageBeamInstance / GpuBeam)
        // still carries all three, so bringing a dial back is an edit here, not a layout change.
        [Tooltip("Extra white-hot glare right at the lens, for the look of a blinding source. " +
                 "0 = off.")]
        [UnityEngine.Serialization.FormerlySerializedAs("RootBoost")]
        [Range(0f, 6f)] public float RootGlare;

        // The gobo is prefiltered to the step size, so step count sets shaft sharpness as much as
        // gradient smoothness. Cost is (covered pixels) x (overlapping beams) x steps and the pixel
        // term usually dominates, which is why steps are the cheaper dial to spend on.
        [Tooltip("Raymarch sample count. Raise for smoother gradients and crisper gobo shafts. " +
                 "Try this before lowering Resolution Scale — it is the cheaper of the two. " +
                 "Measure on a full rig at a pinned resolution; one beam in a small Game view " +
                 "makes any step count look free.")]
        [Range(1, 128)] public int RaymarchSteps;

        [Tooltip("Stop the beam at walls, floors and performers instead of passing through them. " +
                 "Needs URP Depth Texture on.")]
        public bool DepthOcclude;

        /// <summary>
        /// Neutral starting point. Never use <c>default</c> for a look: a zeroed struct is a beam
        /// with no density and no samples, i.e. invisible, which reads as a broken renderer rather
        /// than as an unset field.
        ///
        /// Anisotropy starts at 0.6, forward-scattering, because that is what haze does and
        /// isotropic beams are the flatter-looking outlier. It had defaulted to 0 only because the
        /// control was never wired into the rig path, so nobody had seen it on.
        ///
        /// Turning it on DIMS the ordinary view. The phase function is 1.0 in every direction at
        /// g = 0, but at g = 0.6 a beam seen side-on scatters (1-g²)/(1+g²)^1.5 = 0.40 of that,
        /// while only near head-on does it gain. Master Intensity's default is raised by the
        /// reciprocal, 2.48x, so a side-on beam sits where it always did and the head-on flare is
        /// the part that changes.
        /// </summary>
        public static StageBeamLook Default => new StageBeamLook
        {
            Density       = 1f,
            Anisotropy    = 0.6f,
            AxialFalloff  = 1f,
            Hotspot       = 1f,
            RootGlare     = 0f,
            RaymarchSteps = 48,
            DepthOcclude  = true,
        };

        /// <summary>Stamps these values onto a beam instance, leaving aim, colour and angles alone.</summary>
        public void ApplyTo(ref StageBeamInstance b)
        {
            b.Density       = Density;
            b.Anisotropy    = Anisotropy;
            b.AxialFalloff  = AxialFalloff;
            b.Hotspot       = Hotspot;
            b.RootBoost     = RootGlare;
            b.RootBoostFrac = 0.1f;   // reach and white bias fixed — see RootGlare's comment
            b.RootWhite     = 0.6f;
            b.RaymarchSteps = RaymarchSteps;
            b.DepthOcclude  = DepthOcclude ? 1f : 0f;
        }
    }
}
