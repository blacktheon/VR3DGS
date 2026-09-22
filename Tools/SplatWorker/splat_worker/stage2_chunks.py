"""Deterministic adaptive octree ownership for the immutable Stage 2 LOD0."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import tempfile

import numpy as np

from .contracts import SourceManifest, file_sha256, write_json_atomic
from .source import read_source
from .stage2_input import checked_file, read_json


def gaussian_axis_extents(quaternions, scales):
    """Four-sigma 3D AABB support; no assumption of isotropic orientation.

    The renderer's unblurred screen quad has a maximum four-sigma corner
    radius. Screen-space antialias padding is view dependent; these bounds
    are metadata, not enabled as an independent culling rule in this stage.
    """
    q = np.asarray(quaternions, dtype=np.float64)
    scales = np.asarray(scales, dtype=np.float64)
    if q.ndim != 2 or q.shape[1] != 4 or scales.shape != (len(q), 3):
        raise ValueError("Expected Nx4 wxyz rotations and Nx3 scales")
    norm = np.linalg.norm(q, axis=1)
    if not np.isfinite(q).all() or not np.isfinite(scales).all() or np.any(norm == 0) or np.any(scales <= 0):
        raise ValueError("Invalid Gaussian rotation or scale")
    w, x, y, z = (q / norm[:, None]).T
    rotation = np.stack((
        1-2*(y*y+z*z), 2*(x*y-w*z), 2*(x*z+w*y),
        2*(x*y+w*z), 1-2*(x*x+z*z), 2*(y*z-w*x),
        2*(x*z-w*y), 2*(y*z+w*x), 1-2*(x*x+y*y)
    ), axis=1).reshape(-1, 3, 3)
    return 4 * np.sqrt(np.sum(rotation**2 * scales[:, None, :]**2, axis=2))


def build_chunks(input_manifest, output_dir, *, target_count=32768, minimum_cell_size=.25, maximum_depth=10):
    if type(target_count) is not int or target_count <= 0:
        raise ValueError("Target count must be a positive integer")
    if type(maximum_depth) is not int or not 0 <= maximum_depth <= 20:
        raise ValueError("Maximum depth must be an integer from 0 to 20")
    if isinstance(minimum_cell_size, bool) or not np.isfinite(minimum_cell_size) or minimum_cell_size <= 0:
        raise ValueError("Minimum cell size must be finite and positive")
    output = Path(output_dir).resolve()
    if output.exists():
        raise FileExistsError(f"Chunk layout versions are immutable: {output}")
    input_path = Path(input_manifest).resolve()
    raw_input = input_path.read_bytes()
    accepted = read_json(input_path)
    if accepted.get("schema_version") != 1 or accepted.get("coordinate_profile") != "RUB_to_RUF_once":
        raise ValueError("Unsupported input contract")
    count = accepted.get("accepted_count")
    if type(count) is not int or count <= 0:
        raise ValueError("Expected a nonempty accepted input")
    ply = checked_file(input_path.parent, accepted["ply_path"], accepted["ply_sha256"])
    ids_path = checked_file(input_path.parent, accepted["source_ids_path"], accepted["source_ids_sha256"])
    source_ids = np.fromfile(ids_path, "<u4")
    if len(source_ids) != count or len(np.unique(source_ids)) != count or np.any(source_ids >= accepted["original_source_count"]):
        raise ValueError("Original source-ID ownership is invalid")
    source = SourceManifest.load(input_path.parent / accepted["source_manifest_path"])
    if source.sha256 != accepted["ply_sha256"] or source.vertex_count != count:
        raise ValueError("Export inspection differs from the accepted input")
    # A relocated portable package uses its local PLY, not the old absolute source path.
    from dataclasses import replace
    source = replace(source, source_path=str(ply))
    positions = np.empty((count, 3), dtype=np.float64)
    extents = np.empty_like(positions)
    with read_source(source) as table:
        for start in range(0, count, 65536):
            stop = min(count, start + 65536)
            decoded = table.decoded_slice(start, stop)
            positions[start:stop] = decoded["means"] * [1, 1, -1]
            extents[start:stop] = gaussian_axis_extents(decoded["quats"], decoded["scales"])
    model = np.asarray(accepted["model_local_to_world"], dtype=np.float64)
    if model.shape != (16,) or not np.isfinite(model).all() or abs(np.linalg.det(model.reshape(4,4))) < 1e-12:
        raise ValueError("Invalid accepted model transform")
    axis_scale = np.linalg.norm(model.reshape(4, 4)[:3, :3], axis=0)
    minimum_local = minimum_cell_size / axis_scale.min()
    lo, hi = positions.min(axis=0), positions.max(axis=0)
    side = max(float(np.max(hi-lo)), minimum_local)
    root_min = (lo+hi)/2 - side/2
    settings = {"target_count": target_count, "minimum_cell_size": minimum_cell_size, "maximum_depth": maximum_depth}
    identity = hashlib.sha256(raw_input + json.dumps(settings, sort_keys=True).encode()).hexdigest()
    ownership = np.empty(count, dtype="<u4")
    leaves, membership = [], []
    offset = 0

    def visit(rows, low, size, depth, path):
        nonlocal offset
        identical = np.all(positions[rows] == positions[rows[0]])
        if len(rows) <= target_count or depth >= maximum_depth or size / 2 < minimum_local or identical:
            index = len(leaves)
            ownership[rows] = index
            leaves.append({"id": path, "group": "machine", "count": int(len(rows)), "offset": offset,
                           "depth": depth, "partition_min": low.tolist(), "partition_max": (low+size).tolist(),
                           "render_min": (positions[rows]-extents[rows]).min(axis=0).tolist(),
                           "render_max": (positions[rows]+extents[rows]).max(axis=0).tolist(),
                           "available_lods": [0], "lod0_storage": "shared_accepted_asset_member_rows"})
            membership.append(rows)
            offset += len(rows)
            return
        midpoint = low + size/2
        octants = np.sum((positions[rows] >= midpoint) * [1,2,4], axis=1)
        for child in range(8):
            selected = rows[octants == child]
            if not len(selected):
                continue
            child_min = low + np.array([child & 1, (child >> 1) & 1, (child >> 2) & 1]) * size/2
            visit(selected, child_min, size/2, depth+1, path+str(child))

    visit(np.arange(count, dtype="<u4"), root_min, side, 0, "r")
    members = np.concatenate(membership)
    if len(members) != count or not np.array_equal(np.sort(members), np.arange(count)):
        raise ValueError("Partition has missing or duplicated rows")
    output.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix=output.name+".building-", dir=output.parent) as temporary:
        stage = Path(temporary)/"layout"
        stage.mkdir()
        ownership.tofile(stage/"ownership.bin")
        members.tofile(stage/"members.bin")
        manifest = {
            "schema_version": 1, "layout_id": "octree-"+identity[:16], "input_id": accepted["input_id"],
            "input_manifest": str(input_path), "input_manifest_sha256": hashlib.sha256(raw_input).hexdigest(),
            "input_ply_sha256": accepted["ply_sha256"], "count": count,
            "coordinate_space": "renderer_local_RUF", "model_local_to_world": accepted["model_local_to_world"],
            "calibration_status": accepted["calibration_status"], "settings": settings,
            "boundary_rule": "center >= midpoint uses upper child; bits X=1 Y=2 Z=4",
            "render_bounds_rule": "4 sigma full covariance; screen AA padding not used for independent culling",
            "root_min": root_min.tolist(), "root_max": (root_min+side).tolist(),
            "ownership_path": "ownership.bin", "ownership_sha256": file_sha256(stage/"ownership.bin"),
            "members_path": "members.bin", "members_sha256": file_sha256(stage/"members.bin"),
            "chunks": leaves, "available_lods": [0], "importance_guided_splits": False,
            "status": "LOD0 ownership ready; lower levels require a validated generation backend",
        }
        write_json_atomic(stage/"chunks.json", manifest)
        if input_path.read_bytes() != raw_input:
            raise ValueError("Accepted input changed during partitioning")
        os.rename(stage, output)
    return output/"chunks.json"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--input", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--target-count", type=int, default=32768)
    parser.add_argument("--minimum-cell-size", type=float, default=.25)
    parser.add_argument("--maximum-depth", type=int, default=10)
    args = parser.parse_args()
    print(build_chunks(args.input, args.output, target_count=args.target_count,
                       minimum_cell_size=args.minimum_cell_size, maximum_depth=args.maximum_depth))


if __name__ == "__main__":
    main()
