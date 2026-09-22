# Surface customization — 22 September 2026

The authored `Surfaces` planes now have five orthographic screenshots of the complete source. The three `Deletable` boxes remove their contained splat centers from the active ranking. The original source file remains intact, and the original heatmap importance values and survivor ranking order are preserved.

## Captures and materials

All five captures used all **6,011,316** original splats, before either floor or box filtering. Each camera faces the plane from its local +Y side. Camera framing follows the plane's world dimensions and actual UV coordinates; the PNGs can appear rotated when viewed separately because their axes follow those UVs.

| Authored plane | Texture resolution |
|---|---:|
| `Surfaces/back` | 4417 × 8192 |
| `Surfaces/right1_1` | 4652 × 8192 |
| `Surfaces/right1_2` | 4991 × 8192 |
| `Surfaces/right2_1` | 4652 × 8192 |
| `Surfaces/right2_2` | 4991 × 8192 |

The PNGs and assigned URP Unlit materials are in `Assets/SplatPreprocess/Generated/SurfaceBakes/final-20260922/`. Unlit shading preserves the captured colors without applying scene lighting a second time. The default imports keep the full resolution uncompressed, with mipmaps. The Android override uses ASTC 6×6 at maximum compression quality, retaining the requested resolution. The original PNGs remain lossless.

Capture provenance: `SplatData/surface-bakes/final-20260922-5e54fc42eda04c5eab8d812f7e866637/surface-bake.json`. Every capture records its camera matrices, geometry and UV signature, image SHA-256, source SHA-256, exact full-source GPU membership and unchanged source buffers. All five had complete image coverage and restored the temporary capture state successfully. Their materials were applied to the original five planes without changing transforms.

The initial 1024 probe exposed a Windows file lock on mutable metadata inside Assets. Reports now live outside AssetDatabase imports. The subsequent five-image probe and final five-image bake both completed; the failed attempt is retained as diagnostic evidence.

## Deletion and preserved importance

Membership uses each splat's center transformed into the BoxCollider's local space, with inclusive box faces. Inactive authoring boxes are included. Overlapping volumes are combined before filtering; no original row IDs are renumbered.

| Quantity | Count |
|---|---:|
| Complete source | 6,011,316 |
| Existing below-floor exclusions | 76,964 |
| Unique splats inside the three boxes | 709,730 |
| Total remaining eligible splats | 5,224,622 |
| 30% preview | 1,567,386 |

The individual boxes contain 295,521, 288,197 and 239,822 centers; the overlap accounts for 113,810 duplicate per-box entries. Percentages use **5,224,622** as their denominator. The 100% comparison therefore retains the box and floor deletions.

Derived data: `SplatData/results/surface-customization-20260922-01/`.

- Rank ID: `customized-7351c469-ee224a91-2a00455c5897`.
- Parent: `round1-7351c469-49c144f2-234587103f50`.
- Original source SHA-256: `7351c4694b28360c8898c47f799a63e8b69fb941eac0a62a830a407cf044605d`.
- Unchanged `importance.bin` SHA-256: `92d3cc1a0dcc1b3c266b44f808e6df3d542d864fc164ef3e211f191300b41d90`.

This is a filtered derivative of the existing ranking, not a new scoring round. `SplatData/current_round1.json` still identifies the original scoring run. Its old image-error measurements do not describe this modified scene.

## Preview and controls

The scene is saved at **30%, candidate mode**. `Slider No Snapping/Grabbable` has local X = −0.024 within its existing −0.06 to +0.06 range, also corresponding to 30% when Play Mode begins. A bookmarks the current view; B compares the retained 100% set with the remembered candidate.

For Edit Mode, open **Tools → Splats → Stage 1 Preprocessor → Review** and change **Keep splats (%)**. Leave **Show original (100%)** unchecked for the candidate. Save the scene to persist the chosen percentage. The physical slider controls Play Mode independently of that Editor field.

Preview loading validates the source, NavMesh, active walls, boxes and surfaces against the customized snapshot. Moving the head camera or resizing the Game view does not invalidate the ranking. Editing source geometry or deletion/surface placement requires updated data. The seven old directional wall planes remain inactive; the floor remains active and normal mesh visibility is preserved.

## Validation and performance limits

- CPU worker suite: **79 passed** (contribution GPU suite excluded; this operation did not rescore).
- Data audit: survivor rank is exactly the parent order with excluded IDs removed, heatmap bytes are unchanged, and excluded IDs occur in no candidate prefix. Evidence: `SplatData/validation/surface-customization/data-audit.json`.
- Live GPU membership: exact counts and row membership at 0%, 30% and 100%, with unchanged resident source buffers. Evidence: `SplatData/validation/surface-customization/live-gpu-membership.txt`.
- Unity suite: **105 passed**, including camera-boundary coalescing, pre-validation empty selection, preserved preview defaults, geometry boundaries, UV framing and capture math. Evidence: `SplatData/validation/m1/customization-camera-boundary-green.xml`.
- Five camera captures checked 30%, 100%, 0%, rapid 100 → 0 → 30, and repeated 30%. Each had exact GPU row membership. The three 30% PNGs were byte-identical; the zero capture contained the plane meshes and no splats. Evidence: `SplatData/validation/surface-customization/edit-camera-preview.txt` and adjacent PNGs.
- A real script-domain reload restored the customized rank, candidate 30%, GPU count 1,567,386, all controls, five textured planes and the physical slider position without dirtying the scene. No review session remained open and no Console errors were present. Evidence: `SplatData/validation/surface-customization/final-reload.txt`.

The Editor fix commits the latest requested selection at the camera boundary and records a camera-local URP draw after depth sorting. It avoids relying on Edit Mode `Update` to submit a global native draw, which could leave the preview blank after reload or retain stale draw requests. Opaque mesh depth is preserved. This path applies only to Edit Mode URP; Play Mode and device rendering retain the existing native path.

Deletion and percentage selection reduce sorting and drawing work; this reference workflow still holds the complete source buffers in memory. The five ASTC 6×6 mip chains have an approximate format footprint of 110 MiB, compared with roughly 988 MiB as uncompressed RGBA8. These are texture-size estimates, not measured headset memory or frame rate. Standalone APK data packaging, per-eye behavior and sustained Android performance still need the target device.

Generated captures, manifests and large binaries are in ignored local project folders. Preserve those folders with this scene; the Git sources alone do not contain the baked assets or ranking.

## Repeating the processing

Use the existing source and parent manifests with the customization worker and a newly captured scene snapshot:

```powershell
# Run from Tools/SplatWorker using its local Python environment.
.venv/Scripts/python.exe -m splat_worker.customization --source-manifest <source_manifest.json> --parent-manifest <parent/rank_manifest.json> --scene <scene.json> --output <new_result_directory>
```

`Stage1SurfaceBaker.Begin(newAssetsFolder, 8192)` requires unpaused Play Mode and returns a report path. For a dedicated capture session, save the scene with review actions, physical slider, preview panel and selection controller disabled; capture all five planes before applying the new cuts. After the report reaches `captured` with state restored, leave Play Mode, call `Stage1SurfaceBaker.Apply(reportPath)`, restore those controls, load the new customized ranking and save the scene. Use a fresh folder for every bake. `Apply` checks source and PNG hashes plus the authored transforms and UVs before assigning anything.
