"""Source inspection and immutable access."""

from __future__ import annotations

from pathlib import Path

import numpy as np

from .contracts import SourceManifest, SourceProperty, file_sha256, write_json_atomic

REQUIRED = ("x", "y", "z", "rot_0", "rot_1", "rot_2", "rot_3",
            "scale_0", "scale_1", "scale_2", "opacity", "f_dc_0", "f_dc_1", "f_dc_2")
HEADER_LIMIT = 1024 * 1024


class SourceValidationError(ValueError):
    """The source cannot be used without explicitly repairing the baseline."""


def _header(path: Path):
    fields = []
    count = None
    format_seen = False
    vertex_active = False
    with path.open("rb") as stream:
        if stream.readline(16).strip() != b"ply":
            raise SourceValidationError("Expected a PLY header")
        while stream.tell() < HEADER_LIMIT:
            line = stream.readline(HEADER_LIMIT - stream.tell() + 1)
            if not line:
                raise SourceValidationError("Missing end_header")
            try:
                tokens = line.decode("ascii").strip().split()
            except UnicodeDecodeError as error:
                raise SourceValidationError("Header must be ASCII") from error
            if not tokens or tokens[0] in ("comment", "obj_info"):
                continue
            if tokens[0] == "format":
                if format_seen or tokens != ["format", "binary_little_endian", "1.0"]:
                    raise SourceValidationError("Only binary_little_endian PLY 1.0 is supported")
                format_seen = True
            elif tokens[0] == "element":
                if len(tokens) != 3:
                    raise SourceValidationError("Invalid element declaration")
                try:
                    element_count = int(tokens[2])
                except ValueError as error:
                    raise SourceValidationError("Invalid vertex count") from error
                if tokens[1] != "vertex" or count is not None or not 0 <= element_count <= 2147483647:
                    raise SourceValidationError("Expected one vertex element with a supported nonnegative count")
                count = element_count
                vertex_active = True
            elif tokens[0] == "property":
                if not vertex_active or len(tokens) != 3 or tokens[1] not in ("float", "float32"):
                    raise SourceValidationError("This splat importer requires scalar float32 vertex properties")
                if tokens[2] in fields:
                    raise SourceValidationError(f"Duplicate property: {tokens[2]}")
                fields.append(tokens[2])
            elif tokens == ["end_header"]:
                if not format_seen or count is None or not set(REQUIRED).issubset(fields):
                    raise SourceValidationError("Missing format, vertex count, or required splat properties")
                offset = stream.tell()
                if offset > HEADER_LIMIT:
                    raise SourceValidationError("PLY header exceeds the size limit")
                rest = [name for name in fields if name.startswith("f_rest_")]
                degrees = {0: 0, 9: 1, 24: 2, 45: 3}
                if len(rest) not in degrees or set(rest) != {f"f_rest_{i}" for i in range(len(rest))}:
                    raise SourceValidationError("SH coefficients must be complete, contiguous degrees 0–3")
                properties = tuple(SourceProperty(name, "float32", i * 4) for i, name in enumerate(fields))
                return count, offset, properties, degrees[len(rest)]
            else:
                raise SourceValidationError(f"Unsupported header declaration: {tokens[0]}")
    raise SourceValidationError("PLY header exceeds the size limit")


def _dtype(properties):
    return np.dtype([(p.name, "<f4") for p in properties])


def _sigmoid(values):
    values = np.asarray(values, dtype=np.float64)
    exp = np.exp(-np.abs(values))
    return np.where(values >= 0, 1 / (1 + exp), exp / (1 + exp)).astype(np.float32)


def _columns(records, names):
    return np.stack([records[name] for name in names], axis=-1)


