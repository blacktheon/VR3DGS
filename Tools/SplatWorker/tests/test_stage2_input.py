import json
import struct
from pathlib import Path

import numpy as np
import pytest

from splat_worker.contracts import file_sha256
from splat_worker.source import inspect_source, read_source
from tests.test_customization import _inputs, _write_json


def _export(paths, count=4, **kwargs):
    from splat_worker.stage2_input import export_accepted
    return export_accepted(paths[0], paths[1], count, paths[3], **kwargs)


def test_exports_exact_original_bytes_and_ids_in_rank_order(tmp_path):
    paths = _inputs(tmp_path)
    _export(paths)
    output = paths[3]
    manifest = json.loads((output / 'stage2_input.json').read_text())
    assert manifest['accepted_count'] == 4
    assert manifest['rank_eligible_count'] == 11
    assert manifest['original_source_count'] == 12
    assert np.fromfile(output / 'source_ids.bin', '<u4').tolist() == [10, 2, 9, 0]
    assert np.fromfile(output / 'importance.bin', '<f4').tolist() == [.5] * 4
    exported = inspect_source(output / 'accepted.ply', tmp_path / 'reinspect')
    original_manifest = tmp_path / 'inspected/source_manifest.json'
    with read_source(original_manifest) as original, read_source(exported) as saved:
        for row, source_id in enumerate([10, 2, 9, 0]):
            assert saved.raw_record(row) == original.raw_record(source_id)
    assert manifest['ply_sha256'] == file_sha256(output / 'accepted.ply')
    assert manifest['source_ids_sha256'] == file_sha256(output / 'source_ids.bin')
    assert manifest['model_local_to_world'] == [1,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1]
    assert manifest['available_lods'] == [0]
    assert manifest['calibration_status'] == 'user_authored_unity_units'


@pytest.mark.parametrize('count', [-1, 0, 12, True, 3.5])
def test_invalid_acceptance_never_publishes(tmp_path, count):
    paths = _inputs(tmp_path)
    with pytest.raises(ValueError):
        _export(paths, count)
    assert not paths[3].exists()


@pytest.mark.parametrize('defect', ['hash', 'duplicates', 'range', 'source', 'importance'])
def test_rejects_corrupt_or_mismatched_provenance(tmp_path, defect):
    paths = _inputs(tmp_path)
    rank = json.loads(paths[1].read_text())
    if defect in ('hash', 'duplicates', 'range'):
        rows = np.fromfile(paths[1].parent / 'rank.bin', '<u4')
        rows[0] = 2 if defect != 'range' else 12
        rows.tofile(paths[1].parent / 'rank.bin')
        if defect != 'hash':
            rank['rank_sha256'] = file_sha256(paths[1].parent / 'rank.bin')
    elif defect == 'source':
        rank['source_hash'] = '0' * 64
    else:
        (paths[1].parent / 'importance.bin').write_bytes(struct.pack('<f', float('nan')) * 12)
    _write_json(paths[1], rank)
    with pytest.raises(ValueError):
        _export(paths)
    assert not paths[3].exists()


def test_export_does_not_replace_an_existing_accepted_version(tmp_path):
    paths = _inputs(tmp_path)
    paths[3].mkdir()
    (paths[3] / 'owned.txt').write_text('keep')
    with pytest.raises(FileExistsError):
        _export(paths)
    assert (paths[3] / 'owned.txt').read_text() == 'keep'


def test_copies_visible_surface_assets_with_verified_hashes(tmp_path):
    paths = _inputs(tmp_path)
    texture = tmp_path / 'top.png'
    texture.write_bytes(b'fixture texture contents')
    presentation = tmp_path / 'presentation.json'
    _write_json(presentation, {'schema_version': 1, 'surfaces': [
        {'name': 'Surfaces/top', 'texture_path': str(texture), 'texture_sha256': file_sha256(texture),
         'material_path': 'Assets/top.mat', 'local_to_world': np.eye(4).reshape(-1).tolist()}
    ]})
    _export(paths, presentation_path=presentation)
    manifest = json.loads((paths[3] / 'stage2_input.json').read_text())
    surface = manifest['surfaces'][0]
    assert (paths[3] / surface['texture']).read_bytes() == texture.read_bytes()
    assert surface['texture_sha256'] == file_sha256(texture)


def test_rejects_changed_surface_file_before_publishing(tmp_path):
    paths = _inputs(tmp_path)
    texture = tmp_path / 'top.png'
    texture.write_bytes(b'changed')
    presentation = tmp_path / 'presentation.json'
    _write_json(presentation, {'schema_version': 1, 'surfaces': [
        {'name': 'Surfaces/top', 'texture_path': str(texture), 'texture_sha256': '0' * 64}
    ]})
    with pytest.raises(ValueError):
        _export(paths, presentation_path=presentation)
    assert not paths[3].exists()


def test_scene_with_visible_surfaces_cannot_publish_without_them(tmp_path):
    from splat_worker.scene_geometry import scene_hash
    paths = _inputs(tmp_path)
    scene = json.loads((paths[1].parent/'scene.json').read_text())
    scene['surfaces'] = [{'name': 'Surfaces/top'}]
    _write_json(paths[1].parent/'scene.json', scene)
    rank = json.loads(paths[1].read_text())
    rank['scene_hash'] = scene_hash(scene)
    _write_json(paths[1], rank)
    with pytest.raises(ValueError, match='surface'):
        _export(paths)
    assert not paths[3].exists()


@pytest.mark.parametrize('changed', ['importance', 'original_ids'])
def test_input_identity_includes_provenance_when_appearance_is_identical(tmp_path, changed):
    # All original rows have identical attributes, but their identities still matter.
    paths = _inputs(tmp_path, positions=[[0, 1, 0]] * 12)
    _export(paths)
    first = json.loads((paths[3] / 'stage2_input.json').read_text())
    rank = json.loads(paths[1].read_text())
    if changed == 'importance':
        np.full(12, .75, dtype='<f4').tofile(paths[1].parent / 'importance.bin')
    else:
        order = np.fromfile(paths[1].parent / 'rank.bin', '<u4')
        order[0], order[4] = order[4], order[0]
        order.tofile(paths[1].parent / 'rank.bin')
        rank['rank_sha256'] = file_sha256(paths[1].parent / 'rank.bin')
        _write_json(paths[1], rank)
    second_paths = (*paths[:3], tmp_path / 'second')
    _export(second_paths)
    second = json.loads((second_paths[3] / 'stage2_input.json').read_text())
    assert first['ply_sha256'] == second['ply_sha256']
    assert first['input_id'] != second['input_id']
