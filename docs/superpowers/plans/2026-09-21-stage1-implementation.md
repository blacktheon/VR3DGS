# Stage 1 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans for implementation in this same local project, or superpowers:subagent-driven-development only if the user selects delegation. Steps use checkbox syntax for tracking. This document is a plan; unchecked steps are not implemented.

**Goal:** Make the current imported model visible, then implement the single-project contribution-scoring, exact-percentage review, accumulated-view iteration, and unchanged-row export workflow.

**Architecture:** Unity owns authoring, review and job control. A hidden local Python/CUDA process consumes versioned files and publishes complete results atomically. A small, documented extension to the installed Aras package preserves source identity and selects a frozen rank prefix before depth sorting.

**Tech stack:** Unity 6000.3.19f1, URP 17.3.0, existing Input System/Meta/OpenXR packages, C#, compute shaders, project-local Python 3.11, NumPy, PyTorch/cu128, gsplat plus a forward contribution accumulator. Worker versions are locked only after a successful native GPU smoke test.

**Spec:** [Accepted Stage 1 report](../../../../STAGE1_PREPROCESSING_PLAN.md) and [project integration design/evidence](../specs/2026-09-21-stage1-project-integration.md). Read both before execution. [Stage 2 report](../../../../STAGE2_LOD_PLAN.md) defines the output boundary.

**Planning update:** The user will implement the VR setup for walkable interaction in the existing Game scene after planning. Stage 1 integrates with that setup through explicit view/action bindings. See the [renderer comparison and VR handoff](../../stage1-renderer-comparison.md). Aras remains the recommended initial renderer; the alternative's stereo/MSAA and SH0 advantages are recorded alongside its Linear-color tradeoff.

## Global constraints

- Use the existing local Unity/Git root `C:/Work/Unity/VR3DGS/VR3DGS`. Do not create a second Unity project or a worktree for Editor execution.
- Preserve the existing scene, imported assets, packages, user changes and unsaved Editor work. Do not reset or stage unrelated changes.
- The user owns VR rig/tracking configuration, locomotion, gameplay collision and walkable interaction. Stage 1 must not replace the rig, install movement systems or disable the user's inputs automatically.
- Baseline: 6,011,316 original rows in `ScottVickers_CleanUp.ply`, identified by SHA-256 and zero-based source row ID. Original attributes are authoritative for export.
- One Unity UI; hidden external worker; Python stays outside the future Quest player.
- Exact count: `k = floor(N * keep_percent / 100)`, with explicit 0 and 100 endpoints. A frozen ranking defines nested subsets.
- RIGHT A bookmarks exact view, candidate percentage, display mode, source and ranking version. RIGHT B toggles full original versus the remembered candidate.
- Ordinary movement records poses/projections and compatible scene metadata, not slider values.
- Every scoring round uses the full original and all compatible historical paths/bookmarks. Verification memberships remain separate.
- Rough guides are soft analysis evidence. They do not become opaque occluders or hard keep rules.
- PC Editor/desktop first; PCVR optional and verified separately. No Stage 2 A/B controls, LOD generation, retraining, streaming or Android performance work.
- English communication unless the user asks in Chinese. The current unit transform is uncalibrated, not established meters.

## Review focus

1. Reordered or duplicate-position splats must preserve identity: importer permutation tests in Task 3.
2. A reload, cancellation or crashed worker must not duplicate jobs or publish partial rankings: process tests in Task 2.
3. Zero selection, tied depth and rapid percentage changes must yield a finite, correctly ordered image: GPU tests in Task 4.
4. A bookmarked original view must retain the remembered candidate and survive session merging: state/union tests in Tasks 7 and 8.
5. A stale source, changed calibration or contaminated held-out set must be rejected explicitly: contract/export tests in Tasks 1, 6 and 8.

## Milestones and stopping evidence

| Milestone | Deliverable | Required evidence |
|---|---|---|
| M0: current model visible | Repair the existing renderer setup | MCP reconnects to this project; required kernels supported; an actual Game-camera capture shows the model |
| M1: trustworthy source and worker | Inspection UI, source manifest, job lifecycle, GPU environment check | Full source scan; malformed fixtures rejected; cancel/reload tests; CUDA smoke render succeeds |
| M2: identity and coordinates | Controlled import, source-ID sidecar, shared cameras | ID bijection; diagnostic splat projection/opacity checks; reference camera bundle |
| M3: rank and preview | Measured contribution rank and exact GPU selection | CPU/GPU contribution oracle; exact selected counts; nested subsets; no reimport on slider movement |
| E1: first complete technical loop | Score full source at a small camera set, review, verify, export and reimport | Actual subset report, unchanged-row proof, preview-versus-reimport captures |
| M4: authored coverage | Surface/opening/panel guides, allowed head volume and generated camera sets | Guides absent from captures; legal eye positions; reproducible independent set membership |
| M5: repeated inspection loop | Pose recording, bookmarks, historical union and new scoring rounds | Old views retained, marks exact, discarded splats can return, independent nearby/global checks |
| M6: accepted Stage 1 output | User-selected percentage, fresh final verification and provenance | Final file reimport checked; unresolved visual failures visible; reproducible export package |
| Optional PCVR gate | User's completed VR setup participates in review | Bind the user's camera/origin/actions; real left/right eye capture and A/B press tests on the connected headset; measured timing |

The smallest E1 uses the complete 6,011,316-splat source with four scoring cameras, two independently sampled verification cameras and a small fixed regression set. Low-resolution technical checks come first, followed by at least one panel-resolution view. This is evidence that the pipeline works, not adequate coverage to accept a production reduction. Synthetic fixtures test algorithms; they never substitute for the actual-source E1.

## Proposed files and responsibilities

All paths below are relative to the Unity/Git root. Preserve Unity `.meta` files for new assets. Assembly definitions keep Editor code and Python out of player assemblies.

