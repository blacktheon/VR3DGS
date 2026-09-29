# Stage 1 — first round with the authored scene

Control update (2026-09-22): headset movement recording and runtime A-button Save view capture are temporarily disabled at the user's request. Review creates no session files, and the recording/Save view controls are hidden. The percentage slider and B comparison remain active. To restore capture, uncomment `#define STAGE1_REVIEW_CAPTURE` at the top of `Assets/SplatPreprocess/Runtime/Integration/Stage1ReviewActions.cs`; this also restores the related UI. Existing recordings, camera bindings, recorder code and editor surface/floor screenshot baking are preserved. Save view currently records camera metadata and a priority bookmark, not a PNG image.

Latest scene update: five authored `Surfaces` planes have full-source 8K screenshot materials, and the three `Deletable` boxes exclude 709,730 unique splats. The customized ranking preserves the existing importance scores and is configured at 30% (1,567,386 of 5,224,622 survivors). See [surface customization](stage1-surface-customization.md) for the current assets, controls and validation. The scoring measurements below describe the completed original round, before this customization.

The first scoring and automated verification round uses the saved `Assets/Scenes/Stage1.unity`, its floor and machine-top NavMesh, the original 6,011,316-row source, and the planes under `Walls`. The user's next step is the original workflow step 7: inspect the candidates while walking and climbing in the existing scene. Saving views with A is currently paused.

Open the saved scene and enter Play Mode for Editor/PCVR review. The customized rank is configured; Play Mode uses the authored physical slider position. Pose recording is currently disabled. This version uses local rank files; standalone APK data packaging and Android performance validation remain later work.

To preview a percentage without pressing Play, open **Tools → Splats → Stage 1 Preprocessor → Review**. Drag or type **Keep splats (%)** under **Edit Mode preview**. Leave **Show original (100%)** unchecked to see the candidate; checking it compares against the eligible original while remembering your percentage. Save the scene to persist that choice. The configured rank reloads after scripts recompile or the saved scene opens, with source and geometry validation. The Edit Mode value does not move the physical slider; Play Mode uses its authored handle position.

## Scene rules and controls

- Viewpoint feet lie on the authored NavMesh. Eye heights are 0.3, 0.8, 1.3 and 1.8 units **above each surface**, including the machine top. Scale follows the authored Unity scene; no independent physical measurement was supplied.
- `Walls/Floor` excludes splat centers below its world plane. The original PLY and original row IDs remain unchanged.
- The seven old directional wall planes are currently inactive. Their supported rule uses local +Y as the front: from that side, a center is suppressed only within the 0.1-unit rear band and when its camera ray crosses the finite plane rectangle. Front-side and deeper centers remain visible. This is a center-based Gaussian rule, not clipping every Gaussian's entire footprint.
- Plane meshes use their normal Mesh Renderer settings in both Edit and Play Mode. The splat wall binding does not hide or enable them; its finite directional filtering is independent of mesh visibility. Their colliders and the authored NavMesh remain intact. Any visible opaque plane also renders normally with its material's depth behavior; the offline report measures the splat subset alone.
- `Slider No Snapping/Grabbable` keeps its authored X limits of −0.06 to +0.06. Its position maps to **Keep splats (%)**, from 0 to 100. The current X of −0.024 corresponds to 30%.
- **Right A:** temporarily inactive. Its saved-view bookmark implementation is preserved for later restoration.
- **Right B:** toggle the eligible original and the candidate. It preserves the candidate percentage. Original also obeys the floor and directional-wall rules.
- The sample right-A Jump action is disabled to honor the no-jump requirement. Other locomotion, crouch and climbing settings are preserved.
- Added the splat render feature to `Mobile_Renderer.asset`, which this scene actually uses in Play Mode. Reconnected the climbing event handler and its movement-disable/enable events to the existing `PlayerController` locomotor; all three references were missing. The rig and movement configuration remain user authored.
- Review status appears beside the slider and in the desktop Game view. Pose recording and Save view controls are hidden while capture is disabled. Existing ordinary poses and bookmarks remain under `SplatData/sessions/<session>/`; no new recording files are created by review.

## Original completed processing

Full job: `20260921-162435-1314a11cfcc740fd9515a42b7012aa37`.

| Quantity | Result |
|---|---:|
| Original source N | 6,011,316 |
| Below-floor exclusions | 76,964 |
| Eligible original M | 5,934,352 |
| Scoring views | 192 |
| Independent held-out / regression views | 32 / 16 |
| Processing time on RTX 5060 Ti | 57.75 s |
| Contribution scoring portion | 21.286 s |
| Peak CUDA allocated / reserved | 1.51 / 3.01 GiB |
| Maximum instrumented vs stock RGB/alpha error | 0 |

