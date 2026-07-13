// =============================================================================
//  StageBeamSetup.cs
// -----------------------------------------------------------------------------
//  ScriptableRendererFeature setup logic for the Stage Beam Setup window:
//   - collect renderer datas from the active URP assets (GraphicsSettings default
//     + every QualitySettings level)
//   - find / add (sub-asset + m_RendererFeatureMap sync + Undo) / remove the
//     StageBeamRendererFeature on a renderer data
//   - Render Graph Compatibility Mode detection (the beam pass is RG-only)
//  Self-contained on purpose: stage-beam has no dependency other than URP, so
//  this mirrors the FeatureSetup utility pattern instead of referencing it.
// =============================================================================
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Origuma.StageBeam.Editor
{
    public static class StageBeamSetup
    {
        // ------------------------------------------------------------------
        //  Active URP renderer data collection (default + all quality levels)
        // ------------------------------------------------------------------
        public static List<ScriptableRendererData> CollectActiveRendererDatas()
        {
            var result = new List<ScriptableRendererData>();
            var assets = new List<UniversalRenderPipelineAsset>();

            if (GraphicsSettings.defaultRenderPipeline is UniversalRenderPipelineAsset defaultAsset)
                assets.Add(defaultAsset);

            for (int i = 0; i < QualitySettings.count; i++)
                if (QualitySettings.GetRenderPipelineAssetAt(i) is UniversalRenderPipelineAsset qualityAsset
                    && !assets.Contains(qualityAsset))
                    assets.Add(qualityAsset);

            foreach (var asset in assets)
                foreach (var data in asset.rendererDataList)
                    if (data != null && !result.Contains(data))
                        result.Add(data);

            return result;
        }

        /// <summary>Whether any active renderer data already has the Stage Beam feature
        /// (used by the StageBeamLight inspector guard).</summary>
        public static bool HasRendererFeature(out bool detectionWorked)
        {
            var datas = CollectActiveRendererDatas();
            detectionWorked = datas.Count > 0;
            foreach (var data in datas)
                if (FindFeature(data) != null)
                    return true;
            return false;
        }

        /// <summary>The beam pass is written against Render Graph only, so it won't run when
        /// Compatibility Mode is on. Used to warn in the setup window. Unity 6000.4 removed
        /// Compatibility Mode entirely (RenderGraphSettings is obsolete/unused there), so on
        /// 6.4+ this is always false.</summary>
        public static bool IsRenderGraphCompatibilityMode()
        {
#if UNITY_6000_4_OR_NEWER
            return false;
#else
            return GraphicsSettings.TryGetRenderPipelineSettings<RenderGraphSettings>(out var rgSettings)
                   && rgSettings.enableRenderCompatibilityMode;
#endif
        }

        // ------------------------------------------------------------------
        //  Find / add / remove on one renderer data
        // ------------------------------------------------------------------
        public static StageBeamRendererFeature FindFeature(ScriptableRendererData data)
        {
            if (data == null || data.rendererFeatures == null) return null;
            foreach (var f in data.rendererFeatures)
                if (f is StageBeamRendererFeature found)
                    return found;
            return null;
        }

        public const string FeatureName = "Stage Beam Renderer Feature";

        // Mirrors URP's own "Add Renderer Feature" button: the feature becomes a sub-asset of
        // the renderer data, referenced from m_RendererFeatures with its local file id recorded
        // in m_RendererFeatureMap (URP uses the map to repair lost references).
        public static void AddFeature(ScriptableRendererData data)
        {
            if (data == null || FindFeature(data) != null) return;

            var feature = ScriptableObject.CreateInstance<StageBeamRendererFeature>();
            feature.name = FeatureName;

            Undo.RegisterCreatedObjectUndo(feature, $"Add {FeatureName}");
            if (EditorUtility.IsPersistent(data))
                AssetDatabase.AddObjectToAsset(feature, data);
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(feature, out _, out long localId);

            var so = new SerializedObject(data);
            so.Update();
            var listProp = so.FindProperty("m_RendererFeatures");
            var mapProp  = so.FindProperty("m_RendererFeatureMap");

            int idx = listProp.arraySize;
            listProp.arraySize = idx + 1;
            listProp.GetArrayElementAtIndex(idx).objectReferenceValue = feature;
            if (mapProp != null)
            {
                mapProp.arraySize = idx + 1;
                mapProp.GetArrayElementAtIndex(idx).longValue = localId;
            }

            so.ApplyModifiedProperties();
            EditorUtility.SetDirty(data);
            AssetDatabase.SaveAssets();

            Debug.Log($"[StageBeam] Added {FeatureName}: {AssetDatabase.GetAssetPath(data)}");
        }

        public static void RemoveFeature(ScriptableRendererData data)
        {
            if (data == null) return;

            var so = new SerializedObject(data);
            so.Update();
            var listProp = so.FindProperty("m_RendererFeatures");
            var mapProp  = so.FindProperty("m_RendererFeatureMap");

            var toDestroy = new List<Object>();
            for (int i = listProp.arraySize - 1; i >= 0; i--)
            {
                var obj = listProp.GetArrayElementAtIndex(i).objectReferenceValue;
                if (obj is StageBeamRendererFeature)
                {
                    listProp.GetArrayElementAtIndex(i).objectReferenceValue = null;
                    listProp.DeleteArrayElementAtIndex(i);
                    if (mapProp != null && i < mapProp.arraySize) mapProp.DeleteArrayElementAtIndex(i);
                    toDestroy.Add(obj);
                }
            }

            so.ApplyModifiedProperties();
            foreach (var feat in toDestroy)
                Undo.DestroyObjectImmediate(feat);

            EditorUtility.SetDirty(data);
            AssetDatabase.SaveAssets();

            Debug.Log($"[StageBeam] Removed {FeatureName}: {AssetDatabase.GetAssetPath(data)}");
        }
    }
}
