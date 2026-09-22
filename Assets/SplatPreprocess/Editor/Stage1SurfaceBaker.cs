using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Gsplat;
using Gsplat.Editor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace SplatPreprocess.Editor
{
    /// <summary>
    /// Captures the complete source before customization. Begin requires fresh Play Mode frames;
    /// Apply assigns the persisted materials after returning to Edit Mode and does not save the scene.
    /// </summary>
    public static class Stage1SurfaceBaker
    {
        const int IsolationLayer = 31;
        const int ExpectedSourceCount = 6011316;
        static readonly string[] ExpectedSurfaces = { "back", "right1_1", "right1_2", "right2_1", "right2_2" };

        public sealed class SurfaceFrame
        {
            public Vector3 Center, Eye, Forward, Up, Right;
            public float WorldWidth, WorldHeight, OrthoSize, Aspect, Near, Far;
            public int Width, Height;
            public bool FlipX;
        }

        [Serializable]
        public sealed class SurfaceCapture
        {
            public string name, hierarchy_path, global_object_id, mesh_signature;
            public string image, preview, material, image_sha256;
            public int width, height, runtime_frame, source_draw_count;
            public long image_bytes, covered_pixels, nearly_opaque_pixels, nonblack_pixels;
            public double coverage_fraction, nearly_opaque_fraction, mean_alpha, mean_srgb;
            public bool flip_x_for_mesh_uv, exact_full_source_membership, source_buffers_unchanged;
            public float world_width, world_height, orthographic_size, aspect, near, far;
            public float[] local_to_world, bounds_min, bounds_max, eye, forward, up, right;
            public float[] world_to_camera, projection, gpu_projection;
        }

        [Serializable]
        public sealed class CaptureReport
        {
            public int schema_version = 1;
            public string status = "capturing", error, created_utc, completed_utc, applied_utc;
            public string scene_path, output_asset_folder, report_path, source_asset_path, source_asset_guid, source_file_sha256;
            public string unity_version, graphics_api, color_space, render_target_format;
            public string texture_import_profile = "Default: original resolution, uncompressed sRGB, mipmaps; Android: ASTC 6x6, maximum compression quality, original requested maximum dimension. Source PNGs remain lossless.";
            public string profile = "All original source rows; no rank, deletion, cutout or wall masks; source-only orthographic camera on each plane's local +Y side; no XR, lighting, shadows or post-processing. RGBA8 sRGB screenshot over transparent black, saved with opaque alpha for URP Unlit. Black means no contributing source color. Mesh UV handedness is corrected in the image pixels.";
            public int source_count, max_dimension, source_sh_degree, original_selected_count;
            public bool source_gamma_to_linear, state_restored, materials_applied;
            public string original_rank_id, original_display_mode;
            public string[] restoration_errors;
            public float[] source_local_to_world, source_bounds_min, source_bounds_max;
            public List<SurfaceCapture> captures = new();
        }

        sealed class BehaviourSnapshot
        {
            public Behaviour Behaviour;
            public bool Enabled;
        }

        sealed class CameraSnapshot
        {
            public Camera Camera;
            public int CullingMask;
        }

        static bool running, originalEnabled, originalRunInBackground, originalHadRank, originalGamma;
        static int originalLayer, originalKeep, originalSH, index, preparedFrame, lastCapturedFrame;
        static float originalBrightness, originalDownscale;
        static GsplatRenderer.GsplatSortMode originalSortMode;
        static uint originalSortRefresh;
        static GsplatRenderer source;
        static GsplatAsset sourceAsset;
        static GsplatResource sourceResource;
        static uint[] originalRank;
        static Stage1Wall[] originalWalls;
        static MeshFilter[] surfaces;
        static SurfaceFrame[] frames;
        static float[][] surfaceMatrices;
        static string[] surfaceSignatures;
        static readonly List<BehaviourSnapshot> behaviours = new();
        static readonly List<CameraSnapshot> cameras = new();
        static Camera captureCamera;
        static RenderTexture target;
        static CaptureReport report;
        static string reportPath;
        static double deadline;
        static bool prepared;

        public static bool IsRunning => running;
        public static string ReportPath => reportPath;
        public static string Status => report == null ? "idle" : report.status + " " + report.captures.Count + "/5" +
            (string.IsNullOrEmpty(report.error) ? string.Empty : " " + report.error);

        /// <summary>Use a new Assets/... folder. A 1024 probe uses the same path as the final 8192 bake.</summary>
        public static string Begin(string outputAssetFolder, int maxDimension = 8192)
        {
            if (running) throw new InvalidOperationException("A surface bake is already active.");
            if (!EditorApplication.isPlaying || EditorApplication.isPaused)
                throw new InvalidOperationException("Surface capture requires unpaused Play Mode so native draw submissions clear between runtime frames.");
            if (maxDimension < 16 || maxDimension > SystemInfo.maxTextureSize)
                throw new ArgumentOutOfRangeException(nameof(maxDimension), "The requested dimension must fit the graphics device.");
            var scene = SceneManager.GetActiveScene();
            if (string.IsNullOrEmpty(scene.path)) throw new InvalidOperationException("Save the authored scene before starting a surface bake.");
            var candidates = SceneComponents<GsplatRenderer>(scene);
            if (candidates.Length != 1 || !candidates[0].gameObject.activeInHierarchy)
                throw new InvalidOperationException("The active scene must have one source renderer on an active GameObject.");
            var candidate = candidates[0];
            if (!(candidate.GsplatAsset is GsplatAssetUncompressed) || candidate.GsplatAsset.SplatCount != ExpectedSourceCount ||
                candidate.GsplatAsset.PrunedSplatCount != 0 || candidate.GsplatAsset.SHBands != 0)
                throw new InvalidOperationException("Surface baking requires the unchanged, unpruned SH0 source with all 6,011,316 original rows.");
            if (candidate.Cutouts.Length != 0) throw new InvalidOperationException("Disable package cutouts before capturing the complete original source.");
            if (UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Any(value => value.gameObject.layer == IsolationLayer) ||
                UnityEngine.Object.FindObjectsByType<GsplatRenderer>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Any(value => value != candidate && (value.gameObject.layer == IsolationLayer || value.isActiveAndEnabled)))
                throw new InvalidOperationException("The temporary source isolation layer 31 must be unused, and no other splat renderer may be active.");
            if (!Shader.Find("Universal Render Pipeline/Unlit")) throw new InvalidOperationException("The URP Unlit shader is unavailable.");

            var selectedSurfaces = FindSurfaces(scene);
            var selectedFrames = selectedSurfaces.Select(value => BuildFrame(value.sharedMesh, value.transform.localToWorldMatrix,
                candidate.GsplatAsset.Bounds, candidate.transform.localToWorldMatrix, maxDimension)).ToArray();
            var assetFolder = NormalizeNewAssetFolder(outputAssetFolder);
            string assetPath = AssetDatabase.GetAssetPath(candidate.GsplatAsset);
            var importer = AssetImporter.GetAtPath(assetPath) as GsplatImporter;
            if (!importer || importer.Compression != CompressionMode.Uncompressed || importer.SourceCoordinates != SourceCoordinates.RUB || importer.OpacityPruneThreshold != 0)
                throw new InvalidOperationException("The source importer must retain the audited Uncompressed/RUB/SH0/zero-pruning profile.");
            string sourceHash = HashFile(ProjectPath(assetPath));
            var controllers = SceneComponents<Stage1SelectionController>(scene).Where(value => value.Renderer == candidate).ToArray();
            var wallBinders = SceneComponents<Stage1AuthoredWalls>(scene).Where(value => value.Renderer == candidate).ToArray();
            if (controllers.Length > 1 || wallBinders.Length > 1)
                throw new InvalidOperationException("Only one review controller and one wall binder may own the capture source.");
            var controller = controllers.SingleOrDefault();
            var wallBinder = wallBinders.SingleOrDefault();
            uint[] savedRank = null;
            if (candidate.HasStage1Selection)
            {
                if (!controller || controller.State == null)
                    throw new InvalidOperationException("The current GPU selection has no validated review rank available for restoration.");
                var loaded = Stage1RankLoader.Load(ProjectPath(controller.RankManifestPath), controller.State.SourceHash, controller.State.SceneHash);
                if (loaded.Manifest.rank_id != controller.State.FrozenRankId || loaded.Manifest.source_hash != sourceHash ||
                    loaded.Manifest.eligible_count != candidate.Stage1EligibleCount)
                    throw new InvalidDataException("The current selection cannot be restored from its configured frozen rank.");
                savedRank = loaded.Order;
            }
            if (candidate.HasStage1Walls && (!wallBinder || wallBinder.WallCount == 0))
                throw new InvalidOperationException("The current wall mask has no authored wall data available for restoration.");

            source = candidate;
            sourceAsset = source.GsplatAsset;
            sourceResource = source.GsplatResource;
            originalLayer = source.gameObject.layer;
            originalEnabled = source.enabled;
            originalHadRank = source.HasStage1Selection;
            originalKeep = source.Stage1SelectedCount;
            originalRank = savedRank;
            originalWalls = source.HasStage1Walls ? wallBinder.Walls.ToArray() : Array.Empty<Stage1Wall>();
            originalGamma = source.GammaToLinear;
            originalSH = source.SHDegree;
            originalBrightness = source.Brightness;
            originalDownscale = source.SplatDownscaleFactor;
            originalSortMode = source.SortMode;
            originalSortRefresh = source.SortRefreshRate;
            originalRunInBackground = Application.runInBackground;
            surfaces = selectedSurfaces;
            frames = selectedFrames;
            surfaceMatrices = surfaces.Select(value => Matrix(value.transform.localToWorldMatrix)).ToArray();
            surfaceSignatures = surfaces.Select(value => MeshSignature(value.sharedMesh)).ToArray();
            // Asset import can hold Windows file handles while texture imports are in progress.
            // Keep the mutable journal outside Assets so replacing it never races TextScriptImporter.
            reportPath = ProjectPath(Path.Combine("SplatData", "surface-bakes", Path.GetFileName(assetFolder) + "-" + Guid.NewGuid().ToString("N"), "surface-bake.json"));
            behaviours.Clear(); cameras.Clear();
            foreach (var behaviour in controllers.Cast<Behaviour>().Concat(wallBinders))
                behaviours.Add(new BehaviourSnapshot { Behaviour = behaviour, Enabled = behaviour.enabled });
            report = new CaptureReport
            {
                created_utc = UtcNow(), scene_path = scene.path, output_asset_folder = assetFolder, report_path = reportPath,
                source_asset_path = assetPath, source_asset_guid = AssetDatabase.AssetPathToGUID(assetPath), source_file_sha256 = sourceHash,
                source_count = checked((int)sourceAsset.SplatCount), source_sh_degree = sourceAsset.SHBands,
                max_dimension = maxDimension, unity_version = Application.unityVersion, graphics_api = SystemInfo.graphicsDeviceType.ToString(),
                color_space = QualitySettings.activeColorSpace.ToString(), render_target_format = "RGBA8 sRGB, 24-bit depth, no MSAA",
                source_gamma_to_linear = QualitySettings.activeColorSpace == ColorSpace.Linear,
                original_selected_count = originalKeep, original_rank_id = controller?.State?.FrozenRankId,
                original_display_mode = controller?.State?.DisplayMode,
                source_local_to_world = Matrix(source.transform.localToWorldMatrix),
                source_bounds_min = Vector(sourceAsset.Bounds.min), source_bounds_max = Vector(sourceAsset.Bounds.max)
            };
            running = true; index = 0; prepared = false; lastCapturedFrame = -1;
            try
            {
                CreateAssetFolders(assetFolder);
                foreach (var snapshot in behaviours) snapshot.Behaviour.enabled = false;
                Application.runInBackground = true;
                source.enabled = true;
                source.ClearStage1Selection(); source.ClearStage1Walls();
                source.SHDegree = sourceAsset.SHBands;
                source.Brightness = 1; source.SplatDownscaleFactor = 0;
                source.GammaToLinear = report.source_gamma_to_linear;
                source.SortMode = GsplatRenderer.GsplatSortMode.Always; source.SortRefreshRate = 1;
                source.ForceRefresh();
                var owner = new GameObject("Stage1 temporary surface capture") { hideFlags = HideFlags.HideAndDontSave };
                captureCamera = owner.AddComponent<Camera>();
                captureCamera.enabled = false;
                captureCamera.orthographic = true;
                captureCamera.clearFlags = CameraClearFlags.SolidColor;
                captureCamera.backgroundColor = Color.clear;
                captureCamera.cullingMask = 1 << IsolationLayer;
                captureCamera.allowHDR = false; captureCamera.allowMSAA = false; captureCamera.allowDynamicResolution = false;
                var data = captureCamera.GetUniversalAdditionalCameraData();
                data.allowXRRendering = false; data.renderPostProcessing = false; data.renderShadows = false;
                data.antialiasing = AntialiasingMode.None;
                IsolateOtherCameras();
                EditorApplication.update += Tick;
                AssemblyReloadEvents.beforeAssemblyReload += Cancel;
                EditorApplication.playModeStateChanged += PlayModeChanged;
                EditorApplication.quitting += Cancel;
                SaveReport();
                EditorApplication.QueuePlayerLoopUpdate();
                return reportPath;
            }
            catch (Exception error) { Finish("failed", error); throw; }
        }

        static void Tick()
        {
            try
            {
                if (!EditorApplication.isPlaying || EditorApplication.isPaused) throw new InvalidOperationException("Play Mode stopped or paused during surface capture.");
                if (!source || !source.isActiveAndEnabled || source.GsplatAsset != sourceAsset)
                    throw new InvalidOperationException("The complete source renderer became unavailable or changed.");
                if (!SameMatrix(source.transform.localToWorldMatrix, report.source_local_to_world))
                    throw new InvalidOperationException("The source transform changed during surface capture.");
                IsolateOtherCameras();
                if (index == surfaces.Length)
                {
                    // Let the last native layer-31 submission retire before restoring normal cameras.
                    if (Time.frameCount <= lastCapturedFrame + 1) { EditorApplication.QueuePlayerLoopUpdate(); return; }
                    AssetDatabase.SaveAssets();
                    Finish("captured", null);
                    return;
                }
                if (!prepared)
                {
                    ValidateSurfaceUnchanged(index);
                    ConfigureCamera(frames[index]);
                    preparedFrame = Time.frameCount;
                    deadline = EditorApplication.timeSinceStartup + 120;
                    prepared = true;
                    EditorApplication.QueuePlayerLoopUpdate();
                    return;
                }
                if (EditorApplication.timeSinceStartup > deadline) throw new TimeoutException("The complete source did not become ready on a fresh runtime frame.");
                if (Time.frameCount < preparedFrame + 2 || Time.frameCount <= lastCapturedFrame + 1)
                { EditorApplication.QueuePlayerLoopUpdate(); return; }
                if (source.SplatCount != sourceAsset.SplatCount) { EditorApplication.QueuePlayerLoopUpdate(); return; }
                ValidateSurfaceUnchanged(index);
                if (sourceResource == null) sourceResource = source.GsplatResource;
                if (!ReferenceEquals(sourceResource, source.GsplatResource)) throw new InvalidOperationException("The resident source buffers changed during capture.");
                if (source.HasStage1Selection || source.HasStage1Walls || source.Cutouts.Length != 0)
                    throw new InvalidOperationException("A rank, wall or cutout mask was reapplied while the full source was being captured.");
                if (source.GammaToLinear != report.source_gamma_to_linear || source.Brightness != 1 || source.SplatDownscaleFactor != 0 || source.SHDegree != sourceAsset.SHBands)
                    throw new InvalidOperationException("The source color or detail profile changed during surface capture.");
                var request = new UniversalRenderPipeline.SingleCameraRequest { destination = target };
                if (!RenderPipeline.SupportsRenderRequest(captureCamera, request)) throw new NotSupportedException("URP single-camera render requests are unavailable.");
                try
                {
                    // The normal Update is submitted on the original layer. Exactly one extra draw
                    // is visible to this isolated camera in this native runtime frame.
                    source.gameObject.layer = IsolationLayer;
                    source.Update();
                    if (source.RemainingCount != sourceAsset.SplatCount) throw new InvalidOperationException("Capture did not submit all original source rows.");
                    RenderPipeline.SubmitRenderRequest(captureCamera, request);
                }
                finally { if (source) source.gameObject.layer = originalLayer; }
                var capture = CreateCaptureMetadata(surfaces[index], frames[index]);
                ValidateAllSourceIds();
                capture.exact_full_source_membership = true;
                CapturePixels(capture, frames[index]);
                ImportMaterial(capture);
                report.captures.Add(capture);
                SaveReport();
                lastCapturedFrame = Time.frameCount;
                index++; prepared = false;
                EditorApplication.QueuePlayerLoopUpdate();
            }
            catch (Exception error)
            {
                Finish("failed", error);
                Debug.LogError("Stage 1 surface capture: " + error.Message);
            }
        }

        static void ConfigureCamera(SurfaceFrame frame)
        {
            if (target) { captureCamera.targetTexture = null; target.Release(); UnityEngine.Object.DestroyImmediate(target); }
            target = new RenderTexture(frame.Width, frame.Height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)
            {
                name = "Stage1 source-only surface target", hideFlags = HideFlags.HideAndDontSave,
                antiAliasing = 1, useMipMap = false, autoGenerateMips = false
            };
            if (!target.Create()) throw new InvalidOperationException("The requested surface render target could not be allocated.");
            captureCamera.transform.SetPositionAndRotation(frame.Eye, Quaternion.LookRotation(frame.Forward, frame.Up));
            captureCamera.targetTexture = target;
            captureCamera.nearClipPlane = frame.Near; captureCamera.farClipPlane = frame.Far;
            captureCamera.orthographicSize = frame.OrthoSize; captureCamera.aspect = frame.Aspect;
            captureCamera.ResetWorldToCameraMatrix(); captureCamera.ResetProjectionMatrix();
        }

        static void ValidateSurfaceUnchanged(int surfaceIndex)
        {
            if (!surfaces[surfaceIndex] || !SameMatrix(surfaces[surfaceIndex].transform.localToWorldMatrix, surfaceMatrices[surfaceIndex]) ||
                MeshSignature(surfaces[surfaceIndex].sharedMesh) != surfaceSignatures[surfaceIndex])
                throw new InvalidOperationException("An authored surface transform or UV layout changed during capture.");
        }

        static SurfaceCapture CreateCaptureMetadata(MeshFilter surface, SurfaceFrame frame)
        {
            string prefix = (index + 1).ToString("00", CultureInfo.InvariantCulture) + "-" + surface.name;
            return new SurfaceCapture
            {
                name = surface.name, hierarchy_path = HierarchyPath(surface.transform),
                global_object_id = GlobalObjectId.GetGlobalObjectIdSlow(surface).ToString(), mesh_signature = MeshSignature(surface.sharedMesh),
                image = report.output_asset_folder + "/" + prefix + ".png",
                preview = report.output_asset_folder + "/" + prefix + "-preview.png",
                material = report.output_asset_folder + "/" + prefix + ".mat",
                width = frame.Width, height = frame.Height, runtime_frame = Time.frameCount,
                source_draw_count = checked((int)source.RemainingCount), source_buffers_unchanged = ReferenceEquals(sourceResource, source.GsplatResource),
                flip_x_for_mesh_uv = frame.FlipX, world_width = frame.WorldWidth, world_height = frame.WorldHeight,
                orthographic_size = frame.OrthoSize, aspect = frame.Aspect, near = frame.Near, far = frame.Far,
                local_to_world = Matrix(surface.transform.localToWorldMatrix), bounds_min = Vector(surface.sharedMesh.bounds.min), bounds_max = Vector(surface.sharedMesh.bounds.max),
                eye = Vector(frame.Eye), forward = Vector(frame.Forward), up = Vector(frame.Up), right = Vector(frame.Right),
                world_to_camera = Matrix(captureCamera.worldToCameraMatrix), projection = Matrix(captureCamera.projectionMatrix),
                gpu_projection = Matrix(GL.GetGPUProjectionMatrix(captureCamera.projectionMatrix, true))
            };
        }

        static void ValidateAllSourceIds()
        {
            int count = report.source_count;
            var ids = new uint[count];
            source.SorterResource.OrderBuffer.GetData(ids, 0, 0, count);
            var seen = new bool[count];
            foreach (uint id in ids)
            {
                if (id >= count || seen[id]) throw new InvalidDataException("The capture's sorted source IDs are not a permutation of every original row.");
                seen[id] = true;
            }
        }

        static void CapturePixels(SurfaceCapture capture, SurfaceFrame frame)
        {
            var previousTarget = RenderTexture.active;
            Texture2D pixels = null, preview = null;
            try
            {
                pixels = new Texture2D(frame.Width, frame.Height, TextureFormat.RGBA32, false, false);
                RenderTexture.active = target;
                pixels.ReadPixels(new Rect(0, 0, frame.Width, frame.Height), 0, 0, false);
                var colors = pixels.GetPixels32();
                foreach (var color in colors)
                {
                    if (color.a > 1) capture.covered_pixels++;
                    if (color.a >= 242) capture.nearly_opaque_pixels++;
                    if (color.r > 1 || color.g > 1 || color.b > 1) capture.nonblack_pixels++;
                    capture.mean_alpha += color.a / 255.0;
                    capture.mean_srgb += (color.r + color.g + color.b) / (3.0 * 255);
                }
                capture.coverage_fraction = capture.covered_pixels / (double)colors.Length;
                capture.nearly_opaque_fraction = capture.nearly_opaque_pixels / (double)colors.Length;
                capture.mean_alpha /= colors.Length; capture.mean_srgb /= colors.Length;
                if (capture.nonblack_pixels < 100 || capture.covered_pixels < 100)
                    throw new InvalidDataException(surfaceName(capture) + " produced an empty source-only capture.");
                for (int y = 0; y < frame.Height; y++)
                {
                    int row = y * frame.Width;
                    if (frame.FlipX)
                        for (int x = 0; x < frame.Width / 2; x++)
                        {
                            int a = row + x, b = row + frame.Width - 1 - x;
                            var temporary = colors[a]; colors[a] = colors[b]; colors[b] = temporary;
                        }
                    for (int x = 0; x < frame.Width; x++) colors[row + x].a = 255;
                }
                pixels.SetPixels32(colors);
                WriteNewFile(ProjectPath(capture.image), pixels.EncodeToPNG());
                capture.image_sha256 = HashFile(ProjectPath(capture.image));
                capture.image_bytes = new FileInfo(ProjectPath(capture.image)).Length;
                float scale = Mathf.Min(1, 1024f / Math.Max(frame.Width, frame.Height));
                int width = Math.Max(1, Mathf.RoundToInt(frame.Width * scale)), height = Math.Max(1, Mathf.RoundToInt(frame.Height * scale));
                var thumbnail = new Color32[width * height];
                for (int y = 0; y < height; y++)
                    for (int x = 0; x < width; x++)
                    {
                        int sx = Math.Min(frame.Width - 1, (int)((x + .5f) * frame.Width / width));
                        int sy = Math.Min(frame.Height - 1, (int)((y + .5f) * frame.Height / height));
                        thumbnail[y * width + x] = colors[sy * frame.Width + sx];
                    }
                preview = new Texture2D(width, height, TextureFormat.RGBA32, false, false);
                preview.SetPixels32(thumbnail);
                WriteNewFile(ProjectPath(capture.preview), preview.EncodeToPNG());
            }
            finally
            {
                RenderTexture.active = previousTarget;
                if (pixels) UnityEngine.Object.DestroyImmediate(pixels);
                if (preview) UnityEngine.Object.DestroyImmediate(preview);
            }
        }

        static string surfaceName(SurfaceCapture capture) => "Surface " + capture.name;

        static void ImportMaterial(SurfaceCapture capture)
        {
            AssetDatabase.ImportAsset(capture.image, ImportAssetOptions.ForceSynchronousImport);
            var importer = AssetImporter.GetAtPath(capture.image) as TextureImporter;
            if (!importer) throw new InvalidOperationException("The captured PNG did not import as a texture.");
            importer.textureType = TextureImporterType.Default;
            importer.sRGBTexture = true; importer.alphaSource = TextureImporterAlphaSource.None;
            importer.alphaIsTransparency = false; importer.mipmapEnabled = true; importer.isReadable = false;
            importer.npotScale = TextureImporterNPOTScale.None;
            importer.maxTextureSize = Mathf.NextPowerOfTwo(Math.Max(capture.width, capture.height));
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.wrapMode = TextureWrapMode.Clamp; importer.filterMode = FilterMode.Trilinear; importer.anisoLevel = 8;
            importer.SetPlatformTextureSettings(new TextureImporterPlatformSettings
            {
                name = "Android", overridden = true, maxTextureSize = importer.maxTextureSize,
                format = TextureImporterFormat.ASTC_6x6, textureCompression = TextureImporterCompression.CompressedHQ,
                compressionQuality = 100, crunchedCompression = false
            });
            importer.SaveAndReimport();
            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(capture.image);
            if (!texture || texture.width != capture.width || texture.height != capture.height)
                throw new InvalidDataException("Texture import rescaled the requested surface resolution.");
            var material = new Material(Shader.Find("Universal Render Pipeline/Unlit")) { name = "Source capture " + capture.name };
            try
            {
                material.SetTexture("_BaseMap", texture); material.SetColor("_BaseColor", Color.white);
                material.SetFloat("_Surface", 0); material.SetFloat("_AlphaClip", 0);
                material.SetFloat("_Cull", (float)CullMode.Back);
                material.SetFloat("_ZWrite", 1); material.SetFloat("_SrcBlend", (float)BlendMode.One); material.SetFloat("_DstBlend", (float)BlendMode.Zero);
                material.DisableKeyword("_SURFACE_TYPE_TRANSPARENT"); material.DisableKeyword("_ALPHATEST_ON");
                material.SetOverrideTag("RenderType", "Opaque"); material.renderQueue = (int)RenderQueue.Geometry;
                AssetDatabase.CreateAsset(material, capture.material);
            }
            catch { UnityEngine.Object.DestroyImmediate(material); throw; }
            AssetDatabase.ImportAsset(capture.preview, ImportAssetOptions.ForceSynchronousImport);
        }

        /// <summary>Assigns every captured material atomically to the exact authored planes. The caller saves the scene.</summary>
        public static string Apply(string captureReportPath)
        {
            if (running || EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("Apply captured surface materials in Edit Mode after the bake has completed.");
            string path = ProjectPath(captureReportPath);
            var saved = JsonUtility.FromJson<CaptureReport>(File.ReadAllText(path));
            if (saved == null || saved.schema_version != 1 || saved.status != "captured" || !saved.state_restored || saved.captures?.Count != 5)
                throw new InvalidDataException("Only a complete, restored five-surface bake can be applied.");
            var scene = SceneManager.GetActiveScene();
            if (scene.path != saved.scene_path) throw new InvalidOperationException("Open the exact scene used for this capture before assigning its materials.");
            var currentSurfaces = FindSurfaces(scene);
            var currentSources = SceneComponents<GsplatRenderer>(scene);
            if (currentSources.Length != 1 || AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(currentSources[0].GsplatAsset)) != saved.source_asset_guid ||
                !SameMatrix(currentSources[0].transform.localToWorldMatrix, saved.source_local_to_world))
                throw new InvalidOperationException("The source asset or transform changed after this bake.");
            if (HashFile(ProjectPath(AssetDatabase.GetAssetPath(currentSources[0].GsplatAsset))) != saved.source_file_sha256)
                throw new InvalidDataException("The current source file no longer matches the captured source SHA-256.");
            var visuals = new MeshRenderer[5];
            var materials = new Material[5];
            var previous = new Material[5][];
            for (int i = 0; i < currentSurfaces.Length; i++)
            {
                var surface = currentSurfaces[i];
                var matches = saved.captures.Where(value => value.hierarchy_path == HierarchyPath(surface.transform)).ToArray();
                if (matches.Length != 1) throw new InvalidDataException("A captured plane identity is missing or ambiguous: " + surface.name);
                var captured = matches[0];
                if (!SameMatrix(surface.transform.localToWorldMatrix, captured.local_to_world) || MeshSignature(surface.sharedMesh) != captured.mesh_signature)
                    throw new InvalidOperationException("The authored geometry or UVs changed after capture: " + surface.name);
                if (!captured.exact_full_source_membership || captured.source_draw_count != saved.source_count ||
                    HashFile(ProjectPath(captured.image)) != captured.image_sha256)
                    throw new InvalidDataException("The full-source capture or image integrity check failed for " + surface.name);
                var material = AssetDatabase.LoadAssetAtPath<Material>(captured.material);
                var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(captured.image);
                if (!material || material.shader.name != "Universal Render Pipeline/Unlit" || !texture || material.GetTexture("_BaseMap") != texture ||
                    texture.width != captured.width || texture.height != captured.height)
                    throw new InvalidDataException("The captured unlit texture material is unavailable or changed for " + surface.name);
                visuals[i] = surface.GetComponent<MeshRenderer>(); materials[i] = material; previous[i] = visuals[i].sharedMaterials;
            }
            Undo.IncrementCurrentGroup();
            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Apply complete-source surface screenshots");
            Undo.RecordObjects(visuals, "Apply complete-source surface screenshots");
            try
            {
                for (int i = 0; i < visuals.Length; i++)
                {
                    visuals[i].sharedMaterials = new[] { materials[i] };
                    PrefabUtility.RecordPrefabInstancePropertyModifications(visuals[i]);
                    EditorUtility.SetDirty(visuals[i]);
                }
                EditorSceneManager.MarkSceneDirty(scene);
                saved.materials_applied = true; saved.applied_utc = UtcNow();
                WorkerJobStore.WriteAtomic(path, saved);
                Undo.CollapseUndoOperations(undoGroup);
                return "Assigned all five captured unlit materials; authored transforms are unchanged. Save the scene to persist the assignment.";
            }
            catch
            {
                for (int i = 0; i < visuals.Length; i++) if (visuals[i]) visuals[i].sharedMaterials = previous[i];
                throw;
            }
        }

        public static void Cancel()
        {
            if (running) Finish("interrupted", new OperationCanceledException("Surface capture was cancelled or the Editor changed mode."));
        }

        static void PlayModeChanged(PlayModeStateChange state) { if (state == PlayModeStateChange.ExitingPlayMode) Cancel(); }

        static void IsolateOtherCameras()
        {
            foreach (var camera in Resources.FindObjectsOfTypeAll<Camera>())
            {
                if (!camera || camera == captureCamera || EditorUtility.IsPersistent(camera)) continue;
                if (cameras.Any(value => value.Camera == camera)) continue;
                cameras.Add(new CameraSnapshot { Camera = camera, CullingMask = camera.cullingMask });
                camera.cullingMask &= ~(1 << IsolationLayer);
            }
        }

        static void Finish(string status, Exception error)
        {
            report.status = status; report.error = error?.ToString(); report.completed_utc = UtcNow();
            var failures = Cleanup();
            report.restoration_errors = failures.ToArray(); report.state_restored = failures.Count == 0;
            if (failures.Count != 0)
            {
                report.status = "failed";
                report.error = (report.error ?? string.Empty) + "\nState restoration failed: " + string.Join("; ", failures);
            }
            try { SaveReport(); }
            catch (Exception persistenceError) { Debug.LogError("Surface bake report could not be saved: " + persistenceError.Message); }
        }

        static List<string> Cleanup()
        {
            EditorApplication.update -= Tick; AssemblyReloadEvents.beforeAssemblyReload -= Cancel;
            EditorApplication.playModeStateChanged -= PlayModeChanged; EditorApplication.quitting -= Cancel;
            var failures = new List<string>();
            void Restore(string label, Action action) { try { action(); } catch (Exception error) { failures.Add(label + ": " + error.Message); } }
            Restore("source", () =>
            {
                if (!source) throw new InvalidOperationException("The source renderer was destroyed.");
                source.gameObject.layer = originalLayer;
                source.SHDegree = originalSH; source.GammaToLinear = originalGamma;
                source.Brightness = originalBrightness; source.SplatDownscaleFactor = originalDownscale;
                source.SortMode = originalSortMode; source.SortRefreshRate = originalSortRefresh;
                if (originalHadRank) { source.SetStage1Rank(originalRank); source.SetStage1KeepCount(originalKeep); }
                else source.ClearStage1Selection();
                if (originalWalls.Length != 0) source.SetStage1Walls(originalWalls); else source.ClearStage1Walls();
                source.enabled = originalEnabled;
                source.ForceRefresh();
            });
            foreach (var snapshot in behaviours)
                Restore("binding", () => { if (snapshot.Behaviour) snapshot.Behaviour.enabled = snapshot.Enabled; });
            foreach (var snapshot in cameras)
                Restore("camera", () => { if (snapshot.Camera) snapshot.Camera.cullingMask = snapshot.CullingMask; });
            Restore("temporary camera", () => { if (captureCamera) { captureCamera.targetTexture = null; UnityEngine.Object.DestroyImmediate(captureCamera.gameObject); } });
            Restore("render target", () => { if (target) { target.Release(); UnityEngine.Object.DestroyImmediate(target); } });
            Application.runInBackground = originalRunInBackground;
            running = false; source = null; sourceAsset = null; sourceResource = null; captureCamera = null; target = null;
            surfaces = null; frames = null; surfaceMatrices = null; surfaceSignatures = null; originalRank = null; originalWalls = null;
            behaviours.Clear(); cameras.Clear();
            EditorApplication.QueuePlayerLoopUpdate();
            return failures;
        }

        static void SaveReport() => WorkerJobStore.WriteAtomic(reportPath, report);

        /// <summary>Derives the camera from existing mesh UVs; never assumes Unity's built-in Plane UV handedness.</summary>
        public static SurfaceFrame BuildFrame(Mesh mesh, Matrix4x4 surfaceLocalToWorld, Bounds sourceBounds, Matrix4x4 sourceLocalToWorld, int maxDimension)
        {
            if (!mesh) throw new ArgumentNullException(nameof(mesh));
            if (maxDimension < 1) throw new ArgumentOutOfRangeException(nameof(maxDimension));
            RequireMatrix(surfaceLocalToWorld); RequireMatrix(sourceLocalToWorld);
            RequireFinite(sourceBounds.min); RequireFinite(sourceBounds.max);
            var vertices = mesh.vertices; var uv = mesh.uv; var triangles = mesh.triangles;
            if (vertices.Length < 4 || uv.Length != vertices.Length || triangles.Length < 3)
                throw new InvalidOperationException("A surface needs a readable planar mesh with a complete UV set.");
            Vector3 origin = default, localU = default, localV = default;
            bool found = false;
            for (int t = 0; t + 2 < triangles.Length; t += 3)
            {
                int a = triangles[t], b = triangles[t + 1], c = triangles[t + 2];
                var ab = uv[b] - uv[a]; var ac = uv[c] - uv[a];
                float determinant = ab.x * ac.y - ab.y * ac.x;
                if (Mathf.Abs(determinant) < 1e-10f) continue;
                localU = ((vertices[b] - vertices[a]) * ac.y - (vertices[c] - vertices[a]) * ab.y) / determinant;
                localV = ((vertices[c] - vertices[a]) * ab.x - (vertices[b] - vertices[a]) * ac.x) / determinant;
                origin = vertices[a] - localU * uv[a].x - localV * uv[a].y;
                found = true; break;
            }
            if (!found) throw new InvalidOperationException("The surface UVs are degenerate.");
            RequireFinite(origin); RequireFinite(localU); RequireFinite(localV);
            float tolerance = Mathf.Max(1e-5f, mesh.bounds.size.magnitude * 1e-5f);
            var uvMin = new Vector2(float.PositiveInfinity, float.PositiveInfinity);
            var uvMax = new Vector2(float.NegativeInfinity, float.NegativeInfinity);
            for (int i = 0; i < vertices.Length; i++)
            {
                RequireFinite(vertices[i]);
                if (!float.IsFinite(uv[i].x) || !float.IsFinite(uv[i].y) || Mathf.Abs(vertices[i].y) > tolerance ||
                    (vertices[i] - (origin + localU * uv[i].x + localV * uv[i].y)).magnitude > tolerance)
                    throw new InvalidOperationException("Each surface must be a flat local XZ plane with one affine UV mapping.");
                uvMin = Vector2.Min(uvMin, uv[i]); uvMax = Vector2.Max(uvMax, uv[i]);
            }
            if (uvMin.sqrMagnitude > 1e-8f || (uvMax - Vector2.one).sqrMagnitude > 1e-8f)
                throw new InvalidOperationException("The surface UVs must span the unit square without tiling.");
            var u = surfaceLocalToWorld.MultiplyVector(localU);
            var v = surfaceLocalToWorld.MultiplyVector(localV);
            var front = surfaceLocalToWorld.inverse.transpose.MultiplyVector(Vector3.up).normalized;
            RequireFinite(u); RequireFinite(v); RequireFinite(front);
            float width = u.magnitude, height = v.magnitude;
            if (width < 1e-6f || height < 1e-6f || Mathf.Abs(Vector3.Dot(u / width, v / height)) > 1e-4f ||
                Mathf.Abs(Vector3.Dot(front, u / width)) > 1e-4f || Mathf.Abs(Vector3.Dot(front, v / height)) > 1e-4f)
                throw new InvalidOperationException("The surface must have finite orthogonal world axes; sheared rectangles are unsupported.");
            var center = surfaceLocalToWorld.MultiplyPoint3x4(origin + .5f * localU + .5f * localV);
            var forward = -front; var up = v / height; var right = Vector3.Cross(up, forward).normalized;
            float frontMax = float.NegativeInfinity, frontMin = float.PositiveInfinity;
            float radius = 0;
            for (int i = 0; i < 8; i++)
            {
                var corner = sourceBounds.center + Vector3.Scale(sourceBounds.extents,
                    new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                var offset = sourceLocalToWorld.MultiplyPoint3x4(corner) - center;
                float distance = Vector3.Dot(offset, front);
                frontMax = Mathf.Max(frontMax, distance); frontMin = Mathf.Min(frontMin, distance);
                radius = Mathf.Max(radius, offset.magnitude);
            }
            float margin = Mathf.Max(.05f, radius * .01f);
            float eyeDistance = Mathf.Max(0, frontMax) + margin;
            return new SurfaceFrame
            {
                Center = center, Eye = center + front * eyeDistance, Forward = forward, Up = up, Right = right,
                WorldWidth = width, WorldHeight = height, OrthoSize = height * .5f, Aspect = width / height,
                Near = Mathf.Max(.001f, margin * .25f), Far = Mathf.Max(margin * 2, eyeDistance - frontMin + margin),
                Width = width >= height ? maxDimension : Math.Max(1, Mathf.RoundToInt(maxDimension * width / height)),
                Height = height >= width ? maxDimension : Math.Max(1, Mathf.RoundToInt(maxDimension * height / width)),
                FlipX = Vector3.Dot(right, u) < 0
            };
        }

        static MeshFilter[] FindSurfaces(Scene scene)
        {
            var roots = scene.GetRootGameObjects().Where(value => value.name == "Surfaces").ToArray();
            if (roots.Length != 1) throw new InvalidOperationException("The authored scene must contain exactly one Surfaces root.");
            var result = roots[0].GetComponentsInChildren<MeshFilter>(true).OrderBy(value => value.name, StringComparer.Ordinal).ToArray();
            if (result.Length != 5 || !result.Select(value => value.name).SequenceEqual(ExpectedSurfaces))
                throw new InvalidOperationException("Surfaces must contain exactly back, right1_1, right1_2, right2_1 and right2_2.");
            foreach (var value in result)
                if (!value.sharedMesh || value.sharedMesh.subMeshCount != 1 || !value.GetComponent<MeshRenderer>())
                    throw new InvalidOperationException(value.name + " requires one authored mesh submesh and a MeshRenderer.");
            return result;
        }

        static T[] SceneComponents<T>(Scene scene) where T : Component => scene.GetRootGameObjects().SelectMany(value => value.GetComponentsInChildren<T>(true)).ToArray();
        static string HierarchyPath(Transform value) => value.parent ? HierarchyPath(value.parent) + "/" + value.name : value.name;
        static string UtcNow() => DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        static string ProjectPath(string path) => Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(Application.dataPath, "..", path));

        static string NormalizeNewAssetFolder(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Choose a new Assets/... output folder.", nameof(path));
            string full = ProjectPath(path);
            string assets = Path.GetFullPath(Application.dataPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(assets, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Capture assets must be inside this project's Assets directory.", nameof(path));
            if (Directory.Exists(full) || File.Exists(full)) throw new IOException("Use a new output folder; existing capture assets are preserved.");
            return "Assets/" + full.Substring(assets.Length).Replace('\\', '/');
        }

        static void CreateAssetFolders(string assetFolder)
        {
            string parent = "Assets";
            foreach (string part in assetFolder.Substring("Assets/".Length).Split('/'))
            {
                string next = parent + "/" + part;
                if (!AssetDatabase.IsValidFolder(next))
                {
                    if (Directory.Exists(ProjectPath(next))) AssetDatabase.ImportAsset(next, ImportAssetOptions.ForceSynchronousImport);
                    else AssetDatabase.CreateFolder(parent, part);
                    if (!AssetDatabase.IsValidFolder(next)) throw new IOException("The output asset folder could not be registered: " + next);
                }
                parent = next;
            }
        }

        static void WriteNewFile(string path, byte[] data)
        {
            using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            output.Write(data, 0, data.Length);
        }

        static string HashFile(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty).ToLowerInvariant();
        }

        static string MeshSignature(Mesh mesh)
        {
            using var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
            {
                var vertices = mesh.vertices; var uv = mesh.uv; var triangles = mesh.triangles;
                writer.Write(vertices.Length); writer.Write(uv.Length); writer.Write(triangles.Length);
                foreach (var value in vertices) { writer.Write(value.x); writer.Write(value.y); writer.Write(value.z); }
                foreach (var value in uv) { writer.Write(value.x); writer.Write(value.y); }
                foreach (int value in triangles) writer.Write(value);
            }
            stream.Position = 0;
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty).ToLowerInvariant();
        }

        static float[] Vector(Vector3 value) => new[] { value.x, value.y, value.z };
        static float[] Matrix(Matrix4x4 value)
        {
            var result = new float[16];
            for (int row = 0; row < 4; row++) for (int col = 0; col < 4; col++) result[row * 4 + col] = value[row, col];
            return result;
        }

        static bool SameMatrix(Matrix4x4 value, float[] saved)
        {
            if (saved == null || saved.Length != 16) return false;
            for (int row = 0; row < 4; row++) for (int col = 0; col < 4; col++)
                if (!float.IsFinite(saved[row * 4 + col]) || Mathf.Abs(value[row, col] - saved[row * 4 + col]) > 1e-5f) return false;
            return true;
        }

        static void RequireFinite(Vector3 value)
        {
            if (!float.IsFinite(value.x) || !float.IsFinite(value.y) || !float.IsFinite(value.z)) throw new InvalidOperationException("Surface geometry contains nonfinite values.");
        }

        static void RequireMatrix(Matrix4x4 value)
        {
            for (int row = 0; row < 4; row++) for (int col = 0; col < 4; col++)
                if (!float.IsFinite(value[row, col])) throw new InvalidOperationException("Surface geometry contains a nonfinite transform.");
            if (Mathf.Abs(value.determinant) < 1e-12f) throw new InvalidOperationException("Surface transforms must be invertible.");
        }
    }
}