| Path | Responsibility |
|---|---|
| `Assets/SplatPreprocess/Editor/SplatPreprocessorWindow.cs` | Setup / Process / Review / Export window and action availability |
| `Assets/SplatPreprocess/Editor/Stage1Preflight.cs` | Live scene, pipeline, kernel, import and environment readiness |
| `Assets/SplatPreprocess/Editor/Worker/WorkerProcessHost.cs` | Hidden process launch, redirected output and main-thread event delivery |
| `Assets/SplatPreprocess/Editor/Worker/WorkerJobStore.cs` | Durable request/state, heartbeat and reload reconciliation |
| `Assets/SplatPreprocess/Editor/Worker/WorkerEnvironment.cs` | Isolated environment setup/check and toolchain selection |
| `Assets/SplatPreprocess/Editor/Import/Stage1ImportService.cs` | Controlled generated import, validated source-ID sidecar and manifest |
| `Assets/SplatPreprocess/Editor/Authoring/Stage1AuthoringTools.cs` | Undoable plane/box/opening/panel/head-volume handles |
| `Assets/SplatPreprocess/Editor/Capture/Stage1CaptureService.cs` | Matched pipeline captures; RGB/alpha output; temporary state restoration |
| `Assets/SplatPreprocess/Editor/Stage1ReportViewer.cs` | Report summaries, panel crops and worst-view replay |
| `Assets/SplatPreprocess/Runtime/Contracts/Stage1Contracts.cs` | Small serializable DTOs and explicit schema/version rules |
| `Assets/SplatPreprocess/Runtime/Stage1Workspace.cs` | Source, renderer, calibration and annotation references under a dedicated scene root |
| `Assets/SplatPreprocess/Runtime/Review/Stage1ReviewState.cs` | Frozen rank, candidate percentage, displayed mode and session state machine |
| `Assets/SplatPreprocess/Runtime/Review/Stage1SelectionController.cs` | Validated rank upload and exact keep-count selection API |
| `Assets/SplatPreprocess/Runtime/Review/Stage1PreviewPanel.cs` | Slider, counts, rank, mode, recording and bookmark feedback |
| `Assets/SplatPreprocess/Runtime/Review/Stage1DesktopNavigation.cs` | Opt-in technical test-camera navigation, independent of user-owned VR locomotion |
| `Assets/SplatPreprocess/Runtime/Integration/IStage1ViewProvider.cs` | Rig-independent current head/eye pose and projection contract |
| `Assets/SplatPreprocess/Runtime/Integration/Stage1SceneBindings.cs` | Explicit user-supplied camera, tracking-origin, source-root and viewing-region references |
| `Assets/SplatPreprocess/Runtime/Integration/Stage1ReviewActions.cs` | Callable bookmark/toggle functions for the user's input bindings and desktop buttons |
| `Assets/SplatPreprocess/Runtime/Recording/Stage1SessionRecorder.cs` | Append-only pose/projection logs and separate bookmarks |
| `Assets/SplatPreprocess/Runtime/Authoring/Stage1Annotations.cs` | Guides, targets and head-volume model |
| `Assets/SplatPreprocess/Tests/EditMode/` and `Tests/PlayMode/` | Contract, state, mapping, GPU and integration tests |
| `Assets/SplatPreprocess/Generated/<source>/<import>/` | New preview assets and sidecars; never overwrite `Assets/Art/3DGS` |
| `Packages/org.nesnausk.gaussian-splatting/` | Embedded copy of the exact installed renderer when patching begins |
| `Tools/SplatWorker/pyproject.toml` and `requirements.lock` | Worker packaging and verified environment lock |
| `Tools/SplatWorker/splat_worker/contracts.py` | Typed worker contracts, validation and hash rules |
| `Tools/SplatWorker/splat_worker/source.py` | PLY header/record inspection, decode, immutable source access |
| `Tools/SplatWorker/splat_worker/jobs.py` and `__main__.py` | Job protocol, atomic publication, CLI entry used by Unity |
| `Tools/SplatWorker/splat_worker/cameras.py` | Coordinate conversion, domain sampling and projection validation |
| `Tools/SplatWorker/splat_worker/backends/gsplat_backend.py` | Pinned projection/rasterizer integration and measured GPU memory |
| `Tools/SplatWorker/splat_worker/backends/cuda/contribution_forward.cu` | Forward `T * alpha` accumulation with original-ID addressing |
| `Tools/SplatWorker/splat_worker/backends/cuda/contribution_bindings.cpp` | PyTorch extension boundary and tensor validation |
| `Tools/SplatWorker/splat_worker/scoring.py` and `ranking.py` | Incremental statistics, conservative guidance and deterministic ordering |
| `Tools/SplatWorker/splat_worker/history.py` | Compatible historical union, exact marks, deduplication and membership tracking |
| `Tools/SplatWorker/splat_worker/verification.py` and `report.py` | Actual-subset comparisons, panel/worst views and report generation |
| `Tools/SplatWorker/splat_worker/export.py` | Byte-preserving source-row selection and provenance |
| `Tools/SplatWorker/tests/fixtures.py`, `tests/test_*.py` | Independent diagnostic PLYs, numerical oracles and process/export tests |
| `Tools/SplatWorker/THIRD_PARTY_NOTICES.md` | Attribution for the pinned CUDA source and dependencies |
| `SplatData/` | Ignored local jobs, sources' manifests, rounds, sessions, reports and exports |

Use `SplatPreprocess.Runtime.asmdef`, `SplatPreprocess.Editor.asmdef`, and test assemblies in their corresponding folders. Core recording and review actions do not require a Meta locomotion assembly. Existing SampleScene supplies the initial review environment. No replacement scene is required; changes are confined to a Stage 1 root, explicit bindings and the deliberate renderer repair.

### User-owned VR integration boundary

`IStage1ViewProvider.TryCaptureViews(out ViewSample sample)` supplies one timestamped head pose, actual eye `ViewRecord` entries (or one mono view), and tracking-origin identity. `ViewSample` belongs to the shared runtime contracts. `Stage1SceneBindings` adapts explicitly assigned cameras and transforms; stereo matrices come from the active stereo camera/provider rather than assumed offsets on disabled eye cameras.

`Stage1ReviewActions.BookmarkCurrentView()` and `ToggleOriginal()` expose the agreed Stage 1 A/B semantics. The user connects their right-controller press events and resolves conflicts with gameplay bindings; Stage 1 does not claim ownership of all controller input. Visible mark feedback is supplied by Stage 1; haptics may be connected through a user-supplied callback.

The user-authored gameplay walkable area may inform the analysis head-viewing region, but the two are separate. Stage 1's region includes head height, leaning and eye positions and guides camera generation; it does not add collision meshes or enforce locomotion. Coordinate/projection/state changes are recorded and validated before using paths for scoring. Until the VR scene is ready, Editor cameras and an opt-in desktop test camera keep preprocessing development independent.

