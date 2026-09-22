using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SplatLOD.Editor
{
    public sealed class Stage2BuilderWindow : EditorWindow
    {
        [SerializeField] string inputManifest = "SplatData/Stage2/inputs/accepted-20260922-03/stage2_input.json";
        [SerializeField] string chunkManifest = "SplatData/Stage2/layouts/octree-20260922-03/chunks.json";
        string message = "";

        [MenuItem("Tools/Splat LOD Builder")]
        public static void Open() => GetWindow<Stage2BuilderWindow>("Splat LOD Builder");

        void OnGUI()
        {
            EditorGUILayout.LabelField("Stage 2 · Accepted model and chunks", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("LOD0 preserves the accepted Stage1 model and textured surfaces. LOD1–LOD3 will be added after the representative-section merging trial.", MessageType.Info);
            inputManifest = EditorGUILayout.TextField("Accepted input", inputManifest);
            chunkManifest = EditorGUILayout.TextField("Chunk layout", chunkManifest);
            using (new EditorGUI.DisabledScope(Application.isPlaying || SceneManager.GetActiveScene().name != "Stage2"))
                if (GUILayout.Button("Build / Update Stage2 Scene"))
                {
                    try
                    {
                        var review = Stage2SceneBuilder.BuildCurrentScene(inputManifest, chunkManifest);
                        EditorSceneManager.SaveScene(SceneManager.GetActiveScene());
                        message = $"Saved {review.Layout.count:N0} splats in {review.Layout.chunks.Length} chunks. Prefab: {Stage2SceneBuilder.LastPrefabPath}";
                    }
                    catch (Exception error) { message = error.Message; }
                }
            var current = UnityEngine.Object.FindFirstObjectByType<Stage2ReviewModel>();
            if (current && current.Layout != null)
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Available representation", "LOD0 · accepted reference");
                EditorGUILayout.LabelField("Splat count", current.Layout.count.ToString("N0"));
                EditorGUILayout.LabelField("Occupied chunks", current.Layout.chunks.Length.ToString());
                EditorGUILayout.LabelField("Scale", "User-authored Unity units");
                var visible = EditorGUILayout.Toggle("Red chunk boxes (right A)", current.BoxesVisible);
                if (visible != current.BoxesVisible)
                {
                    Undo.RecordObject(current, "Toggle Stage2 chunk overlay");
                    current.SetBoxesVisible(visible); EditorUtility.SetDirty(current); SceneView.RepaintAll();
                }
                if (GUILayout.Button("Select accepted model")) Selection.activeGameObject = current.Source.gameObject;
                if (GUILayout.Button("Select a chunk")) Selection.activeGameObject = current.GetComponentInChildren<Stage2Chunk>(true).gameObject;
            }
            EditorGUILayout.Space();
            EditorGUILayout.HelpBox("Right A toggles the red bounds in Play Mode. Stage1 A/B and percentage controls are disabled here. Lower-LOD selection, pointer marks and refinement are not enabled in this milestone.", MessageType.None);
            if (GUILayout.Button("Open Stage2 design document"))
                EditorUtility.RevealInFinder(Path.GetFullPath(Path.Combine(Application.dataPath, "../../STAGE2_LOD_PLAN.md")));
            if (!string.IsNullOrEmpty(message)) EditorGUILayout.HelpBox(message, MessageType.Info);
        }
    }
}
