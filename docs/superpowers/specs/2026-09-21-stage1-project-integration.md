# Stage 1 project integration design

Status: proposed integration design, grounded in read-only inspection on 2026-09-21. No Stage 1 implementation or rendering repair has been performed in this task.

The authoritative functional brief remains [STAGE1_PREPROCESSING_PLAN.md](../../../../STAGE1_PREPROCESSING_PLAN.md). [STAGE2_LOD_PLAN.md](../../../../STAGE2_LOD_PLAN.md) supplies the handoff boundary only. This document resolves implementation choices against the project actually open in Unity; it does not replace the accepted ten-step workflow.

## Intended result

Operate source inspection, annotation, contribution scoring, review, recording, verification, and unchanged-row export from this one Unity project. A hidden local worker handles offline Python/CUDA work. A frozen ranking drives an exact retained-count preview. Repeated scoring always uses the complete original source and accumulated compatible views. Independent reports expose damage at each retained count; the user's performance priority and measured device budget inform the acceptable tradeoff. Stage 1 export alone does not establish the final runtime capacity.

The user has confirmed that the model was imported using the existing plugin and is not yet visible in Game view. Their subsequent priority is performance and larger usable splat counts on standalone Android, with one renderer choice carried through later stages unless it demonstrably fails. Select `wuyize25/gsplat-unity`; this supersedes the earlier recommendation to retain Aras for color fidelity. The user will implement the VR setup for walkable interaction in the Game scene after planning. Communicate in English unless the user asks a question in Chinese. Physical scale has not been supplied; do not interpret the current unit transform as verified meters.

## Observed project state

| Item | Evidence from this task |
|---|---|
| Workspace / Git root | Workspace `C:/Work/Unity/VR3DGS`; Git and Unity root `C:/Work/Unity/VR3DGS/VR3DGS` |
| Local instructions | No applicable `AGENTS.md` found in the checked ancestors or project tree |
| Editor connection | Unity MCP compiled and executed read-only C# against this exact `Application.dataPath` |
| Unity | 6000.3.19f1; Edit Mode; not compiling/updating at inspection |
| Scene | `Assets/Scenes/SampleScene.unity`, loaded and active, `isDirty=false` at inspection |
| Build target / graphics | Android; Editor Direct3D11; Windows configured for Direct3D11 |
| Render pipeline | URP 17.3.0; PC quality; `Assets/Settings/PC_RPAsset.asset`; Linear color space |
| Renderer features | `PC_Renderer.asset` contains SSAO, no `GaussianSplatURPFeature` |
| Render Graph | Current serialized `RenderGraphSettings.m_EnableRenderCompatibilityMode=0`; verify live setting before any repair because a legacy field also exists |
| Model | `Model` has `GaussianSplatRenderer`; position zero, identity rotation, unit scale |
| Model settings | Scale/opacity multipliers 1; SH order 3; sort every frame; existing clean-up asset assigned |
| GPU readiness | All seven queried initialization, view, distance, and radix-sort kernels returned `IsSupported=false` under Direct3D11 |
| Console | MCP returned zero logs/errors/warnings in both filtered and unfiltered queries; this does not override the failed kernel checks |
| Existing XR | Meta Building Blocks Camera Rig, controllers, hands and comprehensive interaction rig; CenterEyeAnchor enabled |
| XR configuration | OpenXR loader registered for Standalone and Android; Meta Horizon is Windows' registered OpenXR runtime; no live headset session tested |
| Dependencies | Input System 1.19.0, Meta XR SDK All 205.0.0, OpenXR 1.17.0; no upgrades proposed |
| Gaussian package | `org.nesnausk.gaussian-splatting` 1.1.1; lock commit `2c6fed37da67a217367261fcfcd3316d34c73e76`; cache fingerprint `7113109d5e73ad37c01ac25bfd620dd1e895359e` |
| Workstation | RTX 5060 Ti; Unity reports 16,050 MB VRAM and 31,861 MB system RAM; driver 595.97 |
| Worker environment | Python 3.14.5 only in Python launcher inventory; NumPy, PyTorch and gsplat absent in that interpreter; CUDA toolkit 12.8.93 installed |
| Native compiler | VS 2022 BuildTools / MSVC 14.44.35207 present; another BuildTools installation also has 14.51.36231. Select the 2022 toolchain explicitly and compile-test it |
| Existing changes | User changes already present in scene, XR/project/package settings, and untracked `Assets/Art`; preserve them. Git HEAD `fd879ea` |

### Source evidence

`C:/Users/black/Downloads/ScottVickers_Full/ScottVickers_CleanUp.ply` exists.

