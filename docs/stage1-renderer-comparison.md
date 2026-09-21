# Project renderer decision, comparison and VR ownership

Checked 2026-09-21. This is a source/documentation comparison, not a performance benchmark or a migration already performed.

**Decision: use wuyize25/gsplat-unity as the single renderer foundation for Stage 1 and the later Android/VR stages.** The user has clarified that performance and supported splat count on standalone Android matter more than visual fidelity or preserving the existing renderer. This supersedes the earlier recommendation to retain Aras for its Linear-color behavior. Keep this choice unless an actual requirement fails; do not maintain two production renderer implementations.

- Installed renderer: `aras-p/UnityGaussianSplatting`, package `org.nesnausk.gaussian-splatting` 1.1.1, project lock commit `2c6fed37da67a217367261fcfcd3316d34c73e76`.
- Selected renderer, not yet installed: `wuyize25/gsplat-unity`, package `wu.yize.gsplat` 1.4.0 in inspected commit `a2bf458d6b16395e6570e9345f9f4408f92684b8`. Pin this revision rather than following a moving branch.
- Offline scoring library: `nerfstudio-project/gsplat`, a separate Python/CUDA project. Choosing the Unity renderer does not require changing this worker library.

## Differences relevant to this project

| Area | Installed Aras renderer | Selected gsplat-unity renderer | Consequence |
|---|---|---|---|
| Render architecture | Splats render into a separate texture, then composite into the scene | Splats draw through Unity's transparent mesh queue | Different color/compositing behavior; changing the renderer requires calibration |
| Current Linear project | Its composite converts the accumulated splat color to linear | Its author recommends Gamma for conventionally gamma-trained splats; per-splat Gamma To Linear is described as an imperfect workaround | Record and validate this tradeoff; color fidelity no longer decides the renderer choice |
| Ordinary transparent meshes | Integration limitations because splats composite as a separate pass | Bounding-box-based ordering with normal transparent meshes | Useful for game integration, but not a guarantee of exact ordering where surfaces interpenetrate |
| Stereo and MSAA | Upstream reports working VR devices, but documents MSAA as unsupported | Explicitly documents URP multi-pass and Single Pass Instanced, with MSAA compatibility | gsplat-unity has documented advantages for this part of a VR setup; neither has been tested on this user's headset in this task |
| SH0 attribute storage | Current import allocates a full higher-order SH buffer despite SH0 input | Allocates higher-order SH only when present | gsplat-unity avoids the current unused 1.07 GiB SH allocation |
| Precision choices | Current Very High preserves full count but packs rotation; shader/view data also use reduced precision | Uncompressed retains FP32 attribute arrays; default Spark mode uses lossy packing | Use a deliberate precision profile for comparison; neither imported form replaces original PLY bytes for archival export |
| Import and identity | Manual asset creation; Morton order differs from PLY row order | Scripted import; inspected uncompressed reader advances source rows in order when opacity pruning is disabled | Migration needs new asset/component setup; source identity must still be tracked explicitly |
| Existing pruning | Editing/cutouts do not implement the agreed contribution ranking | Includes an opacity-prune import threshold and dynamic cutouts | An opacity threshold is not transmittance-based importance and does not implement exact Keep splats (%) semantics |
| Multiple splat objects | Whole objects ordered approximately; overlapping objects can composite incorrectly | Optional global splat sorting, with restrictions | Potentially relevant to later chunks, not grounds to claim Stage 2 is already solved |
| Windows graphics API | D3D12 or Vulkan required | D3D12 or Vulkan required | Changing plugins does not fix the current D3D11 incompatibility |
| Android support evidence | Upstream says mobile is untested by the author and may or may not work | Author explicitly lists Android among tested platforms; requires Vulkan and disabling native display rotation during rendering | Better documented starting point for Android, not a guarantee for every Android GPU |
| Ongoing development | Author states no significant further development is planned | 2026 updates include packing, adjustable sort frequency and import improvements | Favors extending gsplat-unity for this project's later stages |

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

The packed attribute payload is about 14.75 times smaller than this particular existing import, and 3.5 times smaller than gsplat-unity Uncompressed SH0. This is not a 14.75-fold FPS or maximum visible-count claim, and it is not a comparison against the most compressed or modified Aras configuration. Android CPU and GPU allocations also share limited system memory. gsplat-unity still needs CPU asset data, index/key/sort scratch buffers, Unity/XR resources and render targets; 91.73 MiB is not its complete runtime footprint.

## Performance evidence and the decision

No controlled same-device, same-scene Android benchmark comparing these exact two versions was found in the reviewed primary sources. We have not run either on the user's headset. Therefore, gsplat-unity is the selected engineering foundation, not a proven FPS winner or a promise to draw all 6,011,316 splats at VR frame rates.

