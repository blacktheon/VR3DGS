"""Command-line entry point called by the Unity Editor host."""

import argparse
import os
from pathlib import Path
import sys

def main():
    parser = argparse.ArgumentParser(description="Local Unity splat preprocessing worker")
    parser.add_argument("--job", type=Path, required=True)
    args = parser.parse_args()
    directory = args.job.resolve().parent
    # Move inherited pipes to durable files before heavy work. Editor domain reload can
    # then close its handles without blocking or breaking a still-running worker.
    with (directory / "stdout.log").open("ab", buffering=0) as out, (directory / "stderr.log").open("ab", buffering=0) as err:
        os.dup2(out.fileno(), 1)
        os.dup2(err.fileno(), 2)
    sys.stdout.reconfigure(encoding="utf-8")
    sys.stderr.reconfigure(encoding="utf-8")
    try:
        from .jobs import run_job
        return run_job(args.job)
    except Exception as error:
        print(f"Unable to start worker: {type(error).__name__}: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
