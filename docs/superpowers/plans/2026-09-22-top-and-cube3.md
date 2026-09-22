# Top texture and Cube (3) continuation

Continue the accepted surface-customization workflow in the user's live Unity project. The user has already selected 20%, added inactive Deletable/Cube (3), and authored Surfaces/top. Preserve the five existing texture assignments and the inherited heatmap. Original source remains immutable.

- [x] Extend the baker to capture/apply an explicit surface subset with the existing full-source, UV, integrity and cleanup checks; retain legacy report support. Verify a single top selection, missing/duplicate selection rejection, and complete capture count.
- [x] Capture top alone from all 6,011,316 source rows at 8192 longest edge, inspect and assign its unlit material with Android ASTC6x6.
- [x] Derive another permanent rank exclusion using all four boxes and the floor. Compute incremental Cube (3) removal, remaining total, and intersection with the existing 20% prefix; preserve exact importance bytes and rank relative order.
- [x] Use the accepted interpretation of 20% before the new cut, bind the derived rank, set the starting preview and physical slider, and verify 0/start/100, material preservation and saved reload.
- [x] Record the new counts, artifact paths and verification in the handoff.

The user subsequently accepted the resulting 18.93% model and requested that it become Stage2 input. The top was captured at 3130 × 8192 from all 6,011,316 raw source rows and assigned; the exact 988,866 surviving rows preserve the old 20% prefix after Cube (3) removal.

Finalization passed all 110 Unity tests. Its final synchronous-camera PNG equality assertion was overly strict; exact membership was correct. The later normal, separate-frame Stage2 reference comparison resolves capture repeatability and is recorded in `docs/stage2-foundation-handoff.md`. The current expanded Unity suite passes 119 tests.

Stage1 was reopened and initialized normally, verifying the saved exact count, six materials including final top, and enabled review controls (`SplatData/validation/top-customization/saved-reload-final.txt`). Temporary finalizer and request marker were removed. The original source, five prior textures and inherited heatmap are unchanged.
