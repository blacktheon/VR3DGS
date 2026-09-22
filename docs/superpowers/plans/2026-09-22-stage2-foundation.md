# Stage 2 accepted model and chunk foundation

> **For agentic workers:** Use superpowers:executing-plans for this first implementation milestone. The user requested development directly from the existing design and their duplicated live scene.

**Goal:** Save the accepted 988,866 splats and six visible surface textures, populate the user's `Assets/Scenes/Stage2.unity`, and implement validated chunk ownership plus a Game-view/VR chunk overlay.

**Architecture:** Export immutable source records and original IDs into a versioned Stage 2 input. Partition their centers in renderer-local coordinates using a deterministic adaptive octree, with separate conservative render bounds. Keep LOD0 in one compact renderer so transparent splats across chunk boundaries share one depth sort. Chunk objects contain metadata and share a batched wire overlay. The dedicated Editor tool loads and validates this foundation without changing the XR rig, NavMesh, or authored interactions.

**Tech Stack:** Existing Unity 6000.3.19f1, URP, gsplat-unity, C#, local Python 3.11 and NumPy. No new plugin or training dependency.

**Spec:** `C:/Work/Unity/VR3DGS/STAGE2_LOD_PLAN.md`, especially sections 1, 2, 5, A in section 6, 10 and 11.

## Global constraints and scope

- The latest user-selected input is 988,866 exact rows, described as 18.93% of the previous 5,224,622-row model; its current rank denominator is 4,837,536.
- Preserve the original Gaussian attributes, original row identities, inherited importance and source transform. The original source stays immutable.
- All six visible textured surfaces are part of the accepted appearance, not invisible annotation-only guides. Preserve them in Stage2 and reference captures.
- Calibration remains `user_authored_unity_units`; do not claim an independently verified metre scale.
- Use the existing duplicated `Stage2` scene. Stage1 controls must not poll buttons or change the new model there.
- Stage2 baseline LOD0 is the accepted model, not the six-million-row source. Never hide missing LOD1–LOD3 generation behind duplicate LOD0 assets.
- This milestone starts Stage2 with accepted input/chunks/LOD0/overlay. The design's representative merging-backend trial, four-level generation, pointer/marks/refinement and full acceptance are subsequent milestones. The tool must state that only LOD0 is available.
- Remain in the live project/branch because the user explicitly requested their open scenes. Do not switch worktrees, push, or publish.

## Review focus

1. Corrupt or mismatched rank/source identities must reject export before publishing a complete input.
2. Octree split-plane centers and identical positions must have unique ownership and bounded recursion.
3. Rotated/nonuniform source transforms must not be applied twice; render bounds include Gaussian extent.
4. Rebuilding the Stage2 generated hierarchy must preserve authored content and avoid duplicate generated chunks or active Stage1 handlers.
5. Scene reload and A button presses must preserve the model, material references and overlay; rendering must retain a single shared LOD0 sort.

## Tasks

- [x] **1 — Freeze the accepted input.** Confirm current GPU membership and saved preview. Add `splat_worker.stage2_input.export_accepted(source_manifest, rank_manifest, keep_count, output_dir, ...)`: hash-check inputs, select precisely the rank prefix, write original source bytes to `accepted.ply`, emit `source_ids.bin`, `importance.bin`, provenance and accepted percentage denominators. Publish atomically. Tests use known source records and fail on altered rank/source hashes, duplicate/out-of-range IDs, invalid counts and an existing output. Reimport the actual resulting PLY and compare attributes/appearance with the Stage1 preview. Save an accepted-model prefab with the six planes.

- [x] **2 — Build deterministic chunks.** Add `splat_worker.stage2_chunks.build_chunks(input_manifest, output_dir, target_count=32768, minimum_cell_size=0.25, maximum_depth=10)`. Emit `chunks.json`, one ownership index per exported row and concatenated member-row indices. Use >= midpoint for the upper child on each axis and child bits X=1/Y=2/Z=4; chunk IDs follow octree paths. Store partition bounds and covariance-derived conservative bounds (4-sigma per axis, conservatively covering the renderer quad support). LOD0 references the immutable shared input plus member spans. Tests cover boundary ownership, duplicate positions, deterministic reruns, complete coverage, true rotated covariance extent and invalid settings.

- [x] **3 — Populate the duplicated scene.** Add `Assets/SplatLOD/Runtime` and `Editor` assemblies with foundation tests in the existing Edit Mode test assembly, validated layout DTO/loader, chunk metadata, generated-root scene builder and `Tools > Splat LOD Builder`. The builder reuses the compact accepted renderer, changes only its generated hierarchy, disables Stage1 controls in Stage2, preserves all authored setup and generates the red overlay from occupied partition bounds. Right A toggles once per press; Editor control works without Play. Tests cover malformed/duplicate chunks, mismatched input hash, finite bounds, idempotent hierarchy updates and button edges. Keep lower-level controls unavailable until actual assets exist.

- [x] **4 — Verify and hand off.** Run worker and Unity suites, inspect Stage1-versus-export images and actual GPU counts, verify six materials and the saved Stage2 reload, inspect overlay in a camera capture, and have one independent final reviewer check source/provenance/scene preservation. Update the handoff with artifact paths, exact counts, chosen chunk settings and the next representative-backend milestone.

## Execution record

- Existing top finalizer ran: all 110 Unity tests passed, top material assigned, saved exact count 988,866 and controls restored. Its final PNG-byte equality assertion was too strong for observed renders: mean RGB differences about 0.0094/255, maximum 9/255, while GPU membership was exact. Investigate clean-capture repeatability before setting a comparison tolerance; remove the one-use finalizer after closing this evidence.
- Interface ruling: the six baked meshes are accepted visible appearance even though the generic design discusses invisible guides. The user's explicit request to keep surfaces governs this scene.
- Interface ruling: keep one uncompressed LOD0 renderer. The installed plugin's global multi-renderer merge requires SPARK and the current Edit Mode path uses per-renderer sorting; separate uncompressed chunk draw calls would introduce unverified transparency ordering.

- Completed export `accepted-20260922-03`, input `accepted-f530cd928a53119b`, layout `octree-ed2d6a0870f8e567`. All 988,866 original records and six textures retained; original IDs and inherited importance are versioned. Review found the first identity scheme omitted provenance; added two failing-then-passing regressions and regenerated the immutable package.
- Built and saved the user's Stage2 scene and accepted model/surfaces prefab. Layout contains 94 occupied chunks, 25–30,745 rows per chunk. All 740 original scene object/component IDs remain. Single shared compact LOD0 renderer, disabled Stage1 handlers, batched red overlay and Editor window verified.
- Full worker suite: 109 passed. Unity suite: 119 passed, 0 failed, 0 skipped (`stage2-handoff.xml`). Final imported positions/scales/colors/rotations match every original source ID exactly.
- Normal separate-frame camera captures resolved the Editor render-request artifact. Five captures have exact GPU membership/count. Repeat captures are pixel-identical; original versus compact model RGB MAE is 0.0000815/255, PSNR 85.406 dB at one 1920×1080 overview. Only two pixels differ above 4/255. This does not substitute for future multi-view/headset/Android validation.
- Saved Stage1 and Stage2 reloads verified. The six materials and exact count persist, with Stage1 controls enabled only in Stage1. Temporary finalizer/request marker removed. Final capture utility restores camera/renderer/time state even if failure logging fails.
- Independent reviewer findings addressed. Handoff: `docs/stage2-foundation-handoff.md`. Lower LOD merging/generation, B/slider/pointer/marks/refinement remain the explicit next milestones; Stage2 as a whole is not claimed complete.
