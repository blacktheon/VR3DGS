from types import SimpleNamespace

import numpy as np
import pytest

from splat_worker.ranking import keep_count, validate_rank


@pytest.mark.parametrize("n,p,want", [(6011316, 0, 0), (6011316, 5000, 3005658),
                                    (6011316, 10000, 6011316), (7, 3333, 2),
                                    (0, 10000, 0), (2147483647, 9999, 2147268898)])
def test_count_uses_integer_floor_and_handles_endpoints(n, p, want):
    assert keep_count(n, p) == want


@pytest.mark.parametrize("n,p", [(-1, 5000), (4, -1), (4, 10001), (4, 50.5), (True, 2)])
def test_count_rejects_invalid_ranges_and_fractional_units(n, p):
    with pytest.raises(ValueError):
        keep_count(n, p)


def test_valid_rank_is_a_full_permutation_not_score_threshold():
    order = np.array([3, 1, 0, 2], dtype="<u4")
    validate_rank(order, SimpleNamespace(vertex_count=4))
    assert order.tolist() == [3, 1, 0, 2]


@pytest.mark.parametrize("order", [np.array([0, 0, 1, 2], dtype="<u4"),
                                  np.array([0, 1, 2, 4], dtype="<u4"),
                                  np.array([0, 1, 2], dtype="<u4"),
                                  np.array([0, 1, 2, 3], dtype=">u4"),
                                  np.array([[0, 1], [2, 3]], dtype="<u4")])
def test_rank_rejects_duplicates_bounds_shape_count_and_encoding(order):
    with pytest.raises(ValueError):
        validate_rank(order, SimpleNamespace(vertex_count=4))