## Renderer integration points

These locations refer to the inspected upstream package, before embedding:

| Existing file / method | Planned change |
|---|---|
| `Editor/GaussianSplatAssetCreator.cs`: `CreateAsset`, `ReorderMorton` around lines 247/411 | Expose a controlled import entry; emit parallel `storage_to_source` IDs from the same `(Morton code, source ID)` ordering; retain manual importer behavior |
| `Editor/Utils/GaussianFileReader.cs`: `ReadFile`, `LinearizeDataJob` | Reuse named-property decoding; record log-scale, sigmoid opacity, wxyz normalization and rotation packing in the import manifest |
| `Runtime/GaussianSplatRenderer.cs`: `CreateResourcesForAsset`, `InitSortBuffers` | Keep full source allocation; add rank, canonical source/storage mapping and selection/scratch buffers |
| Same file: `SortPoints`, `CalcViewData` | Use selected count and selected storage indices; keep attribute addressing in full-source storage space |
| Same file: `GaussianSplatRenderSystem.SortAndRenderSplats` | Draw k instances; skip zero safely; expose captured splat RGBA before final compositing |
| `Runtime/GpuSorting.cs`: `Args.count` | Sort only selected entries; retain N-sized scratch allocations; verify partial counts and zero bypass |
| `Shaders/Stage1Selection.compute` (new) | Stable parallel compaction of `rank_by_source[id] < k` in ascending source-ID order |
| `Shaders/SplatUtilities.compute`: view/distance kernels | Distinguish dispatch slot from storage ID and full source count from selected count |
| `Shaders/RenderGaussianSplats.shader` | Continue reading sorted storage IDs; optional importance color without changing alpha/footprint |
| `Shaders/GaussianComposite.shader` | Guard empty-alpha division and preserve normal compositing; zero selection must remain finite |
| `Runtime/GaussianSplatURPFeature.cs` | Preserve Render Graph integration; add an opt-in capture hook; no pass when no splats are selected |

Do not implement percentage selection using the destructive editor delete buffer, truncate storage to k rows, or replace depth sorting with importance order. Begin with one splat renderer so separately sorted overlapping objects do not invalidate transparency tests.

## Data contracts, version 1

Every manifest includes `schema_version`, content hashes and relevant source/scene/backend versions. All numeric binary files are little endian; manifests declare dtype, shape, byte length and SHA-256. Reject mismatched identities, missing files, invalid permutations, duplicate IDs and partial outputs before GPU upload.

| Artifact | Required contents |
|---|---|
| `source_manifest.json` | Source SHA-256, byte length, vertex count, header/data offsets, named properties and offsets, original encoding, SH degree, invalid-record statistics, ID rule `zero_based_vertex_row` |
| `scene.json` | Explicit source-to-Unity matrix, calibration status/scale evidence, annotation version, head domain, forbidden volumes, panels, renderer capture profile and state hash |
| `import_manifest.json` | Source hash, input file hash, package revision, asset GUID/hash, precision profile and `storage_to_source.bin` hash; source N and imported count are separate |
| `storage_to_source.bin` | u32 `[imported_count]`; complete import is a permutation of `[0,N)`; export import is a unique subset of original IDs |
| `views_*.jsonl` | View ID, set membership/round, source and scene identity, camera-to-world and world-to-camera matrices, CPU projection, intrinsics, resolution, clipping planes, eye label, pixel convention and seed |
| `importance.bin` | f32 `[N]` display score indexed by source ID; auxiliary statistics have separately declared arrays |
| `rank.bin` | u32 `[N]`, rank position to source ID; zero is highest importance; strict permutation |
| `rank_manifest.json` | Rank hash, source hash, calibration/scene/backend hashes, scoring-view list hash, formula/parameters, evidence statistics, quantization and round ID |
| `session.metadata.json` | Session UUID, source identity, calibration/scene/capture profile, frozen rank ID, time basis and projection-table references |
| `session.jsonl` | Timestamp, head/eye poses, projection/state references; no percentage or slider fields |
| `marks.jsonl` | Mark UUID, exact camera sample(s), candidate percentage, display mode, rank/source/scene IDs, bounded priority request and timestamp |
| `report.json` and images | Subset rank/percentage/count; per-view RGB, alpha and region errors; panel crops; worst views; held-out memberships; all inputs and capture settings |
| Export directory | `final.ply`, `source_ids.bin`, `mask.bin`, frozen `rank.bin`, manifests, calibration/annotations, view lists, session references and fresh final report |

Percentage precision is explicit: the UI represents 0.00–100.00% in hundredths of a percent (`keep_centi_percent`, integer 0–10000). Both languages use integer arithmetic:

```python
def keep_count(n: int, keep_centi_percent: int) -> int:
    if n < 0 or not 0 <= keep_centi_percent <= 10000:
        raise ValueError("invalid source count or percentage")
    return n * keep_centi_percent // 10000
```

Use an i64 intermediate in C#. Store the candidate value independently of display mode. `displayed_count = N` in Original mode and `keep_count(...)` in Candidate mode. A new rank cannot be activated during recording/Play-session review.

`mask.bin` is a u8 `[N]` array of 0/1, indexed by original ID. `source_ids.bin` is u32 `[k]` in exported-row order. Export rows in ascending original ID; preserve their 56-byte records exactly. Update only the header's vertex count. Exporting zero is a valid empty PLY; represent its Unity reimport as an explicit empty result because the upstream renderer requires a positive count.

### Coordinate contract

Matrices serialize row-major with column-vector multiplication, with matrix role stated explicitly. Unity camera space looks down -Z; worker camera space uses +Z forward and image +Y downward. Use `diag(1,-1,-1,1) * unity_world_to_camera` for that camera-space conversion, and transform means/covariances with the declared source-to-Unity transform exactly once. Covariances use `A * covariance * A^T`; reflections must not be treated as ordinary quaternions.

For a supported Unity perspective CPU projection P, start with `fx=P00*W/2`, `fy=P11*H/2`, `cx=(1-P02)*W/2`, `cy=(1+P12)*H/2`. Validate by reprojection and ray tests, including off-axis stereo projections. Do not export only FOV or feed a graphics-API/reversed-Z projection into a pinhole rasterizer. Unsupported oblique/orthographic projections receive an explicit error until implemented.

