using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Gsplat;
using Gsplat.Editor;
using UnityEditor;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;

namespace SplatPreprocess.Editor
{
    [Serializable]
    public sealed class Stage1SceneVertex { public double[] p; }

    [Serializable]
    public sealed class Stage1SceneWall
    {
        public string name;
        public bool floor;
        public double[] world_to_local, local_to_world, normal, position, bounds_min, bounds_max;
    }

    [Serializable]
    public sealed class Stage1SceneBox
    {
        public string name;
        public double[] local_to_world, world_to_local, center, size;
    }

    [Serializable]
    public sealed class Stage1SceneSnapshot
    {
        public int schema_version = 1;
        public string scene_path, source_path, source_hash;
        public string coordinate_profile = "RUB_to_RUF_once";
        public string calibration_status = "user_authored_unity_units";
        public double[] model_local_to_world;
        public Stage1SceneVertex[] nav_vertices;
        public int[] nav_indices, nav_areas;
        public Stage1SceneWall[] walls;
        public Stage1SceneBox[] deletion_boxes;
        public Stage1SceneWall[] surfaces;
        public double[] head_position, head_projection;
        public double head_fov, head_near, head_far;
    }

    [Serializable]
    public sealed class Stage1PreviewLease
    {
        public int schema_version = 1;
        public string owner = "stage1-round1";
        public string lease_id, renderer_global_id, job_id, scene_snapshot_path;
        public bool was_enabled;
        public double created_utc;
    }

    /// <summary>Persists the original renderer state before pausing it. The resolver is the GlobalObjectId boundary.</summary>
    public sealed class Stage1PreviewLeaseStore
    {
        readonly Func<string, GsplatRenderer> resolveRenderer;
        public string Path { get; }
        public Stage1PreviewLeaseStore(string root, Func<string, GsplatRenderer> resolveRenderer)
        {
            Directory.CreateDirectory(root);
            Path = System.IO.Path.Combine(root, "preview-lease.json");
            this.resolveRenderer = resolveRenderer ?? throw new ArgumentNullException(nameof(resolveRenderer));
        }

        public Stage1PreviewLease Read() => WorkerJobStore.Read<Stage1PreviewLease>(Path);

        public Stage1PreviewLease Acquire(string rendererId, GsplatRenderer renderer)
        {
            if (string.IsNullOrEmpty(rendererId) || !renderer) throw new ArgumentException("A persistent renderer identity is required");
            if (File.Exists(Path)) throw new InvalidOperationException("An existing preview lease must finish or recover before another round starts");
            var lease = new Stage1PreviewLease
            {
                lease_id = Guid.NewGuid().ToString("N"), renderer_global_id = rendererId,
                was_enabled = renderer.enabled, created_utc = WorkerJobStore.Now
            };
            // CreateNew is intentional: never overwrite another launcher's recovery journal.
            using (var stream = new FileStream(Path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false))) writer.Write(JsonUtility.ToJson(lease, true));
            renderer.enabled = false;
            return lease;
        }

        public void AttachJob(string jobId)
        {
            if (string.IsNullOrEmpty(jobId)) throw new ArgumentException("Job ID is required", nameof(jobId));
            var lease = RequireOwned();
            if (!string.IsNullOrEmpty(lease.job_id) && lease.job_id != jobId) throw new InvalidOperationException("The preview lease already belongs to a different job");
            lease.job_id = jobId;
            WorkerJobStore.WriteAtomic(Path, lease);
        }

        public void AttachSnapshot(string snapshotPath)
        {
            var lease = RequireOwned();
            lease.scene_snapshot_path = System.IO.Path.GetFullPath(snapshotPath);
            WorkerJobStore.WriteAtomic(Path, lease);
        }

        public bool TryRestore(Func<string, bool> jobStillActive)
        {
            var lease = Read();
            if (!IsOwned(lease) || jobStillActive(lease.job_id)) return false;
            var renderer = resolveRenderer(lease.renderer_global_id);
            if (!renderer) return false; // Its saved scene may be temporarily unloaded; preserve the recovery record.
            renderer.enabled = lease.was_enabled;
            var current = Read();
            if (IsOwned(current) && current.lease_id == lease.lease_id) File.Delete(Path);
            return true;
        }

