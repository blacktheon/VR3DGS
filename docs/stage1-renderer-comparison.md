# Stage 1 renderer comparison and VR ownership

Checked 2026-09-21. This is a source/documentation comparison, not a performance benchmark or a migration already performed.

- Installed renderer: `aras-p/UnityGaussianSplatting`, package `org.nesnausk.gaussian-splatting` 1.1.1, project lock commit `2c6fed37da67a217367261fcfcd3316d34c73e76`.
- Original design's proposed renderer: `wuyize25/gsplat-unity`, package `wu.yize.gsplat` 1.4.0 in inspected commit `a2bf458d6b16395e6570e9345f9f4408f92684b8`.
- Offline scoring library: `nerfstudio-project/gsplat`, a separate Python/CUDA project. Choosing the Unity renderer does not require changing this worker library.

## Differences relevant to this project

| Area | Installed Aras renderer | Proposed gsplat-unity renderer | Consequence |
|---|---|---|---|
| Render architecture | Splats render into a separate texture, then composite into the scene | Splats draw through Unity's transparent mesh queue | Different color/compositing behavior; changing the renderer requires calibration |
| Current Linear project | Its composite converts the accumulated splat color to linear | Its author recommends Gamma for conventionally gamma-trained splats; per-splat Gamma To Linear is described as an imperfect workaround | Aras is the safer initial reference for this project's current color pipeline |
| Ordinary transparent meshes | Integration limitations because splats composite as a separate pass | Bounding-box-based ordering with normal transparent meshes | Useful for game integration, but not a guarantee of exact ordering where surfaces interpenetrate |
| Stereo and MSAA | Upstream reports working VR devices, but documents MSAA as unsupported | Explicitly documents URP multi-pass and Single Pass Instanced, with MSAA compatibility | gsplat-unity has documented advantages for this part of a VR setup; neither has been tested on this user's headset in this task |
| SH0 attribute storage | Current import allocates a full higher-order SH buffer despite SH0 input | Allocates higher-order SH only when present | gsplat-unity avoids the current unused 1.07 GiB SH allocation |
| Precision choices | Current Very High preserves full count but packs rotation; shader/view data also use reduced precision | Uncompressed retains FP32 attribute arrays; default Spark mode uses lossy packing | Use a deliberate precision profile for comparison; neither imported form replaces original PLY bytes for archival export |
| Import and identity | Manual asset creation; Morton order differs from PLY row order | Scripted import; inspected uncompressed reader advances source rows in order when opacity pruning is disabled | Migration needs new asset/component setup; source identity must still be tracked explicitly |
| Existing pruning | Editing/cutouts do not implement the agreed contribution ranking | Includes an opacity-prune import threshold and dynamic cutouts | An opacity threshold is not transmittance-based importance and does not implement exact Keep splats (%) semantics |
| Multiple splat objects | Whole objects ordered approximately; overlapping objects can composite incorrectly | Optional global splat sorting, with restrictions | Potentially relevant to later chunks, not grounds to claim Stage 2 is already solved |
| Windows graphics API | D3D12 or Vulkan required | D3D12 or Vulkan required | Changing plugins does not fix the current D3D11 incompatibility |