Color/background, filtering, radius cutoff, alpha threshold, early termination, exposure, splat scale and opacity multiplier are versioned. Unity's gamma-to-linear composite and packed attributes mean cross-renderer identity is not assumed.

### Job contract

Unity launches the absolute isolated Python executable directly with `UseShellExecute=false`, `CreateNoWindow=true`, redirected stdout/stderr and correctly quoted arguments. The command is `python -m splat_worker --job <absolute-job-json>`. No shell commands are assembled from source paths.

A job directory contains request, append-only event log, status, heartbeat and cancellation marker. States are Queued, Running, Cancelling, Succeeded, Failed, Cancelled and Interrupted. Publish into a temporary result directory, validate, rename on the same volume, then atomically replace the small current-result pointer. Failure never replaces the last successful rank. Reconcile a worker using job ID, PID and process start time; PID alone is insufficient.

Persist state outside Assets. Drain process pipes asynchronously; queue Unity API work onto `EditorApplication.update`. Before a GPU job, exit review and suspend/release the Stage 1 renderer resources. Restore preview state after completion/failure/reload. Do not kill unrelated Python processes.

## Task 0 — Restore the existing model's rendering

**Files:** Modify only `ProjectSettings/ProjectSettings.asset` Windows graphics API configuration and `Assets/Settings/PC_Renderer.asset` plus the new feature subasset, using Unity APIs. Inspect the live URP global settings. Do not patch package code yet.

**Consumes:** Current Model, current PC pipeline, MCP connection. **Produces:** A documented usable desktop baseline and capture.

- [ ] Re-run the read-only baseline: project path, dirty scenes, active pipeline/renderer, current graphics API, required compute-kernel `IsSupported`, feature list and camera pose. These checks already fail for the observed DX11 setup.
- [ ] Set Windows graphics API to Direct3D12. At the reviewed restart checkpoint, preserve unsaved work and reopen this same project with D3D12. Reconnect MCP; query the actual API and all kernels before proceeding.
- [ ] Add one `GaussianSplatURPFeature` to the active PC renderer, using Undo and saving only that asset. Keep Render Graph compatibility mode off. Confirm the feature is active and not duplicated.
- [ ] Frame the existing model using an inspection camera/view that includes its bounds. Record any camera adjustment separately from calibration. Capture through the actual Game-camera URP path; check fresh console results and the visible image.
- [ ] Record M0 evidence. If kernels still fail, inspect the new compilation error before adding another change. If kernels pass but the image is absent, inspect camera/frustum, feature scheduling, culling and ordinary scene depth one boundary at a time.

**Gate:** A positive kernel check alone does not pass M0; a visible model capture is required. No promise of PCVR performance follows from this desktop result.

## Task 1 — Source inspection and immutable contracts

**Files:** Create `contracts.py`, `source.py`, `ranking.py` count/validation helpers, `tests/fixtures.py`, `tests/test_source.py`, `tests/test_contracts.py`, and C# contract/count tests under `Tests/EditMode`. Add the Python package configuration and local-data ignore rules with this deliverable.

**Interfaces:** `inspect_source(path: Path, output_dir: Path) -> SourceManifest`; `read_source(manifest: SourceManifest) -> SourceTable`; `keep_count(n: int, keep_centi_percent: int) -> int`; `validate_rank(order: ndarray, source: SourceManifest) -> None`. `SourceTable` exposes immutable raw records and decoded named attributes.

- [ ] Write fixtures with reordered PLY properties, a duplicate-position pair with different color, truncated payload, one NaN, and a zero quaternion. Add concrete assertions:

```python
def test_exact_counts():
    assert keep_count(6_011_316, 0) == 0
    assert keep_count(6_011_316, 5000) == 3_005_658
    assert keep_count(6_011_316, 10000) == 6_011_316
    assert keep_count(7, 3333) == 2

def test_source_identity_survives_inspection(named_property_ply, tmp_path):
    before = named_property_ply.read_bytes()
    manifest = inspect_source(named_property_ply, tmp_path)
    assert named_property_ply.read_bytes() == before
    assert manifest.vertex_count == 4
    assert manifest.id_rule == "zero_based_vertex_row"
```

- [ ] Run `python -m pytest tests/test_source.py tests/test_contracts.py -q` from `Tools/SplatWorker`; confirm failures concern missing behavior.
- [ ] Implement a bounded-memory header parser and full validity scan. Decode fields by name, normalize valid wxyz quaternions, exponentiate scale and apply sigmoid opacity in scratch arrays. Reject malformed source as an actionable inspection failure; do not silently drop rows or change N.
- [ ] Run the tests, then inspect the actual source. Verify all 6,011,316 rows, byte length and known hash; save ranges, invalid counts and decoding conventions. Preserve a clear uncalibrated marker.
- [ ] Check the narrowly scoped diff and checkpoint this deliverable. Do not include the pre-existing imported assets or project changes in its commit.

## Task 2 — Unity window, durable jobs and GPU environment check

**Files:** Create the window, preflight, three Worker C# files, assembly definitions, `jobs.py`, `__main__.py`, `tests/test_jobs.py`, and `Tests/EditMode/WorkerJobStoreTests.cs`. Add `Tools/SplatWorker/.venv`, build/cache outputs, generated assets and `SplatData` to `.gitignore` without removing existing entries.

**Interfaces:** `WorkerProcessHost.Start(JobRequest) -> string jobId`; `Cancel(string jobId)`; `WorkerJobStore.Reconcile() -> JobSnapshot[]`; worker `run_job(request: JobRequest) -> JobResult`. `JobRequest` carries ID, operation, source/scene/rank/view references and output directory. Operations: inspect, check_environment, score, verify, export.

- [ ] Write process tests using a tiny subprocess fixture that emits progress, fills stderr, waits for cancellation and optionally exits with an error. Test a path containing spaces and non-ASCII characters, and a reload against the same live job:

```python
def test_failed_job_does_not_replace_current(job_harness):
    previous = job_harness.publish_success("rank-a")
    job_harness.run_failing_job()
    assert job_harness.current_result() == previous

def test_reconciliation_does_not_launch_twice(job_harness):
    job_id = job_harness.start_waiting_job()
    job_harness.recreate_host_and_reconcile()
    assert job_harness.launch_count(job_id) == 1
```

