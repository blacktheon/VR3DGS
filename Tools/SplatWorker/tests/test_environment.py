import pytest
import os

from splat_worker.environment import validate_runtime, configure_toolchain


def test_pinned_runtime_is_accepted():
    validate_runtime((3, 11, 16), "2.7.1+cu128", "937e29912570c372bed6747a5c9bf85fed877bae")


@pytest.mark.parametrize("python,torch,commit,problem", [
    ((3, 14, 0), "2.7.1+cu128", "937e29912570c372bed6747a5c9bf85fed877bae", "Python 3.11"),
    ((3, 11, 16), "2.7.1+cpu", "937e29912570c372bed6747a5c9bf85fed877bae", "cu128"),
    ((3, 11, 16), "2.7.1+cu128", "different", "gsplat commit"),
])
def test_unreviewed_runtime_is_rejected(python, torch, commit, problem):
    with pytest.raises(ValueError, match=problem):
        validate_runtime(python, torch, commit)


@pytest.mark.skipif(os.name != "nt", reason="Windows worker profile")
def test_real_windows_toolchain_handles_program_files_spaces(tmp_path):
    previous = dict(os.environ)
    try:
        report = configure_toolchain(tmp_path)
        assert "/2022/" in report["compiler"].replace("\\", "/")
        assert report["msvc_version"].startswith("14.44")
        assert "release 12.8" in report["nvcc_version"]
    finally:
        os.environ.clear()
        os.environ.update(previous)
