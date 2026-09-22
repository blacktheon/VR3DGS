using System;
using System.IO;
using System.Linq;
using Gsplat;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace SplatLOD.Editor
{
    /// <summary>Developer evidence from normal, distinct Play Mode camera frames, including opaque surfaces.</summary>
    public static class Stage2ReferenceCapture
    {
        static Stage2ReviewModel review;
        static GsplatRenderer source;
        static GsplatAsset accepted, original;
        static uint[] ids;
        static Camera camera;
        static Camera[] otherCameras;
        static RenderTexture target;
        static string output;
        static int index, readyFrame;
        static bool prepared, captured, originalBoxes, background;
        static float timeScale;
        static double deadline;
        static readonly string[] Names = { "reference", "reference-repeat", "accepted", "accepted-repeat", "chunk-overlay" };
        public static string Status { get; private set; } = "idle";

        public static void Begin(string originalAssetPath, string sourceIdsPath, string outputDirectory)
        {
            if (!Application.isPlaying || EditorApplication.isPaused || source)
                throw new InvalidOperationException("Begin once in unpaused Play Mode");
            review = UnityEngine.Object.FindFirstObjectByType<Stage2ReviewModel>();
            if (!review || review.Layout == null) throw new InvalidOperationException("Build Stage2 first");
            original = AssetDatabase.LoadAssetAtPath<GsplatAsset>(originalAssetPath);
            if (!original) throw new InvalidOperationException("Missing original reference");
            var bytes = File.ReadAllBytes(sourceIdsPath);
            if (bytes.Length != review.Layout.count * 4) throw new InvalidDataException("Source-ID count mismatch");
            ids = new uint[bytes.Length / 4]; Buffer.BlockCopy(bytes, 0, ids, 0, bytes.Length);
            if (ids.Distinct().Count() != ids.Length || ids.Any(id => id >= original.SplatCount))
                throw new InvalidDataException("Invalid original source IDs");
            output = Path.GetFullPath(outputDirectory);
            if (Directory.Exists(output)) throw new IOException("Use a fresh capture directory");
            Directory.CreateDirectory(output);
            source = review.Source; accepted = source.GsplatAsset; originalBoxes = review.BoxesVisible;
            timeScale = Time.timeScale; background = Application.runInBackground;
            otherCameras = UnityEngine.Object.FindObjectsByType<Camera>(FindObjectsSortMode.None).Where(c => c.enabled).ToArray();
            try
            {
                foreach (var c in otherCameras) c.enabled = false;
                Time.timeScale = 0; Application.runInBackground = true;
                var go = new GameObject("Temporary Stage2 comparison camera") { hideFlags = HideFlags.HideAndDontSave };
                camera = go.AddComponent<Camera>();
                camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = new Color(.08f, .08f, .08f, 1);
                camera.allowHDR = false; camera.allowMSAA = false; camera.orthographic = true;
                camera.nearClipPlane = .05f; camera.farClipPlane = 100;
                var bounds = accepted.Bounds;
                var center = source.transform.TransformPoint(bounds.center);
                camera.transform.SetPositionAndRotation(center + new Vector3(7, 5, -8), Quaternion.LookRotation(new Vector3(-7, -5, 8), Vector3.up));
                camera.orthographicSize = Mathf.Max(bounds.size.x, bounds.size.y, bounds.size.z) * .52f;
                camera.aspect = 16f / 9;
                var data = camera.GetUniversalAdditionalCameraData();
                data.allowXRRendering = false; data.renderPostProcessing = false; data.renderShadows = false; data.antialiasing = AntialiasingMode.None;
                target = new RenderTexture(1920, 1080, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                target.Create(); camera.targetTexture = target;
                index = 0; prepared = false; captured = false; Status = "running";
                deadline = EditorApplication.timeSinceStartup + 90;
                File.WriteAllText(Path.Combine(output, "frames.txt"), "Normal URP camera frames; physics frozen; six accepted surfaces and authored opaque meshes included.\nCamera: " + camera.transform.position + " rotation=" + camera.transform.rotation + " orthographic size=" + camera.orthographicSize + "\n");
                RenderPipelineManager.endCameraRendering += OnRendered;
                EditorApplication.update += Tick;
                AssemblyReloadEvents.beforeAssemblyReload += Cancel;
                EditorApplication.playModeStateChanged += OnPlayState;
            }
            catch { Cleanup(); throw; }
        }

        static void Tick()
        {
            try
            {
                if (!Application.isPlaying || !source) throw new InvalidOperationException("Capture interrupted");
                if (EditorApplication.timeSinceStartup > deadline) throw new TimeoutException("Camera did not complete capture frames");
                if (captured) { index++; prepared = false; captured = false; }
                if (index == Names.Length) { Status = "passed"; Cleanup(); return; }
                if (!prepared)
                {
                    if (index == 0 || index == 2)
                    {
                        source.ClearStage1Selection();
                        var serialized = new SerializedObject(source);
                        serialized.FindProperty("GsplatAsset").objectReferenceValue = index == 0 ? original : accepted;
                        serialized.ApplyModifiedPropertiesWithoutUndo();
                        source.ReloadAsset();
                        if (index == 0) { source.SetStage1Rank(ids); source.SetStage1KeepCount(ids.Length); }
                    }
                    review.SetBoxesVisible(index == 4);
                    readyFrame = Time.frameCount + 4; prepared = true;
                }
                EditorApplication.QueuePlayerLoopUpdate();
            }
            catch (Exception error) { Fail(error); }
        }

        static void OnRendered(ScriptableRenderContext context, Camera rendered)
        {
            if (rendered != camera || !prepared || captured || Time.frameCount < readyFrame) return;
            try
            {
                if (source.RemainingCount != ids.Length) throw new InvalidDataException("Unexpected GPU draw count");
                var sorted = new uint[ids.Length]; source.SorterResource.OrderBuffer.GetData(sorted);
                var expected = new bool[source.GsplatAsset.SplatCount];
                if (index < 2) foreach (var id in ids) expected[id] = true;
                else for (int i = 0; i < ids.Length; ++i) expected[i] = true;
                foreach (var id in sorted)
                {
                    if (id >= expected.Length || !expected[id]) throw new InvalidDataException("GPU source membership mismatch");
                    expected[id] = false;
                }
                var previous = RenderTexture.active;
                var pixels = new Texture2D(target.width, target.height, TextureFormat.RGB24, false);
                try
                {
                    RenderTexture.active = target;
                    pixels.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0); pixels.Apply();
                    File.WriteAllBytes(Path.Combine(output, Names[index] + ".png"), pixels.EncodeToPNG());
                }
                finally { RenderTexture.active = previous; UnityEngine.Object.DestroyImmediate(pixels); }
                File.AppendAllText(Path.Combine(output, "frames.txt"), Names[index] + " frame=" + Time.frameCount + " GPU count=" + source.RemainingCount + " exact membership=true\n");
                captured = true;
            }
            catch (Exception error) { Fail(error); }
        }

        static void Fail(Exception error)
        {
            Status = "failed: " + error;
            try { File.WriteAllText(Path.Combine(output, "error.txt"), Status); }
            catch (Exception loggingError) { Debug.LogWarning("Capture error could not be saved: " + loggingError.Message); }
            finally { Cleanup(); }
            Debug.LogError(Status);
        }
        public static void Cancel() { if (!source) return; Status = "cancelled"; Cleanup(); }
        static void OnPlayState(PlayModeStateChange state) { if (state == PlayModeStateChange.ExitingPlayMode) Cancel(); }
        static void Cleanup()
        {
            RenderPipelineManager.endCameraRendering -= OnRendered; EditorApplication.update -= Tick;
            AssemblyReloadEvents.beforeAssemblyReload -= Cancel; EditorApplication.playModeStateChanged -= OnPlayState;
            try
            {
                if (source)
                {
                    source.ClearStage1Selection();
                    var serialized = new SerializedObject(source); serialized.FindProperty("GsplatAsset").objectReferenceValue = accepted;
                    serialized.ApplyModifiedPropertiesWithoutUndo(); source.ReloadAsset();
                }
            }
            finally
            {
                try
                {
                    if (review) review.SetBoxesVisible(originalBoxes);
                    if (camera) UnityEngine.Object.DestroyImmediate(camera.gameObject);
                    if (target) { target.Release(); UnityEngine.Object.DestroyImmediate(target); }
                }
                finally
                {
                    if (otherCameras != null) foreach (var c in otherCameras) if (c) c.enabled = true;
                    Time.timeScale = timeScale; Application.runInBackground = background;
                    source = null; camera = null; target = null;
                }
            }
        }
    }
}
