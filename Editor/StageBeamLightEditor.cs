using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Origuma.StageBeam.Editor
{
    /// <summary>
    /// Inspector for <see cref="StageBeamLight"/>. Adds a setup reminder above the default
    /// inspector: warns when the active URP renderer doesn't appear to have
    /// <see cref="StageBeamRendererFeature"/> added, since without it the beam is invisible.
    /// </summary>
    [CustomEditor(typeof(StageBeamLight))]
    [CanEditMultipleObjects]
    public sealed class StageBeamLightEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawSetupHelp();
            EditorGUILayout.Space();
            DrawDefaultInspector();
        }

        private static void DrawSetupHelp()
        {
            EditorGUILayout.HelpBox(
                "Setup: add a Stage Beam Renderer Feature to your URP Renderer asset " +
                "(URP Asset > Renderer List > your renderer > Add Renderer Feature). " +
                "A Stage Beam Driver is created automatically the first time a Stage Beam " +
                "Light is enabled — no manual setup needed for that part.",
                MessageType.Info);

            if (!TryHasStageBeamRendererFeature(out var hasFeature)) return;
            if (!hasFeature)
            {
                EditorGUILayout.HelpBox(
                    "No Stage Beam Renderer Feature was found on the active URP Renderer. " +
                    "The beam will not be visible until one is added.",
                    MessageType.Warning);
            }
        }

        /// <summary>
        /// Attempts to determine whether the active URP renderer has a
        /// <see cref="StageBeamRendererFeature"/>. Returns false (detection inconclusive) rather
        /// than throwing if any part of the URP asset/renderer-data API is unavailable.
        /// </summary>
        private static bool TryHasStageBeamRendererFeature(out bool hasFeature)
        {
            hasFeature = false;
            try
            {
                var urpAsset = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
                if (urpAsset == null) return false;

                // ScriptableRendererData[] is private on UniversalRenderPipelineAsset; reach it
                // via SerializedObject instead of reflection so it works across URP versions.
                var so = new SerializedObject(urpAsset);
                var rendererDataListProp = so.FindProperty("m_RendererDataList");
                if (rendererDataListProp == null || !rendererDataListProp.isArray) return false;

                for (var i = 0; i < rendererDataListProp.arraySize; i++)
                {
                    var rendererData =
                        rendererDataListProp.GetArrayElementAtIndex(i).objectReferenceValue as
                            ScriptableRendererData;
                    if (rendererData == null) continue;

                    var features = rendererData.rendererFeatures;
                    if (features == null) continue;

                    foreach (var feature in features)
                    {
                        if (feature is StageBeamRendererFeature)
                        {
                            hasFeature = true;
                            return true;
                        }
                    }
                }

                return true; // detection worked, feature genuinely absent
            }
            catch
            {
                return false;
            }
        }
    }
}
