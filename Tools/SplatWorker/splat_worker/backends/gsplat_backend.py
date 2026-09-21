"""Build unchanged pinned gsplat sources with Windows-compatible host flags.

Upstream v1.5.3's JIT loader passes GCC -Wno-attributes to MSVC, which
fails with D8021. Use PyTorch's public loader and register the resulting
extension under the module name upstream already supports for prebuilds.
"""

import importlib.metadata
import json
from pathlib import Path
import sys
import tempfile

from ..contracts import write_json_atomic


def _build_directory(output_root, stem="gsplat_cuda"):
    cache = Path(output_root) / "cuda"
    cache.mkdir(parents=True, exist_ok=True)
    pointer = cache / (stem + ".current.json")
    name = stem
    try:
        saved = json.loads(pointer.read_text(encoding="utf-8"))
        candidate = saved.get("directory") if isinstance(saved, dict) else None
        if (isinstance(candidate, str) and Path(candidate).name == candidate
                and (candidate == name or candidate.startswith(stem + "-"))):
            name = candidate
    except (OSError, ValueError):
        pass
    build = cache / name
    if (build / "lock").exists():
        # A dead Python worker may leave both PyTorch's baton and live compiler
        # descendants. Never remove their lock or reuse their build directory.
        build = Path(tempfile.mkdtemp(prefix=stem + "-", dir=cache))
    else:
        build.mkdir(parents=True, exist_ok=True)
    return build, pointer


def load_cuda_backend(output_root):
    """Load while the caller holds gpu_workspace_lock for this output root."""
    from torch.utils.cpp_extension import load

    package = Path(importlib.metadata.distribution("gsplat").locate_file("gsplat"))
    cuda = package / "cuda"
    sources = sorted((cuda / "csrc").glob("*.cu")) + sorted((cuda / "csrc").glob("*.cpp")) + [cuda / "ext.cpp"]
    build, pointer = _build_directory(output_root)
    extension = load(name="gsplat_cuda", sources=[str(p) for p in sources],
                     extra_cflags=["/O2"], extra_cuda_cflags=["-O3", "-use_fast_math"],
                     extra_include_paths=[str(cuda / "include"), str(cuda / "csrc/third_party/glm")],
                     build_directory=str(build), verbose=False)
    write_json_atomic(pointer, {"directory": build.name})
    sys.modules["gsplat.csrc"] = extension
    return {"loader": "torch.utils.cpp_extension.load", "host_flags": ["/O2"],
            "cuda_flags": ["-O3", "-use_fast_math"], "extension_path": extension.__file__}
