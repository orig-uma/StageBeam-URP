using UnityEngine;

namespace Origuma.StageBeam
{
    /// <summary>
    /// Scene-view visualization of the volumetric-shadow occluders — whatever the ACTIVE
    /// builder (the renderer feature's automatic one, or a StageBeamOcclusionVolume override)
    /// produced this frame: the volume box (blue), sphere occluders (orange) and box occluders
    /// (orange). Drop it on any GameObject when "why is/isn't this occluding?" needs answering;
    /// it renders nothing at runtime and costs nothing when gizmos are hidden.
    /// </summary>
    [AddComponentMenu("Rendering/Stage Beam Occlusion Debug View")]
    [ExecuteAlways]
    public sealed class StageBeamOcclusionDebugView : MonoBehaviour
    {
        [Tooltip("Also draw when the object is not selected.")]
        public bool AlwaysDraw = true;

        private void OnDrawGizmos() { if (AlwaysDraw) Draw(); }
        private void OnDrawGizmosSelected() { if (!AlwaysDraw) Draw(); }

        private static void Draw()
        {
            var b = StageBeamOcclusionBuilder.ActiveDebug;
            if (b == null) return;

            Gizmos.color = new Color(0.3f, 0.7f, 1f, 0.4f);
            Gizmos.DrawWireCube(b.BoxCenter, b.BoxSize);

            Gizmos.color = new Color(1f, 0.4f, 0.2f, 0.7f);
            for (int i = 0; i < b.SphereCount; i++)
            {
                var s = b.GetSphere(i);
                Gizmos.DrawWireSphere(new Vector3(s.x, s.y, s.z), s.w);
            }

            var prev = Gizmos.matrix;
            for (int i = 0; i < b.BoxCount; i++)
            {
                b.GetBox(i, out var center, out var halfExtents, out var rotation);
                Gizmos.matrix = Matrix4x4.TRS(center, rotation, Vector3.one);
                Gizmos.DrawWireCube(Vector3.zero, halfExtents * 2f);
            }
            Gizmos.matrix = prev;
        }
    }
}