The color, XR and setup claims above follow the [gsplat-unity README](https://github.com/wuyize25/gsplat-unity/tree/a2bf458d6b16395e6570e9345f9f4408f92684b8) and [Aras pipeline notes](https://raw.githubusercontent.com/aras-p/UnityGaussianSplatting/main/docs/render-pipeline-integration.md). The inspected Aras `GaussianComposite.shader` performs gamma-to-linear conversion after splat accumulation; this differs from converting each splat before blending. The source PLY's training color space has not been independently established, so reference-camera validation is still required.

gsplat-unity's ordering/layout mechanisms are described in its [implementation notes](https://github.com/wuyize25/gsplat-unity/blob/a2bf458d6b16395e6570e9345f9f4408f92684b8/Documentation~/Implementation%20Details.md). Its current global-sort implementation requires all active assets to use Spark compression and at most 255 active renderers, otherwise it falls back to individual rendering. See [the actual eligibility checks](https://github.com/wuyize25/gsplat-unity/blob/a2bf458d6b16395e6570e9345f9f4408f92684b8/Runtime/GsplatSorter.cs). This is not unrestricted global sorting of arbitrary uncompressed chunk sets.

## Memory implications for this exact SH0 source

All rows below retain the full 6,011,316 splats. Figures concern core attribute payload, excluding CPU copies, sorting/scratch buffers, render targets, stereo resources and Unity serialization overhead. The two alternatives are calculated from source layouts, not measured imported assets in this project.

| Representation | Core attribute payload | Evidence |
|---|---:|---|
| Existing Aras Very High import | 1,353.22 MiB / 1.32 GiB | Actual sizes of its four `.bytes` files; includes color texture padding |
| gsplat-unity Uncompressed, SH0 | 321.04 MiB | 56 bytes per splat: position 12 + scale 12 + quaternion 16 + color/opacity 16; no higher-order SH allocation |
| gsplat-unity Spark, SH0 | 91.73 MiB | 16-byte packed attributes per splat; lossy positions/scales/color/rotation |

The buffer allocations are explicit in [GsplatResource.cs](https://github.com/wuyize25/gsplat-unity/blob/a2bf458d6b16395e6570e9345f9f4408f92684b8/Runtime/GsplatResource.cs); [GsplatAssetSpark.cs](https://github.com/wuyize25/gsplat-unity/blob/a2bf458d6b16395e6570e9345f9f4408f92684b8/Runtime/GsplatAssetSpark.cs) documents the packing. Smaller payload is not a measured frame-rate result. The Aras SH0 allocation can also be addressed with a targeted storage extension; it is not an unavoidable cost of retaining that renderer.

## Recommendation for Stage 1

Retain Aras for the initial reference and preprocessing loop. This recommendation is based on the existing Linear pipeline, already imported asset and inspected integration points. Resolve D3D12/URP setup before using either renderer as a baseline. Continue to evaluate SH0 storage and actual stereo performance rather than assuming them from package choice.

gsplat-unity is a credible alternative if URP Single Pass Instanced, MSAA and compact SH0 storage become decisive requirements. A fair evaluation would import the same full source in Uncompressed mode with opacity pruning zero, use matched transforms/cameras, resolve the Gamma/Linear policy explicitly, and compare close-panel appearance, silhouettes, per-eye correctness, peak VRAM and frame timing. Its default Spark compression is a separate quality experiment, not the baseline for judging renderer fidelity.

The original design recommendation was provisional. Neither Unity package provides the complete planned source-ID/ranking/Keep-percent/bookmark/history/verification/export workflow. That remains Stage 1 implementation work with either renderer. No standalone Quest capacity or performance has been established.

## User-owned VR setup and the Stage 1 handoff

The user will implement the VR setup for walkable interaction in the existing Game scene after planning. That includes rig/tracking configuration, room-scale movement or locomotion, collision boundaries and gameplay interaction. Stage 1 will not create a replacement rig, change those movement systems or disable their inputs automatically.

Stage 1 owns the splat renderer integration, worker, guides and analysis head-volume authoring, exact percentage state, recording/bookmark functions, scoring, reports and export. The analysis head volume describes where cameras may sample; it is separate from gameplay collision or walkable-area enforcement. Soft analysis guides are not automatically collision geometry.

The handoff needs only:

1. The actual review camera/head pose and tracking-origin transform, plus real per-eye view/projection matrices when stereo is active.
2. The source model root and agreed source-to-world transform/calibration.
3. The permitted head-viewing region, including relevant leaning/eye-height limits, expressed in the same frame.
4. Explicit input connections from right A to `BookmarkCurrentView()` and right B to `ToggleOriginal()`, with the existing agreed Stage 1 meanings. The user owns action binding and gameplay conflicts; Stage 1 supplies callable actions and visible feedback.

Until that setup is ready, technical checks can use Editor reference cameras and an opt-in desktop test camera. Stage 1's core has no mandatory Meta locomotion dependency. Once the user supplies the scene, validate the bindings, coordinate frame, exact A/B behavior and real stereo rendering without replacing the rig.
