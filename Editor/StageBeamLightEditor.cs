using UnityEditor;
using UnityEngine;

namespace Origuma.StageBeam.Editor
{
    /// <summary>
    /// Inspector for <see cref="StageBeamLight"/>. Adds a setup reminder above the default
    /// inspector: warns when the active URP renderer doesn't appear to have
    /// <see cref="StageBeamRendererFeature"/> added (without it the beam is invisible), with a
    /// button that opens the <see cref="StageBeamSetupWindow"/>.
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
            if (StageBeamSetup.HasRendererFeature(out var detectionWorked) || !detectionWorked)
                return;

            EditorGUILayout.HelpBox(
                "No Stage Beam Renderer Feature was found on the active URP Renderer. " +
                "The beam will not be visible until one is added. " +
                "(A Stage Beam Driver is created automatically at runtime — no setup needed there.)",
                MessageType.Warning);

            if (GUILayout.Button("Open Stage Beam Setup"))
                StageBeamSetupWindow.Open();
        }
    }
}
