#!/usr/bin/env python3
"""Run a diagnostic-only switched fixture, with a dotnet child-process shim."""

import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import time
import xml.etree.ElementTree as ET

from run import ENV, ROOT


def child():
    arguments = sys.argv[1:]
    verb = arguments[0]
    command = [os.environ["RMUT_PROFILE_DOTNET"], *arguments]
    if verb == "build":
        command.append("-clp:PerformanceSummary")

    started = time.time()
    result = subprocess.run(command, capture_output=True,
                            env={**os.environ, "DOTNET_HOST_PATH": command[0]})
    finished = time.time()
    output = Path(os.environ["RMUT_PROFILE_OUTPUT"])
    log = output / f"{started:.6f}-{verb}-{os.getpid()}.log"
    log.write_bytes(result.stdout + result.stderr)
    sys.stdout.buffer.write(result.stdout)
    sys.stderr.buffer.write(result.stderr)
    tests = []
    if verb == "test":
        results = Path(arguments[arguments.index("--results-directory") + 1])
        for path in results.glob("*.trx"):
            root = ET.parse(path).getroot()
            tests.append({
                "times": root.find("{*}Times").attrib,
                "durations": [row.get("duration") for row in root.findall(".//{*}UnitTestResult")],
            })
    with (output / "children.jsonl").open("a") as stream:
        print(json.dumps(dict(verb=verb, cwd=os.getcwd(), started=started, finished=finished,
                              exit_code=result.returncode, tests=tests)), file=stream)
    return result.returncode


def main():
    executable, output = (Path(value).resolve() for value in sys.argv[1:3])
    workload = sys.argv[3] if len(sys.argv) > 3 else "switched-operators"
    spec = json.loads((ROOT / "benchmarks/workloads.json").read_text())[workload]
    output.mkdir()
    workspace = output / "fixture"
    shutil.copytree(ROOT / spec["source"], workspace)
    with (output / "rmut.log").open("w") as log:
        subprocess.run(
            ["strace", "-f", "-ttt", "-T", "-s", "256", "-e",
             "trace=execve,execveat,wait4,exit_group,mkdir,mkdirat,copy_file_range,sendfile",
             "-o", output / "process-tree.log",
             executable, "run", "--project", spec["project"], "--test-project",
             spec["test_project"], *spec["rmut_args"],
             "--json", output / "rmut.json"],
            env={**ENV, "DOTNET_HOST_PATH": str(Path(__file__).resolve()),
                 "RMUT_PROFILE_DOTNET": shutil.which("dotnet"), "RMUT_PROFILE_OUTPUT": str(output)},
            cwd=workspace, stdout=log, stderr=subprocess.STDOUT, timeout=300, check=True,
        )


if __name__ == "__main__":
    if "RMUT_PROFILE_DOTNET" in os.environ:
        sys.exit(child())
    main()
