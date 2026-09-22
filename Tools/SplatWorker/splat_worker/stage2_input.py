"""Freeze an accepted Stage 1 prefix as immutable, renderer-compatible LOD0."""

from __future__ import annotations

import argparse
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import shutil
import tempfile

import numpy as np

from .contracts import SourceManifest, file_sha256, write_json_atomic
from .scene_geometry import scene_hash
from .source import inspect_source, read_source


def read_json(path):
    value = json.loads(Path(path).read_text(encoding="utf-8-sig"))
    if not isinstance(value, dict):
        raise ValueError(f"Expected an object: {path}")
    return value


def checked_file(root, relative, expected_hash):
    root = Path(root).resolve()
    path = (root / relative).resolve()
    if not path.is_relative_to(root) or not path.is_file():
        raise ValueError(f"Missing or external artifact: {relative}")
    if not isinstance(expected_hash, str) or len(expected_hash) != 64 or file_sha256(path) != expected_hash:
        raise ValueError(f"Artifact hash mismatch: {relative}")
    return path


def export_accepted(source_manifest, rank_manifest, keep_count, output_dir, *, presentation_path=None):
    output = Path(output_dir).resolve()
    if output.exists():
        raise FileExistsError(f"Accepted versions are immutable: {output}")
    source_manifest = Path(source_manifest).resolve()
    pointer = read_json(source_manifest)
    if "result_dir" in pointer:
        source_manifest = (source_manifest.parent / pointer["result_dir"] / "source_manifest.json").resolve()
    source = SourceManifest.load(source_manifest)
    rank_path = Path(rank_manifest).resolve()
    rank = read_json(rank_path)
    if rank.get("schema_version") != 1 or rank.get("source_hash") != source.sha256 or rank.get("source_count") != source.vertex_count:
        raise ValueError("Rank does not describe the inspected original source")
    eligible = rank.get("eligible_count")
    if type(eligible) is not int or not 0 < eligible <= source.vertex_count:
        raise ValueError("Invalid eligible source count")
    if type(keep_count) is not int or not 0 < keep_count <= eligible:
        raise ValueError("Accepted count must be an integer from 1 to the eligible count")
    scene_path = rank_path.parent / "scene.json"
    scene = read_json(scene_path)
    if scene.get("source_hash") != source.sha256 or scene_hash(scene) != rank.get("scene_hash"):
        raise ValueError("Rank scene identity mismatch")
    if scene.get("coordinate_profile") != "RUB_to_RUF_once":
        raise ValueError("This export currently requires the verified RUB-to-RUF convention")
    transform = np.asarray(scene.get("model_local_to_world"), dtype=float)
    if transform.shape != (16,) or not np.isfinite(transform).all() or abs(np.linalg.det(transform.reshape(4, 4))) < 1e-12:
        raise ValueError("Invalid source transform")
    ranked = checked_file(rank_path.parent, rank["rank_path"], rank["rank_sha256"])
    if ranked.stat().st_size != eligible * 4:
        raise ValueError("Rank length mismatch")
    order = np.fromfile(ranked, dtype="<u4")
    if np.any(order >= source.vertex_count) or len(np.unique(order)) != eligible:
        raise ValueError("Rank contains duplicate or out-of-range source IDs")
    selected = order[:keep_count]
    importance = None
    importance_hash = None
    if rank.get("importance_path"):
        importance_path = (rank_path.parent / rank["importance_path"]).resolve()
        if not importance_path.is_relative_to(rank_path.parent) or importance_path.stat().st_size != source.vertex_count * 4:
            raise ValueError("Importance length or path mismatch")
        importance_hash = file_sha256(importance_path)
        if rank.get("importance_sha256") and rank["importance_sha256"] != importance_hash:
            raise ValueError("Importance hash mismatch")
        importance = np.fromfile(importance_path, dtype="<f4")
        if not np.isfinite(importance).all():
            raise ValueError("Importance contains nonfinite values")
    presentation = read_json(presentation_path) if presentation_path else {"schema_version": 1, "surfaces": []}
    if presentation.get("schema_version") != 1:
        raise ValueError("Unsupported presentation schema")
    surfaces = presentation.get("surfaces", [])
    if len({s["name"] for s in surfaces}) != len(surfaces):
        raise ValueError("Duplicate surface names")
    expected_surfaces = {s["name"] for s in scene.get("surfaces", [])}
    if expected_surfaces and expected_surfaces != {s["name"] for s in surfaces}:
        raise ValueError("The accepted package must include every visible scene surface")
    output.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix=output.name + ".building-", dir=output.parent) as temporary:
        stage = Path(temporary) / "package"
        stage.mkdir()
        with read_source(source) as table:
            header = ("ply\nformat binary_little_endian 1.0\n"
                      f"comment Accepted Stage 1 rows; original source SHA256 {source.sha256}\n"
                      f"element vertex {keep_count}\n" +
                      "".join(f"property float {p.name}\n" for p in source.properties) + "end_header\n")
            with (stage / "accepted.ply").open("wb") as stream:
                stream.write(header.encode("ascii"))
                for start in range(0, keep_count, 65536):
                    stream.write(table.records[selected[start:start + 65536]].tobytes())
        selected.tofile(stage / "source_ids.bin")
        mask = np.zeros(source.vertex_count, dtype=np.uint8)
        mask[selected] = 1
        mask.tofile(stage / "keep_mask.bin")
        if importance is not None:
            importance[selected].tofile(stage / "importance.bin")
        inspected = inspect_source(stage / "accepted.ply", stage / "inspection")
        exported_source = inspected.to_dict()
        exported_source["source_path"] = str(output / "accepted.ply")
        write_json_atomic(stage / "source_manifest.json", exported_source)
        # The inspection scratch contains the temporary path; publish only the relocated manifest.
        shutil.rmtree(stage / "inspection")
        shutil.copy2(scene_path, stage / "stage1_scene.json")
        shutil.copy2(rank_path, stage / "stage1_rank_manifest.json")
        write_json_atomic(stage / "presentation.json", presentation)
        saved_surfaces = []
        for index, surface in enumerate(surfaces):
            texture = Path(surface["texture_path"])
            if file_sha256(texture) != surface["texture_sha256"]:
                raise ValueError("Surface texture changed: " + surface["name"])
            relative = f"surfaces/{index:02d}.png"
            (stage / "surfaces").mkdir(exist_ok=True)
            shutil.copy2(texture, stage / relative)
            if file_sha256(stage / relative) != surface["texture_sha256"]:
                raise ValueError("Surface texture copy mismatch")
            saved_surfaces.append({**surface, "texture": relative})
        if file_sha256(source.source_path) != source.sha256 or file_sha256(ranked) != rank["rank_sha256"]:
            raise ValueError("Source or rank changed during export")
        ply_hash = file_sha256(stage / "accepted.ply")
        source_ids_hash = file_sha256(stage / "source_ids.bin")
        selected_importance_hash = file_sha256(stage / "importance.bin") if importance is not None else ""
        identity = {"ply": ply_hash, "transform": transform.tolist(),
                    "scene": file_sha256(stage / "stage1_scene.json"), "surfaces": saved_surfaces,
                    "source_ids": source_ids_hash, "importance": selected_importance_hash,
                    "original_source": source.sha256, "original_importance": importance_hash,
                    "rank": file_sha256(stage / "stage1_rank_manifest.json")}
        input_hash = hashlib.sha256(json.dumps(identity, sort_keys=True).encode()).hexdigest()
        manifest = {
            "schema_version": 1, "input_id": f"accepted-{input_hash[:16]}",
            "created_utc": datetime.now(timezone.utc).isoformat(),
            "accepted_count": keep_count, "original_source_count": source.vertex_count,
            "original_source_sha256": source.sha256, "rank_id": rank["rank_id"],
            "rank_sha256": rank["rank_sha256"], "rank_eligible_count": eligible,
            "rank_keep_percent": keep_count * 100 / eligible,
            "previous_eligible_count": rank.get("parent_eligible_count", eligible),
            "previous_model_keep_percent": keep_count * 100 / rank.get("parent_eligible_count", eligible),
            "ply_path": "accepted.ply", "ply_sha256": ply_hash,
            "source_ids_path": "source_ids.bin", "source_ids_sha256": source_ids_hash,
            "keep_mask_path": "keep_mask.bin", "keep_mask_sha256": file_sha256(stage / "keep_mask.bin"),
            "source_ids_encoding": "little_endian_uint32_original_vertex_rows_in_inherited_rank_order",
            "keep_mask_encoding": "uint8_per_original_source_row",
            "importance_available": importance is not None,
            "importance_path": "importance.bin" if importance is not None else "",
            "importance_sha256": selected_importance_hash,
            "original_importance_sha256": importance_hash,
            "source_manifest_path": "source_manifest.json", "scene_path": "stage1_scene.json",
            "scene_sha256": file_sha256(stage / "stage1_scene.json"),
            "coordinate_profile": "RUB_to_RUF_once", "model_local_to_world": transform.tolist(),
            "calibration_status": scene.get("calibration_status", "uncalibrated"),
            "quaternion_order": source.quaternion_order, "scale_encoding": source.scale_encoding,
            "opacity_encoding": source.opacity_encoding, "sh_degree": source.sh_degree,
            "available_lods": [0], "attributes": "original record bytes, unchanged",
            "surfaces": saved_surfaces, "evidence": presentation.get("evidence", []),
        }
        write_json_atomic(stage / "stage2_input.json", manifest)
        os.rename(stage, output)
    return output / "stage2_input.json"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source-manifest", required=True)
    parser.add_argument("--rank-manifest", required=True)
    parser.add_argument("--keep-count", required=True, type=int)
    parser.add_argument("--output", required=True)
    parser.add_argument("--presentation")
    args = parser.parse_args()
    print(export_accepted(args.source_manifest, args.rank_manifest, args.keep_count, args.output,
                          presentation_path=args.presentation))


if __name__ == "__main__":
    main()
