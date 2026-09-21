import pytest

from splat_worker.gpu_lock import gpu_workspace_lock


def test_second_gpu_job_is_rejected_and_lock_releases_after_exception(tmp_path):
    with pytest.raises(ValueError, match="simulated failure"):
        with gpu_workspace_lock(tmp_path):
            with pytest.raises(RuntimeError, match="GPU job"):
                with gpu_workspace_lock(tmp_path):
                    pytest.fail("Concurrent GPU work was admitted")
            raise ValueError("simulated failure")
    with gpu_workspace_lock(tmp_path):
        pass
