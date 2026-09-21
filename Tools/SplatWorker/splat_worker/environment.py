"""Isolated worker toolchain selection and a real CUDA rasterization check."""

import importlib.metadata
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import time

from .contracts import write_json_atomic

GSPLAT_COMMIT = "937e29912570c372bed6747a5c9bf85fed877bae"


def validate_runtime(python_version, torch_version, gsplat_commit):
    if tuple(python_version[:2]) != (3, 11):
        raise ValueError("This worker requires its isolated Python 3.11 environment")
    if torch_version != "2.7.1+cu128":
        raise ValueError("Expected PyTorch 2.7.1+cu128; rerun worker setup")
    if gsplat_commit != GSPLAT_COMMIT:
        raise ValueError("Unexpected gsplat commit; rerun worker setup")


def configure_toolchain(output_root):
    if os.name != "nt":
        raise RuntimeError("This initial environment profile requires Windows and VS 2022")
    vs_root = Path(os.environ.get("ProgramFiles(x86)", "C:/Program Files (x86)"))
    vcvars = vs_root / "Microsoft Visual Studio/2022/BuildTools/VC/Auxiliary/Build/vcvars64.bat"
    cuda = Path(os.environ.get("ProgramFiles", "C:/Program Files")) / "NVIDIA GPU Computing Toolkit/CUDA/v12.8"
    if not vcvars.is_file():
        raise RuntimeError(f"VS 2022 C++ Build Tools not found at {vcvars}")
    if not (cuda / "bin/nvcc.exe").is_file():
        raise RuntimeError(f"CUDA 12.8 toolkit not found at {cuda}")
    # This fixed, locally discovered batch path is the only shell invocation. Never
    # interpolate source paths or job data, and never log the inherited environment.
    command = '"' + os.environ.get("COMSPEC", "cmd.exe") + '" /d /s /c ""' + str(vcvars) + '" >nul && set"'
    result = subprocess.run(command,
                            capture_output=True, text=True, check=True,
                            creationflags=subprocess.CREATE_NO_WINDOW)
    for line in result.stdout.splitlines():
        key, separator, value = line.partition("=")
        if separator and key:
            os.environ[key] = value
    os.environ.update(CUDA_HOME=str(cuda), DISTUTILS_USE_SDK="1", MAX_JOBS="2",
                      TORCH_EXTENSIONS_DIR=str(Path(output_root).resolve() / "cuda"))
    os.environ["PATH"] = str(Path(sys.executable).parent) + os.pathsep + str(cuda / "bin") + os.pathsep + os.environ["PATH"]
    compiler = shutil.which("cl")
    if not compiler or "/2022/" not in compiler.replace("\\", "/"):
        raise RuntimeError("VS 2022 toolchain activation did not select the expected compiler")
    version = subprocess.run([str(cuda / "bin/nvcc.exe"), "--version"], capture_output=True,
                             text=True, check=True, creationflags=subprocess.CREATE_NO_WINDOW)
    return {"compiler": compiler, "msvc_version": os.environ.get("VCToolsVersion", ""),
            "cuda_toolkit": str(cuda), "nvcc_version": version.stdout.strip()}


def check_environment(context, request):
    context.progress(0.05, "Selecting VS 2022 and CUDA 12.8")
    toolchain = configure_toolchain(context.output_root)
    import torch

    direct_url = json.loads(importlib.metadata.distribution("gsplat").read_text("direct_url.json") or "{}")
    commit = direct_url.get("vcs_info", {}).get("commit_id", "")
    validate_runtime(sys.version_info, torch.__version__, commit)
    if not torch.cuda.is_available():
        raise RuntimeError("CUDA is unavailable; CPU source inspection remains usable")
    capability = torch.cuda.get_device_capability()
    os.environ["TORCH_CUDA_ARCH_LIST"] = f"{capability[0]}.{capability[1]}"
    context.progress(0.15, "Compiling/loading gsplat CUDA extension; first run can take several minutes")
    started = time.monotonic()
    from .backends.gsplat_backend import load_cuda_backend
    extension = load_cuda_backend(context.output_root)
    from gsplat import rasterization

    torch.cuda.reset_peak_memory_stats()
    device = "cuda"
    with torch.no_grad():
        rgb, alpha, _ = rasterization(
            means=torch.tensor([[0., 0., 2.]], device=device),
            quats=torch.tensor([[1., 0., 0., 0.]], device=device),
            scales=torch.tensor([[.15, .15, .15]], device=device),
            opacities=torch.tensor([.75], device=device),
            colors=torch.tensor([[1., .2, .1]], device=device),
            viewmats=torch.eye(4, device=device)[None],
            Ks=torch.tensor([[[30., 0., 16.5], [0., 30., 16.5], [0., 0., 1.]]], device=device),
            width=33, height=33, packed=False, near_plane=.01, far_plane=100.,
            rasterize_mode="classic")
        torch.cuda.synchronize()
        context.check_cancelled()
        if not bool(torch.isfinite(rgb).all() and torch.isfinite(alpha).all()):
            raise RuntimeError("CUDA rasterizer returned non-finite pixels")
        center = alpha[0, 16, 16, 0].item()
        if abs(center - .75) > 1e-4:
            raise RuntimeError(f"One-splat alpha oracle failed: expected .75, got {center}")
        expected = alpha * torch.tensor([1., .2, .1], device=device)
        if (rgb - expected).abs().max().item() > 1e-5:
            raise RuntimeError("One-splat premultiplied RGB oracle failed")
        import numpy as np
        np.savez_compressed(context.result_dir / "smoke_render.npz", rgb=rgb.cpu().numpy(), alpha=alpha.cpu().numpy())
    report = {"schema_version": 1, "python": sys.version, "interpreter": sys.executable,
              "torch": torch.__version__, "torch_cuda": torch.version.cuda,
              "gsplat_version": importlib.metadata.version("gsplat"), "gsplat_commit": commit,
              "toolchain": toolchain, "extension": extension, "device": torch.cuda.get_device_name(),
              "capability": list(capability), "extension_and_render_seconds": time.monotonic() - started,
              "peak_allocated_bytes": torch.cuda.max_memory_allocated(),
              "peak_reserved_bytes": torch.cuda.max_memory_reserved(),
              "rasterization": {"width": 33, "height": 33, "splats": 1,
                                "finite": True, "center_alpha": center, "oracle_passed": True}}
    write_json_atomic(context.result_dir / "environment.json", report)
    context.progress(1., "CUDA extension and actual rasterization passed")
    return {"environment_report": "environment.json", "smoke_render": "smoke_render.npz"}