- [ ] Run the worker lifecycle tests and EditMode process-store tests. Implement the file protocol and hidden launch described above. Keep long operations off the Unity main thread; reject a second GPU job while one is active.
- [ ] Wire Setup/Inspect and progress/log/cancel controls to the real source inspector. Reopen the window and trigger a script reload during the subprocess test; retain the existing job and last successful result.
- [ ] Create a separate Python 3.11 environment through the Editor setup action. Initial candidate: PyTorch 2.7.1/cu128, gsplat v1.5.3 at commit `937e29912570c372bed6747a5c9bf85fed877bae`; select VS 2022/MSVC 14.44 explicitly. Do not replace Python 3.14, CUDA, the graphics driver or Unity packages. Lock exact dependencies after a successful compile.
- [ ] Run `check_environment`: report interpreter, CUDA runtime/toolkit, compiler, device capability, extension build, a small actual rasterization, finite RGB/alpha and peak allocated/reserved GPU memory. Save results. A successful torch import is insufficient. If extension compilation fails, keep CPU inspection/export usable and report the exact failure.
- [ ] Run cancellation/reload/crash tests and inspect the final diff before checkpointing.

## Task 3 — Source-ID import and coordinate calibration

**Files:** Embed the inspected renderer package; modify its creator for a controlled import and sidecar; create `Stage1ImportService.cs`, `Stage1Workspace.cs`, `cameras.py`, `tests/test_cameras.py`, and `Tests/EditMode/Stage1ImportTests.cs`. Create generated diagnostic assets under the Stage 1 folder.

**Interfaces:** `Stage1ImportService.Import(SourceManifest, ImportProfile, optional originalIds) -> ImportedSource`; `export_camera(camera, scene) -> ViewRecord`; `convert_to_worker_camera(view, scene) -> WorkerCamera`. `ImportedSource` carries asset reference, count, manifest and storage-to-source mapping.

- [ ] Add an importer fixture whose PLY order differs from Morton order, including duplicate Morton keys and identical positions. Verify every stored row maps back to the correct original attributes; deliberately shuffle a mapping and require rejection.
- [ ] Modify the exact Morton reorder loop to write the same permutation into a u32 sidecar. For subset PLY reimports, compose the permutation with the export's original-ID sidecar. Do not infer identity from position equality. Import into a new output directory.
- [ ] Add camera fixtures for asymmetric perspective projection, axis-colored splats, nonunit uniform scale, reflection, near/far clipping and a rotated anisotropic splat. Validate projected centers to 0.25 pixel on synthetic cases and quaternion/covariance conversion numerically.
- [ ] Implement named camera/transform conventions from the contract. Capture front, side, oblique and close-panel reference views. Compare within each renderer and report cross-renderer differences separately, including color/composite and packed-rotation effects.
- [ ] Preserve the current uncalibrated transform until the user identifies a known distance and endpoints. Calibration scales by `real_distance / measured_source_distance`; store evidence and start a new scene version. It must not silently make earlier sessions compatible.
- [ ] Run import/projection tests, validate the full-source ID bijection, and checkpoint the package patch and new files. Record upstream revision and hashes, preserving all existing GUIDs.

## Task 4 — Exact GPU subset preview and review state

**Files:** Create the four Review state/selection/panel/navigation files, the three Integration view-provider/bindings/actions files, `Shaders/Stage1Selection.compute` inside the embedded package, and GPU/state tests. Modify the renderer/view/distance/draw/composite integration points listed above.

**Interfaces:** `Stage1ReviewState.SetCandidate(int centiPercent)`, `ToggleOriginal()`, `GetBookmark(ViewRecord) -> MarkRecord`; `Stage1SelectionController.LoadRank(RankManifest, uint[] order)` and `SetKeepCount(int count)`; the view-provider and review-action methods defined in the VR integration boundary above. The state exposes candidate percentage, mode, selected count, displayed count and frozen rank ID.

- [ ] Test B/slider behavior independently of graphics. A representative sequence is:

```csharp
state.SetCandidate(4000);
state.ToggleOriginal();
state.SetCandidate(2500);
Assert.That(state.DisplayedCount, Is.EqualTo(sourceCount));
Assert.That(state.CandidateCentiPercent, Is.EqualTo(2500));
state.ToggleOriginal();
Assert.That(state.DisplayedCount, Is.EqualTo(sourceCount / 4));
```

- [ ] Test GPU selection against a CPU oracle for counts 0, 1, 7, 1023, 1024, 1025 and 4097; equal scores; decreasing/increasing percentages; and shuffled storage. With a frozen rank, lower k must always be a subset of higher k.
- [ ] Upload rank/mapping once. On k changes, compact predicates in canonical source-ID order using block counts, an exclusive block-prefix scan, then scatter. Re-seed per-camera depth-sort inputs from this immutable selected list so equal-depth ties cannot depend on the previous frame.
- [ ] Dispatch distance and view work for k; read/write attributes/view data by storage ID; set sort `Args.count=k`; draw k. Preserve full N allocations. B restores all original IDs through the same path. Handle k=0 before dispatch/sort/draw and guard transparent composite division by zero.
- [ ] Show exact label `Keep splats (%)`, candidate/display mode, requested/kept count, source hash prefix and rank version. Desktop technical navigation is opt-in and uses its own test camera. Connect the user's camera through explicit bindings when ready; do not drive or reconfigure their rig, locomotion or gameplay cameras.
- [ ] Validate source-buffer instance identities and importer invocation count during rapid slider movement. They must remain unchanged. Compare overlapping/tied-depth fixture images against an independently constructed retained subset; all output pixels must be finite.
- [ ] Measure selected-count-dependent GPU work on the real source, without promising linear speedup or memory reduction. A late fragment-mask-only prototype does not pass this task. Checkpoint after the count/image tests pass.

## Task 5 — Measured contribution backend and frozen ranking

**Files:** Create the backend Python/C++/CUDA files, `scoring.py`, the remainder of `ranking.py`, `THIRD_PARTY_NOTICES.md`, `tests/test_contributions.py`, `tests/test_ranking.py`, and backend fixtures. Extend the Process section of the window.

**Interfaces:** `render_full(source: SourceTable, view: WorkerCamera, profile: RenderProfile) -> RenderOutput`; `accumulate_contributions(source, view, profile) -> ContributionStats`; `score_round(source, views, guides, parameters) -> ScoreResult`; `freeze_rank(scores, source) -> RankManifest`. `RenderOutput` contains RGB and alpha; statistics have N-addressed peak, weighted sum, contributing-pixel count, projected/tested coverage and optional bookmark-error evidence.

