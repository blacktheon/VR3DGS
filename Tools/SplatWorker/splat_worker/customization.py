"""Derive a box-filtered rank without changing its inherited source heatmap.

This CPU operation never scores or sorts splats. It publishes a new directory
only after validating the immutable source, frozen parent and complete output.
All binary ID arrays contain little-endian uint32 original PLY row numbers.
"""

from __future__ import annotations

import argparse
from contextlib import ExitStack
import hashlib
import json
import os
from pathlib import Path
import tempfile

import numpy as np

from .contracts import SourceManifest, file_sha256, write_json_atomic
from .ranking import keep_count
from .scene_geometry import floor_eligible, scene_hash
from .source import read_source


def _json_bytes(path):
    raw = Path(path).read_bytes()
    value = json.loads(raw.decode("utf-8-sig"))
    if not isinstance(value, dict):
        raise ValueError(f"Expected a JSON object: {path}")
    return raw, value


def _sha(raw):
    return hashlib.sha256(raw).hexdigest()


def _count(value, name, maximum):
    if isinstance(value, bool) or not isinstance(value, int) or not 0 <= value <= maximum:
        raise ValueError(f"{name} must be an integer in [0, {maximum}]")
    return value


def _finite(value, shape, label):
    try:
        array = np.asarray(value, dtype=np.float64)
    except (TypeError, ValueError) as error:
        raise ValueError(f"{label} must be finite numbers with shape {shape}") from error
    if array.shape != shape or not np.isfinite(array).all():
        raise ValueError(f"{label} must be finite numbers with shape {shape}")
    return array


def _transform(value, label):
    matrix = _finite(value, (16,), label).reshape(4, 4)
    if not np.allclose(matrix[3], [0, 0, 0, 1], rtol=0, atol=1e-7):
        raise ValueError(f"{label} must be an affine transform")
    try:
        inverse = np.linalg.inv(matrix)
    except np.linalg.LinAlgError as error:
        raise ValueError(f"{label} must be invertible") from error
    if not np.isfinite(inverse).all():
        raise ValueError(f"{label} inverse must be finite")
    return matrix


def _transform_pair(value, label):
    forward = _transform(value.get("local_to_world"), f"{label}.local_to_world")
    inverse = _transform(value.get("world_to_local"), f"{label}.world_to_local")
    # Unity serializes float matrices. Permit their roundoff, not incompatible
    # inverse transforms; membership itself has no expanded boundary tolerance.
    if (not np.allclose(forward @ inverse, np.eye(4), rtol=0, atol=2e-5)
            or not np.allclose(inverse @ forward, np.eye(4), rtol=0, atol=2e-5)):
        raise ValueError(f"{label} transforms must be mutual inverses")
    return forward, inverse


def _validate_scene(scene, source):
    if scene.get("schema_version") != 1:
        raise ValueError("Unsupported scene schema")
    if scene.get("source_hash") != source.sha256:
        raise ValueError("Scene/source hash mismatch")
    if scene.get("coordinate_profile") != "RUB_to_RUF_once":
        raise ValueError("Scene coordinate profile must be RUB_to_RUF_once")
    model = _transform(scene.get("model_local_to_world"), "model_local_to_world")
    floors = 0
    for group in ("walls", "surfaces"):
        values = scene.get(group, [])
        if not isinstance(values, list):
            raise ValueError(f"{group} must be an array")
        for index, plane in enumerate(values):
            label = f"{group}[{index}]"
            if not isinstance(plane, dict) or not isinstance(plane.get("floor"), bool):
                raise ValueError(f"{label} must have a boolean floor flag")
            _transform_pair(plane, label)
            _finite(plane.get("position"), (3,), f"{label}.position")
            normal = _finite(plane.get("normal"), (3,), f"{label}.normal")
            if not np.isclose(np.linalg.norm(normal), 1, rtol=0, atol=1e-5):
                raise ValueError(f"{label}.normal must be a unit vector")
            lower = _finite(plane.get("bounds_min"), (3,), f"{label}.bounds_min")
            upper = _finite(plane.get("bounds_max"), (3,), f"{label}.bounds_max")
            if np.any(lower > upper) or lower[0] == upper[0] or lower[2] == upper[2]:
                raise ValueError(f"{label} must have ordered nonempty planar bounds")
            if group == "surfaces" and plane["floor"]:
                raise ValueError(f"{label} must not be a floor")
            floors += int(group == "walls" and plane["floor"])
    if floors != 1:
        raise ValueError("Exactly one authored Floor plane is required")
    values = scene.get("deletion_boxes", [])
    if not isinstance(values, list):
        raise ValueError("deletion_boxes must be an array")
    boxes = []
    for index, box in enumerate(values):
        label = f"deletion_boxes[{index}]"
        if not isinstance(box, dict) or not isinstance(box.get("name"), str):
            raise ValueError(f"{label} must have a name")
        _, inverse = _transform_pair(box, label)
        center = _finite(box.get("center"), (3,), f"{label}.center")
        size = _finite(box.get("size"), (3,), f"{label}.size")
        if np.any(size < 0):
            raise ValueError(f"{label}.size must be nonnegative")
        boxes.append((box["name"], inverse, center - size / 2, center + size / 2))
    # Hashing also rejects non-finite values in otherwise unused snapshot fields.
    return model, boxes, scene_hash(scene)


