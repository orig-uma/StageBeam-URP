using UnityEditor;
using UnityEngine;

namespace Origuma.StageBeam.Editor
{
    /// <summary>GameObject creation menu for a ready-to-go <see cref="StageBeamLight"/>.
    /// Lives in its own "Stage Beam" submenu (down with the other custom entries, not
    /// squatting inside Unity's built-in Light menu).</summary>
    public static class StageBeamMenu
    {
        [MenuItem("GameObject/Stage Beam/Stage Beam Light", false, 2050)]
        private static void CreateStageBeamLight(MenuCommand menuCommand)
        {
            var go = new GameObject("Stage Beam Light", typeof(StageBeamLight));
            GameObjectUtility.SetParentAndAlign(go, menuCommand.context as GameObject);
            Undo.RegisterCreatedObjectUndo(go, "Create Stage Beam Light");
            Selection.activeObject = go;
        }

        // Optional: volumetric shadows work with zero scene setup (Shadows = Volume on the
        // renderer feature). This override object is only for manual box/resolution control.
        // Drop on any GameObject to SEE the shadow occluders (auto or override path) in the
        // Scene view — the "why is/isn't this occluding?" answer.
        [MenuItem("GameObject/Stage Beam/Occlusion Debug View", false, 2052)]
        private static void CreateOcclusionDebugView(MenuCommand menuCommand)
        {
            var go = new GameObject("Stage Beam Occlusion Debug", typeof(StageBeamOcclusionDebugView));
            GameObjectUtility.SetParentAndAlign(go, menuCommand.context as GameObject);
            Undo.RegisterCreatedObjectUndo(go, "Create Stage Beam Occlusion Debug View");
            Selection.activeObject = go;
        }

        [MenuItem("GameObject/Stage Beam/Occlusion Volume (Override)", false, 2051)]
        private static void CreateOcclusionVolume(MenuCommand menuCommand)
        {
            var go = new GameObject("Stage Beam Occlusion Volume", typeof(StageBeamOcclusionVolume));
            GameObjectUtility.SetParentAndAlign(go, menuCommand.context as GameObject);
            Undo.RegisterCreatedObjectUndo(go, "Create Stage Beam Occlusion Volume");
            Selection.activeObject = go;
        }
    }
}