- [ ] Implement a small independent CPU front-to-back oracle and test the numerical meaning before CUDA work:

```python
def composite_weights(alphas):
    transmittance = 1.0
    weights = []
    for alpha in alphas:
        weights.append(transmittance * alpha)
        transmittance *= 1.0 - alpha
    return weights

def test_foreground_transmittance_is_measured():
    assert composite_weights([0.8, 0.8, 0.8]) == pytest.approx(
        [0.8, 0.16, 0.032], abs=1e-7)
```

Add separate oracle cases for the pinned renderer's alpha clamp/cutoff and exclusive early-termination behavior. Include an occluded rear splat, a visible slit, a briefly visible small panel and an offscreen splat. Projection visibility alone must not pass these tests.

- [ ] Reuse gsplat v1.5.3's projection and tile-intersection data. Build an attributed forward-kernel extension based on its pinned `RasterizeToPixels3DGSFwd.cu`, accumulating at the same `vis=alpha*T` point actually used in compositing. The packed projection index must map through gsplat's Gaussian IDs to original source IDs. Do not index N-sized statistics by a tile-intersection or packed-camera index.
- [ ] Match the pinned rasterizer's alpha cutoff, clamp, pixel-center convention and termination order. Keep its full foreground context. Compare instrumented RGB/alpha against stock output on fixtures: maximum absolute difference at most 1e-5 in the same float output path, or investigate before relaxing it. Compare per-splat weights to the CPU oracle within 1e-5.
- [ ] Avoid an N-by-pixel tensor. Per camera, accumulate nonnegative peak with atomic max and sums with u64 fixed-point atomics at 24 fractional bits; reset per camera and reduce in sorted view-ID order on the CPU using f64. Quantization error is bounded by contributing-pixel count divided by `2^24`; record it. Guard image-size/sum overflow. This makes reduction order independent of thread scheduling; different backends still receive different versions.
- [ ] Start with camera batch size one, gradients disabled, `radius_clip=0`, pinned classic rasterization and explicit cutoffs. Measure peak GPU memory. An out-of-memory error leaves the previous rank active. Reduce camera batch size or process independent screen tiles with all their foreground splats; never score arbitrary point chunks as if isolated. Lowering resolution changes the declared profile and requires a distinct result.
- [ ] Aggregate raw statistics and implement the following explicit initial heuristic. It is a versioned experiment to evaluate, not a promised optimal pruning rule:

```python
# Each view contributes once after compatible-pose deduplication.
# priority is 1 normally and at most 3 for a bookmarked view.
P = max(per_view_peak)                         # preserve brief strong evidence
A = weighted_mean(per_view_sum / pixel_count, priority)
C = weighted_mean(per_view_peak >= 1.0/255.0, priority)
E = max(per_mark_sum_of_weight_times_rgb_error / mark_pixel_count,
        default=0.0)                          # error clipped to [0,1]
base = 0.70 * P + 0.20 * A + 0.10 * C + 0.05 * E
score = base * (1.0 + surface_bonus)           # surface_bonus in [0,0.05]
```

`weighted_mean` divides by the sum of unique view priorities, not raw frame count. Surface proximity uses oriented Gaussian extent and may add only a bounded bonus in this first version. Interior/opening hints remain diagnostic evidence; they cannot zero a measured contribution. Preserve all raw statistics and an unobserved/weakly-observed flag. A zero score is not an invisibility guarantee.

- [ ] Freeze ordering from finite f64 scores using descending score, then ascending original ID. Export the f32 display scores separately; their rounding must not redefine ordering. Require exact rank reproducibility for identical quantized statistics and versioned parameters:

```python
def test_tied_scores_use_source_ids():
    score = np.array([0.5, 0.8, 0.8, 0.0], dtype=np.float64)
    order = np.lexsort((np.arange(4, dtype=np.uint32), -score))
    assert order.tolist() == [1, 2, 0, 3]
```

- [ ] Run CPU/GPU/rank tests, then the four-camera full-source pilot. Publish a complete rank only after N, hash, permutation and statistics validation. Load it between review sessions, then verify Task 4 count semantics against this actual rank. Checkpoint evidence and code.

## Task 6 — Verification, unchanged-row export and E1 reimport

**Files:** Create `verification.py`, `report.py`, `export.py`, `Stage1CaptureService.cs`, `Stage1ReportViewer.cs`, `tests/test_verification.py`, `tests/test_export.py`, and `Tests/PlayMode/Stage1ReimportTests.cs`.

**Interfaces:** `verify_candidates(source, rank, views, percentages, profile) -> VerificationReport`; `export_subset(source, rank, centi_percent, output_dir) -> ExportManifest`; `Stage1CaptureService.Capture(ViewRecord[], CaptureProfile, CandidateSpec) -> CaptureManifest`. `CandidateSpec` names rank, percentage and whether full-original or retained mode is requested.

- [ ] Test byte-preserving export from a fixture whose float records include signed zero and unusual finite values; numerical reconstruction is not sufficient:

```python
def test_export_copies_original_records(source_fixture, frozen_rank, tmp_path):
    exported = export_subset(source_fixture, frozen_rank, 5000, tmp_path)
    ids = np.fromfile(exported.source_ids_path, dtype="<u4")
    expected = b"".join(source_fixture.raw_record(int(i)) for i in ids)
    assert exported.vertex_payload() == expected
    assert len(ids) == keep_count(source_fixture.vertex_count, 5000)
    assert ids.tolist() == sorted(ids.tolist())
```

Also test 0/100%, corrupted rank, source hash mismatch, edited source after inspection, wrong sidecar length and existing output-path collision. Export to a fresh directory; never overwrite the original PLY.

