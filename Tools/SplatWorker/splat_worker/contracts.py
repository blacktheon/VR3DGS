"""Versioned metadata and atomic writes shared by worker operations."""

from __future__ import annotations

import hashlib
import json
import os
import tempfile
from dataclasses import asdict, dataclass
from pathlib import Path

SCHEMA_VERSION = 1


def file_sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with Path(path).open("rb") as stream:
        for block in iter(lambda: stream.read(8 * 1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def write_json_atomic(path: Path, value: dict) -> None:
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    encoded = json.dumps(value, indent=2, allow_nan=False) + "\n"
    fd, temporary = tempfile.mkstemp(prefix=path.name + ".", suffix=".tmp", dir=path.parent)
    try:
        with os.fdopen(fd, "w", encoding="utf-8", newline="\n") as stream:
            stream.write(encoded)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary, path)
    finally:
        if os.path.exists(temporary):
            os.unlink(temporary)


@dataclass(frozen=True)
class SourceProperty:
    name: str
    scalar_type: str
    byte_offset: int


@dataclass(frozen=True)
class SourceManifest:
    source_path: str
    sha256: str
    byte_length: int
    vertex_count: int
    data_offset: int
    record_bytes: int
    properties: tuple[SourceProperty, ...]
    sh_degree: int
    statistics: dict
    schema_version: int = SCHEMA_VERSION
    id_rule: str = "zero_based_vertex_row"
    encoding: str = "binary_little_endian"
    quaternion_order: str = "wxyz"
    scale_encoding: str = "natural_log"
    opacity_encoding: str = "logit"
    calibration_status: str = "uncalibrated"

    def to_dict(self) -> dict:
        return asdict(self)

    @classmethod
    def from_dict(cls, value: dict) -> SourceManifest:
        if value.get("schema_version") != SCHEMA_VERSION:
            raise ValueError("Unsupported source manifest schema")
        value = dict(value)
        value["properties"] = tuple(SourceProperty(**p) for p in value["properties"])
        result = cls(**value)
        if (result.id_rule != "zero_based_vertex_row" or result.encoding != "binary_little_endian"
                or result.quaternion_order != "wxyz" or result.scale_encoding != "natural_log"
                or result.opacity_encoding != "logit"):
            raise ValueError("Unsupported source conventions")
        return result

    @classmethod
    def load(cls, path: Path) -> SourceManifest:
        return cls.from_dict(json.loads(Path(path).read_text(encoding="utf-8")))