        public static bool IsOwned(Stage1PreviewLease lease) => lease != null && lease.schema_version == 1 && lease.owner == "stage1-round1";
        Stage1PreviewLease RequireOwned()
        {
            var lease = Read();
            if (!IsOwned(lease)) throw new InvalidOperationException("No Stage 1 preview lease is owned by this service");
            return lease;
        }
    }

    [InitializeOnLoad]
    public static class Stage1Round1Service
    {
        [Serializable]
        sealed class IdentityReport
        {
            public int schema_version, source_count, asset_count, asset_pruned_count, sh_degree;
            public string status, source_sha256, imported_file_sha256, asset_guid, compression, source_coordinates, id_rule, mapping_encoding, error;
            public float opacity_prune_threshold;
            public long checked_rows, checked_scalars, mismatch_rows, mismatch_scalars, storage_to_source_bytes;
            public bool bitwise_float32_comparison, source_and_imported_file_hashes_match, duplicate_position_fixture_passed, shuffled_fixture_rejected;
        }

        [Serializable]
        sealed class FrozenRankProfile { public WallProfile profile; }

        [Serializable]
        sealed class WallProfile { public float rear_depth; }

        static double nextRecovery;
        static bool launching;
        static string lastRecoveryError;
        public static string LeasePath => System.IO.Path.Combine(WorkerEnvironment.DataRoot, "preview-lease.json");
        static Stage1PreviewLeaseStore Leases => new Stage1PreviewLeaseStore(WorkerEnvironment.DataRoot, ResolveRenderer);

        static Stage1Round1Service() => EditorApplication.update += Tick;

        public static string ExportScene(string sourcePath)
        {
            var snapshot = CaptureScene(sourcePath, out _);
            var directory = System.IO.Path.Combine(WorkerEnvironment.DataRoot, "inputs");
            Directory.CreateDirectory(directory);
            var path = System.IO.Path.Combine(directory, "scene-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N") + ".json");
            var temporary = path + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false))) writer.Write(JsonUtility.ToJson(snapshot, true));
                ValidateSnapshotJson(snapshot, File.ReadAllText(temporary));
                File.Move(temporary, path);
            }
            catch (InvalidDataException exception)
            {
                var rejected = path + ".rejected";
                if (File.Exists(temporary)) File.Move(temporary, rejected);
                throw new InvalidDataException(exception.Message + ". Rejected snapshot: " + rejected, exception);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return path;
        }

        public static void ValidateSnapshotJson(Stage1SceneSnapshot expected, string json)
        {
            if (expected == null || string.IsNullOrEmpty(json) || !json.Contains("\"nav_vertices\"") || !json.Contains("\"walls\""))
                throw new InvalidDataException("Scene serialization omitted the authored NavMesh or walls");
            var actual = JsonUtility.FromJson<Stage1SceneSnapshot>(json);
            if (actual == null) throw new InvalidDataException("Scene serialization returned no snapshot");
            CheckValue("schema_version", actual.schema_version, expected.schema_version);
            CheckValue("source_hash", actual.source_hash, expected.source_hash);
            CheckValue("source_path", actual.source_path, expected.source_path);
            CheckValue("scene_path", actual.scene_path, expected.scene_path);
            CheckValue("coordinate_profile", actual.coordinate_profile, expected.coordinate_profile);
            CheckValue("calibration_status", actual.calibration_status, expected.calibration_status);
            CheckArray("model_local_to_world", actual.model_local_to_world, expected.model_local_to_world);
            CheckArray("nav_indices", actual.nav_indices, expected.nav_indices);
            CheckArray("nav_areas", actual.nav_areas, expected.nav_areas);
            CheckArray("head_position", actual.head_position, expected.head_position);
            CheckArray("head_projection", actual.head_projection, expected.head_projection);
            CheckValue("head_fov", actual.head_fov, expected.head_fov);
            CheckValue("head_near", actual.head_near, expected.head_near);
            CheckValue("head_far", actual.head_far, expected.head_far);
            CheckValue("nav_vertices.Length", actual.nav_vertices?.Length ?? -1, expected.nav_vertices?.Length ?? -1);
            if (actual.nav_vertices == null || actual.nav_vertices.Length == 0) throw new InvalidDataException("Scene serialization returned no NavMesh vertices");
            CheckValue("walls.Length", actual.walls?.Length ?? -1, expected.walls?.Length ?? -1);
            if (actual.walls == null || actual.walls.Count(wall => wall != null && wall.floor) != 1)
                throw new InvalidDataException("Scene serialization must preserve exactly one Floor");
            for (var i = 0; i < expected.nav_vertices.Length; i++)
            {
                if (actual.nav_vertices[i] == null) throw new InvalidDataException("Scene serialization omitted nav_vertices[" + i + "]");
                CheckArray("nav_vertices[" + i + "].p", actual.nav_vertices[i].p, expected.nav_vertices[i].p);
            }
            for (var i = 0; i < expected.walls.Length; i++)
            {
                var a = actual.walls[i]; var b = expected.walls[i];
                var field = "walls[" + i + "]";
                if (a == null) throw new InvalidDataException("Scene serialization omitted " + field);
                CheckValue(field + ".name", a.name, b.name); CheckValue(field + ".floor", a.floor, b.floor);
                CheckArray(field + ".world_to_local", a.world_to_local, b.world_to_local);
                CheckArray(field + ".local_to_world", a.local_to_world, b.local_to_world);
                CheckArray(field + ".normal", a.normal, b.normal); CheckArray(field + ".position", a.position, b.position);
                CheckArray(field + ".bounds_min", a.bounds_min, b.bounds_min); CheckArray(field + ".bounds_max", a.bounds_max, b.bounds_max);
            }
            ValidateCustomizationSnapshot(expected, actual);
        }

        public static void ValidateCustomizationSnapshot(Stage1SceneSnapshot expected, Stage1SceneSnapshot actual)
        {
            var aBoxes = actual.deletion_boxes ?? Array.Empty<Stage1SceneBox>();
            var bBoxes = expected.deletion_boxes ?? Array.Empty<Stage1SceneBox>();
            CheckValue("deletion_boxes.Length", aBoxes.Length, bBoxes.Length);
            for (int i = 0; i < bBoxes.Length; i++)
            {
                var a = aBoxes[i]; var b = bBoxes[i]; var field = "deletion_boxes[" + i + "]";
                if (a == null || b == null) throw new InvalidDataException("Missing " + field);
                CheckValue(field + ".name", a.name, b.name);
                CheckArray(field + ".local_to_world", a.local_to_world, b.local_to_world);
                CheckArray(field + ".world_to_local", a.world_to_local, b.world_to_local);
                CheckArray(field + ".center", a.center, b.center);
                CheckArray(field + ".size", a.size, b.size);
            }
            var aSurfaces = actual.surfaces ?? Array.Empty<Stage1SceneWall>();
            var bSurfaces = expected.surfaces ?? Array.Empty<Stage1SceneWall>();
            CheckValue("surfaces.Length", aSurfaces.Length, bSurfaces.Length);
            for (int i = 0; i < bSurfaces.Length; i++)
            {
                var a = aSurfaces[i]; var b = bSurfaces[i]; var field = "surfaces[" + i + "]";
                if (a == null || b == null) throw new InvalidDataException("Missing " + field);
                CheckValue(field + ".name", a.name, b.name);
                CheckArray(field + ".local_to_world", a.local_to_world, b.local_to_world);
                CheckArray(field + ".world_to_local", a.world_to_local, b.world_to_local);
                CheckArray(field + ".position", a.position, b.position);
                CheckArray(field + ".normal", a.normal, b.normal);
                CheckArray(field + ".bounds_min", a.bounds_min, b.bounds_min);
                CheckArray(field + ".bounds_max", a.bounds_max, b.bounds_max);
            }
        }

        public static void ValidateReviewSnapshot(Stage1SceneSnapshot current, string savedJson)
        {
            var recorded = JsonUtility.FromJson<Stage1SceneSnapshot>(savedJson);
            if (recorded == null) throw new InvalidDataException("The saved ranking has no scene snapshot");
            // The frozen scoring cameras are stored separately. Navigating the review
            // camera or resizing Game view does not alter the source or eligibility.
            recorded.head_position = current.head_position;
            recorded.head_projection = current.head_projection;
            recorded.head_fov = current.head_fov;
            recorded.head_near = current.head_near;
            recorded.head_far = current.head_far;
            ValidateSnapshotJson(current, JsonUtility.ToJson(recorded));
        }

        static void CheckValue<T>(string field, T actual, T expected)
        {
            // Every numeric double in this snapshot originated in a Unity float32 field.
            // Unity's JSON reader may shift a double by one ULP; compare the original float32
            // bit pattern, not a geometric tolerance. A one-bit Unity geometry change still fails.
            var equal = actual is double actualNumber && expected is double expectedNumber
                ? FloatBits(field, actualNumber) == FloatBits(field, expectedNumber)
                : EqualityComparer<T>.Default.Equals(actual, expected);
            if (!equal)
                throw new InvalidDataException("Scene serialization changed " + field + ": expected " + Describe(expected) + ", read " + Describe(actual));
        }

        static int FloatBits(string field, double value)
        {
            var single = (float)value;
            if (double.IsNaN(value) || double.IsInfinity(value) || float.IsNaN(single) || float.IsInfinity(single))
                throw new InvalidDataException("Scene serialization has a nonfinite float32 value at " + field);
            if (single == 0f) return 0; // Signed zero is the same authored position/coefficient.
            return BitConverter.ToInt32(BitConverter.GetBytes(single), 0);
        }

        static void CheckArray<T>(string field, T[] actual, T[] expected)
        {
            if (actual == null || expected == null) throw new InvalidDataException("Scene serialization omitted " + field);
            CheckValue(field + ".Length", actual.Length, expected.Length);
            for (var i = 0; i < expected.Length; i++) CheckValue(field + "[" + i + "]", actual[i], expected[i]);
        }

        static string Describe<T>(T value)
        {
            if (value is double number) return number.ToString("R", CultureInfo.InvariantCulture);
            if (value is float single) return single.ToString("R", CultureInfo.InvariantCulture);
            return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "null";
        }

        public static string StartRound(string sourcePath, string samplingJson = "")
        {
            RequireEditMode();
            if (launching) throw new InvalidOperationException("The scoring round is already starting");
            if (WorkerEnvironment.SetupRunning) throw new InvalidOperationException("Wait for worker setup to finish");
            var jobs = new WorkerJobStore(WorkerEnvironment.DataRoot);
            if (jobs.Reconcile().Any(job => !job.IsTerminal)) throw new InvalidOperationException("A worker job is already active; wait or cancel it first");
            if (!File.Exists(WorkerEnvironment.Python)) throw new FileNotFoundException("Install the local worker before scoring", WorkerEnvironment.Python);
            TryRestorePreview();
            if (File.Exists(LeasePath)) throw new InvalidOperationException("A preview recovery lease already exists; restore its saved scene before starting another round");
            launching = true;
            var leases = Leases;
            var acquired = false;
            try
            {
                var snapshotPath = ExportScene(sourcePath);
                var renderer = SourceRenderer(SceneManager.GetActiveScene());
                var identity = GlobalObjectId.GetGlobalObjectIdSlow(renderer).ToString();
                leases.Acquire(identity, renderer);
                acquired = true;
                leases.AttachSnapshot(snapshotPath);
                var jobId = new WorkerProcessHost(jobs, WorkerEnvironment.Python, WorkerEnvironment.WorkerFolder).Start("round1", sourcePath, snapshotPath, samplingJson);
                leases.AttachJob(jobId);
                return jobId;
            }
            catch
            {
                // Recovery matches the immutable snapshot to the request if launch succeeded before AttachJob failed.
                if (acquired) TryRestorePreview();
                throw;
            }
            finally { launching = false; }
        }

        public static bool TryRestorePreview()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || !File.Exists(LeasePath)) return false;
            var leases = Leases;
            var lease = leases.Read();
            if (!Stage1PreviewLeaseStore.IsOwned(lease)) return false;
            var store = new WorkerJobStore(WorkerEnvironment.DataRoot);
            var jobs = store.Reconcile();
            if (string.IsNullOrEmpty(lease.job_id) && !string.IsNullOrEmpty(lease.scene_snapshot_path))
            {
                foreach (var job in jobs.Where(job => job.operation == "round1"))
                {
                    var request = WorkerJobStore.Read<JobRequest>(System.IO.Path.Combine(store.JobDirectory(job.job_id), "request.json"));
                    if (request == null || request.scene_path != lease.scene_snapshot_path) continue;
                    leases.AttachJob(job.job_id);
                    lease.job_id = job.job_id;
                    break;
                }
            }
            return leases.TryRestore(id => !string.IsNullOrEmpty(id) && jobs.Any(job => job.job_id == id && !job.IsTerminal));
        }

        public static void LoadReviewRank(string manifestPath, string sourcePath)
            => LoadReviewRankInternal(manifestPath, sourcePath, null);

        /// <summary>Restore saved preview state after a reload without rewriting or dirtying the scene.</summary>
        public static void ReloadConfiguredReview(Stage1SelectionController controller)
        {
            if (!controller || string.IsNullOrEmpty(controller.RankManifestPath)) throw new InvalidOperationException("No saved preview ranking is configured");
            var path = controller.RankManifestPath;
            if (!System.IO.Path.IsPathRooted(path)) path = System.IO.Path.Combine(WorkerEnvironment.ProjectRoot, path);
            var snapshot = WorkerJobStore.Read<Stage1SceneSnapshot>(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path), "scene.json"));
            if (snapshot == null) throw new InvalidDataException("The configured ranking has no source scene snapshot");
            LoadReviewRankInternal(path, snapshot.source_path, controller);
        }

        static void LoadReviewRankInternal(string manifestPath, string sourcePath, Stage1SelectionController restoring)
        {
            RequireEditMode();
            var store = new WorkerJobStore(WorkerEnvironment.DataRoot);
            if (WorkerEnvironment.SetupRunning || store.Reconcile().Any(job => !job.IsTerminal))
                throw new InvalidOperationException("Finish processing before loading a review rank");
            var snapshot = CaptureScene(sourcePath, out var renderer);
            var savedScenePath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(manifestPath)), "scene.json");
            if (!File.Exists(savedScenePath)) throw new InvalidDataException("The completed rank has no saved scene snapshot");
            try { ValidateReviewSnapshot(snapshot, File.ReadAllText(savedScenePath)); }
            catch (InvalidDataException exception)
            {
                throw new InvalidDataException("The saved ranking differs from the current scene or customization. Update the customization or process the current scene before loading it. " + exception.Message, exception);
            }
            var manifest = WorkerJobStore.Read<RankManifest>(manifestPath);
            if (manifest == null) throw new FileNotFoundException("The completed rank manifest is missing", manifestPath);
            if (manifest.source_hash != snapshot.source_hash) throw new InvalidDataException("The completed ranking uses another source");
            var frozenProfile = WorkerJobStore.Read<FrozenRankProfile>(manifestPath);
            if (frozenProfile?.profile == null) throw new InvalidDataException("The completed rank does not record its wall rear-depth profile");
            ValidateWallProfile(frozenProfile.profile.rear_depth);
            var controllers = SceneManager.GetActiveScene().GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren<Stage1SelectionController>(true)).ToArray();
            if (controllers.Length != 1) throw new InvalidOperationException("Bind exactly one Stage 1 selection controller in the review scene first");
            var rank = Stage1RankLoader.Load(manifestPath, snapshot.source_hash, manifest.scene_hash);
            var controller = controllers[0];
            if (restoring)
            {
                if (restoring != controller || controller.Renderer != renderer) throw new InvalidDataException("Restore the configured source renderer binding before preview");
                controller.LoadRank(rank.Manifest, rank.Order);
                return;
            }
            Undo.RecordObject(controller, "Load Stage 1 review ranking");
            controller.Bind(renderer, System.IO.Path.GetFullPath(manifestPath), snapshot.source_hash, manifest.scene_hash);
            controller.LoadRank(rank.Manifest, rank.Order);
            EditorUtility.SetDirty(controller);
        }

        public static Stage1SceneBox[] CaptureDeletionBoxes(Transform root)
        {
            if (!root) return Array.Empty<Stage1SceneBox>();
            return root.GetComponentsInChildren<BoxCollider>(true).OrderBy(box => HierarchyPath(box.transform), StringComparer.Ordinal).Select(box =>
            {
                if (box.size.x <= 0 || box.size.y <= 0 || box.size.z <= 0 || Mathf.Abs(box.transform.localToWorldMatrix.determinant) < 1e-12f)
                    throw new InvalidDataException("Deletion boxes require positive dimensions and an invertible transform: " + box.name);
                return new Stage1SceneBox { name = HierarchyPath(box.transform), center = Vector(box.center), size = Vector(box.size),
                    local_to_world = Matrix(box.transform.localToWorldMatrix), world_to_local = Matrix(box.transform.worldToLocalMatrix) };
            }).ToArray();
        }

        public static Stage1SceneWall[] CaptureSurfaces(Transform root)
        {
            if (!root) return Array.Empty<Stage1SceneWall>();
            return root.GetComponentsInChildren<MeshFilter>(true).Where(mesh => mesh.gameObject.activeInHierarchy)
                .OrderBy(mesh => HierarchyPath(mesh.transform), StringComparer.Ordinal).Select(mesh =>
                {
                    if (!mesh.sharedMesh || !mesh.GetComponent<MeshRenderer>()) throw new InvalidDataException("Surface requires a mesh and renderer: " + mesh.name);
                    var bounds = mesh.sharedMesh.bounds;
                    if (bounds.size.x <= 0 || bounds.size.z <= 0 || Mathf.Abs(bounds.min.y) > 1e-5 || Mathf.Abs(bounds.max.y) > 1e-5 ||
                        Mathf.Abs(mesh.transform.localToWorldMatrix.determinant) < 1e-12f)
                        throw new InvalidDataException("Surface must be a finite local XZ plane: " + mesh.name);
                    return new Stage1SceneWall { name = HierarchyPath(mesh.transform), floor = false,
                        local_to_world = Matrix(mesh.transform.localToWorldMatrix), world_to_local = Matrix(mesh.transform.worldToLocalMatrix),
                        position = Vector(mesh.transform.position), normal = Vector(mesh.transform.up), bounds_min = Vector(bounds.min), bounds_max = Vector(bounds.max) };
                }).ToArray();
        }

        /// <summary>Check the fresh full-row audit against the inspected source and the exact scene renderer asset.</summary>
        public static void ValidateIdentityReport(string reportPath, string expectedSourceHash, string expectedAssetGuid, int expectedSourceCount)
        {
            var report = WorkerJobStore.Read<IdentityReport>(reportPath);
            void Require(bool condition, string field)
            {
                if (!condition) throw new InvalidDataException("Source identity audit did not validate " + field + ". Report: " + reportPath);
            }
            Require(IsHex(expectedSourceHash, 64) && IsHex(expectedAssetGuid, 32) && expectedSourceCount > 0, "the expected source/asset identity");
            Require(report != null, "a persisted report");
            Require(report.schema_version == 1 && report.status == "passed" && string.IsNullOrEmpty(report.error), "successful completion" + (string.IsNullOrEmpty(report.error) ? "" : ": " + report.error));
            Require(report.source_sha256 == expectedSourceHash, "source_sha256 against the inspected source");
            Require(report.imported_file_sha256 == expectedSourceHash && report.source_and_imported_file_hashes_match, "the imported source file hash");
            Require(string.Equals(report.asset_guid, expectedAssetGuid, StringComparison.OrdinalIgnoreCase), "asset_guid against the scene renderer");
            Require(report.source_count == expectedSourceCount && report.asset_count == expectedSourceCount && report.asset_pruned_count == 0, "the full original source count");
            Require(report.compression == "Uncompressed" && report.source_coordinates == "RUB" && report.sh_degree == 0 && report.opacity_prune_threshold == 0f, "the Uncompressed/RUB/SH0/unpruned import profile");
            Require(report.checked_rows == expectedSourceCount && report.checked_scalars == (long)expectedSourceCount * 14 && report.mismatch_rows == 0 && report.mismatch_scalars == 0, "all original rows and 14 attributes per row");
            Require(report.bitwise_float32_comparison && report.duplicate_position_fixture_passed && report.shuffled_fixture_rejected, "exact float32 row identity");
            Require(report.id_rule == "zero_based_vertex_row" && report.mapping_encoding == "little_endian_uint32" && report.storage_to_source_bytes == (long)expectedSourceCount * 4, "the original-row storage map");
        }

        /// <summary>The completed first-round worker profile records a fixed 0.1-unit rear band.</summary>
        public static void ValidateWallProfile(float rearDepth)
        {
            if (rearDepth != .1f)
                throw new InvalidDataException("Round 1 requires the authored wall rear depth to be exactly 0.1 Unity units, matching the frozen rank profile");
        }

        /// <summary>Read only the active finite wall planes supported by the persistent preview binding.</summary>
        public static Stage1SceneWall[] CaptureActiveWalls(Transform wallsRoot)
        {
            if (!wallsRoot) throw new ArgumentNullException(nameof(wallsRoot));
            var meshes = wallsRoot.GetComponentsInChildren<MeshFilter>(true)
                .Where(mesh => mesh.gameObject.activeInHierarchy)
                .OrderBy(mesh => HierarchyPath(mesh.transform), StringComparer.Ordinal).ToArray();
            if (meshes.Count(mesh => mesh.name == "Floor") != 1)
                throw new InvalidDataException("The Walls group must contain exactly one active Floor mesh");
            if (meshes.Count(mesh => mesh.name != "Floor") > 16)
                throw new InvalidDataException("At most 16 active non-Floor authored walls are supported by the preview");
            var walls = new Stage1SceneWall[meshes.Length];
            for (var i = 0; i < meshes.Length; i++)
            {
                var mesh = meshes[i];
                if (!mesh.sharedMesh || !mesh.GetComponent<MeshRenderer>())
                    throw new InvalidDataException(mesh.name + ": each authored plane requires a MeshFilter mesh and MeshRenderer");
                var min = mesh.sharedMesh.bounds.min;
                var max = mesh.sharedMesh.bounds.max;
                Vector(min); Vector(max);
                if (max.x <= min.x || max.z <= min.z || Mathf.Abs(min.y) > 1e-5f || Mathf.Abs(max.y) > 1e-5f)
                    throw new InvalidDataException(mesh.name + ": only finite, flat local XZ rectangles at local Y=0 are supported");
                var transform = mesh.transform;
                var localToWorld = transform.localToWorldMatrix;
                Matrix(localToWorld);
                var determinant = Finite(localToWorld.determinant);
                var normal = transform.up;
                Vector(normal);
                if (Math.Abs(determinant) < 1e-12 || normal.sqrMagnitude < .999f)
                    throw new InvalidDataException(mesh.name + ": the plane transform must be invertible with a valid front direction");
                var front = normal.normalized;
                var xAxis = localToWorld.MultiplyVector(Vector3.right).normalized;
                var zAxis = localToWorld.MultiplyVector(Vector3.forward).normalized;
                if (Mathf.Abs(Vector3.Dot(front, xAxis)) > 1e-4f || Mathf.Abs(Vector3.Dot(front, zAxis)) > 1e-4f)
                    throw new InvalidDataException(mesh.name + ": sheared transforms whose up direction is not the plane normal are unsupported");
                walls[i] = new Stage1SceneWall
                {
                    name = mesh.name, floor = mesh.name == "Floor", world_to_local = Matrix(transform.worldToLocalMatrix),
                    local_to_world = Matrix(localToWorld), normal = Vector(normal),
                    position = Vector(transform.position), bounds_min = Vector(min), bounds_max = Vector(max)
                };
            }
            return walls;
        }

        static Stage1SceneSnapshot CaptureScene(string sourcePath, out GsplatRenderer renderer)
        {
            RequireEditMode();
            var scene = SceneManager.GetActiveScene();
            if (!scene.IsValid() || !scene.isLoaded || string.IsNullOrEmpty(scene.path)) throw new InvalidOperationException("Save the active review scene before capturing it");
            if (scene.isDirty) throw new InvalidOperationException("Save the current scene changes before capturing the scoring input");
            if (!File.Exists(sourcePath)) throw new FileNotFoundException("Choose the inspected original source PLY", sourcePath);
            var store = new WorkerJobStore(WorkerEnvironment.DataRoot);
            var pointer = store.Current("inspect") ?? throw new InvalidOperationException("Inspect the full source before scoring");
            var source = WorkerJobStore.Read<SourceManifest>(System.IO.Path.Combine(pointer.result_dir, "source_manifest.json"));
            if (source == null) throw new InvalidDataException("The source inspection manifest is missing");
            source.Validate();
            if (!string.Equals(System.IO.Path.GetFullPath(source.source_path), System.IO.Path.GetFullPath(sourcePath), StringComparison.OrdinalIgnoreCase) ||
                new FileInfo(sourcePath).Length != source.byte_length)
                throw new InvalidDataException("Inspect the selected source before using it in this round");
            renderer = SourceRenderer(scene);
            if (!(renderer.GsplatAsset is GsplatAssetUncompressed asset) || asset.SplatCount != source.vertex_count || asset.PrunedSplatCount != 0 ||
                source.sh_degree != 0 || asset.SHBands != 0 || (asset.SHs != null && asset.SHs.Length != 0))
                throw new InvalidDataException("The source renderer must contain every original row in the uncompressed, unpruned SH0 reference asset");
            var assetPath = AssetDatabase.GetAssetPath(asset);
            var assetGuid = AssetDatabase.AssetPathToGUID(assetPath);
            var importer = AssetImporter.GetAtPath(assetPath) as GsplatImporter;
            if (!IsHex(assetGuid, 32) || !importer || importer.Compression != CompressionMode.Uncompressed ||
                importer.SourceCoordinates != SourceCoordinates.RUB || importer.OpacityPruneThreshold != 0f)
                throw new InvalidDataException("The scene renderer must use a saved GsplatImporter asset with Uncompressed compression, explicit RUB coordinates and zero opacity pruning");
            var allTransforms = scene.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren<Transform>(true)).ToArray();
            var wallRoots = allTransforms.Where(transform => transform.name == "Walls").ToArray();
            if (wallRoots.Length != 1) throw new InvalidOperationException("The active scene must contain exactly one authored Walls group");
            var walls = CaptureActiveWalls(wallRoots[0]);
            var sourceRenderer = renderer;
            var wallBindings = scene.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren<Stage1AuthoredWalls>(true))
                .Where(binding => binding.Renderer == sourceRenderer).ToArray();
            if (wallBindings.Length > 1) throw new InvalidDataException("Only one authored wall binding may target the source renderer");
            foreach (var binding in wallBindings)
            {
                if (binding.WallsRoot != wallRoots[0]) throw new InvalidDataException("The preview wall binding must use the same authored Walls group as the scoring snapshot");
                ValidateWallProfile(binding.RearDepth);
            }
            var nav = NavMesh.CalculateTriangulation();
            if (nav.vertices.Length == 0 || nav.indices.Length < 3 || nav.indices.Length % 3 != 0 || nav.areas.Length != nav.indices.Length / 3)
                throw new InvalidOperationException("Bake a nonempty NavMesh before generating review views");
            var cameras = scene.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren<Camera>(true)).Where(camera => camera.name == "CenterEyeAnchor").ToArray();
            if (cameras.Length != 1) throw new InvalidOperationException("The active scene must contain one CenterEyeAnchor camera");
            var head = cameras[0];
            Transform OptionalRoot(string name)
            {
                var roots = allTransforms.Where(value => value.name == name).ToArray();
                if (roots.Length > 1) throw new InvalidDataException("Only one " + name + " group may define customization geometry");
                return roots.Length == 0 ? null : roots[0];
            }
            var snapshot = new Stage1SceneSnapshot
            {
                scene_path = scene.path, source_path = System.IO.Path.GetFullPath(source.source_path), source_hash = source.sha256,
                model_local_to_world = Matrix(renderer.transform.localToWorldMatrix),
                nav_vertices = nav.vertices.Select(vertex => new Stage1SceneVertex { p = Vector(vertex) }).ToArray(),
                nav_indices = nav.indices, nav_areas = nav.areas, walls = walls,
                deletion_boxes = CaptureDeletionBoxes(OptionalRoot("Deletable")), surfaces = CaptureSurfaces(OptionalRoot("Surfaces")),
                head_position = Vector(head.transform.position), head_projection = Matrix(head.projectionMatrix),
                head_fov = Finite(head.fieldOfView), head_near = Finite(head.nearClipPlane), head_far = Finite(head.farClipPlane)
            };
            // The audit chooses among loaded assets: bind its proof back to this exact renderer GUID.
            // Keep its immutable artifacts outside the scene DTO so timestamps and paths never affect scene_hash.
            var auditDirectory = System.IO.Path.Combine(WorkerEnvironment.DataRoot, "identity-audits",
                "audit-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N"));
            Stage1SourceIdentityAudit.Run(sourcePath, auditDirectory);
            ValidateIdentityReport(System.IO.Path.Combine(auditDirectory, "identity_report.json"), source.sha256, assetGuid, source.vertex_count);
            return snapshot;
        }

        static bool IsHex(string value, int length) => value != null && value.Length == length && value.All(Uri.IsHexDigit);

        static GsplatRenderer SourceRenderer(Scene scene)
        {
            var renderers = scene.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren<GsplatRenderer>(true)).ToArray();
            if (renderers.Length != 1 || !renderers[0].GsplatAsset) throw new InvalidOperationException("The active scene needs exactly one assigned source Gsplat renderer");
            return renderers[0];
        }

        static double[] Matrix(Matrix4x4 value)
        {
            var result = new double[16];
            for (var row = 0; row < 4; row++) for (var column = 0; column < 4; column++) result[4 * row + column] = Finite(value[row, column]);
            return result;
        }
        static double[] Vector(Vector3 value) => new[] { Finite(value.x), Finite(value.y), Finite(value.z) };
        static double Finite(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value)) throw new InvalidDataException("The authored scene contains a nonfinite transform or coordinate");
            return value == 0f ? 0d : value;
        }
        static string HierarchyPath(Transform value)
        {
            var path = value.name;
            while (value.parent) { value = value.parent; path = value.name + "/" + path; }
            return path;
        }
        static void RequireEditMode()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Exit Play Mode before scoring or loading another frozen rank");
        }
        static GsplatRenderer ResolveRenderer(string id) => GlobalObjectId.TryParse(id, out var parsed) ? GlobalObjectId.GlobalObjectIdentifierToObjectSlow(parsed) as GsplatRenderer : null;
        static void Tick()
        {
            if (launching || EditorApplication.timeSinceStartup < nextRecovery) return;
            nextRecovery = EditorApplication.timeSinceStartup + .5;
            try { TryRestorePreview(); lastRecoveryError = null; }
            catch (Exception exception)
            {
                if (lastRecoveryError != exception.Message) Debug.LogError("Stage 1 preview recovery: " + exception.Message);
                lastRecoveryError = exception.Message;
            }
        }
    }
}
