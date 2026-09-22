"""CPU-only checks for immutable heatmaps and source-addressed box deletion."""

import copy
import json
from pathlib import Path
import subprocess
import sys

import numpy as np
import pytest

from splat_worker.contracts import file_sha256
from splat_worker.ranking import keep_count
from splat_worker.scene_geometry import scene_hash
from splat_worker.source import inspect_source
from tests.fixtures import FIELDS, write_ply


def _matrix(value=None):
    return np.asarray(np.eye(4) if value is None else value, dtype=float).reshape(-1).tolist()


def _box(name, center, size, transform=None, **extras):
    transform = np.eye(4) if transform is None else np.asarray(transform, dtype=float)
    return {"name": name, "local_to_world": _matrix(transform),
            "world_to_local": _matrix(np.linalg.inv(transform)), "center": center,
            "size": size, **extras}


def _scene(source_hash):
    return {"schema_version": 1, "source_hash": source_hash,
            "coordinate_profile": "RUB_to_RUF_once",
            "calibration_status": "user_authored_unity_units",
            "model_local_to_world": _matrix(), "nav_vertices": [], "nav_indices": [],
            "walls": [{"name": "Floor", "floor": True, "position": [0, 0, 0],
                       "normal": [0, 1, 0], "local_to_world": _matrix(),
                       "world_to_local": _matrix(), "bounds_min": [-10, 0, -10],
                       "bounds_max": [10, 0, 10]}],
            "deletion_boxes": [], "surfaces": []}


def _write_json(path, value):
    path.write_text(json.dumps(value, indent=2), encoding="utf-8")


def _inputs(tmp_path, positions=None, order=None):
    positions = positions if positions is not None else [
        [0, 1, 0], [1, 1, 0], [2, 1, 0], [3, 1, 0], [4, 1, 0], [0, -1, 0],
        [5, 0, 0], [0, 1, 4], [6, 1, 0], [7, 1, 0], [8, 1, 0], [9, 1, 0]]
    records = []
    for position in positions:
        row = dict.fromkeys(FIELDS, 0.)
        row.update(zip(("x", "y", "z"), position))
        row["rot_0"] = 1.
        records.append(row)
    source_path = write_ply(tmp_path / "source.ply", records)[0]
    inspected = tmp_path / "inspected"
    source = inspect_source(source_path, inspected)
    pointer = tmp_path / "current_inspect.json"
    _write_json(pointer, {"schema_version": 1, "result_dir": "inspected"})
    scene = _scene(source.sha256)
    parent = tmp_path / "parent"
    parent.mkdir()
    order = [10, 2, 9, 0, 8, 7, 1, 11, 6, 3, 4] if order is None else order
    np.asarray(order, dtype="<u4").tofile(parent / "rank.bin")
    # f64 scores can differ while their f32 heatmap values tie. Re-sorting these
    # f32 values would destroy the supplied frozen order.
    np.full(len(positions), .5, dtype="<f4").tofile(parent / "importance.bin")
    _write_json(parent / "scene.json", scene)
    manifest = {"schema_version": 1, "rank_id": "parent-frozen-rank",
                "source_hash": source.sha256, "source_count": len(positions),
                "eligible_count": len(order), "floor_excluded_count": len(positions) - len(order),
                "rank_path": "rank.bin", "rank_sha256": file_sha256(parent / "rank.bin"),
                "importance_path": "importance.bin", "scene_hash": scene_hash(scene),
                "profile": {"rear_depth": .1, "floor_rule": "center_signed_distance>=0"},
                "coordinate_profile": "RUB_to_RUF_once"}
    _write_json(parent / "rank_manifest.json", manifest)
    scene["deletion_boxes"] = [
        _box("A", [0, 1, 0], [2, 2, 2]),
        _box("B", [1.5, 1, 0], [1, 2, 2]),
        _box("Inactive guide", [3, 1, 0], [1, 1, 1], active=False)]
    scene_path = tmp_path / "scene.json"
    _write_json(scene_path, scene)
    return pointer, parent / "rank_manifest.json", scene_path, tmp_path / "derived"


