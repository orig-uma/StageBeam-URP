using UnityEngine;

namespace Origuma.StageBeam
{
    /// <summary>
    /// Explicit volumetric-shadow shape for everything under this transform, overriding the
    /// automatic per-renderer guessing (bounds boxes / bone spheres) AND taking those renderers
    /// out of mesh voxelization. Put one on a character root — or on a whole cast's root, since
    /// HumanoidCapsules covers every humanoid below it — and body, hair, clothes and cloth-sim
    /// proxies are all represented by the authored shapes instead.
    ///
    /// This is the answer for rigs the automatic path can't read well (e.g. MagicaCloth
    /// characters, whose extra cloth bones/renderers skew bone sampling and inflate bounds):
    /// the occluder becomes exactly the capsule you see in the Scene view — nothing hidden,
    /// nothing frame-dependent.
    ///
    /// It is also the cost lever. Mesh voxelization spends a draw call per renderer per axis, so a
    /// cast of dancers is hundreds of them; a hint replaces that with a compute splat that costs
    /// none. The trade is silhouette accuracy — capsules instead of the real deforming mesh.
    /// Shapes are budgeted by the builder's MaxOccluders (an articulated humanoid is ~17).
    /// </summary>
    [AddComponentMenu("Rendering/Stage Beam Occluder Hint")]
    [DisallowMultipleComponent]
    public sealed class StageBeamOccluderHint : MonoBehaviour
    {
        public enum OccluderShape
        {
            /// <summary>A vertical capsule in this transform's local space — coarse but fully
            /// predictable.</summary>
            Capsule,
            /// <summary>An oriented box in this transform's local space.</summary>
            Box,
            /// <summary>Everything under this transform casts no volumetric shadow.</summary>
            Ignore,
            /// <summary>Capsules along the HUMANOID skeleton (torso, head, arms, legs) — the
            /// articulated option for characters: follows animation exactly, and the Humanoid
            /// mapping never includes cloth/hair bones, so cloth sims can't skew it. Covers EVERY
            /// humanoid below this object, so one hint can represent a whole cast. Falls back to
            /// the single Capsule when no humanoid Animator is found below it.</summary>
            HumanoidCapsules,
        }

        [Tooltip("HumanoidCapsules = articulated character shadow from the Humanoid avatar " +
                 "(best for characters; immune to cloth/hair bones; covers every humanoid below " +
                 "this object). Capsule = one coarse capsule. Box = set pieces. " +
                 "Ignore = exclude from volumetric shadows.")]
        public OccluderShape Shape = OccluderShape.HumanoidCapsules;

        [Tooltip("HumanoidCapsules: base limb radius in metres (arms). Torso/legs/head use " +
                 "proportional multiples of this.")]
        public float LimbRadius = 0.09f;

        [Tooltip("Local-space centre offset. For a character root at the feet, " +
                 "y = half the height puts the capsule on the body.")]
        public Vector3 Center = new Vector3(0f, 0.85f, 0f);

        [Tooltip("Capsule: total height along local Y (metres).")]
        public float Height = 1.7f;
        [Tooltip("Capsule: radius (metres).")]
        public float Radius = 0.3f;

        [Tooltip("Box: local size (metres).")]
        public Vector3 BoxSize = new Vector3(1f, 1f, 1f);

        /// <summary>One world-space occluder segment (sphere-swept line) for the builder.</summary>
        public struct Segment
        {
            public Vector3 A, B;
            public float Radius;
        }

