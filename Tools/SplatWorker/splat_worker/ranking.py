"""Exact retained counts and frozen-rank validation."""

from numbers import Integral

import numpy as np


def keep_count(n, keep_centi_percent):
    if (isinstance(n, bool) or not isinstance(n, Integral) or n < 0
            or isinstance(keep_centi_percent, bool) or not isinstance(keep_centi_percent, Integral)
            or not 0 <= keep_centi_percent <= 10000):
        raise ValueError("Count must be nonnegative; percentage must be an integer from 0 to 10000")
    return int(n) * int(keep_centi_percent) // 10000


def validate_rank(order, source):
    if not isinstance(order, np.ndarray) or order.dtype != np.dtype("<u4") or order.ndim != 1:
        raise ValueError("Rank must be a one-dimensional little-endian uint32 array")
    n = source.vertex_count
    if order.size != n:
        raise ValueError("Rank length does not match source count")
    if n and (int(order.max()) >= n or np.any(np.bincount(order, minlength=n) != 1)):
        raise ValueError("Rank must contain every source ID exactly once")
