using System;
using System.Collections.Generic;
using System.IO;
using Gsplat;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SplatPreprocess.Editor
{
    [InitializeOnLoad]
    public static class Stage1PreviewAutoLoader
    {
        static readonly HashSet<int> attempted = new();
        static double nextCheck;
        public static bool Suspended { get; set; }

        static Stage1PreviewAutoLoader()
        {
            GsplatRenderer.EditorPreviewPreparing += PrepareCameraPreview;
            EditorApplication.update += Tick;
            EditorSceneManager.sceneOpened += (scene, mode) => attempted.Clear();
            EditorSceneManager.sceneSaved += scene => attempted.Clear();
            EditorApplication.playModeStateChanged += state => { if (state == PlayModeStateChange.EnteredEditMode) attempted.Clear(); };
        }

        static void PrepareCameraPreview(GsplatRenderer renderer)
        {
            foreach (var controller in UnityEngine.Object.FindObjectsByType<Stage1SelectionController>(FindObjectsSortMode.None))
            {
                if (!controller.isActiveAndEnabled || controller.Renderer != renderer) continue;
                try { controller.EnsurePreviewSelection(); }
                catch (Exception error) { controller.ReportPreviewLoadError(error.Message); }
            }
        }

        static void Tick()
        {
            if (Suspended || EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating ||
                EditorApplication.timeSinceStartup < nextCheck || WorkerEnvironment.SetupRunning || File.Exists(Stage1Round1Service.LeasePath)) return;
            nextCheck = EditorApplication.timeSinceStartup + 1;
            var scene = SceneManager.GetActiveScene();
            if (!scene.IsValid() || !scene.isLoaded || string.IsNullOrEmpty(scene.path) || scene.isDirty) return;
            foreach (var root in scene.GetRootGameObjects())
                foreach (var controller in root.GetComponentsInChildren<Stage1SelectionController>(true))
                {
                    if (!controller.isActiveAndEnabled || controller.State != null || !controller.Renderer ||
                        !controller.Renderer.isActiveAndEnabled || string.IsNullOrEmpty(controller.RankManifestPath) || !attempted.Add(controller.GetInstanceID())) continue;
                    try
                    {
                        Stage1Round1Service.ReloadConfiguredReview(controller);
                        foreach (var panel in scene.GetRootGameObjects())
                            foreach (var view in panel.GetComponentsInChildren<Stage1PreviewPanel>(true)) view.Refresh();
                        EditorApplication.QueuePlayerLoopUpdate();
                        SceneView.RepaintAll();
                        foreach (var window in Resources.FindObjectsOfTypeAll<EditorWindow>()) if (window.GetType().Name == "GameView") window.Repaint();
                    }
                    catch (Exception error) { controller.ReportPreviewLoadError(error.Message); }
                }
        }
    }
}
