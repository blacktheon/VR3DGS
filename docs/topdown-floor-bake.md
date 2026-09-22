# Top-down floor texture — 22 September 2026

`ScottVicker_TopDown.ply` is imported into `Assets/Scenes/TakeTopDownScreenshot.unity`. A **5238 × 8192** orthographic screenshot is assigned as the floor material in **Stage1, Stage2 and TakeTopDownScreenshot**. The capture scene previews the baked floor; its imported source renderer is disabled but remains available for another bake.

## Assets and evidence

- Original: `C:/Users/black/Downloads/ScottVickers_Full/ScottVicker_TopDown.ply`.
- Imported copy: `Assets/SplatPreprocess/Generated/TopDownSource-20260922/ScottVicker_TopDown.ply`.
- Full-resolution image: `Assets/SplatPreprocess/Generated/SurfaceBakes/floor-topdown-20260922/01-Floor.png`.
- Material: `Assets/SplatPreprocess/Generated/SurfaceBakes/floor-topdown-20260922/01-Floor.mat`.
- Small image: the same folder's `01-Floor-preview.png`.
- Capture report: `SplatData/surface-bakes/floor-topdown-20260922-bcbd31cc572741178ad09c7ac8482775/surface-bake.json`.
- Alignment, scene backups, change audit and reload verification: `SplatData/topdown/`.

The source contains **6,577,198 SH0 splats**, imported uncompressed with no pruning. SHA-256: `db1eeae7cd03be604d7e0d5d5d15f09a0ac381ff4e77a6ccc41e307aa427ca56`. Every source row was submitted and its GPU identity verified during capture. No Stage1 percentage, deletion or wall mask was applied.

The new export has a small rotation relative to the earlier source. Exact scale/color fingerprints provided 21,330 position correspondences; a rigid fit retained 21,304 inliers within 0.001 source units. The median residual is 8.07e-8 units. `alignment.json` records the fit, original model matrix and RUB-to-RUF reflection. This adjusts only the new capture source; the existing scene model transforms are unchanged.

The camera faces down from the plane's +Y side and uses its actual UV mapping and 8.0225 × 12.546 Unity-unit footprint. The transparent-background probe covers about 45.62% of the plane. The final capture uses the original floor material's base color (0.5, 0.5, 0.5, 1) behind the scan, so unsupported areas remain neutral gray. Its reported alpha coverage includes this opaque background.

The material is opaque URP Unlit with a lossless source PNG. Editor import preserves the full resolution uncompressed; Android uses ASTC 6×6, mipmaps and an 8192 maximum dimension. The PNG is 34,461,026 bytes, SHA-256 `0bdf301dcf43c630d04e358f3a1da1a76ab2497ebebf7307f5173f97bdf8bd17`.

## Verification

- Unity suite: **120 passed, 0 failed, 0 skipped**, including the new source-identity/pruning regression. Evidence: `SplatData/validation/m1/topdown-final.xml`.
- Final report records `captured`, exact full-source membership, successful state restoration and material application.
- All three scenes were reopened and the material, imported dimensions and enabled floor colliders verified.
- Against this turn's backups, Stage1 and Stage2 each change only the existing Floor MeshRenderer's material reference. No scene objects were added or removed; model settings, floor geometry, colliders, NavMesh and authored controls remain as the user left them.
- Stage1's user-adjusted 20.10% preview is preserved. Stage2's immutable 988,866-splat model and six machine-surface materials are unchanged. The new floor is an environment appearance update, so future screenshot comparisons should use the current floor in both reference and candidate renders.

`Stage1SurfaceBaker.BeginVerifiedSource` now supports an explicitly inspected source count/hash and optional camera background; the existing original-source entry points keep their prior count and transparent-background behavior. Large generated assets and local evidence remain ignored by Git and must accompany the scenes when copying the project.