def _artifact_path(directory, value, label):
    if not isinstance(value, str) or not value or Path(value).is_absolute():
        raise ValueError(f"Parent {label} must be a relative artifact path")
    path = (directory / value).resolve()
    if not path.is_relative_to(directory):
        raise ValueError(f"Parent {label} must stay within its result directory")
    return path


def _load_parent(path, source):
    raw, parent = _json_bytes(path)
    n = source.vertex_count
    if parent.get("schema_version") != 1:
        raise ValueError("Unsupported parent rank schema")
    if parent.get("source_hash") != source.sha256:
        raise ValueError("Parent/source hash mismatch")
    if _count(parent.get("source_count"), "Parent source_count", 2**32 - 1) != n:
        raise ValueError("Parent source count does not match inspected source")
    m = _count(parent.get("eligible_count"), "Parent eligible_count", n)
    if not isinstance(parent.get("rank_id"), str) or not parent["rank_id"].strip():
        raise ValueError("Parent rank_id is required")
    if parent.get("coordinate_profile") != "RUB_to_RUF_once":
        raise ValueError("Parent coordinate profile must be RUB_to_RUF_once")
    profile = parent.get("profile")
    if not isinstance(profile, dict) or profile.get("rear_depth") != .1:
        raise ValueError("Parent profile must use rear_depth 0.1")
    rank_path = _artifact_path(path.parent, parent.get("rank_path"), "rank_path")
    rank_bytes = rank_path.read_bytes()
    if len(rank_bytes) != m * 4:
        raise ValueError("Parent rank length does not match eligible_count")
    if _sha(rank_bytes) != parent.get("rank_sha256"):
        raise ValueError("Parent rank SHA-256 mismatch")
    order = np.frombuffer(rank_bytes, dtype="<u4")
    if m and int(order.max()) >= n:
        raise ValueError("Parent rank has an out-of-range source ID")
    membership = np.zeros(n, dtype=bool)
    membership[order] = True
    if np.count_nonzero(membership) != m:
        raise ValueError("Parent rank must contain unique source IDs")
    importance_path = _artifact_path(path.parent, parent.get("importance_path"), "importance_path")
    importance = importance_path.read_bytes()
    if len(importance) != n * 4 or not np.isfinite(np.frombuffer(importance, dtype="<f4")).all():
        raise ValueError("Parent importance must contain one finite float32 per original source ID")
    importance_hash = _sha(importance)
    if parent.get("importance_sha256", importance_hash) != importance_hash:
        raise ValueError("Parent importance SHA-256 mismatch")
    parent_scene_path = path.parent / "scene.json"
    parent_scene_raw, parent_scene = _json_bytes(parent_scene_path)
    if scene_hash(parent_scene) != parent.get("scene_hash"):
        raise ValueError("Parent scene hash mismatch")
    provenance = {"method": "filter_parent_rank_by_floor_and_box_center_union", "rescored": False,
                  "heatmap": "inherited_unchanged", "parent_manifest_path": str(path),
                  "parent_manifest_sha256": _sha(raw), "parent_rank_id": parent["rank_id"],
                  "parent_rank_sha256": parent["rank_sha256"], "parent_scene_hash": parent["scene_hash"],
                  "parent_importance_sha256": importance_hash, "parent_source_count": n,
                  "parent_eligible_count": m}
    identities = {path: _sha(raw), rank_path: parent["rank_sha256"], importance_path: importance_hash,
                  parent_scene_path: _sha(parent_scene_raw)}
    return parent, order, membership, importance, provenance, identities