- [ ] Render actual subsets at 100%, 75%, 50%, 25% and the selected user percentage. Zero is an endpoint correctness case. Candidate rendering must recompute compositing with discarded splats absent; an original image multiplied by a mask is invalid.
- [ ] Produce per-view RGB MAE/PSNR, alpha MAE/max change, thresholded silhouette mismatch, panel-region scores, before/after/difference images, retained counts and worst-view ordering. Use source-only reference captures with a fixed background and no analysis overlays. Ordinary floor/cubes/controllers must not become unrecorded occluders in the comparison profile.
- [ ] Implement Unity captures through the configured URP path, not an assumed `Camera.Render` fallback. Preserve/restore camera matrices, dimensions, renderer selection, model, visibility, background and temporary capture resources in `try/finally`. Read splat RGBA before final compositing for alpha checks. Initially use a small representative set; include all agreed final regression/panel views for final acceptance.
- [ ] Keep different comparisons labeled: Python candidate versus Python original; Unity candidate versus Unity original; Python-versus-Unity calibration; and exported/reimported Unity candidate versus preview. A quality report must not combine these into one unexplained metric.
- [ ] Implement raw-row streaming export with IDs/mask/rank/provenance. Hash and validate the produced PLY. Reimport that exact file through the controlled importer, composing original IDs. Temporarily render the reimported asset by itself, then restore the preview; do not render both overlapping models together.
- [ ] For synthetic reimport cases, require exact selected IDs and raw records. For real Unity preview-versus-reimport captures at the same profile, start with at most one 8-bit RGB level of difference and alpha error at most 1e-3. Investigate larger differences in packing, ordering or capture state; do not silently loosen the gate. These equivalence tolerances are distinct from the user's acceptable pruning error.
- [ ] Complete E1 on the full cleaned source: one real contribution rank, responsive exact slider, held-out/regression report, byte-preserving test export and actual reimport check. Label the small camera coverage as a technical pilot. Checkpoint only after recording this evidence.

## Task 7 — Guides, head domain, generated views and bookmarks

**Files:** Create authoring data/tools, session recorder and corresponding EditMode/PlayMode tests; use Task 4's scene bindings/actions; extend `cameras.py`, `scoring.py` and the preview panel. Add `tests/test_sampling.py`.

**Interfaces:** `Stage1Annotations.Export() -> SceneManifest`; `generate_views(scene, SamplingConfig) -> ViewSets`; `Stage1SessionRecorder.Start(SessionMetadata)`, `AppendPose(PoseSample)`, `AppendMark(MarkRecord)`, `Stop()`; `Stage1ReviewActions` forwards the user's discrete input events into review state and recording.

- [ ] Test that guide serialization and Unity Undo preserve IDs, transforms, roles and openings. Make handles for planes/oriented boxes, panel rectangles, allowed head volumes and forbidden regions. Persist exact guides as source-relative analysis geometry. Keep them out of reference render layers and do not create opaque occlusion meshes.
- [ ] Sample positions throughout authored head volumes with coarse global coverage and denser panel/opening inspection. Include head height, leaning/crouching limits, nearest inspection distance and eye offsets; reject forbidden locations. Directional samples cover authored targets and general orientations. Enforce `calibration_status` before interpreting meter-valued limits.
- [ ] Generate scoring and verification lists using separate deterministic seeds and actual pose/projection membership checks; different seeds alone are not proof of disjointness. Preserve a fixed global regression list. Test:

```python
def test_generated_views_stay_legal_and_separate(scene_fixture):
    sets = generate_views(scene_fixture, SamplingConfig(seed_scoring=11,
                          seed_verify=29, seed_regression=47))
    assert all(scene_fixture.allows_eye(v.eye_position) for v in sets.all_views())
    assert sets.scoring_pose_keys().isdisjoint(sets.verify_pose_keys())
    assert sets.manifest() == generate_views(scene_fixture,
                                             sets.config).manifest()
```

- [ ] Add importance display using the frozen score-to-storage map. Normal/heatmap mode must use the same selected set, opacity, footprint and depth order. Show sparse/unobserved evidence separately so black does not claim invisibility.
- [ ] Record ordinary movement at a bounded cadence with movement/rotation/projection-change triggers and monotonic timestamps. Store head and available per-eye matrices; buffer writes and flush on stop, scene exit and Play Mode exit. Keep source/rank/state metadata in the session header or referenced change events.
- [ ] Implement `BookmarkCurrentView()` for right A press events; snapshot exact view, candidate percent, currently displayed original/candidate mode, source/rank/scene versions and capped priority. Provide visible confirmation and an optional feedback callback for the user's haptics. Preserve multiple percentage observations at the same view.
- [ ] Implement `ToggleOriginal()` through the tested review state machine. Desktop buttons and the user's controller action bindings call the same functions. Document rising-edge binding requirements; identify conflicts for the user to resolve without disabling or replacing their input handlers.
- [ ] Test that recording JSON has no percentage fields, while marks do; A while Original is showing still records the remembered candidate. Test held-button behavior, one event per press, projection changes and interrupted final log lines. Checkpoint once desktop authoring/recording works.

## Task 8 — Historical union, second scoring round and independent verification

**Files:** Create `history.py`, `tests/test_history.py`; extend scoring, verification and the Process/Review window.

**Interfaces:** `merge_history(initial_views, session_paths, mark_paths, CompatibilityPolicy) -> ScoringViewSet`; `generate_nearby_verify(history, scene, seed) -> ViewSet`; `promote_view(view_id, next_round) -> MembershipEvent`. These produce versioned view IDs and retain their complete membership history.

- [ ] Test two historical sessions from different ranking rounds but the same compatible source/scene. Both must remain eligible. Test that equal position/different orientation stays distinct, exact bookmarks supersede nearby ordinary representatives, different marked percentages survive, and repeated marks cannot grow weight without bound:

```python
def test_union_retains_history_and_exact_marks(history_fixture):
    result = merge_history(**history_fixture.inputs)
    assert result.contains(history_fixture.old_session_view)
    assert result.contains(history_fixture.new_session_view)
    assert result.contains_exact(history_fixture.bookmark_view)
    assert result.priority(history_fixture.bookmark_view) <= 3.0
    assert result.mark_percentages(history_fixture.bookmark_view) == {2500, 4000}
```

