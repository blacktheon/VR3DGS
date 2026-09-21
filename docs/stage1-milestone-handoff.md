# Stage 1 installation and development handoff — 2026-09-21

The selected gsplat-unity renderer is installed, and the complete Scott Vickers source is visible in `Assets/Scenes/Stage1.unity`. This milestone completes plan Tasks 0–2. Tasks 3–9 remain pending; the Unity window identifies the unfinished controls. Open **Tools → Splats → Stage 1 Preprocessor** for source inspection and worker setup/checks. See [the workflow guide](stage1-workflow.md) for operation and fresh-checkout restoration.

| Verified result | Evidence in this checkout |
|---|---|
| gsplat-unity 1.4.0 pinned at `a2bf458d6b16395e6570e9345f9f4408f92684b8`; D3D12 and five supported sorting kernels | `Packages/packages-lock.json`, `SplatData/validation/m0/baseline.json` |
| 6,011,316 original and uploaded splats, zero pruned, Uncompressed/RUB/SH0 | Baseline JSON and `SplatData/validation/m0/full-source-inspection.png`; also `game-camera.png` |
| All original rows valid; original and generated copy share SHA-256 `7351c4694b28360c8898c47f799a63e8b69fb941eac0a62a830a407cf044605d` | `SplatData/sources/7351c4694b28/source_manifest.json` and final file-hash checks |
| 43 Python tests pass, including bounded abandoned-build recovery using PyTorch's actual file baton | Final command: `& .venv/Scripts/python.exe -m pytest -q --tb=short` from `Tools/SplatWorker` |
| 14 Unity EditMode tests pass | `SplatData/validation/m1/m1-final-editmode.xml`; Unity code was unchanged by the final Python recovery fix |
| A running worker survives an actual Unity script reload; later cancellation publishes no result | `SplatData/validation/m1/reload-probe/evidence.json` |
| Final real CUDA smoke render passes finite-pixel, alpha 0.75 and premultiplied RGB checks | `SplatData/results/20260921-103204-3400194d34d7416780cc1b88591e8e05/environment.json` |

The final GPU check used CPython 3.11.16, PyTorch 2.7.1+cu128, gsplat 1.5.3 at `937e29912570c372bed6747a5c9bf85fed877bae`, MSVC 14.44.35207 and CUDA 12.8.93 on an RTX 5060 Ti. Its one-splat render reserved 2 MiB. That validates this backend; it does not establish full-source CUDA memory use or Android capacity. No Android device was connected during the device probe. Sustained frame timing, headset stereo and maximum practical splat count remain unmeasured.

Next implementation work is source/storage identity and numerical Unity/Python camera-coordinate equivalence, followed by exact GPU subset selection and contribution scoring. Bookmarks, A/B comparison, session merging, verification, unchanged-row export and reimport checks follow those contracts. The user supplies the VR rig, walkable interaction and input bindings. Physical scale remains uncalibrated until a known distance and source endpoints are supplied.

An independent reviewer examined the initial milestone and independently passed the 42-test pre-fix Python suite. The Important finding was a persistent PyTorch build lock after a crash. Its regression test first timed out on the old loader, then passed after recovery switched to a fresh build directory without deleting potentially live compiler files. The final full suite passed 43/43, and the GPU check above passed. No second review was requested.

Two minor review findings remain deferred: environment reports lack the compiled extension hash and NVIDIA driver version; and VS 2022 activation currently uses that installation's default toolset instead of selecting and validating 14.44 explicitly. The successful run records the actual compiler path/version. These are reproducibility improvements to make before portable setup or comparative performance claims.

The implementation decisions, in their original order, are retained here for later stages:

1. Use the existing checkout on `codex/stage1-preprocessing`, per the user's same-project requirement. Cost if wrong: live-editor coordination; preserve and recheck user changes.
2. Keep the user's removal of Aras. Cost if a rollback is needed: restore it deliberately from Git history.
3. Verify renderer configuration through UPM resolution, supported kernels and actual rendering. Cost if insufficient: add coverage for a demonstrated configuration failure.
4. Continue independent CPU work while the incorrect/retrying Unity connection is unavailable. Restart RPCs need a durable one-shot guard or an external restart. Cost if wrong: repeated Editor shutdown; the connection has now recovered.
5. Reconcile the actual Python worker PID and creation time, with launcher identity only during startup. Windows venv launchers can use a different PID. Cost if wrong: misclassifying a live job; subprocess and domain-reload evidence cover this boundary.
6. Initially try raw RUF coordinates and preview translation with unit scale. Cost if wrong: preview adjustment. The coordinate choice is superseded by decision 9; no source bytes changed.
7. Add a disabled optional inspection camera under the new Stage 1 root because the untracked Edit Mode rig sits at floor level. Cost if unwanted: remove that optional camera.
8. Build unchanged upstream gsplat CUDA source with PyTorch's public loader and MSVC `/O2`, because upstream's GCC host flag fails with D8021 on this system. Cost if wrong: revise the build profile; rasterizer math is unchanged.
9. Persist the importer-recommended RUB source profile with one Unity conversion, Uncompressed storage and zero pruning. Cost if wrong: revise the profile during Task 3's numerical checks; the original source remains intact.
10. Let the CLI own stdout/stderr files so Unity reloads cannot close its pipes. Cost if wrong: revise log transport; the durable request protocol remains.
11. Defer releasing preview GPU resources until Task 5's full-source work. The tiny M1 smoke check keeps the scene visible. Cost on constrained hardware: add that suspension earlier.

The review deliberately left these boundaries to their designated later work; they remain explicit limits of this milestone:

| Deferred judgment | Decision and consequence |
|---|---|
| Both importer identity mappings and camera/coordinate equivalence | Task 3; no quantitative cross-renderer equivalence is claimed. A mismatch requires mapping/profile correction before scoring. |
| Percentage selection, zero selection, depth ties and rapid changes | Task 4; the current preview is full-source only. Additional renderer work is required. |
| Contribution scoring, full-source GPU memory and preview suspension | Task 5; only the one-splat smoke result is established. Full-source memory pressure can require suspension/batching. |
| Verification isolation, changed calibration, bookmarks, merging, export and reimport | Tasks 6–9; those workflows are unavailable until implemented and tested. |
| Physical metric scale | Await a known real measurement; unit scale cannot establish walkable clearances. |
| Android capacity, sustained performance, stereo and VR bindings | Await the exact device and user VR setup; desktop counts cannot establish a headset budget. |
| Fresh live Unity render and EditMode execution | Root agent supplied actual captures and test XML; reviewer assessed saved evidence. Repeat if later renderer changes invalidate it. |
| Other operating systems and nonstandard toolchain locations | Outside this initial Windows profile; add explicit profiles before supporting those environments. |

These files and the local validation data preserve the milestone record while the broader plan remains in progress. Large source/worker/cache artifacts are ignored by Git; follow the workflow guide when restoring a fresh checkout.
