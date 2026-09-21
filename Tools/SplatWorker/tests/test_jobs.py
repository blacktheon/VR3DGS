import json
import os
from pathlib import Path
import subprocess
import sys
import time

import pytest
import psutil

from splat_worker.jobs import run_job, reconcile_job
from tests.fixtures import write_ply


def make_job(root, source, job_id="job-a", operation="inspect"):
    directory = root / "jobs" / job_id
    directory.mkdir(parents=True)
    request = directory / "request.json"
    request.write_text(json.dumps({"schema_version": 1, "job_id": job_id, "operation": operation,
                                   "source_path": str(source), "output_root": str(root / "output")}), encoding="utf-8")
    return request


def status(request):
    return json.loads((request.parent / "status.json").read_text())


def test_success_publishes_complete_manifest_and_failed_job_keeps_previous_result(tmp_path):
    source, _, _ = write_ply(tmp_path / "original.ply")
    request = make_job(tmp_path, source)
    assert run_job(request) == 0
    pointer_path = tmp_path / "output/current_inspect.json"
    previous = pointer_path.read_bytes()
    pointer = json.loads(previous)
    assert (Path(pointer["result_dir"]) / "source_manifest.json").exists()
    assert status(request)["state"] == "Succeeded"
    failed = make_job(tmp_path, tmp_path / "missing.ply", "failed-job")
    assert run_job(failed) == 1
    assert status(failed)["state"] == "Failed"
    assert pointer_path.read_bytes() == previous


def test_cancelled_job_never_publishes_and_is_terminal(tmp_path):
    source, _, _ = write_ply(tmp_path / "source.ply")
    request = make_job(tmp_path, source)
    (request.parent / "cancel.requested").touch()
    assert run_job(request) == 2
    assert status(request)["state"] == "Cancelled"
    assert not (tmp_path / "output/current_inspect.json").exists()


def test_same_job_cannot_execute_twice(tmp_path):
    source, _, _ = write_ply(tmp_path / "source.ply")
    request = make_job(tmp_path, source)
    assert run_job(request) == 0
    before = (request.parent / "status.json").read_bytes()
    with pytest.raises(FileExistsError):
        run_job(request)
    assert (request.parent / "status.json").read_bytes() == before


def test_real_cli_handles_spaces_unicode_and_shell_characters_without_a_shell(tmp_path):
    directory = tmp_path / "machine data 测试 & literal"
    directory.mkdir()
    source, _, _ = write_ply(directory / "original model.ply")
    request = make_job(directory, source)
    completed = subprocess.run([sys.executable, "-m", "splat_worker", "--job", str(request)],
                               capture_output=True, timeout=15)
    assert completed.returncode == 0, completed.stderr.decode(errors="replace")
    assert status(request)["state"] == "Succeeded"
    assert (request.parent / "stdout.log").exists()
    assert (request.parent / "stderr.log").exists()


WAIT_FIXTURE = r'''
import sys,time
from splat_worker.jobs import run_job
def wait(ctx, request):
    ctx.progress(0.1, "waiting")
    print("x" * 1048576, file=sys.stderr, flush=True)
    while True:
        ctx.check_cancelled()
        time.sleep(0.02)
raise SystemExit(run_job(sys.argv[1], handlers={"wait":wait}))
'''


def test_reconciliation_preserves_live_job_and_cancellation_survives_host_recreation(tmp_path):
    request = make_job(tmp_path, "unused", operation="wait")
    with (request.parent / "fixture-stderr.log").open("wb") as err:
        process = subprocess.Popen([sys.executable, "-c", WAIT_FIXTURE, str(request)],
                                   stdout=subprocess.DEVNULL, stderr=err)
        try:
            deadline = time.monotonic() + 8
            while not (request.parent / "status.json").exists():
                assert process.poll() is None, "fixture worker exited before starting"
                assert time.monotonic() < deadline
                time.sleep(0.02)
            first = reconcile_job(request.parent)
            second = reconcile_job(request.parent)
            assert first["worker_pid"] == second["worker_pid"]
            worker = psutil.Process(first["worker_pid"])
            # Windows venv python.exe may be a redirector that owns a separate worker.
            assert worker.pid == process.pid or process.pid in [p.pid for p in worker.parents()]
            assert first["state"] == second["state"] == "Running"
            (request.parent / "cancel.requested").touch()
            assert process.wait(timeout=8) == 2
            assert status(request)["state"] == "Cancelled"
        finally:
            (request.parent / "cancel.requested").touch()
            if process.poll() is None:
                try:
                    process.wait(timeout=5)
                except subprocess.TimeoutExpired:
                    process.terminate()
                    process.wait(timeout=5)
    assert (request.parent / "fixture-stderr.log").stat().st_size > 1000000


def test_dead_worker_is_interrupted_without_restarting_or_publishing(tmp_path):
    request = make_job(tmp_path, "unused")
    (request.parent / "status.json").write_text(json.dumps({"state": "Running", "worker_pid": 2147483647,
                                                           "worker_started_utc": 1.0, "job_id": "job-a"}))
    recovered = reconcile_job(request.parent)
    assert recovered["state"] == "Interrupted"
    assert not (tmp_path / "output/current_inspect.json").exists()


def test_pid_reuse_does_not_attach_to_an_unrelated_process(tmp_path):
    request = make_job(tmp_path, "unused")
    (request.parent / "status.json").write_text(json.dumps({"state": "Running", "worker_pid": os.getpid(),
                                                           "worker_started_utc": 1.0, "job_id": "job-a"}))
    assert reconcile_job(request.parent)["state"] == "Interrupted"


def test_job_keeps_an_append_only_progress_history(tmp_path):
    source, _, _ = write_ply(tmp_path / "original.ply")
    request = make_job(tmp_path, source)
    assert run_job(request) == 0
    events = [json.loads(line) for line in (request.parent / "events.jsonl").read_text().splitlines()]
    assert len(events) >= 3
    assert events[0]["state"] == "Running"
    assert events[-1]["state"] == "Succeeded"
    assert all(event["job_id"] == "job-a" for event in events)
    assert [event["updated_utc"] for event in events] == sorted(event["updated_utc"] for event in events)
