# Round 1 using the user's authored scene

This continues the accepted Stage 1 implementation. The user has supplied locomotion, interactables, a two-level NavMesh and planes under `Walls`, and requests original workflow steps 5/6 plus A/B and the existing physical slider. No replacement authoring or locomotion system is required. Original workflow step numbers differ from implementation task numbers.

Use the saved scene snapshot and original source hash. Preserve the user's current scene/material/NavMesh/XR edits; their pre-integration copies are kept in this plan's local workspace. Implement independent renderer and runtime-control modules in parallel with root-owned offline processing; root alone operates Unity and integrates the scene. This updates the earlier inline-only execution preference for independent modules, without creating a worktree or another project.

View heights default to 0.3–1.8 Unity units above each NavMesh surface, including the roof. The user has been asked to clarify local versus absolute height and the wall band. Use deterministic triangle-area sampling stratified over both surface levels, four crouch-to-standing heights, and target-facing directions. Save actual camera matrices and distinct scoring, held-out and fixed-regression membership. The first round is finite sampled coverage, not proof of invisibility at every possible pose. Physical meter calibration remains based on the user-authored scene units, not an independently measured source dimension.

The floor is an explicit hard exclusion requested by the user: discard source centers below its world plane from the eligible review baseline. Preserve original bytes and original row IDs; record original count N, eligible count M, excluded IDs and floor rule. The percentage denominator is M for this authored-cleanup baseline, and B Original displays all M. This explicit baseline change supersedes the original all-N slider contract for this scene; source provenance always retains N.

Other walls are finite directional bands, not infinite half-space blockers. Initial rule: for a camera on local +Y side, suppress a center only if it lies within 0.1 unit behind the plane and the camera-to-center ray crosses the plane's rectangle. Front-side and deeper centers remain eligible. Record this center-based Gaussian classification in the profile, and apply it identically to scoring, candidate verification and Unity review. Hide analysis wall mesh renderers during review so they do not introduce unrelated hard occlusion. Keep gameplay colliders and NavMesh untouched.

Processing uses gsplat's pinned projection/tile ordering with a separately attributed CUDA forward accumulator at the actual alpha-times-transmittance compositing point. Test stock RGB/alpha equivalence and independent occlusion/clamp/cutoff/termination oracles before full-source scoring. Preserve source-addressed peak, integer fixed-point sums, pixel hits and coverage; deterministic f64 ranking ties use source ID. Candidate images rerender real subsets at 100/75/50/25 percent and zero endpoint; held-out views never enter the scoring set. Reports label sampling, coordinate/color profile, scene/source identity, memory and timing.

The live renderer compacts the rank prefix before sorting/drawing, preserves resident source buffers and reseeds canonical order per camera. Bind `Slider No Snapping/Grabbable` X translation to the existing [-0.06, 0.06] range without changing its movement. Right A records an exact-view priority bookmark and candidate percentage; right B toggles original/candidate without changing the remembered value. Pose-only session recording is separate from bookmarks.

- [x] Capture and validate authored geometry; test coordinates, floor/wall rules and disjoint sampling.
- [x] Implement and test CUDA contributions against CPU and stock renderer; pin actual backend provenance.
- [x] Implement early exact GPU selection and existing-control integration with RED/GREEN tests.
- [x] Run the complete original source through the first scoring and held-out verification round; preserve atomic outputs and GPU preview lease.
- [x] Load the frozen rank, bind the scene controls, capture representative Unity original/candidate views and verify count endpoints.
- [x] Review the integrated change, fix important findings, save reports and provide measured results and remaining limitations.

Completed evidence, measured candidate counts/errors, controls and remaining device-validation boundaries are recorded in `docs/stage1-round1-handoff.md`. The scene is saved at 50% for the user's step 7 inspection.

Final export/reimport and second-round history merging are later original workflow steps and are not acceptance requirements for the user's first-round processing request.