Percentages use M. Each verification candidate is a real subset rerender, compared against the eligible original from the same held-out camera. Mean RGB absolute error below is expressed as a percentage of the 0–1 channel range; it is not a perceptual quality score or a headset performance measurement.

| Keep | Splats | Mean RGB error | Worst-view mean RGB error |
|---|---:|---:|---:|
| 100% | 5,934,352 | 0% | 0% |
| 75% | 4,450,764 | 0.00670% | 0.03022% |
| 50% | 2,967,176 | 0.10459% | 0.24512% |
| 25% | 1,483,588 | 0.82932% | 1.90606% |
| 0% | 0 | endpoint checked | endpoint checked |

Start inspection at 50%, then compare 25% and 75% around labels, controls, edges and close views. No explicit label/panel regions were authored, so the aggregate scores do not establish that every small detail survives. The finite camera sample also does not prove that any splat is invisible from every possible pose.

The renderer selects exactly the frozen rank prefix **before depth sorting and drawing**. Source buffers remain resident; lowering the slider reduces selected work without promising proportional memory savings. Android/Quest frame rate, stereo behavior and maximum sustained splat capacity still require testing on the target headset.

## Files and repeatable workflow

- `SplatData/current_round1.json` identifies the published full round.
- `SplatData/results/20260921-162435-1314a11cfcc740fd9515a42b7012aa37/report.html` contains the image gallery, ordered by worst 25% error. `report.json` has per-view metrics, timing and backend provenance.
- The same result directory holds `rank_manifest.json`, `rank.bin`, source-addressed `importance.bin`, raw contribution statistics, eligible/excluded original IDs, the immutable scene snapshot and all three camera sets.
- Rank: `round1-7351c469-49c144f2-234587103f50`.
- Original source SHA-256: `7351c4694b28360c8898c47f799a63e8b69fb941eac0a62a830a407cf044605d`.
- `SplatData/validation/round1-source-identity/identity_report.json` verifies every loaded uncompressed attribute against the original row: 6,011,316 rows, 84,158,424 float32 checks, zero mismatches. The explicit storage-to-source map is retained alongside it.

Use **Splat Preprocessor → Process** to capture and process a newly saved authored scene. Processing owns a recoverable preview lease and pauses only the source renderer while the CUDA worker runs. Published results change only after the complete scoring/verification operation succeeds. Use **Review → Load selected completed rank** between review sessions. Changing model placement, NavMesh, active wall geometry or the fixed wall profile requires a new round; source/import identity is checked before capture and loading.

Large generated data and the local Python environment remain under ignored project folders. Preserve them with the local project if you want to retain this exact round. Final PLY export, compressed reimport, history merging and a second scoring round are later workflow steps.

## Original round validation

- **90/90 Unity EditMode tests and 55/55 Python tests passed.** Unity evidence: `SplatData/validation/m1/round1-final-green.xml`.
- The plane-visibility follow-up passed **90/90 Unity tests** after five visibility regressions reproduced the previous forced-hiding behavior. Evidence: `SplatData/validation/m1/plane-visibility-red.xml` and `plane-visibility-green.xml`. Mesh visibility remains independent of the directional splat geometry through updates, disable/enable, hierarchy changes and invalid geometry.
- **10 live Play Mode captures passed**, covering both NavMesh levels at 0/25/50/75/100%. Actual GPU sort/draw membership matched the frozen original-row rank exactly, with unchanged resident source buffers. Both zero endpoints had zero selected splats and zero source pixels. Evidence and images: `SplatData/validation/round1-runtime-final/validation.json`.
- Physical slider endpoints and intermediate values, B toggling without losing the candidate, and A bookmarks in both display modes passed a Play Mode smoke check. Ordinary pose records omit candidate percentage; the two bookmarks retain it. Evidence: `SplatData/validation/round1-play/control_smoke.txt`. Test sessions are isolated under this validation directory.
- An injected report-write failure restored the original source layer and 50% selection, removed the temporary camera, and released capture callbacks. Evidence: `SplatData/validation/round1-capture-write-failure/recovery.txt`.
- Independent review findings about fresh source identity, rank recovery, active-wall/profile agreement, and capture cleanup were fixed and reviewed again; no Important findings remain in that review scope.
- Final Edit Mode reload repeated the full source/import identity audit, validated the saved scene against the frozen snapshot, and saved the scene with seven directional walls and the centered 50% slider. A/B polling and ordinary recording are enabled; no test recording remains open. Evidence: `SplatData/validation/round1-final-readiness.txt`.

These are desktop Unity and CUDA checks. No headset session was available: actual controller delivery, per-eye rendering, standalone Android frame rate and sustained device capacity are not yet measured. The no-headset OpenXR startup error seen during desktop Play Mode is separate from the completed renderer and control-state checks. Earlier failed capture attempts are retained as diagnostic evidence; `round1-runtime-final` is the passing live-capture result.
