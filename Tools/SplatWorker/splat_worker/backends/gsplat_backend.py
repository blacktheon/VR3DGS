"""Build unchanged pinned gsplat sources with Windows-compatible host flags.

Upstream v1.5.3's JIT loader passes GCC -Wno-attributes to MSVC, which
fails with D8021. Use PyTorch's public loader and register the resulting
extension under the module name upstream already supports for prebuilds.
"""

import importlib.metadata
from pathlib import Path
import sys


def load_cuda_backend(output_root):
    from torch.utils.cpp_extension import load

    package = Path(importlib.metadata.distribution("gsplat").locate_file("gsplat"))
    cuda = package / "cuda"
    sources = sorted((cuda / "csrc").glob("*.cu")) + sorted((cuda / "csrc").glob("*.cpp")) + [cuda / "ext.cpp"]
    build = Path(output_root) / "cuda/gsplat_cuda"
    build.mkdir(parents=True, exist_ok=True)
    extension = load(name="gsplat_cuda", sources=[str(p) for p in sources],
                     extra_cflags=["/O2"], extra_cuda_cflags=["-O3", "-use_fast_math"],
                     extra_include_paths=[str(cuda / "include"), str(cuda / "csrc/third_party/glm")],
                     build_directory=str(build), verbose=False)
    sys.modules["gsplat.csrc"] = extension
    return {"loader": "torch.utils.cpp_extension.load", "host_flags": ["/O2"],
            "cuda_flags": ["-O3", "-use_fast_math"], "extension_path": extension.__file__}