def _derive(paths, **kwargs):
    from splat_worker.customization import derive_customization
    return derive_customization(*paths, **kwargs)


def _read_json(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def _ids(path):
    return np.fromfile(path, dtype="<u4").tolist()


def test_filters_overlapping_and_inactive_boxes_without_rescoring_or_renumbering(tmp_path):
    paths = _inputs(tmp_path)
    result = _derive(paths, chunk_size=2)
    assert Path(result) == paths[3]
    assert _ids(result / "rank.bin") == [10, 9, 8, 7, 11, 6, 4]
    assert (result / "importance.bin").read_bytes() == (paths[1].parent / "importance.bin").read_bytes()
    assert (result / "scene.json").read_bytes() == paths[2].read_bytes()
    assert _ids(result / "box_excluded_ids.bin") == [0, 1, 2, 3]
    assert _ids(result / "floor_excluded_ids.bin") == [5]
    assert _ids(result / "excluded_ids.bin") == [0, 1, 2, 3, 5]
    report = _read_json(result / "report.json")
    assert report["source_count"] == 12
    assert report["parent_eligible_count"] == 11
    assert report["eligible_count"] == 7
    assert report["floor_excluded_count"] == 1
    assert report["box_union_count"] == 4
    assert report["box_union_eligible_count"] == 4
    assert report["box_overlap_count"] == 1
    assert [box["inside_count"] for box in report["boxes"]] == [2, 2, 1]
    assert [_ids(result / box["excluded_ids_path"]) for box in report["boxes"]] == [[0, 1], [1, 2], [3]]
    rank = np.fromfile(result / "rank.bin", dtype="<u4")
    for percent in range(10001):
        assert not set(rank[:keep_count(7, percent)]) & {0, 1, 2, 3, 5}
    assert rank[:keep_count(7, 3000)].tolist() == [10, 9]
    manifest = _read_json(result / "rank_manifest.json")
    assert manifest["schema_version"] == 1
    assert manifest["source_count"] == 12 and manifest["eligible_count"] == 7
    assert manifest["scene_hash"] == scene_hash(_read_json(paths[2]))
    assert manifest["rank_sha256"] == file_sha256(result / "rank.bin")
    assert manifest["importance_sha256"] == file_sha256(paths[1].parent / "importance.bin")
    assert manifest["profile"]["rear_depth"] == .1
    assert manifest["inherited_heatmap"] is True
    provenance = manifest["derivation"]
    assert provenance["rescored"] is False
    assert provenance["parent_rank_id"] == "parent-frozen-rank"
    assert provenance["parent_manifest_sha256"] == file_sha256(paths[1])
    assert provenance["parent_rank_sha256"] == _read_json(paths[1])["rank_sha256"]


def test_oriented_nonuniform_box_uses_local_bounds_after_one_source_reflection(tmp_path):
    # Model maps source (x,y,z) to world (8-4z, 1+y, -3-2x).
    positions = [[1, 1, 2], [1, 1, 1.5], [1, 1, 1.499], [1, 2.001, 2], [1, 1, -2]]
    paths = _inputs(tmp_path, positions=positions, order=[4, 3, 2, 1, 0])
    scene = _read_json(paths[2])
    scene["model_local_to_world"] = _matrix([[0, 0, 4, 8], [0, 1, 0, 1], [-2, 0, 0, -3], [0, 0, 0, 1]])
    # Box local X maps to world +Z, local Z to world -X, with nonuniform scale.
    transform = [[0, 0, -2, 0], [0, 1, 0, 1], [3, 0, 0, -5], [0, 0, 0, 1]]
    scene["deletion_boxes"] = [_box("rotated", [0, 1, 0], [2, 2, 2], transform)]
    _write_json(paths[2], scene)
    result = _derive(paths, chunk_size=2)
    assert _ids(result / "box_excluded_ids.bin") == [0, 1]
    assert _ids(result / "rank.bin") == [4, 3, 2]


def test_floor_and_box_overlap_remains_one_exclusion(tmp_path):
    paths = _inputs(tmp_path)
    scene = _read_json(paths[2])
    scene["deletion_boxes"] = [_box("crosses floor", [0, 0, 0], [1, 2, 1])]
    _write_json(paths[2], scene)
    result = _derive(paths)
    report = _read_json(result / "report.json")
    assert _ids(result / "excluded_ids.bin") == [0, 5]
    assert report["box_union_count"] == 2
    assert report["box_union_eligible_count"] == 1
    assert report["excluded_count"] == 2
    assert report["eligible_count"] == 10


def test_fails_when_new_floor_survivor_has_no_parent_score_coverage(tmp_path):
    paths = _inputs(tmp_path)
    scene = _read_json(paths[2])
    scene["walls"][0]["position"] = [0, -2, 0]
    _write_json(paths[2], scene)
    with pytest.raises(ValueError, match="[Pp]arent.*coverage|coverage.*parent"):
        _derive(paths)
    assert not paths[3].exists()


@pytest.mark.parametrize("corruption", ["hash", "duplicate", "range", "length", "count", "importance", "importance_nan"])
def test_corrupt_parent_is_rejected_before_publication(tmp_path, corruption):
    paths = _inputs(tmp_path)
    parent = _read_json(paths[1])
    rank_path = paths[1].parent / "rank.bin"
    if corruption in {"hash", "duplicate", "range"}:
        rank = np.fromfile(rank_path, dtype="<u4")
        rank[0] = rank[1] if corruption != "range" else 12
        rank.tofile(rank_path)
        if corruption != "hash":
            parent["rank_sha256"] = file_sha256(rank_path)
    elif corruption == "length":
        rank_path.write_bytes(rank_path.read_bytes()[:-4])
        parent["rank_sha256"] = file_sha256(rank_path)
    elif corruption == "count":
        parent["source_count"] = 13
    elif corruption == "importance":
        (paths[1].parent / "importance.bin").write_bytes(b"truncated")
    else:
        importance = np.full(12, .5, dtype="<f4")
        importance[0] = np.nan
        importance.tofile(paths[1].parent / "importance.bin")
    _write_json(paths[1], parent)
    with pytest.raises(ValueError):
        _derive(paths)
    assert not paths[3].exists()
    assert not list(tmp_path.glob(".derived.*"))


@pytest.mark.parametrize("target", ["parent", "scene", "source"])
def test_source_identity_mismatch_is_rejected(tmp_path, target):
    paths = _inputs(tmp_path)
    if target == "source":
        source_path = tmp_path / "source.ply"
        raw = bytearray(source_path.read_bytes())
        raw[-1] ^= 1
        source_path.write_bytes(raw)
    else:
        path = paths[1] if target == "parent" else paths[2]
        value = _read_json(path)
        value["source_hash"] = "0" * 64
        _write_json(path, value)
    with pytest.raises(ValueError, match="[Ss]ource|source"):
        _derive(paths)
    assert not paths[3].exists()


@pytest.mark.parametrize("invalid", ["singular_box", "inverse", "nonfinite_box", "negative_size", "singular_model", "floor_normal", "no_floor", "surface"])
def test_invalid_authored_geometry_is_rejected(tmp_path, invalid):
    paths = _inputs(tmp_path)
    scene = _read_json(paths[2])
    if invalid == "singular_box":
        scene["deletion_boxes"][0]["local_to_world"][0] = 0
    elif invalid == "inverse":
        scene["deletion_boxes"][0]["world_to_local"][3] = 1
    elif invalid == "nonfinite_box":
        scene["deletion_boxes"][0]["center"][0] = float("inf")
    elif invalid == "negative_size":
        scene["deletion_boxes"][0]["size"][0] = -1
    elif invalid == "singular_model":
        scene["model_local_to_world"][0] = 0
    elif invalid == "floor_normal":
        scene["walls"][0]["normal"] = [0, 0, 0]
    elif invalid == "no_floor":
        scene["walls"] = []
    else:
        scene["surfaces"] = [copy.deepcopy(scene["walls"][0])]
        scene["surfaces"][0]["floor"] = False
        scene["surfaces"][0]["world_to_local"][0] = float("nan")
    _write_json(paths[2], scene)
    with pytest.raises(ValueError):
        _derive(paths)
    assert not paths[3].exists()


def test_empty_result_and_no_boxes_keep_valid_source_addressing(tmp_path):
    paths = _inputs(tmp_path, positions=[[0, 1, 0]], order=[0])
    result = _derive(paths)
    assert _ids(result / "rank.bin") == []
    assert _read_json(result / "rank_manifest.json")["eligible_count"] == 0
    scene = _read_json(paths[2])
    scene["deletion_boxes"] = []
    _write_json(paths[2], scene)
    result = _derive((*paths[:3], tmp_path / "no_boxes"))
    assert _ids(result / "rank.bin") == [0]


def test_outputs_are_deterministic_across_chunk_sizes_and_copy_scene_bytes(tmp_path):
    paths = _inputs(tmp_path)
    scene_bytes = b"\xef\xbb\xbf" + paths[2].read_bytes() + b"\r\n  "
    paths[2].write_bytes(scene_bytes)
    first = _derive(paths, chunk_size=1)
    second = _derive((*paths[:3], tmp_path / "other"), chunk_size=100)
    first_files = {p.relative_to(first): p.read_bytes() for p in first.rglob("*") if p.is_file()}
    second_files = {p.relative_to(second): p.read_bytes() for p in second.rglob("*") if p.is_file()}
    assert first_files == second_files
    assert (first / "scene.json").read_bytes() == scene_bytes


def test_late_write_failure_cleans_staging_and_never_publishes(tmp_path, monkeypatch):
    paths = _inputs(tmp_path)
    from splat_worker import customization
    original = customization.write_json_atomic

    def fail_report(path, value):
        if Path(path).name == "report.json":
            raise OSError("simulated full disk")
        original(path, value)

    monkeypatch.setattr(customization, "write_json_atomic", fail_report)
    with pytest.raises(OSError, match="full disk"):
        _derive(paths)
    assert not paths[3].exists()
    assert not list(tmp_path.glob(".derived.*"))
    assert _ids(paths[1].parent / "rank.bin") == [10, 2, 9, 0, 8, 7, 1, 11, 6, 3, 4]


def test_existing_output_is_never_overwritten(tmp_path):
    paths = _inputs(tmp_path)
    paths[3].mkdir()
    sentinel = paths[3] / "previous.bin"
    sentinel.write_bytes(b"previous valid result")
    with pytest.raises(FileExistsError):
        _derive(paths)
    assert sentinel.read_bytes() == b"previous valid result"
    assert list(paths[3].iterdir()) == [sentinel]


def test_cli_accepts_source_manifest_directly_without_a_gpu_job(tmp_path):
    paths = _inputs(tmp_path)
    completed = subprocess.run([
        sys.executable, "-m", "splat_worker.customization",
        "--source-manifest", str(tmp_path / "inspected/source_manifest.json"),
        "--parent-manifest", str(paths[1]), "--scene", str(paths[2]),
        "--output", str(paths[3])], capture_output=True, text=True, timeout=20)
    assert completed.returncode == 0, completed.stderr
    assert _ids(paths[3] / "rank.bin") == [10, 9, 8, 7, 11, 6, 4]
    assert _read_json(paths[3] / "rank_manifest.json")["source_count"] == 12
