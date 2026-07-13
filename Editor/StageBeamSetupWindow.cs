// =============================================================================
//  StageBeamSetupWindow.cs
// -----------------------------------------------------------------------------
//  Setup EditorWindow for adding / removing / toggling the StageBeamRendererFeature
//  on Universal Renderer Data assets. Renderer datas are collected automatically
//  from the active URP assets (GraphicsSettings default + all quality levels),
//  with a manual ObjectField as a fallback. Warns when Render Graph Compatibility
//  Mode is on (the beam pass is Render Graph only).
// =============================================================================
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace Origuma.StageBeam.Editor
{
    public sealed class StageBeamSetupWindow : EditorWindow
    {
        [MenuItem("Window/Origuma/Stage Beam Setup")]
        public static void Open()
        {
            var window = GetWindow<StageBeamSetupWindow>(false, "Stage Beam Setup");
            window.minSize = new Vector2(420, 240);
            window.Show();
        }

        private ScriptableRendererData _manualRendererData;
        private Vector2 _scroll;

        private void OnGUI()
        {
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Stage Beam Renderer Feature Setup", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Beams are drawn by the Stage Beam Renderer Feature. Add it to the Universal " +
                "Renderer Data your project renders with — without it, beams are simulated but " +
                "nothing is drawn.",
                MessageType.Info);

            if (StageBeamSetup.IsRenderGraphCompatibilityMode())
            {
                EditorGUILayout.HelpBox(
                    "Render Graph Compatibility Mode is enabled. The Stage Beam pass is Render " +
                    "Graph only and will not run. Disable Compatibility Mode under Project " +
                    "Settings > Graphics > Render Graph.",
                    MessageType.Warning);
            }

            EditorGUILayout.Space(4);
            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            var datas = StageBeamSetup.CollectActiveRendererDatas();
            if (datas.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    "No Renderer Data found on the active URP assets. Use the manual field below.",
                    MessageType.Warning);
            }
            else
            {
                EditorGUILayout.LabelField("Active Renderer Data", EditorStyles.boldLabel);
                foreach (var data in datas)
                    DrawRendererDataEntry(data);
            }

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Manual Assignment", EditorStyles.boldLabel);
            _manualRendererData = (ScriptableRendererData)EditorGUILayout.ObjectField(
                "Universal Renderer Data", _manualRendererData, typeof(ScriptableRendererData), false);
            if (_manualRendererData != null && !datas.Contains(_manualRendererData))
                DrawRendererDataEntry(_manualRendererData);

            EditorGUILayout.EndScrollView();
        }

        private static void DrawRendererDataEntry(ScriptableRendererData data)
        {
            if (data == null) return;

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUI.DisabledScope(true))
                    EditorGUILayout.ObjectField(GUIContent.none, data, typeof(ScriptableRendererData), false);

                var existing = StageBeamSetup.FindFeature(data);
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(
                        new GUIContent("Stage Beam", "Volumetric beam raymarch pass"),
                        GUILayout.Width(150));
                    if (existing == null)
                    {
                        EditorGUILayout.LabelField("Not added", GUILayout.Width(110));
                        if (GUILayout.Button("Add", GUILayout.Width(70)))
                            StageBeamSetup.AddFeature(data);
                    }
                    else
                    {
                        EditorGUILayout.LabelField(existing.isActive ? "Added (active)" : "Added (inactive)",
                            GUILayout.Width(110));
                        EditorGUI.BeginChangeCheck();
                        var active = EditorGUILayout.ToggleLeft("Active", existing.isActive, GUILayout.Width(60));
                        if (EditorGUI.EndChangeCheck())
                        {
                            Undo.RecordObject(existing, "Toggle Stage Beam Feature");
                            existing.SetActive(active);
                            EditorUtility.SetDirty(data);
                            AssetDatabase.SaveAssets();
                        }
                        if (GUILayout.Button("Remove", GUILayout.Width(70)))
                            StageBeamSetup.RemoveFeature(data);
                    }
                }
            }
        }
    }
}
