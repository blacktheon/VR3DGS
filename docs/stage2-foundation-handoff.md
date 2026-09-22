# Stage 2 foundation — 22 September 2026

The accepted **988,866 splats plus six textured surfaces** are saved as an immutable input and reusable Unity prefab. `Assets/Scenes/Stage2.unity` uses the compact asset with the authored locomotion, NavMesh, floor and interactables retained. This is the first milestone of `C:/Work/Unity/VR3DGS/STAGE2_LOD_PLAN.md`; the complete four-level system is still in development.

## Current artifacts

| Item | Project-relative path / value |
|---|---|
| Scene | `Assets/Scenes/Stage2.unity` |
| Reusable model and six surfaces | `Assets/SplatLOD/Generated/accepted-f530cd928a53119b/AcceptedMachine.prefab` |
| Compact model | `Assets/SplatLOD/Generated/accepted-f530cd928a53119b/accepted.ply` |
| Immutable input | `SplatData/Stage2/inputs/accepted-20260922-03/stage2_input.json` |
| Layout and binary ownership | `SplatData/Stage2/layouts/octree-20260922-03/` |
| Input identity | `accepted-f530cd928a53119b` |
| Layout identity | `octree-ed2d6a0870f8e567` |
| Accepted PLY SHA-256 | `2eb965c4cc5deea3e5f4385932f31f106111a2df2cfa38fdb3ab191f9a9c7892` |
| Scene before this milestone | `SplatData/Stage2/backups/Stage2-before-foundation.unity` |
| Visual/data/reload evidence | `SplatData/Stage2/validation/foundation-frames/` |

**18.927034%** is relative to the former 5,224,622-splat model. The same selection is **20.441522%** of the later 4,837,536 eligible set. Stage2 stores exactly 988,866 rows, without recalculating from a rounded percentage or retaining six-million-row buffers for its model.

The export preserves original Gaussian records, source-row IDs, inherited importance, transform and floor/box exclusions. Original source and Stage1 heatmap data remain available. The version identity incorporates appearance, source IDs, importance and frozen-rank provenance, preventing collisions between equal-looking exports with different identities.

All six PNGs are copied into the immutable input (245,577,736 bytes total). The prefab references the existing five materials under `Assets/SplatPreprocess/Generated/SurfaceBakes/final-20260922/` and the top material under `.../top-final-20260922/`. Their hashes and transforms are checked before building. Preserve these generated directories and `.meta` files when copying the project: large assets and `SplatData` are outside ordinary source control.

## Available now

- Open **Tools → Splat LOD Builder**. The current input/layout are prefilled. **Build / Update Stage2 Scene** validates identities, surface transforms and complete ownership, then saves the scene and prefab.
- **Red chunk boxes (right A)** previews the overlay in Edit Mode. In Play Mode, right-controller **A** toggles once per press; a desktop Game-view button offers the same action. The saved default is off.
- **94 occupied chunks** cover each accepted row exactly once. Settings: target 32,768 splats, minimum cell size 0.25 user-authored Unity units, maximum depth 10. The actual layout reaches depth 4 with 25–30,745 splats per chunk.
- Chunks expose identity, member range, partition bounds and conservative render bounds. Center ownership is deterministic at split planes; full Gaussian covariance contributes a four-sigma axis extent to render bounds.
- One compact uncompressed LOD0 renderer retains the shared transparent depth sort. Chunk objects contain metadata, and red lines use one batched mesh. Per-chunk culling and lower-LOD switching are not implemented yet.
- Stage1 A/B, percentage slider, preview panel and wall-binding behaviours are disabled only in Stage2. Stage1 retains its exact accepted count and original controls.

Calibration remains `user_authored_unity_units`; no independently measured metre scale was supplied. The explicit request to preserve baked surfaces governs their visibility over the generic design's annotation-only guidance.

## Validation

- Worker suite: **109 passed** (`.venv/Scripts/python.exe -m pytest -q` from `Tools/SplatWorker`).
- Unity Edit Mode suite: **119 passed, 0 failed, 0 skipped**; `SplatData/validation/m1/stage2-handoff.xml`.
- Every Position, Scale, Color and Rotation in the final imported compact asset exactly equals its original source row addressed by `source_ids.bin`.
- Five distinct normal URP Play Mode frames checked exact GPU membership and draw count: original accepted selection, repeat, compact export, repeat, and overlay. Each draws 988,866 rows. Both repeat pairs are pixel-identical.
- At the documented 1920×1080 overview, original versus compact export differs at 240 of 2,073,600 pixels: RGB MAE **0.0000815/255**, PSNR **85.406 dB**, maximum channel difference 13/255, only two pixels above 4/255. This is one overview check, not multi-view or headset acceptance. Tiny sorting differences remain possible despite exact attributes; cross-representation pixels are not claimed to be byte-identical.
- Saved Stage2 reload restores the compact asset, six materials, 94 chunks, 752 overlay vertices, overlay off and disabled Stage1 handlers. No temporary camera remains. All 740 original scene object/component IDs survive; 287 generated IDs were added.
- Stage1 was reopened and allowed to initialize normally: exact count 988,866, final top material and enabled original controls verified. Unity was returned to saved Stage2 in Edit Mode.
- Independent review identified export-identity and capture-cleanup issues; both were corrected. Identity regressions failed before the fix and passed afterward.

The earlier `validation/foundation/` render-request comparison is superseded: synchronous Editor requests inconsistently omitted opaque meshes. Use `foundation-frames/` for visual evidence. Development exports `accepted-01` (incomplete surfaces) and `accepted-02` (older identity scheme) are not current.

Headset stereo appearance, physical controller operation and Android timing/memory have not been measured. The A-button edge logic and Game-view overlay were verified locally.

## Next milestone

Validate importance-aware, full-covariance Gaussian merging on a representative panel/housing section against immutable LOD0. Measure actual counts and Unity appearance before generating LOD1–LOD3 for every chunk. Then add B automatic/uniform switching, the discrete 0–3 slider, right-hand pointer/quality marks and targeted refinement, with mixed-level boundary/transparency checks. These controls remain unavailable until their underlying assets exist.

The worker CLIs support repeatable input and chunk generation:

```powershell
# Run in Tools/SplatWorker; choose new, unused output directories.
.\.venv\Scripts\python.exe -m splat_worker.stage2_input --source-manifest ../../SplatData/current_inspect.json --rank-manifest ../../SplatData/results/top-customization-20260922-01/rank_manifest.json --keep-count 988866 --output ../../SplatData/Stage2/inputs/NEW_VERSION --presentation ../../SplatData/Stage2/inputs/accepted-20260922-03/presentation.json
.\.venv\Scripts\python.exe -m splat_worker.stage2_chunks --input ../../SplatData/Stage2/inputs/NEW_VERSION/stage2_input.json --output ../../SplatData/Stage2/layouts/NEW_LAYOUT --target-count 32768 --minimum-cell-size 0.25 --maximum-depth 10
```
