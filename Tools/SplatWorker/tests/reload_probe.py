"""Manual Unity domain-reload integration fixture, never a production operation."""
import sys
import time
from splat_worker.jobs import run_job


def wait(context, request):
    context.progress(.25, "Integration probe: waiting for cancellation after Unity reload")
    deadline = time.monotonic() + 90
    while time.monotonic() < deadline:
        context.check_cancelled()
        time.sleep(.05)
    raise RuntimeError("Reload probe was not cancelled within 90 seconds")


if __name__ == "__main__":
    raise SystemExit(run_job(sys.argv[1], handlers={"inspect": wait}))
