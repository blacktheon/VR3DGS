# Top surface and Cube (3) — 22 September 2026

This continues the [five-surface customization](stage1-surface-customization.md). The working interpretation is **start with the previous model's selected 20%, then remove Cube (3), without replacing removed splats**. The existing five materials and original importance values are preserved.

## Counts and percentage denominator

| Quantity | Splats |
|---|---:|
| Complete immutable source | 6,011,316 |
| Previous model after floor and first three boxes | 5,224,622 |
| Previous 20% selection | 1,044,924 |
| Additional splats removed by Cube (3) from the previous model | 387,086 |
| Additional removal within the previous 20% selection | 56,058 |
| Exact starting selection after this cut | **988,866** |
| New retained 100% set | **4,837,536** |

The exact starting selection is **18.927034% of the previous model**, or **20.441522% of the new retained set**. The percentage control rounds this to **20.44%**. Its stored exact count preserves all 988,866 survivors; rounding the percentage and recalculating the count would otherwise lose another 74 splats.

Cube (3) contains 541,127 original centers, of which 154,041 were already removed by the previous boxes. The four-box union contains 1,096,816 centers. Including the existing 76,964 below-floor exclusions gives 1,173,780 total exclusions. The new retained set is 80.473826% of the immutable raw source.

Deletion uses splat centers in BoxCollider local space, includes boundary faces and inactive authoring boxes, and preserves original row IDs. The new ranking is exactly the previous ranking with Cube (3) members removed. Its first 988,866 rows are exactly the survivors of the old 20% prefix. No removed ID can return when the slider or B comparison shows 100%.

## Top texture

`Surfaces/top` was captured orthographically from its local +Y side using **all 6,011,316 raw splats**, before any floor, box or percentage filtering. Resolution is **3130 × 8192**, following the authored plane's dimensions and UVs. Image coverage was complete and the bake restored temporary capture state successfully.

- Texture: `Assets/SplatPreprocess/Generated/SurfaceBakes/top-final-20260922/01-top.png`
- Material: `Assets/SplatPreprocess/Generated/SurfaceBakes/top-final-20260922/01-top.mat`
- Small preview: `Assets/SplatPreprocess/Generated/SurfaceBakes/top-final-20260922/01-top-preview.png`
- Capture report: `SplatData/surface-bakes/top-final-20260922-5f5cc88e21d74af381358111f7ca9190/surface-bake.json`

The material uses URP Unlit. The lossless source PNG and uncompressed Editor import retain full detail; the Android override uses ASTC 6×6, mipmaps, maximum compression quality and an 8192 maximum dimension. Existing five materials remain in `SurfaceBakes/final-20260922/`.

The baker now accepts an explicit hierarchy-path subset:

```csharp
Stage1SurfaceBaker.BeginSelected(newAssetsFolder, new[] { "Surfaces/top" }, 8192);
```

As before, capture requires an unpaused, dedicated Play Mode session with review controls disabled. After completion, leave Play Mode and apply the report, restore the controls and validated ranking, and save. `Begin` without a subset now captures all authored surfaces. Apply validates source/image hashes and matching transforms/UVs, and changes only surfaces listed in the report. Legacy five-surface reports remain supported.

## Preview behavior

The saved start is candidate mode with the exact 988,866 count. The physical slider's local X is −0.035472 in its existing −0.06 to +0.06 range, corresponding to the rounded 20.44% control value. An unchanged slider value and B's comparison round trip preserve the exact count. Moving the slider to a different percentage resumes ordinary percentage selection over the new 4,837,536 denominator.

The exact count is bound to this ranking's identity and saved with the scene, preventing accidental reuse for a different processed model. A bookmarks the actual candidate count and records whether it is exact. B compares against the retained 100% set, including all permanent exclusions.

Edit Mode percentage changes remain available under **Tools → Splats → Stage 1 Preprocessor → Review → Keep splats (%)**. The rounded percentage is for control/display; the count is authoritative for this starting selection.

## Data and validation

- Derived rank: `SplatData/results/top-customization-20260922-01/rank_manifest.json`
- Rank ID: `customized-7351c469-aaaacab8-08bfa69f5251`
- Parent rank: `customized-7351c469-ee224a91-2a00455c5897`
- Scene snapshot: `SplatData/inputs/scene-20260922-020809-190-e67de492445b42139143593d5a0e1d3a.json`
- Independent data audit: `SplatData/validation/top-customization/data-audit.json`
- Unchanged importance SHA-256: `92d3cc1a0dcc1b3c266b44f808e6df3d542d864fc164ef3e211f191300b41d90`
- Original source SHA-256: `7351c4694b28360c8898c47f799a63e8b69fb941eac0a62a830a407cf044605d`

The binary audit verifies the complete survivor rank, the exact starting prefix, and unchanged importance bytes. This operation does not rescore the model; `SplatData/current_round1.json` still identifies the original scoring run.

Final integration is complete: the top material is assigned, exact candidate count saved, original controls restored, and normal scene reload verified after Editor initialization. Evidence: `SplatData/validation/top-customization/saved-reload-final.txt`. The Unity suite passed 110 tests at integration and 119 after the subsequent Stage2 foundation was added.

The temporary finalizer's PNG-byte equality check was too strict for its synchronous Editor captures; exact source membership remained correct. The later separate-frame reference capture includes all six surfaces and opaque meshes, with repeatability and export comparison documented in [Stage2 handoff](stage2-foundation-handoff.md). The one-use finalizer and request marker have been removed.

The renderer still retains the full source buffers in memory. Cuts reduce sorting/drawing work; no new Android frame-rate measurement is claimed. Generated assets and large data are ignored local project files and must be preserved alongside the scene.
