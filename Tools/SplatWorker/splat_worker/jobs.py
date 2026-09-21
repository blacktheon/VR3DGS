"""Durable local worker jobs and atomic result publication."""

from __future__ import annotations

import json
import os
from pathlib import Path
import re
import threading
import time
import traceback
from contextlib import nullcontext

import psutil

from .contracts import SCHEMA_VERSION, write_json_atomic
from .source import inspect_source
from .environment import check_environment
from .gpu_lock import gpu_workspace_lock

TERMINAL_STATES = {"Succeeded", "Failed", "Cancelled", "Interrupted"}


def _load_json(path):
    return json.loads(Path(path).read_text(encoding="utf-8-sig"))


class JobContext:
    def __init__(self, request, directory):
        self.request = request
        self.directory = directory
        self.output_root = Path(request["output_root"]).resolve()
        self.result_dir = self.output_root / "results" / ("." + request["job_id"] + ".partial")
        self.final_dir = self.output_root / "results" / request["job_id"]
        self.status = {"schema_version": SCHEMA_VERSION, "job_id": request["job_id"],
                       "operation": request["operation"], "state": "Running", "progress": 0.0,
                       "message": "Starting", "error": "", "result_dir": "",
                       "worker_pid": os.getpid(), "worker_started_utc": psutil.Process().create_time()}

    def check_cancelled(self):
        if (self.directory / "cancel.requested").exists():
            raise InterruptedError("Cancelled by the Unity Editor")

    def progress(self, fraction, message):
        self.check_cancelled()
        self.status.update(progress=min(1.0, max(0.0, float(fraction))), message=message)
        self.save_status()

    def save_status(self):
        self.status["updated_utc"] = time.time()
        write_json_atomic(self.directory / "status.json", self.status)
        with (self.directory / "events.jsonl").open("a", encoding="utf-8") as events:
            events.write(json.dumps(self.status, allow_nan=False) + "\n")

    def heartbeat(self):
        write_json_atomic(self.directory / "heartbeat.json", {
            "job_id": self.request["job_id"], "worker_pid": os.getpid(),
            "worker_started_utc": self.status["worker_started_utc"], "updated_utc": time.time()})


def _inspect(context, request):
    manifest = inspect_source(Path(request["source_path"]), context.result_dir,
                              progress=context.progress,
                              cancelled=lambda: (context.directory / "cancel.requested").exists())
    return {"source_manifest": "source_manifest.json", "source_hash": manifest.sha256,
            "vertex_count": manifest.vertex_count}


def run_job(request_path, handlers=None):
    request_path = Path(request_path).resolve()
    request = _load_json(request_path)
    if request.get("schema_version") != SCHEMA_VERSION:
        raise ValueError("Unsupported job request schema")
    job_id = request.get("job_id", "")
    if not re.fullmatch(r"[a-zA-Z0-9_-]{1,96}", job_id) or request_path.parent.name != job_id:
        raise ValueError("Job ID must identify its request directory")
    directory = request_path.parent
    # A request is run at most once, even if a host restarts or retries the same command.
    with (directory / "worker.lock").open("x", encoding="utf-8") as lock:
        json.dump({"job_id": job_id, "pid": os.getpid(), "created_utc": psutil.Process().create_time()}, lock)
    context = JobContext(request, directory)
    context.save_status()
    stop = threading.Event()

    def pulse():
        while not stop.is_set():
            try:
                context.heartbeat()
            except OSError:
                # Status and process identity remain available if a transient read holds the file.
                pass
            stop.wait(0.5)

    heartbeat = threading.Thread(target=pulse, name="job-heartbeat", daemon=True)
    heartbeat.start()
    operations = {"inspect": _inspect, "check_environment": check_environment}
    if handlers:
        operations.update(handlers)
    try:
        context.check_cancelled()
        if request["operation"] not in operations:
            raise ValueError(f"Operation is not available: {request['operation']}")
        context.result_dir.mkdir(parents=True, exist_ok=False)
        lock = gpu_workspace_lock(context.output_root) if request["operation"] in {"check_environment", "score", "verify"} else nullcontext()
        with lock:
            artifacts = operations[request["operation"]](context, request)
        context.check_cancelled()
        write_json_atomic(context.result_dir / "result.json", {
            "schema_version": SCHEMA_VERSION, "job_id": job_id, "operation": request["operation"],
            "artifacts": artifacts, "completed_utc": time.time()})
        context.result_dir.rename(context.final_dir)
        write_json_atomic(context.output_root / f"current_{request['operation']}.json", {
            "schema_version": SCHEMA_VERSION, "job_id": job_id, "operation": request["operation"],
            "result_dir": str(context.final_dir)})
        context.status.update(state="Succeeded", progress=1.0, message="Complete", result_dir=str(context.final_dir))
        code = 0
    except InterruptedError as error:
        context.status.update(state="Cancelled", message=str(error))
        code = 2
    except Exception as error:
        detail = f"{type(error).__name__}: {error}"
        # Compiler output can be megabytes; keep status/UI bounded, full traceback in error.log.
        context.status.update(state="Failed", message="Worker failed", error=detail[:2000])
        with (directory / "error.log").open("a", encoding="utf-8") as stream:
            traceback.print_exc(file=stream)
        code = 1
    finally:
        stop.set()
        heartbeat.join(timeout=2)
        context.save_status()
    return code


def reconcile_job(job_dir):
    job_dir = Path(job_dir)
    state = _load_json(job_dir / "status.json")
    if state["state"] in TERMINAL_STATES:
        return state
    try:
        process = psutil.Process(state["worker_pid"])
        same_process = process.is_running() and abs(process.create_time() - state["worker_started_utc"]) < 0.02
    except (psutil.Error, KeyError, OverflowError):
        same_process = False
    if not same_process:
        state.update(state="Interrupted", message="Worker process ended without publishing a completed result",
                     updated_utc=time.time())
        write_json_atomic(job_dir / "status.json", state)
    return state
