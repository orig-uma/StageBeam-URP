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
        [MenuItem("Tools/Origuma StageLight/Create Stage Beam Load Test")]
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

            // Also drop a volumetric-shadow setup so the load test can exercise shadows at scale.
            test.SpawnOccluders = true;
            var vol = new GameObject("Stage Beam Occlusion Volume").AddComponent<StageBeamOcclusionVolume>();
            vol.Size = new Vector3(24f, 16f, 24f);
            vol.OccluderMask = ~0;
            Undo.RegisterCreatedObjectUndo(vol.gameObject, "Create Occlusion Volume");

            var computeGuid = AssetDatabase.FindAssets("StageBeamOcclusion t:ComputeShader");
            if (computeGuid.Length > 0)
                vol.Occlusion = AssetDatabase.LoadAssetAtPath<ComputeShader>(
                    AssetDatabase.GUIDToAssetPath(computeGuid[0]));

            Selection.activeGameObject = go;
            Debug.Log("[StageBeam] Load test + occlusion volume created. On the active URP Renderer's " +
                      "StageBeamRendererFeature set Shadows = Volume, then press Play. The spawned " +
                      "occluder spheres cast volumetric shadows; toggle 'Half Resolution' to compare GPU time.");
        }
    }
}
#endif
