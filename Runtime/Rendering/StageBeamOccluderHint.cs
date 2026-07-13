using UnityEngine;

namespace Origuma.StageBeam
{
    /// <summary>
    /// Explicit volumetric-shadow shape for everything under this transform, overriding the
    /// automatic per-renderer guessing (bounds boxes / bone spheres). Put ONE on a character
    /// root and every renderer below it — body, hair, clothes, cloth-sim proxies — is
    /// represented by this single, stable, VISIBLE shape instead.
    ///
    /// This is the answer for rigs the automatic path can't read well (e.g. MagicaCloth
    /// characters, whose extra cloth bones/renderers skew bone sampling and inflate bounds):
    /// the occluder becomes exactly the capsule you see in the Scene view — nothing hidden,
    /// nothing frame-dependent.
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
            /// mapping never includes cloth/hair bones, so cloth sims can't skew it. Falls back
            /// to the single Capsule when no humanoid Animator is found below this object.</summary>
            HumanoidCapsules,
        }

        [Tooltip("HumanoidCapsules = articulated character shadow from the Humanoid avatar " +
                 "(best for characters; immune to cloth/hair bones). Capsule = one coarse " +
                 "capsule. Box = set pieces. Ignore = exclude from volumetric shadows.")]
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

        /// <summary>Buffer size callers should use for <see cref="FillHumanoidSegments"/>.</summary>
        public const int MaxSegments = 24;

        private Animator _animator;
        private Transform[] _boneA, _boneB;

        /// <summary>
        /// Fills world-space humanoid segments into <paramref name="dest"/> and returns the
        /// count (0 = no humanoid avatar below this object → caller falls back to the single
        /// capsule). Bone transforms are cached; allocation-free per frame.
        /// </summary>
        public int FillHumanoidSegments(Segment[] dest)
        {
            if (_boneA == null && !CacheHumanoid()) return 0;
            if (_animator == null || !_animator.isActiveAndEnabled) { _boneA = null; return 0; }

            int n = 0;
            for (int i = 0; i < _boneA.Length && n < dest.Length; i++)
            {
                var a = _boneA[i];
                var b = _boneB[i];
                if (a == null || b == null) continue;
                dest[n++] = new Segment
                {
                    A = a.position,
                    B = b.position,
                    Radius = Mathf.Max(0.01f, LimbRadius * HumanoidPairs[i].r),
                };
            }
            return n;
        }

        private bool CacheHumanoid()
        {
            _animator = GetComponentInChildren<Animator>(true);
            if (_animator == null || !_animator.isHuman) { _animator = null; return false; }

            _boneA = new Transform[HumanoidPairs.Length];
            _boneB = new Transform[HumanoidPairs.Length];
            for (int i = 0; i < HumanoidPairs.Length; i++)
            {
                var a = _animator.GetBoneTransform(HumanoidPairs[i].a);
                var b = _animator.GetBoneTransform(HumanoidPairs[i].b);
                // Optional bones (Chest/Neck): bridge over missing links so the torso chain
                // stays connected — fall back to the other end.
                _boneA[i] = a != null ? a : b;
                _boneB[i] = b != null ? b : a;
            }
            return true;
        }

        private void OnValidate() => _boneA = null;   // re-resolve after edits

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
