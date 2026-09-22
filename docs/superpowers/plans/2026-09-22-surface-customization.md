# Surface textures and box deletion

User requests five high-detail orthographic captures from the complete source, assigned to authored Surfaces planes; removal of splats within the three Deletable boxes; preservation of the existing heatmap; working preview set to 30%.

## Implementation contract

- Preserve the original source and the existing source-addressed importance bytes. Derive a new rank by filtering the original frozen order with current floor eligibility and the union of all three authored box volumes, including inactive guide boxes. Membership uses the splat center inside the oriented BoxCollider local bounds, including faces. Never attach the package's incompatible cutout component.
- Existing snapshot DTO gains optional `deletion_boxes` (name, local_to_world, world_to_local, center, size) and `surfaces` (same planar geometry fields as walls). Validate both at preview-load boundaries. Full source/import audit remains mandatory. The derived manifest explicitly identifies its inherited heatmap/rank parent; it is not newly scored against the changed scene.
- Bake before deletion with all N source rows, normal colors, no wall mask and only the source renderer visible to capture. Derive each orthographic camera from the plane's +Y front and actual UV mapping. Target 8192 pixels on the longest edge, preserve aspect ratio, and use matching unlit materials to avoid double lighting. Restore scene/camera/renderer state even on failure. Keep per-plane capture matrices, resolution, color/profile and source provenance.
- Keep rank/source membership exact before sorting/drawing. Candidate percentages use the survivors; 100% never reintroduces deleted rows. Original data remains resident in this reference workflow, so count reduction is not claimed as proportional GPU-memory savings.
- Repair missing review/wall/slider bindings in the user scene. Make Edit Mode preview reload a validated configured rank after domain/scene reload, preserve the selected percentage and default this customized scene to 30%. Keep existing rig, locomotion and unrelated scene edits.

## Work

- [x] Extend snapshot/validation and repair reliable preview lifecycle (root).
- [x] Add deterministic inherited-rank box filtering and audit outputs with meaningful CPU tests (worker module).
- [x] Add surface camera/texture baking and material assignment with orientation and cleanup checks (Editor module).
- [x] Capture five full-source surfaces, derive the deletion rank, bind it and save at 30%.
- [x] Verify exact deletions, 0/30/100 membership, texture orientation/detail, reload behavior and final scene; document counts and limitations.

Independent worker and Editor modules may be delegated under the parallel-agents skill. Root alone operates Unity and integrates scene mutations. User authorization covers the requested reversible project changes; no additional approval stage is needed. Work remains in this live checkout.

Live reload verification found that the validated selection could remain pending without an Edit Mode draw. The preview fix now also covers URP camera preparation and one camera-local draw per refresh, avoiding repeated global native submissions. This is Editor-only; Play Mode capture and the device renderer keep their existing path. A reviewer implements the bounded renderer change while root owns regression tests and live integration.
