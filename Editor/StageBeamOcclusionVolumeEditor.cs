using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Origuma.StageBeam.Editor
{
    /// <summary>
    /// Custom inspector for <see cref="StageBeamOcclusionVolume"/>. Makes the override model
    /// obvious: shows a mode-dependent notice and, in "Use Renderer Feature Settings" mode, DISABLES
    /// the fields the feature drives (so it's clear those come from the Renderer Feature, not here).
    /// </summary>
    [CustomEditor(typeof(StageBeamOcclusionVolume))]
    public sealed class StageBeamOcclusionVolumeEditor : UnityEditor.Editor
    {
        // The shadow-behaviour fields the feature overwrites in "Use Renderer Feature Settings"
        // mode (see StageBeamRendererFeature.ApplyFeatureOcclusionSettings). Grayed out in that mode.
        private static readonly HashSet<string> FeatureDriven = new HashSet<string>
        {
            "OccluderMask", "MeshVoxelize", "VoxelizeAxisCount", "StaticDynamicSplit",
            "Strength", "ShadowDensity", "ShadowSteps", "MaxShadowDistance", "TemporalSmoothing",
        };

        public override void OnInspectorGUI()
        {
            var vol = (StageBeamOcclusionVolume)target;
            bool useFeature = vol.Mode == StageBeamOcclusionVolume.SettingsMode.UseRendererFeatureSettings;

            if (useFeature)
                EditorGUILayout.HelpBox(
                    "Use Renderer Feature Settings (default): this component only pins the volume " +
                    "box/region (and advanced knobs). The greyed-out fields below come from the " +
                    "Renderer Feature — edit occlusion behaviour there.",
                    MessageType.None);
            else
                EditorGUILayout.HelpBox(
                    "Override mode: this component owns ALL occlusion settings — the Renderer " +
                    "Feature's occlusion fields are ignored while it's present.",
                    MessageType.Info);
            EditorGUILayout.Space();

            serializedObject.Update();
            var prop = serializedObject.GetIterator();
            bool enterChildren = true;
            while (prop.NextVisible(enterChildren))
            {
                enterChildren = false;
                if (prop.name == "m_Script")
                {
                    using (new EditorGUI.DisabledScope(true))
                        EditorGUILayout.PropertyField(prop);
                    continue;
                }
                bool disable = useFeature && FeatureDriven.Contains(prop.name);
                using (new EditorGUI.DisabledScope(disable))
                    EditorGUILayout.PropertyField(prop, true);
            }
            serializedObject.ApplyModifiedProperties();
        }
    }
}