        // Humanoid bone-pair table: consecutive pairs form the torso chain; limbs are explicit.
        // Radii are multiples of LimbRadius.
        private static readonly (HumanBodyBones a, HumanBodyBones b, float r)[] HumanoidPairs =
        {
            (HumanBodyBones.Hips,          HumanBodyBones.Spine,         1.9f),
            (HumanBodyBones.Spine,         HumanBodyBones.Chest,         1.9f),
            (HumanBodyBones.Chest,         HumanBodyBones.Neck,          1.7f),
            (HumanBodyBones.Neck,          HumanBodyBones.Head,          1.1f),
            (HumanBodyBones.Head,          HumanBodyBones.Head,          1.4f),   // head blob
            (HumanBodyBones.LeftUpperArm,  HumanBodyBones.LeftLowerArm,  1.0f),
            (HumanBodyBones.LeftLowerArm,  HumanBodyBones.LeftHand,      0.9f),
            (HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, 1.0f),
            (HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand,     0.9f),
            (HumanBodyBones.LeftUpperLeg,  HumanBodyBones.LeftLowerLeg,  1.4f),
            (HumanBodyBones.LeftLowerLeg,  HumanBodyBones.LeftFoot,      1.1f),
            (HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg, 1.4f),
            (HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot,     1.1f),
            // Connectors: fill the armpit (chest→shoulder) and crotch (hips→upper leg)
            // regions, so no false light slivers leak between limbs and torso when the light
            // is overhead — the gaps that isolated per-limb capsules leave open.
            (HumanBodyBones.Chest,         HumanBodyBones.LeftUpperArm,  1.3f),
            (HumanBodyBones.Chest,         HumanBodyBones.RightUpperArm, 1.3f),
            (HumanBodyBones.Hips,          HumanBodyBones.LeftUpperLeg,  1.5f),
            (HumanBodyBones.Hips,          HumanBodyBones.RightUpperLeg, 1.5f),
        };

        /// <summary>Segments ONE humanoid contributes. Callers should size their buffer by
        /// <see cref="HumanoidSegmentCapacity"/>, which accounts for every character below.</summary>
        public const int MaxSegments = 24;

        /// <summary>
        /// Buffer size <see cref="FillHumanoidSegments"/> needs to represent EVERY humanoid below
        /// this hint. One hint on a cast of dancers is the natural way to author it, so the count
        /// scales with the characters found — a fixed 24 would silently drop all but the first.
        /// </summary>
        public int HumanoidSegmentCapacity
        {
            get
            {
                if (_animators == null) CacheHumanoid();
                return Mathf.Max(MaxSegments,
                    (_animators != null ? _animators.Length : 0) * HumanoidPairs.Length);
            }
        }

        // Every humanoid below this hint, not just the first. A hint dropped on a group root used
        // to represent only one character while EXCLUDING the whole group from mesh voxelization,
        // so the rest of the cast silently stopped casting any shadow at all.
        private Animator[] _animators;
        private Transform[] _boneA, _boneB;   // flattened: [animator * pairs + pair]

        /// <summary>
        /// Fills world-space humanoid segments into <paramref name="dest"/> and returns the
        /// count (0 = no humanoid avatar below this object → caller falls back to the single
        /// capsule). Bone transforms are cached; allocation-free per frame.
        /// </summary>
        public int FillHumanoidSegments(Segment[] dest)
        {
            if (_boneA == null && !CacheHumanoid()) return 0;

            int pairs = HumanoidPairs.Length;
            int n = 0;
            for (int a = 0; a < _animators.Length && n < dest.Length; a++)
            {
                var anim = _animators[a];
                if (anim == null) { _boneA = null; return 0; }   // destroyed → re-cache next build
                if (!anim.isActiveAndEnabled) continue;          // hidden character casts nothing

                for (int i = 0; i < pairs && n < dest.Length; i++)
                {
                    var ba = _boneA[a * pairs + i];
                    var bb = _boneB[a * pairs + i];
                    if (ba == null || bb == null) continue;
                    dest[n++] = new Segment
                    {
                        A = ba.position,
                        B = bb.position,
                        Radius = Mathf.Max(0.01f, LimbRadius * HumanoidPairs[i].r),
                    };
                }
            }
            return n;
        }

