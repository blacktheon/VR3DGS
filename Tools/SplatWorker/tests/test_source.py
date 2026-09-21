import hashlib
import json
import struct

import numpy as np
import pytest

from splat_worker.source import SourceValidationError, inspect_source, read_source
from tests.fixtures import FIELDS, rows, write_ply


def test_named_properties_preserve_identity_and_original_bytes(tmp_path):
    path, header, payload = write_ply(tmp_path / "named.ply", fields=list(reversed(FIELDS)))
    original = path.read_bytes()
    manifest = inspect_source(path, tmp_path / "result", chunk_size=2)
    assert path.read_bytes() == original
    assert manifest.vertex_count == 4
    assert manifest.id_rule == "zero_based_vertex_row"
    assert manifest.sha256 == hashlib.sha256(original).hexdigest()
    assert manifest.data_offset == len(header)
    assert manifest.record_bytes == 56
    assert manifest.sh_degree == 0
    assert manifest.calibration_status == "uncalibrated"
    table = read_source(manifest)
    assert table.raw_record(1) == payload[56:112]
    assert table.raw_record(0) != table.raw_record(1)
    assert not table.records.flags.writeable
    decoded = table.decoded_slice(0, 2)
    np.testing.assert_array_equal(decoded["means"], [[1, 2, 3], [1, 2, 3]])
    np.testing.assert_array_equal(decoded["quats"], [[1, 0, 0, 0], [1, 0, 0, 0]])
    np.testing.assert_array_equal(decoded["scales"], np.ones((2, 3)))
    np.testing.assert_array_equal(decoded["opacities"], [0.5, 0.5])
    np.testing.assert_array_equal(decoded["shs"][:, 0, 0], [0, 1])
    saved = json.loads((tmp_path / "result/source_manifest.json").read_text())
    assert saved["vertex_count"] == 4
    assert saved["statistics"]["invalid_records"] == 0


@pytest.mark.parametrize("kind", ["truncated", "trailing", "nan_last", "zero_quaternion", "scale_overflow"])
def test_invalid_records_or_layout_never_silently_change_denominator(tmp_path, kind):
    data = rows()
    if kind == "nan_last":
        data[-1]["f_dc_2"] = float("nan")
    if kind == "zero_quaternion":
        data[2]["rot_0"] = 0
    if kind == "scale_overflow":
        data[3]["scale_2"] = 1000
    path, _, _ = write_ply(tmp_path / "invalid.ply", records=data)
    if kind == "truncated":
        path.write_bytes(path.read_bytes()[:-1])
    if kind == "trailing":
        path.write_bytes(path.read_bytes() + b"x")
    with pytest.raises(SourceValidationError):
        inspect_source(path, tmp_path / "result", chunk_size=2)
    assert not (tmp_path / "result/source_manifest.json").exists()


@pytest.mark.parametrize("change", [b"format ascii 1.0", b"format binary_big_endian 1.0"])
def test_unsupported_encoding_is_explicit(tmp_path, change):
    path, _, _ = write_ply(tmp_path / "wrong.ply")
    path.write_bytes(path.read_bytes().replace(b"format binary_little_endian 1.0", change))
    with pytest.raises(SourceValidationError):
        inspect_source(path, tmp_path / "result")


def test_duplicate_or_missing_required_properties_are_rejected(tmp_path):
    for fields in (FIELDS[:-1], [*FIELDS[:-1], "x"]):
        path, _, _ = write_ply(tmp_path / "wrong.ply", fields=fields)
        with pytest.raises(SourceValidationError):
            inspect_source(path, tmp_path / "result")


def test_reading_rejects_source_modified_after_inspection(tmp_path):
    path, _, _ = write_ply(tmp_path / "source.ply")
    manifest = inspect_source(path, tmp_path / "result")
    changed = bytearray(path.read_bytes())
    changed[-4:] = struct.pack("<f", 0.125)
    path.write_bytes(changed)
    with pytest.raises(SourceValidationError, match="hash"):
        read_source(manifest)


def test_sigmoid_is_stable_for_extreme_logits_and_sh_rest_is_channel_major(tmp_path):
    data = rows()
    fields = FIELDS + [f"f_rest_{i}" for i in range(9)]
    for row in data:
        row.update({f"f_rest_{i}": float(i + 1) for i in range(9)})
    data[0]["opacity"], data[1]["opacity"] = 1000, -1000
    path, _, _ = write_ply(tmp_path / "sh1.ply", data, fields)
    manifest = inspect_source(path, tmp_path / "result")
    assert manifest.sh_degree == 1
    decoded = read_source(manifest).decoded_slice(0, 2)
    np.testing.assert_array_equal(decoded["opacities"], [1, 0])
    np.testing.assert_array_equal(decoded["shs"][0, 1:, :], [[1, 4, 7], [2, 5, 8], [3, 6, 9]])