An [upstream issue from November 2025](https://github.com/wuyize25/gsplat-unity/issues/10) reports only 20 FPS for 500,000 splats on a Meta Quest. It does not specify enough matched conditions to rank these plugins, and predates the [March 2026 Spark packing and subsequent controls](https://github.com/wuyize25/gsplat-unity/blob/a2bf458d6b16395e6570e9345f9f4408f92684b8/CHANGELOG.md). It is evidence that plugin selection alone does not establish usable capacity, not a current fixed ceiling. Aras likewise [does not promise mobile support](https://github.com/aras-p/UnityGaussianSplatting).

The decision rests on compact SH0 storage, explicit Android/URP stereo support and an actively evolving implementation. Lower attribute bandwidth and avoiding Aras's separate splat-composite target may help, but neither guarantees lower frame time: gsplat-unity does projection work in vertex shaders, and both still pay for sorting and overlapping transparent pixels. A close view of large splats can be slower than a distant view containing more splats. Later contribution pruning, chunk LOD and a bounded active set remain necessary parts of the performance plan. Total model count, resident count and submitted count must be reported separately; source data cannot tell us the maximum smooth visible count on an unspecified device.

Use two explicit profiles in this one plugin:

- **Stage 1 reference:** Uncompressed SH0, opacity pruning zero, complete upload before drawing, sort every capture. This isolates pruning error and keeps original-row export unchanged.
- **Android performance:** Spark SH0, Vulkan and URP Single Pass Instanced as the intended starting configuration; confirm the actual stereo mode on device. Begin with MSAA off and fixed render resolution to measure the renderer, then measure any MSAA/foveation/sort-frequency changes separately. Packed attributes are a derived representation, not a replacement for original PLY records.

Keep the project's present Linear setting during the first migration and record the Gamma To Linear workaround in the capture profile. A Gamma project is a possible later measured rendering-profile change, not a prerequisite for choosing this plugin. Neither color policy nor compression is allowed to change silently between a reference and its candidate. Preserve old Aras assets as rollback material, with only the selected renderer active in production review.

## Early Android performance check

Bring the first device check forward instead of waiting until all Stage 1 work is finished. It follows basic desktop visibility and the availability of the user's VR setup/device. It does not require implementing locomotion or the complete preprocessing pipeline. Desktop correctness work can continue while hardware/setup is unavailable; Android suitability remains unverified until this check runs.

1. Record exact device, OS, Unity/package revision, graphics API, stereo mode, per-eye render size and application refresh rate. Use a standalone APK, not Quest Link or Editor frame timing.
2. Use the same source/model transform and fixed overview, walking and close-panel views. Exercise representative deterministic subsets at 100k, 250k, 500k, 1M and 2M, then larger counts only while memory and frame time permit. Before contribution ranking exists, label the test subsets as engineering fixtures, not accepted pruning outputs.
3. Measure app CPU/GPU frame time, native application FPS, dropped/reprojected frames, peak memory and load time. Start with a provisional 72 Hz target (13.89 ms per frame), then use the confirmed device/product requirement. The renderer receives only part of that budget because gameplay/XR also run.
4. Run the proposed shipping budget for at least ten minutes with head movement and close views; record sustained timing and thermal degradation. Keep render scale, refresh rate and reprojection policy fixed, and report any deliberate changes.
5. Choose an active-splat budget from those measurements. Do not interpret loading six million splats as rendering all six million smoothly. Retain gsplat-unity through routine budget/LOD tuning; revisit the plugin only for a demonstrated compatibility, correctness, memory or performance failure that remains after profiling and reasonable optimization.

Neither Unity package provides the complete source-ID/ranking/Keep-percent/bookmark/history/verification/export workflow. That remains Stage 1 implementation work. The existing Game-view problem still requires a supported Editor graphics API, the selected plugin's URP feature and verified framing; migration itself is not the fix.

## User-owned VR setup and the Stage 1 handoff

The user will implement the VR setup for walkable interaction in the existing Game scene after planning. That includes rig/tracking configuration, room-scale movement or locomotion, collision boundaries and gameplay interaction. Stage 1 will not create a replacement rig, change those movement systems or disable their inputs automatically.

Stage 1 owns the splat renderer integration, worker, guides and analysis head-volume authoring, exact percentage state, recording/bookmark functions, scoring, reports and export. The analysis head volume describes where cameras may sample; it is separate from gameplay collision or walkable-area enforcement. Soft analysis guides are not automatically collision geometry.

The handoff needs only:

1. The actual review camera/head pose and tracking-origin transform, plus real per-eye view/projection matrices when stereo is active.
2. The source model root and agreed source-to-world transform/calibration.
3. The permitted head-viewing region, including relevant leaning/eye-height limits, expressed in the same frame.
4. Explicit input connections from right A to `BookmarkCurrentView()` and right B to `ToggleOriginal()`, with the existing agreed Stage 1 meanings. The user owns action binding and gameplay conflicts; Stage 1 supplies callable actions and visible feedback.

Until that setup is ready, technical checks can use Editor reference cameras and an opt-in desktop test camera. Stage 1's core has no mandatory Meta locomotion dependency. Once the user supplies the scene, validate the bindings, coordinate frame, exact A/B behavior and real stereo rendering without replacing the rig.
