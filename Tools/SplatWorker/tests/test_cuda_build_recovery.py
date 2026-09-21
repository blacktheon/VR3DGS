"""Exercise PyTorch's real lock behavior without compiling CUDA in unit tests."""

import json
from pathlib import Path
import subprocess
import sys

import pytest


PROBE = r'''
import json
from pathlib import Path
import sys
from types import SimpleNamespace

from torch.utils.file_baton import FileBaton
import torch.utils.cpp_extension

def simulated_compile(**kwargs):
    directory = Path(kwargs["build_directory"])
    baton = FileBaton(str(directory / "lock"), wait_seconds=0.01)
    if not baton.try_acquire():
        baton.wait()  # The production dependency would block here indefinitely.
    else:
        try:
            (directory / "gsplat_cuda.pyd").write_bytes(b"test-only compiled extension")
        finally:
            baton.release()
    return SimpleNamespace(__file__=str(directory / "gsplat_cuda.pyd"))

torch.utils.cpp_extension.load = simulated_compile
from splat_worker.backends.gsplat_backend import load_cuda_backend
from splat_worker.gpu_lock import gpu_workspace_lock

root = Path(sys.argv[1])
with gpu_workspace_lock(root):
    first = load_cuda_backend(root)
    cached = load_cuda_backend(root)
    interrupted = Path(first["extension_path"]).parent
    (interrupted / "lock").write_bytes(b"orphaned compiler baton")
    (interrupted / "compiler-output.tmp").write_bytes(b"still owned by a possible compiler child")
    recovered = load_cuda_backend(root)
print(json.dumps({"first": first, "cached": cached, "recovered": recovered}))
'''


def test_abandoned_build_recovers_without_waiting_or_touching_compiler_files(tmp_path):
    legacy = tmp_path / "cuda" / "gsplat_cuda"
    legacy.mkdir(parents=True)
    (legacy / "lock").write_bytes(b"abandoned legacy build")
    (legacy / "compiler-output.tmp").write_bytes(b"legacy compiler output")
    try:
        probe = subprocess.run([sys.executable, "-c", PROBE, str(tmp_path)],
                               capture_output=True, text=True, timeout=20)
    except subprocess.TimeoutExpired:
        pytest.fail("The next GPU check waited on an abandoned PyTorch build lock")
    assert probe.returncode == 0, probe.stderr
    report = json.loads(probe.stdout.strip().splitlines()[-1])
    first = Path(report["first"]["extension_path"]).parent
    recovered = Path(report["recovered"]["extension_path"]).parent
    assert first != legacy
    assert report["cached"]["extension_path"] == report["first"]["extension_path"]
    assert recovered not in (legacy, first)
    assert (legacy / "lock").read_bytes() == b"abandoned legacy build"
    assert (legacy / "compiler-output.tmp").read_bytes() == b"legacy compiler output"
    assert (first / "lock").read_bytes() == b"orphaned compiler baton"
    assert (first / "compiler-output.tmp").read_bytes() == b"still owned by a possible compiler child"
    assert not (recovered / "lock").exists()