- SHA-256: `7351c4694b28360c8898c47f799a63e8b69fb941eac0a62a830a407cf044605d`.
- 6,011,316 records; 363-byte header; 56 bytes per record; 336,634,059 total bytes, exactly matching header plus records.
- Binary little endian. Float fields in file order: `x y z rot_0 rot_1 rot_2 rot_3 scale_0 scale_1 scale_2 opacity f_dc_0 f_dc_1 f_dc_2`.
- A 4,096-row distributed sample contained no nonfinite values; sampled quaternion norms were approximately one, scale values negative, and opacity values on both sides of zero. These support the installed importer's log-scale/logit-opacity interpretation. A complete validity scan and calibration remain implementation work.
- Earlier `ScottVicker_Full.ply` also exists; its header declares 15,174,024 vertices. It is not the agreed denominator.
- No capture photos or pose files were present alongside these two files. Their other locations and alignment are unverified and are optional for the first loop.

The existing imported asset has all 6,011,316 splats. Its four buffers total 1,418,958,512 bytes: positions 72,135,792; other attributes 96,181,056; colors 96,468,992; SH 1,154,172,672. This is approximately 1.32 GiB, 4.22 times the PLY size. The SH buffer alone is about 1.07 GiB. It is not evidence of splat reduction.

## Integration choices

| Approach | Tradeoff | Decision |
|---|---|---|
| Adopt gsplat-unity for all stages | Compact SH0/Spark storage, documented Android/URP stereo support; requires controlled migration and color-profile validation | Selected for the user's performance priority |
| Extend the installed Aras renderer | Preserves existing assets and Linear color behavior; current import wastes SH0 storage and mobile support is uncertain | Preserve existing assets for rollback; no parallel Stage 1 implementation |
| Implement scoring entirely in Unity compute | Avoids Python but adds the entire instrumented analysis renderer and reporting stack | Outside the minimum implementation |

Pin `wu.yize.gsplat` at commit `a2bf458d6b16395e6570e9345f9f4408f92684b8` (package metadata 1.4.0). Use this Unity renderer for Stage 1 and subsequent runtime development. The separate Python/CUDA gsplat worker choice is unchanged. See the [renderer decision and evidence](../../stage1-renderer-comparison.md): 6,011,316 SH0 splats need about 91.73 MiB of packed core attributes, versus the current import's 1.32 GiB. This supports a memory-efficiency decision, not a claim of measured FPS superiority. No controlled same-device Android comparison or maximum usable count has been established.

### First migration and visibility check

Install the pinned gsplat-unity package into this project, use Direct3D12 for the Windows Editor and add `GsplatURPFeature` to the active PC renderer. Import the unchanged source into a new generated asset and bind `GsplatRenderer`. Preserve the old imported asset/component as rollback material, disabling its rendering while the new one is active. Check Render Graph compatibility mode, shader support and camera framing. Verify actual compute-kernel support after restart, then capture the model through the active render pipeline.

Changing the graphics API requires an Editor restart. Treat that as a distinct checkpoint: finish the plan review, recheck scene dirtiness, preserve unsaved work, restart this same project, then reconnect MCP and verify its path. Do not start another Editor against the locked project, terminate Unity forcibly, or silently save every dirty asset.

Both packages need wave-operation-capable APIs; changing the package does not fix Direct3D11. The selected package's [setup instructions](https://github.com/wuyize25/gsplat-unity/tree/a2bf458d6b16395e6570e9345f9f4408f92684b8) require Render Graph compatibility mode off and, on Android, Vulkan with Apply display rotation during rendering disabled. Inspect the active Android quality/renderer separately rather than assuming the current PC renderer is used there. Camera placement/orientation and importer axis conversion must be verified; the old identity transform is not calibration.

Keep the initial Linear project setting and record the plugin's Gamma To Linear workaround explicitly. Color fidelity is subordinate to performance in this choice, but reference and candidate settings must still match. Use an Uncompressed SH0 profile for Stage 1 reference checks and a Spark SH0 profile for Android performance. Pruning thresholds stay zero; packing and contribution selection are separate operations. No partial upload is accepted as a complete original reference.

### Early Android evidence

Run a small standalone Android performance check after desktop visibility, as soon as the user's device/VR setup is available, before investing in later runtime stages. Record device, per-eye resolution, real stereo mode, native app FPS, CPU/GPU frame times, peak memory, load time and ten-minute sustained behavior at overview and close-panel views. Use a count ladder and a provisional 72 Hz target until the device/product target is confirmed. This is an early compatibility/capacity check, not Stage 3 streaming implementation. Desktop correctness work may continue while hardware is unavailable, with Android suitability explicitly unverified. The measured budget, not the PLY count or compressed size, defines how many splats may be active simultaneously.

### Renderer development

When implementation reaches the ID/selection work, embed the pinned selected package under `Packages/wu.yize.gsplat`, preserving its package identity and GUIDs. Record its upstream revision and changes. Never edit `Library/PackageCache` as the durable implementation. Leave the old Aras package unmodified.

