using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Gsplat;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace SplatPreprocess.Editor
{
    /// <summary>Editor evidence capture across distinct frames; never queues multiple source draws in one frame.</summary>
    public static class Stage1Round1Validation
    {
        [Serializable] sealed class View
        {
            public string id, membership;
            public float[] eye, foot, viewmat, K;
            public int width, height;
            public float near, far;
        }
        [Serializable] sealed class Capture
        {
            public string view_id, image;
            public int percent, expected_count, sort_draw_count, nonblack_pixels, runtime_frame;
            public bool exact_source_membership, source_buffers_unchanged;
            public double mean_rgb, mean_alpha;
        }
        [Serializable] sealed class Report
        {
            public string status = "running", error, rank_id, source_hash, scene_hash;
            public string unity_version, graphics_api;
            public string profile = "Unity URP source-only, linear float render target, black background; compare Unity candidates against Unity original separately from Python metrics";
            public bool gamma_to_linear;
            public List<Capture> captures = new();
        }
        static GsplatRenderer renderer;
        static GsplatResourceUncompressed source;
        static Camera camera;
        static RenderTexture target;
        static RankManifest manifest;
        static uint[] rank;
        static View[] views;
        static Report report;
        static string output;
        static int originalLayer, originalKeep, index, preparedFrame, lastCapturedFrame;
        static bool prepared;
        static bool originalRunInBackground;
        static double deadline;
        static readonly int[] Percents = { 100, 75, 50, 25, 0 };
        public static string Status => report == null ? "idle" : report.status + " " + report.captures.Count + "/" + (views?.Length * Percents.Length ?? 0) + " " + report.error;

        public static string Begin(string rankManifestPath, string outputDirectory)
        {
            if (renderer) throw new InvalidOperationException("A capture is already active");
            if (!EditorApplication.isPlaying || EditorApplication.isPaused) throw new InvalidOperationException("Capture in unpaused Play Mode so native draw submissions have distinct runtime frames");
            var scene = SceneManager.GetActiveScene();
            var sources = scene.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren<GsplatRenderer>(true)).ToArray();
            if (sources.Length != 1 || !sources[0].isActiveAndEnabled) throw new InvalidOperationException("One active source renderer is required");
            var review = scene.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren<Stage1SelectionController>(true)).Single();
            if (review.State == null) throw new InvalidOperationException("Load the current review rank before validation");
            var loaded = Stage1RankLoader.Load(rankManifestPath, review.State.SourceHash, review.State.SceneHash);
            if (loaded.Manifest.rank_id != review.State.FrozenRankId) throw new InvalidOperationException("The requested rank is not the loaded review rank");
            if (!sources[0].HasStage1Selection || sources[0].Stage1EligibleCount != loaded.Manifest.eligible_count)
                throw new InvalidOperationException("Load this round's rank in the preview before capture");
            output = Path.GetFullPath(outputDirectory);
            if (Directory.Exists(output)) throw new IOException("Use a fresh validation directory");
            var allViews = File.ReadLines(Path.Combine(Path.GetDirectoryName(rankManifestPath), "views_verify.jsonl"))
                .Where(line => !string.IsNullOrWhiteSpace(line)).Select(JsonUtility.FromJson<View>).ToArray();
            views = allViews.GroupBy(view => Math.Round(view.foot[1], 2)).Select(group => group.First()).ToArray();
            if (views.Length < 2) throw new InvalidDataException("Expected held-out cameras on both authored NavMesh levels");
            if (scene.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren<Renderer>(true)).Any(value => value.gameObject.layer == 31))
                throw new InvalidOperationException("Temporary source isolation layer 31 is already used by scene renderers");
            manifest = loaded.Manifest; rank = loaded.Order;
            renderer = sources[0]; originalLayer = renderer.gameObject.layer; originalKeep = renderer.Stage1SelectedCount;
            source = renderer.GsplatResource as GsplatResourceUncompressed;
            if (source == null) { renderer = null; throw new InvalidOperationException("An uploaded uncompressed reference is required"); }
            report = new Report { rank_id = manifest.rank_id, source_hash = manifest.source_hash, scene_hash = manifest.scene_hash,
                unity_version = Application.unityVersion, graphics_api = SystemInfo.graphicsDeviceType.ToString(), gamma_to_linear = renderer.GammaToLinear };
            index = 0; prepared = false;
            originalRunInBackground = Application.runInBackground;
            lastCapturedFrame = -1;
            try
            {
                Directory.CreateDirectory(output);
                Application.runInBackground = true;
                var owner = new GameObject("Stage1 temporary evidence camera") { hideFlags = HideFlags.HideAndDontSave };
                camera = owner.AddComponent<Camera>(); camera.enabled = false;
                camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = new Color(0, 0, 0, 0);
                camera.cullingMask = 1 << 31; camera.allowMSAA = false; camera.allowHDR = true;
                var data = camera.GetUniversalAdditionalCameraData(); data.renderPostProcessing = false; data.renderShadows = false;
                data.allowXRRendering = false;
                data.antialiasing = AntialiasingMode.None;
                EditorApplication.update += Tick;
                AssemblyReloadEvents.beforeAssemblyReload += Cancel;
                EditorApplication.playModeStateChanged += PlayModeChanged;
                Save();
                return Path.Combine(output, "validation.json");
            }
            catch { Cleanup(); throw; }
        }

        static void Tick()
        {
            try
            {
                if (!EditorApplication.isPlaying) throw new InvalidOperationException("Leaving Play Mode interrupted evidence capture");
                if (!renderer || !renderer.isActiveAndEnabled) throw new InvalidOperationException("Source renderer became unavailable");
                if (index == views.Length * Percents.Length)
                {
                    report.status = "passed";
                    try { Save(); } finally { Cleanup(); }
                    return;
                }
                var view = views[index / Percents.Length]; var percent = Percents[index % Percents.Length];
                var count = Stage1Counts.KeepCount(manifest.eligible_count, percent * 100);
                if (!prepared)
                {
                    if (target && (target.width != view.width || target.height != view.height)) { target.Release(); UnityEngine.Object.DestroyImmediate(target); target = null; }
                    if (!target) { target = new RenderTexture(view.width, view.height, 24, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear); target.Create(); }
                    var cv = Matrix4x4.identity;
                    for (int row = 0; row < 4; row++) for (int col = 0; col < 4; col++) cv[row, col] = view.viewmat[row * 4 + col];
                    var forward = new Vector3(cv[2, 0], cv[2, 1], cv[2, 2]);
                    var up = -new Vector3(cv[1, 0], cv[1, 1], cv[1, 2]);
                    camera.transform.SetPositionAndRotation(new Vector3(view.eye[0], view.eye[1], view.eye[2]), Quaternion.LookRotation(forward, up));
                    camera.worldToCameraMatrix = Matrix4x4.Scale(new Vector3(1, -1, -1)) * cv;
                    camera.nearClipPlane = view.near; camera.farClipPlane = view.far;
                    camera.aspect = view.width / (float)view.height;
                    camera.fieldOfView = 2 * Mathf.Atan(view.height / (2 * view.K[4])) * Mathf.Rad2Deg;
                    camera.ResetProjectionMatrix(); camera.targetTexture = target;
                    renderer.SetStage1KeepCount(count);
                    preparedFrame = Time.frameCount; deadline = EditorApplication.timeSinceStartup + 30; prepared = true;
                    EditorApplication.QueuePlayerLoopUpdate(); return;
                }
                if (EditorApplication.timeSinceStartup > deadline) throw new TimeoutException("Renderer did not activate the requested prefix");
                if (Time.frameCount < preparedFrame + 2 || Time.frameCount <= lastCapturedFrame) { EditorApplication.QueuePlayerLoopUpdate(); return; }
                if (!renderer.HasStage1Selection) throw new InvalidOperationException("The frozen selection was lost before capture; reload the rank");
                var request = new UniversalRenderPipeline.SingleCameraRequest { destination = target };
                if (!RenderPipeline.SupportsRenderRequest(camera, request)) throw new NotSupportedException("The active pipeline does not support URP single-camera capture");
                // Normal Editor draws remain on the original layer. Submit exactly one isolated draw
                // for this request; this also activates the queued prefix when the background Editor
                // is not invoking ExecuteAlways.Update. Layer restoration cannot depend on disk I/O.
                try
                {
                    renderer.gameObject.layer = 31;
                    renderer.Update();
                    if (renderer.RemainingCount != count) throw new InvalidOperationException("The requested prefix did not activate before capture");
                    RenderPipeline.SubmitRenderRequest(camera, request);
                }
                finally { renderer.gameObject.layer = originalLayer; }
                var capture = new Capture { view_id = view.id, percent = percent, expected_count = count, sort_draw_count = (int)renderer.RemainingCount,
                    image = view.id + "-" + percent + ".png", source_buffers_unchanged = ReferenceEquals(source, renderer.GsplatResource), runtime_frame = Time.frameCount };
                if (!capture.source_buffers_unchanged) throw new InvalidOperationException("Changing a prefix replaced resident source buffers");
                var order = new uint[count];
                if (count > 0) renderer.SorterResource.OrderBuffer.GetData(order, 0, 0, count);
                var membership = new bool[manifest.source_count];
                for (int i = 0; i < count; i++) membership[rank[i]] = true;
                foreach (var id in order)
                {
                    if (id >= membership.Length || !membership[id]) throw new InvalidDataException("GPU sorted prefix contains a duplicate or wrong original source ID");
                    membership[id] = false;
                }
                capture.exact_source_membership = true;
                var previous = RenderTexture.active;
                var pixels = new Texture2D(view.width, view.height, TextureFormat.RGBAFloat, false, true);
                Texture2D png = null;
                try
                {
                    RenderTexture.active = target; pixels.ReadPixels(new Rect(0, 0, view.width, view.height), 0, 0); pixels.Apply();
                    var colors = pixels.GetPixels();
                    foreach (var color in colors)
                    {
                        if (!float.IsFinite(color.r) || !float.IsFinite(color.g) || !float.IsFinite(color.b) || !float.IsFinite(color.a))
                            throw new InvalidDataException("Capture contains nonfinite color values");
                        capture.mean_rgb += (color.r + color.g + color.b) / 3.0; capture.mean_alpha += color.a;
                        if (Mathf.Max(color.r, Mathf.Max(color.g, color.b)) > 1e-4f) capture.nonblack_pixels++;
                    }
                    capture.mean_rgb /= colors.Length; capture.mean_alpha /= colors.Length;
                    if (percent == 0 && capture.nonblack_pixels != 0) throw new InvalidDataException("Zero prefix still renders source pixels");
                    if (percent > 0 && capture.nonblack_pixels < 100) throw new InvalidDataException("Nonempty prefix produced an empty source capture");
                    png = new Texture2D(view.width, view.height, TextureFormat.RGBA32, false, true); png.SetPixels(colors); png.Apply();
                    File.WriteAllBytes(Path.Combine(output, capture.image), png.EncodeToPNG());
                }
                finally { RenderTexture.active = previous; UnityEngine.Object.DestroyImmediate(pixels); if (png) UnityEngine.Object.DestroyImmediate(png); }
                report.captures.Add(capture); Save(); lastCapturedFrame = Time.frameCount; index++; prepared = false;
                EditorApplication.QueuePlayerLoopUpdate();
            }
            catch (Exception error)
            {
                report.status = "failed"; report.error = error.ToString();
                try { Save(); }
                catch (Exception persistenceError) { Debug.LogError("Stage 1 evidence report could not be saved: " + persistenceError.Message); }
                finally { Cleanup(); }
                Debug.LogError("Stage 1 evidence capture: " + error.Message);
            }
        }

        public static void Cancel()
        {
            if (!renderer) return;
            report.status = "interrupted"; report.error = "Editor assembly reload or explicit cancellation";
            try { Save(); } finally { Cleanup(); }
        }
        static void PlayModeChanged(PlayModeStateChange state) { if (state == PlayModeStateChange.ExitingPlayMode) Cancel(); }
        static void Save() => WorkerJobStore.WriteAtomic(Path.Combine(output, "validation.json"), report);
        static void Cleanup()
        {
            EditorApplication.update -= Tick; AssemblyReloadEvents.beforeAssemblyReload -= Cancel;
            EditorApplication.playModeStateChanged -= PlayModeChanged;
            if (renderer) { renderer.gameObject.layer = originalLayer; if (renderer.HasStage1Selection) renderer.SetStage1KeepCount(originalKeep); }
            if (camera) UnityEngine.Object.DestroyImmediate(camera.gameObject);
            if (target) { target.Release(); UnityEngine.Object.DestroyImmediate(target); }
            camera = null; target = null; source = null; renderer = null; rank = null;
            Application.runInBackground = originalRunInBackground;
            EditorApplication.QueuePlayerLoopUpdate();
        }
    }
}
