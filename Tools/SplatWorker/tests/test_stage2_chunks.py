import json
import math
from pathlib import Path

import numpy as np
import pytest

from splat_worker.stage2_input import export_accepted
from tests.test_customization import _inputs


def _build(manifest, output, **kwargs):
    from splat_worker.stage2_chunks import build_chunks
    return build_chunks(manifest, output, **kwargs)


def _accepted(tmp_path, points):
    paths = _inputs(tmp_path, positions=points, order=list(range(len(points))))
    export_accepted(paths[0], paths[1], len(points), paths[3])
    return paths[3] / 'stage2_input.json'


def test_octree_owns_every_boundary_center_once_deterministically(tmp_path):
    # RUB Z is reflected once; the midpoint belongs to child 7 in local RUF.
    points = [[-1,-1,1], [1,1,-1], [0,0,0], [1,-1,1], [-1,1,-1]]
    manifest = _accepted(tmp_path, points)
    first = tmp_path / 'chunks1'
    second = tmp_path / 'chunks2'
    _build(manifest, first, target_count=2, minimum_cell_size=.1, maximum_depth=4)
    _build(manifest, second, target_count=2, minimum_cell_size=.1, maximum_depth=4)
    layout = json.loads((first / 'chunks.json').read_text())
    assert layout['count'] == 5
    assert [c['id'] for c in layout['chunks']] == ['r0', 'r1', 'r6', 'r7']
    ownership = np.fromfile(first / 'ownership.bin', '<u4')
    assert ownership.tolist() == [0,3,3,1,2]
    members = np.fromfile(first / 'members.bin', '<u4')
    assert members.tolist() == [0,3,4,1,2]
    for chunk_index, chunk in enumerate(layout['chunks']):
        ids = members[chunk['offset']:chunk['offset']+chunk['count']]
        assert np.all(ownership[ids] == chunk_index)
        assert chunk['group'] == 'machine'
        assert chunk['available_lods'] == [0]
    assert np.unique(members).tolist() == [0,1,2,3,4]
    assert layout['layout_id'] == json.loads((second / 'chunks.json').read_text())['layout_id']
    assert (first / 'ownership.bin').read_bytes() == (second / 'ownership.bin').read_bytes()
    assert layout['chunks'][0]['render_min'] == [-5,-5,-5]
    assert layout['chunks'][0]['render_max'] == [3,3,3]


def test_repeated_positions_stop_without_empty_or_duplicate_chunks(tmp_path):
    manifest = _accepted(tmp_path, [[2,3,4]]*7)
    folder = tmp_path / 'layout'
    _build(manifest, folder, target_count=1, minimum_cell_size=.1, maximum_depth=8)
    layout = json.loads((folder / 'chunks.json').read_text())
    assert len(layout['chunks']) == 1
    assert layout['chunks'][0]['count'] == 7
    assert np.fromfile(folder / 'ownership.bin', '<u4').tolist() == [0]*7


def test_covariance_extent_respects_rotation_and_anisotropic_size():
    from splat_worker.stage2_chunks import gaussian_axis_extents
    q = np.array([[math.sqrt(.5), 0, 0, math.sqrt(.5)]])
    scales = np.array([[2., .5, .25]])
    extent = gaussian_axis_extents(q, scales)
    np.testing.assert_allclose(extent, [[2.,8.,1.]], rtol=0, atol=1e-12)


@pytest.mark.parametrize('settings', [
    {'target_count': 0}, {'target_count': True}, {'maximum_depth': -1},
    {'maximum_depth': 30}, {'minimum_cell_size': 0}, {'minimum_cell_size': float('nan')}
])
def test_invalid_partition_settings_do_not_publish(tmp_path, settings):
    manifest = _accepted(tmp_path, [[0,0,0]])
    with pytest.raises(ValueError):
        _build(manifest, tmp_path/'layout', **settings)
    assert not (tmp_path/'layout').exists()


def test_mutated_input_is_rejected_before_chunking(tmp_path):
    manifest = _accepted(tmp_path, [[0,0,0], [1,2,3]])
    with (manifest.parent / 'accepted.ply').open('ab') as stream:
        stream.write(b'corrupt')
    with pytest.raises(ValueError):
        _build(manifest, tmp_path/'layout')
    assert not (tmp_path/'layout').exists()