Create Stage 1 data in its own generated folder. Carry original source IDs through both gsplat-unity PLY import modes using an explicit mapping emitted alongside the attributes. The inspected readers preserve PLY row order when opacity pruning is disabled; assert this rather than relying on it implicitly. Compose the mapping with original-ID sidecars on subset reimport. Do not match splats by nearest position. Record the selected import coordinate convention and apply axis conversion exactly once.

Separate selection from source storage. Stable GPU compaction selects exactly k IDs, before distance calculation, sorting and drawing. Depth order remains camera-dependent. Stable source-ID order resolves equal-depth ties consistently between preview and reimport. Keep the source buffers resident; a lower percentage is not a promise of lower VRAM use.

Uncompressed is the diagnostic reference profile; Spark is the intended Android profile and must be validated separately. Both avoid higher-order SH allocation for this SH0 source. Final PLY export reads untouched original row bytes, never decoded or packed renderer attributes. Render quality reports must distinguish pruning loss from packing loss and baseline Python/Unity differences. Keep global sorting and cutouts off in the initial single-renderer Stage 1 path; neither substitutes for exact source-ID selection.

### Authoring and review

Use standard Unity Scene-view handles first: planes, oriented boxes, openings/recesses, panel rectangles and permitted head volumes. Store them under a dedicated Stage 1 root in the existing scene. Guides are analysis metadata/Gizmos, excluded from scoring/reference images. Full headset authoring is unnecessary for the first version.

The user owns the VR rig, tracking/locomotion, gameplay collision boundaries and walkable interaction setup. Stage 1 supplies explicit scene bindings for the user's head/camera/tracking-origin references, callable bookmark/toggle actions, and the recorder. It does not create a replacement rig, configure locomotion or disable existing input handlers automatically. A bookmarks the exact view and candidate percentage; B switches full-original display without replacing the candidate setting. Play-session ranking is immutable. Ordinary trajectory files have no slider values.

Technical checks can use Editor reference cameras and an opt-in desktop test camera while the user prepares VR. When that scene is ready, bind its actual head and per-eye view/projection data, source root and calibrated viewing region; the user connects right A/B to Stage 1 actions. The authored analysis head volume describes camera-sampling limits, independently of gameplay walkable-area enforcement or collision geometry.

### Worker and scoring

Use a project-local Python 3.11 environment and a pinned CUDA-capable PyTorch/gsplat combination, subject to actual compilation and GPU tests. A concrete initial candidate is PyTorch 2.7.1/cu128 and gsplat v1.5.3 (`937e29912570c372bed6747a5c9bf85fed877bae`). This is a test candidate, not a compatibility claim. Record the exact working wheels, Python version, compiler, extension hash and driver in the backend manifest.

gsplat's pinned forward rasterizer exposes the front-to-back `alpha * T` term internally. Add a forward contribution accumulator at that point and verify its images against the uninstrumented renderer. Do not substitute projected radius, frustum visibility, gradients or a top-one contributor for the requested per-splat measurement. See the [pinned forward kernel](https://raw.githubusercontent.com/nerfstudio-project/gsplat/v1.5.3/gsplat/cuda/csrc/RasterizeToPixels3DGSFwd.cu) and [Windows build instructions](https://github.com/nerfstudio-project/gsplat/blob/main/docs/INSTALL_WIN.md).

Process one camera initially with the full original participating in occlusion. Use incremental statistics and bounded memory. Pause Stage 1 GPU preview while offline jobs run. File-based jobs, atomic manifests and a process identity/heartbeat allow cancellation and recovery after domain reload without duplicate workers.

### Validation and export

Verify actual subsets within each renderer. Treat Python-versus-Unity baseline differences separately from deletion damage. Include panel crops, worst views and alpha/silhouette changes. Held-out cameras stay outside that round's score inputs; a failed verification view used for development loses held-out status.

Export selected original rows in source-ID order, with a sidecar mapping and complete provenance. Reimport that actual file through the same precision profile and compare it to the accepted preview, including deterministic equal-depth behavior. Do not accept an export based only on a report rendered before export.

## Decisions still needing external evidence

1. A known physical measurement and identified endpoints are needed before claiming meters or trustworthy head-clearance limits. Inspection and technical fixtures can proceed with an explicit uncalibrated status.
2. VR integration follows the user's VR/walkable-interaction setup and requires a connected headset plus actual per-eye tests. Confirm the exact standalone Android device and refresh target for the early performance check. PCVR/Quest Link and Editor timing do not establish standalone performance. Desktop development can proceed independently.
3. Capture-photo and pose locations can be provided when alignment work is useful. They do not block the first loop.
4. The implementation plan and its first Editor-restart checkpoint are presented for review before product changes.

No Stage 2 chunking/LOD controls, Gaussian retraining, learned visibility network or production streaming are part of this design. The early standalone Android probe is now included to validate the selected foundation; a production Android rollout and capacity guarantees remain outside Stage 1.
