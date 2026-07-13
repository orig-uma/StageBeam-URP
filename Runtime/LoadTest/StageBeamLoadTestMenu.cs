#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace Origuma.StageBeam
{
    /// <summary>
    /// One-click setup for the stage-beam load test: drops a configured
    /// <see cref="StageBeamLoadTest"/> into the open scene and frames a camera on it.
    /// </summary>
    internal static class StageBeamLoadTestMenu
    {
        [MenuItem("Tools/Origuma/Stage Beam Load Test")]
        private static void Create()
        {
            var go = new GameObject("StageBeam LoadTest");
            var test = go.AddComponent<StageBeamLoadTest>();
            Undo.RegisterCreatedObjectUndo(go, "Create Stage Beam Load Test");

            if (Camera.main == null)
            {
                var camGo = new GameObject("Main Camera");
                camGo.tag = "MainCamera";
                var cam = camGo.AddComponent<Camera>();
                cam.transform.position = new Vector3(0f, 2f, -20f);
                cam.transform.LookAt(Vector3.zero);
                Undo.RegisterCreatedObjectUndo(camGo, "Create Camera");
            }

            // Volumetric shadows need no scene object: set Shadows = Volume on the renderer
            // feature and occlusion is built automatically from the spawned occluders.
            test.SpawnOccluders = true;

            Selection.activeGameObject = go;
            Debug.Log("[StageBeam] Load test created. On the active URP Renderer's " +
                      "StageBeamRendererFeature set Shadows = Volume, then press Play — occlusion " +
                      "is automatic (the spawned occluder spheres cast volumetric shadows). " +
                      "Toggle 'Half Resolution' to compare GPU time.");
        }
    }
}
#endif