def _write_ids(path, ids):
    with path.open("wb") as stream:
        np.asarray(ids, dtype="<u4").tofile(stream)
        stream.flush()
        os.fsync(stream.fileno())


def derive_customization(source_manifest_path, parent_manifest_path, scene_path, output_dir, *, chunk_size=65536):
    """Publish an inherited rank and audit files, then return its absolute Path.

    ``source_manifest_path`` accepts an inspected source manifest or its
    ``current_inspect.json`` pointer. Every supplied deletion box participates,
    including inactive guide objects. A center on a local box face is inside.
    ``output_dir`` must not exist; exceptions leave it unpublished. The input
    scene bytes and full source-addressed importance bytes are copied exactly.
    """
    if isinstance(chunk_size, bool) or not isinstance(chunk_size, int) or chunk_size <= 0:
        raise ValueError("chunk_size must be a positive integer")
    output = Path(output_dir).resolve()
    if output.exists():
        raise FileExistsError(f"Output directory already exists: {output}")
    source_path = Path(source_manifest_path).resolve()
    _, source_value = _json_bytes(source_path)
    if "result_dir" in source_value:
        source_path = (source_path.parent / source_value["result_dir"] / "source_manifest.json").resolve()
    source = SourceManifest.load(source_path)
    _count(source.vertex_count, "Source vertex_count", 2**32 - 1)
    scene_path = Path(scene_path).resolve()
    scene_bytes, scene = _json_bytes(scene_path)
    model, boxes, fingerprint = _validate_scene(scene, source)
    parent, order, parent_membership, importance, provenance, identities = _load_parent(
        Path(parent_manifest_path).resolve(), source)
    identities[source_path] = file_sha256(source_path)
    identities[scene_path] = _sha(scene_bytes)
    n = source.vertex_count
    floor_mask = np.zeros(n, dtype=bool)
    box_union = np.zeros(n, dtype=bool)
    box_reports = [{"index": i, "name": box[0], "inside_count": 0, "floor_eligible_inside_count": 0,
                    "excluded_ids_path": f"boxes/{i:03d}_excluded_ids.bin"} for i, box in enumerate(boxes)]
    # Match the scoring path's float32 world centers exactly. The source's RUB
    # reflection is composed once with the authored model transform.
    linear = model[:3, :3] @ np.diag([1., 1., -1.])
    output.parent.mkdir(parents=True, exist_ok=True)
    with read_source(source) as table, tempfile.TemporaryDirectory(prefix=f".{output.name}.", dir=output.parent) as temporary:
        stage = Path(temporary) / "result"
        stage.mkdir()
        (stage / "boxes").mkdir()
        with ExitStack() as stack:
            streams = [stack.enter_context((stage / item["excluded_ids_path"]).open("wb")) for item in box_reports]
            for start in range(0, n, chunk_size):
                stop = min(n, start + chunk_size)
                rows = table.records[start:stop]
                means = np.stack([rows[name] for name in ("x", "y", "z")], axis=1)
                world = (means @ linear.T + model[:3, 3]).astype(np.float32)
                if not np.isfinite(world).all():
                    raise ValueError("Source centers must remain finite under the model transform")
                eligible = floor_eligible(world, scene)
                floor_mask[start:stop] = eligible
                union = box_union[start:stop]
                for (_, inverse, lower, upper), report, stream in zip(boxes, box_reports, streams):
                    local = world @ inverse[:3, :3].T + inverse[:3, 3]
                    inside = np.all((local >= lower) & (local <= upper), axis=1)
                    union |= inside
                    report["inside_count"] += int(np.count_nonzero(inside))
                    report["floor_eligible_inside_count"] += int(np.count_nonzero(inside & eligible))
                    (np.flatnonzero(inside) + start).astype("<u4").tofile(stream)
            for stream in streams:
                stream.flush()
                os.fsync(stream.fileno())
        survivors = floor_mask & ~box_union
        missing = survivors & ~parent_membership
        if np.any(missing):
            raise ValueError(f"Parent score coverage is missing {np.count_nonzero(missing)} current eligible survivors")
        derived_order = order[survivors[order]]
        m = len(derived_order)
        _write_ids(stage / "rank.bin", derived_order)
        _write_ids(stage / "eligible_ids.bin", np.flatnonzero(survivors))
        _write_ids(stage / "floor_excluded_ids.bin", np.flatnonzero(~floor_mask))
        _write_ids(stage / "box_excluded_ids.bin", np.flatnonzero(box_union))
        _write_ids(stage / "excluded_ids.bin", np.flatnonzero(~survivors))
        (stage / "importance.bin").write_bytes(importance)
        (stage / "scene.json").write_bytes(scene_bytes)
        rank_hash = file_sha256(stage / "rank.bin")
        for item in box_reports:
            item["excluded_ids_sha256"] = file_sha256(stage / item["excluded_ids_path"])
        counts = {"source_count": n, "parent_eligible_count": len(order), "eligible_count": m,
                  "floor_excluded_count": int(np.count_nonzero(~floor_mask)),
                  "box_union_count": int(np.count_nonzero(box_union)),
                  "box_union_eligible_count": int(np.count_nonzero(box_union & floor_mask)),
                  "excluded_count": n - m, "parent_rank_removed_count": len(order) - m}
        counts["box_overlap_count"] = sum(box["inside_count"] for box in box_reports) - counts["box_union_count"]
        rank_id = f"customized-{source.sha256[:8]}-{fingerprint[:8]}-{rank_hash[:12]}"
        manifest = {"schema_version": 1, "rank_id": rank_id, "source_hash": source.sha256,
                    "scene_hash": fingerprint, **counts, "rank_path": "rank.bin", "rank_sha256": rank_hash,
                    "importance_path": "importance.bin", "importance_sha256": _sha(importance),
                    "profile": parent["profile"], "coordinate_profile": "RUB_to_RUF_once",
                    "calibration_status": scene.get("calibration_status", "user_authored_unity_units"),
                    "ordering": "inherited_parent_rank_relative_order", "inherited_heatmap": True,
                    "derivation": provenance}
        report = {"schema_version": 1, "rank_id": rank_id, "source_hash": source.sha256,
                  "scene_hash": fingerprint, **counts, "boxes": box_reports, "derivation": provenance,
                  "membership_rule": "floor_signed_distance>=0 and outside_union_of_inclusive_local_box_bounds",
                  "all_supplied_boxes_included": True, "percentage_denominator": "eligible_count",
                  "preview_counts": {str(p): keep_count(m, p * 100) for p in (0, 30, 100)},
                  "excluded_ids_path": "excluded_ids.bin", "floor_excluded_ids_path": "floor_excluded_ids.bin",
                  "box_union_ids_path": "box_excluded_ids.bin"}
        write_json_atomic(stage / "rank_manifest.json", manifest)
        write_json_atomic(stage / "report.json", report)
        if file_sha256(source.source_path) != source.sha256:
            raise ValueError("Source hash changed during customization")
        for path, expected in identities.items():
            if file_sha256(path) != expected:
                raise ValueError(f"Input changed during customization: {path}")
        if (file_sha256(stage / "importance.bin") != provenance["parent_importance_sha256"]
                or (stage / "scene.json").read_bytes() != scene_bytes):
            raise ValueError("Output byte preservation failed")
        if output.exists():
            raise FileExistsError(f"Output directory already exists: {output}")
        stage.rename(output)
    return output


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source-manifest", required=True, help="source_manifest.json or current_inspect.json")
    parser.add_argument("--parent-manifest", required=True, help="Frozen parent rank_manifest.json")
    parser.add_argument("--scene", required=True, help="Exact authored scene snapshot")
    parser.add_argument("--output", required=True, help="New result directory (must not exist)")
    args = parser.parse_args(argv)
    result = derive_customization(args.source_manifest, args.parent_manifest, args.scene, args.output)
    print(json.dumps({"result_dir": str(result), "rank_manifest": str(result / "rank_manifest.json")}))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