- [ ] Build compatibility from source identity, transform/calibration and physical/capture state. Ranking version is recorded provenance, not an exclusion criterion. Guide-only edits do not discard physically compatible paths. Reject changed coordinate frames unless an explicit, validated migration maps historical cameras and preserves original records.
- [ ] Deduplicate by position AND orientation, projection, eye semantics and scene state. Use explicit configurable tolerances, initially 2 cm/2 degrees away from details and 5 mm/0.5 degree near authored panels/openings once calibrated. Hash projection/profile exactly. Preserve distinct exact marked poses even when they are nearby; attach nearby ordinary samples to those representatives.
- [ ] Form the union of initial scoring views, all compatible sessions, marks and new development samples. Replay every view against all original N splats. For marks, reconstruct the saved candidate from its archived rank and percentage and compute localized loss evidence. Missing historical rank files are an explicit diagnostic, never silently replaced by the current rank.
- [ ] Add a regression fixture where a previously zero-ranked splat becomes visible from a new view. It must move upward in the new rank despite being absent from the last preview. Test that stopping at a pose for many frames does not dominate aggregate statistics.
- [ ] Generate fresh verification samples around accumulated paths/bookmarks, perturbing both position and orientation, retaining exact marks for separate development checks. Reject illegal head/eye locations and any overlap with that round's scoring cameras under the declared matching rule. Include global regression and panel views.
- [ ] If a failed held-out view is promoted into next-round scoring, record its new development status and replace it in the independent set. Fixed regression views used to guide tuning remain labeled regression/development evidence, not fresh final holdout.
- [ ] Run a real second round. Demonstrate that old session paths remain present, no ordinary slider values are needed, new ranks activate only between sessions and independent report inputs are distinct. Checkpoint the reproducible round manifests.

## Task 9 — Final acceptance, reimport and optional PCVR

**Files:** Extend Export/Review actions and add a Stage 1 usage guide at `docs/stage1-workflow.md`; add an end-to-end worker test using a small fixture plus an actual-source acceptance checklist generated into each export directory.

**Interfaces:** `accept_export(rank_id, centi_percent, final_verify_views) -> AcceptedExportManifest`; an accepted manifest references the completed final report and reimport report. Acceptance is never inferred merely from a successful file write.

- [ ] Ask for the user's selected percentage after reviewing representative close panels, openings, silhouettes and worst cases. Keep unresolved findings visible; do not make the chosen count an assumed target for later rounds.
- [ ] Generate a fresh final verification set that has not informed ranking. Evaluate it with the fixed global/panel checks at the exact selected subset and capture profile. If it fails and informs further ranking, retire it to development and generate another independent final set.
- [ ] Export original records and complete provenance through Task 6. Reimport the actual final file, compare to the accepted preview and record counts, hashes, image tolerances and any deviations. Verify that the source's current hash still matches the manifest.
- [ ] Bundle source/export identity, original IDs, mask, rank, calibration, guides/domain, scoring/verification membership history, session/mark references, backend versions and final reports. Stage 2 receives this accepted retained set as its finest reference; no LOD assets are generated here.
- [ ] After the user supplies the VR/walkable-interaction setup, bind its view provider/origin/actions. With a connected headset, check actual left/right image differences, eye projection/offsets, A/B edge behavior, UI usability and memory/frame timing. Validate the configured stereo mode explicitly. Mono success is not a stereo test. If unsupported, retain working desktop review and report the precise limitation; do not replace the user's rig as an implicit repair.
- [ ] Document Setup → Process → Review → Export, failure recovery and restart requirements. Run only the relevant suites and actual-source acceptance checks; review the scoped changes and checkpoint the final implemented milestone.

## Validation commands and evidence storage

From `Tools/SplatWorker`, use the isolated interpreter:

```powershell
& .\.venv\Scripts\python.exe -m pytest tests/test_source.py tests/test_contracts.py -q
& .\.venv\Scripts\python.exe -m pytest tests/test_jobs.py tests/test_cameras.py -q
& .\.venv\Scripts\python.exe -m pytest tests/test_contributions.py tests/test_ranking.py -q
& .\.venv\Scripts\python.exe -m pytest tests/test_verification.py tests/test_export.py -q
& .\.venv\Scripts\python.exe -m pytest tests/test_sampling.py tests/test_history.py -q
```

Use Unity's Test Runner through the existing Editor/MCP session for EditMode and PlayMode tests. Do not launch a second batchmode Editor against the open project. Save NUnit results and capture manifests under `SplatData/validation/<milestone>/` with source/package/backend versions. Re-run affected tests after fixes; broaden only when a new concern justifies it.

For GPU validations, store the kernel support snapshot, actual API, device/driver, shader/extension versions, exact camera lists, seed, peak memory, selected source IDs and comparison images. A report's quality metrics need their worst views and panel crops, not just a global mean.

## Execution order and review points

1. Review the integration design and this plan; recommended execution is native incremental work in this task, keeping all Editor actions in the current project.
2. Start with Task 0 and the explicit Editor-restart checkpoint. Confirm the user's model is visible before attempting algorithm development.
3. Execute Tasks 1–6 to reach the first actual-source E1, with calibration status explicit and no accepted quality claims from the pilot.
4. Add authored domain coverage and accumulated inspection in Tasks 7–8, then repeat until an acceptable percentage is found.
5. Execute final verification/export in Task 9. PCVR and SH0 storage optimization remain conditional on observed needs; neither silently blocks desktop correctness.

If an implementation reveals that a planned integration point cannot meet the contract, update this plan and explain the measured failure before replacing the renderer or changing stage scope. Do not claim planned features or untested hardware support as implemented.

## Requirement traceability

| Accepted report requirement | Implementation |
|---|---|
| Source validation, hash, stable IDs, authoritative export attributes | Tasks 1, 3, 6 |
| Shared units/transform, matching cameras, diagnostic arrangements | Task 3; calibration status remains explicit |
| Soft surfaces/openings/panels, allowed head-viewing volume | Task 7 |
| Separate scoring/verification views and global regression | Tasks 5–8 |
| Actual foreground-transmittance × pixel-opacity importance | Task 5 |
| Exact nested percentage with early GPU selection and correct transparency | Task 4 |
| Stage 1 A bookmarks and B original/candidate behavior | Tasks 4 and 7 |
| Pose-only ordinary logs and union of all compatible history | Tasks 7–8 |
| Full-original rescoring, recovery of previously removed splats | Task 8 |
| Real candidate reports, panel crops, alpha/silhouette and worst views | Task 6 |
| Fresh nearby/final views, explicit development promotion | Tasks 8–9 |
| Unchanged rows, provenance, actual reimport matches preview | Tasks 6 and 9 |
| Hidden worker, progress/cancel/reload recovery, GPU handoff | Task 2 |
| Preserve existing project and avoid Stage 2/3 expansion | Global constraints and Task 0 |

Planning self-review: all ten report steps map to tasks above. Physical measurement, headset operation and photo alignment remain explicit external evidence requirements rather than invented facts. No product code, dependencies, scene settings or renderer assets have been changed by writing this plan.