        private bool CacheHumanoid()
        {
            var found = GetComponentsInChildren<Animator>(true);
            int humans = 0;
            for (int i = 0; i < found.Length; i++)
                if (found[i] != null && found[i].isHuman) humans++;
            if (humans == 0) { _animators = null; return false; }

            _animators = new Animator[humans];
            for (int i = 0, w = 0; i < found.Length; i++)
                if (found[i] != null && found[i].isHuman) _animators[w++] = found[i];

            int pairs = HumanoidPairs.Length;
            _boneA = new Transform[humans * pairs];
            _boneB = new Transform[humans * pairs];
            for (int a = 0; a < humans; a++)
            {
                for (int i = 0; i < pairs; i++)
                {
                    var ba = _animators[a].GetBoneTransform(HumanoidPairs[i].a);
                    var bb = _animators[a].GetBoneTransform(HumanoidPairs[i].b);
                    // Optional bones (Chest/Neck): bridge over missing links so the torso chain
                    // stays connected — fall back to the other end.
                    _boneA[a * pairs + i] = ba != null ? ba : bb;
                    _boneB[a * pairs + i] = bb != null ? bb : ba;
                }
            }
            return true;
        }

        /// <summary>Number of humanoid characters this hint represents (0 = falls back to the
        /// single authored capsule). Surfaced so the inspector can say so out loud.</summary>
        public int HumanoidCount
        {
            get
            {
                if (_animators == null) CacheHumanoid();
                return _animators != null ? _animators.Length : 0;
            }
        }

        private void OnValidate() { _boneA = null; _animators = null; }   // re-resolve after edits

        // Emitted world-space capsule endpoints/radius for the builder (scale folded in).
        public void GetWorldCapsule(out Vector3 p0, out Vector3 p1, out float radius)
        {
            var t = transform;
            var ls = t.lossyScale;
            float yScale  = Mathf.Abs(ls.y);
            float rScale  = Mathf.Max(Mathf.Abs(ls.x), Mathf.Abs(ls.z));
            radius = Radius * rScale;
            float half = Mathf.Max(Height * 0.5f * yScale - radius, 0f);
            var c = t.TransformPoint(Center);
            var up = t.up;
            p0 = c - up * half;
            p1 = c + up * half;
        }

        private void OnDrawGizmosSelected() => DrawGizmo(new Color(1f, 0.55f, 0.1f, 0.9f));
        private void OnDrawGizmos()         => DrawGizmo(new Color(1f, 0.55f, 0.1f, 0.35f));

        private static readonly Segment[] GizmoSegments = new Segment[MaxSegments];

        private void DrawGizmo(Color color)
        {
            if (Shape == OccluderShape.Ignore) return;
            Gizmos.color = color;
            if (Shape == OccluderShape.Box)
            {
                var prev = Gizmos.matrix;
                Gizmos.matrix = Matrix4x4.TRS(transform.TransformPoint(Center),
                    transform.rotation, transform.lossyScale);
                Gizmos.DrawWireCube(Vector3.zero, BoxSize);
                Gizmos.matrix = prev;
                return;
            }

            if (Shape == OccluderShape.HumanoidCapsules)
            {
                int n = FillHumanoidSegments(GizmoSegments);
                for (int i = 0; i < n; i++)
                {
                    var s = GizmoSegments[i];
                    Gizmos.DrawWireSphere(s.A, s.Radius);
                    Gizmos.DrawWireSphere(s.B, s.Radius);
                    Gizmos.DrawLine(s.A, s.B);
                }
                if (n > 0) return;
                // No humanoid avatar: fall through and show the fallback capsule instead.
            }

            GetWorldCapsule(out var p0, out var p1, out var r);
            Gizmos.DrawWireSphere(p0, r);
            Gizmos.DrawWireSphere(p1, r);
            var mid = (p0 + p1) * 0.5f;
            Gizmos.DrawWireSphere(mid, r);
        }
    }
}
