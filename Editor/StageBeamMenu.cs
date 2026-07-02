using UnityEditor;
using UnityEngine;

namespace Origuma.StageBeam.Editor
{
    /// <summary>GameObject creation menu for a ready-to-go <see cref="StageBeamLight"/>.</summary>
    public static class StageBeamMenu
    {
        [MenuItem("GameObject/Light/Stage Beam Light", false, 10)]
        private static void CreateStageBeamLight(MenuCommand menuCommand)
        {
            var go = new GameObject("Stage Beam Light", typeof(StageBeamLight));
            GameObjectUtility.SetParentAndAlign(go, menuCommand.context as GameObject);
            Undo.RegisterCreatedObjectUndo(go, "Create Stage Beam Light");
            Selection.activeObject = go;
        }
    }
}