def inspect_source(path, output_dir, *, chunk_size=65536, progress=None, cancelled=None):
    path = Path(path).resolve()
    if not isinstance(chunk_size, int) or chunk_size <= 0:
        raise ValueError("chunk_size must be positive")
    count, offset, properties, degree = _header(path)
    record_bytes = 4 * len(properties)
    expected = offset + count * record_bytes
    if path.stat().st_size != expected:
        raise SourceValidationError(f"Payload size mismatch: expected {expected} bytes, found {path.stat().st_size}")
    source_hash = file_sha256(path)
    ranges = {p.name: [float("inf"), float("-inf")] for p in properties}
    statistics = {"invalid_records": 0, "nonfinite_records": 0, "zero_quaternion_records": 0,
                  "invalid_activated_scale_records": 0, "scanned_records": count, "ranges": ranges}
    records = np.memmap(path, dtype=_dtype(properties), mode="r", offset=offset, shape=(count,)) if count else None
    try:
        for start in range(0, count, chunk_size):
            if cancelled and cancelled():
                raise InterruptedError("Source inspection cancelled")
            block = records[start:start + chunk_size]
            finite = np.ones(len(block), dtype=bool)
            for name in records.dtype.names:
                values = block[name]
                finite &= np.isfinite(values)
                valid_values = values[np.isfinite(values)]
                if valid_values.size:
                    ranges[name][0] = min(ranges[name][0], float(valid_values.min()))
                    ranges[name][1] = max(ranges[name][1], float(valid_values.max()))
            quats = _columns(block, [f"rot_{i}" for i in range(4)]).astype(np.float64)
            norm = np.linalg.norm(quats, axis=1)
            bad_quat = ~np.isfinite(norm) | (norm == 0)
            with np.errstate(over="ignore", under="ignore", invalid="ignore"):
                scales = np.exp(_columns(block, [f"scale_{i}" for i in range(3)]).astype(np.float64)).astype(np.float32)
            bad_scale = np.any(~np.isfinite(scales) | (scales <= 0), axis=1)
            statistics["nonfinite_records"] += int((~finite).sum())
            statistics["zero_quaternion_records"] += int((norm == 0).sum())
            statistics["invalid_activated_scale_records"] += int(bad_scale.sum())
            statistics["invalid_records"] += int((~finite | bad_quat | bad_scale).sum())
            if progress:
                progress(min(start + chunk_size, count) / max(count, 1), "Inspecting original splat records")
    finally:
        if records is not None:
            records._mmap.close()
    if statistics["invalid_records"]:
        raise SourceValidationError(f"Source contains {statistics['invalid_records']} invalid records; "
                                    f"nonfinite={statistics['nonfinite_records']}, "
                                    f"zero quaternion={statistics['zero_quaternion_records']}, "
                                    f"invalid scale={statistics['invalid_activated_scale_records']}. No rows were removed.")
    if file_sha256(path) != source_hash:
        raise SourceValidationError("Source hash changed during inspection")
    if count == 0:
        statistics["ranges"] = {p.name: None for p in properties}
    manifest = SourceManifest(str(path), source_hash, expected, count, offset, record_bytes, properties, degree, statistics)
    write_json_atomic(Path(output_dir) / "source_manifest.json", manifest.to_dict())
    return manifest


class SourceTable:
    """Read-only file-backed original records; decoding always returns scratch arrays."""

    def __init__(self, manifest):
        self.manifest = manifest
        self.records = (np.memmap(manifest.source_path, dtype=_dtype(manifest.properties), mode="r",
                                  offset=manifest.data_offset, shape=(manifest.vertex_count,))
                        if manifest.vertex_count else np.empty(0, dtype=_dtype(manifest.properties)))
        self.records.flags.writeable = False

    def raw_record(self, source_id: int) -> bytes:
        if not 0 <= source_id < self.manifest.vertex_count:
            raise IndexError("Source ID outside original record range")
        return self.records[source_id].tobytes()

    def decoded_slice(self, start: int, stop: int) -> dict[str, np.ndarray]:
        if not 0 <= start <= stop <= self.manifest.vertex_count:
            raise IndexError("Invalid source slice")
        rows = self.records[start:stop]
        quats = _columns(rows, [f"rot_{i}" for i in range(4)]).astype(np.float64)
        quats /= np.linalg.norm(quats, axis=1, keepdims=True)
        dc = _columns(rows, [f"f_dc_{i}" for i in range(3)])[:, None, :]
        rest_count = (self.manifest.sh_degree + 1) ** 2 - 1
        if rest_count:
            rest = _columns(rows, [f"f_rest_{i}" for i in range(rest_count * 3)])
            shs = np.concatenate((dc, rest.reshape(len(rows), 3, rest_count).transpose(0, 2, 1)), axis=1)
        else:
            shs = dc.copy()
        return {"means": _columns(rows, ("x", "y", "z")), "quats": quats.astype(np.float32),
                "scales": np.exp(_columns(rows, [f"scale_{i}" for i in range(3)]).astype(np.float64)).astype(np.float32),
                "opacities": _sigmoid(rows["opacity"]), "shs": shs.astype(np.float32)}

    def close(self):
        if isinstance(self.records, np.memmap):
            self.records._mmap.close()

    def __enter__(self):
        return self

    def __exit__(self, *_):
        self.close()


def read_source(manifest):
    if isinstance(manifest, (str, Path)):
        manifest = SourceManifest.load(manifest)
    path = Path(manifest.source_path)
    if not path.is_file() or path.stat().st_size != manifest.byte_length or file_sha256(path) != manifest.sha256:
        raise SourceValidationError("Source hash/length no longer matches its inspected manifest")
    count, offset, properties, degree = _header(path)
    if (count, offset, properties, degree, 4 * len(properties)) != (
            manifest.vertex_count, manifest.data_offset, manifest.properties, manifest.sh_degree, manifest.record_bytes):
        raise SourceValidationError("Source layout disagrees with the manifest")
    return SourceTable(manifest)
