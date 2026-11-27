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
    if verb == "test" or (verb == "exec" and Path(arguments[1]).name == "vstest.console.dll"):
        results = Path(arguments[arguments.index("--results-directory") + 1] if verb == "test" else
                       next(arg.split(":", 1)[1] for arg in arguments if arg.startswith("--resultsdirectory:")))
        for path in results.glob("*.trx"):
            root = ET.parse(path).getroot()
            tests.append({
                "times": root.find("{*}Times").attrib,
                "durations": [row.get("duration") for row in root.findall(".//{*}UnitTestResult")],
            })
    with (output / "children.jsonl").open("a") as stream:
        print(json.dumps(dict(verb=verb, arguments=arguments, cwd=os.getcwd(), started=started, finished=finished,
                              exit_code=result.returncode, tests=tests)), file=stream)
    return result.returncode


def sample_memory(session, stream):
    processes = []
    for directory in Path("/proc").iterdir():
        if not directory.name.isdecimal():
            continue
        try:
            stat = (directory / "stat").read_text()
            fields = stat.rsplit(")", 1)[1].split()
            if int(fields[3]) != session:
                continue
            memory = (directory / "smaps_rollup").read_text()
            pss_kib = next(int(line.split()[1]) for line in memory.splitlines()
                           if line.startswith("Pss:"))
            command = (directory / "cmdline").read_bytes().split(b"\0")
        except (FileNotFoundError, ProcessLookupError, PermissionError, StopIteration):
            continue
        processes.append(dict(pid=int(directory.name), ppid=int(fields[1]), pss_kib=pss_kib,
                              command=[arg.decode(errors="replace") for arg in command if arg]))
    if processes:
        print(json.dumps(dict(time=time.time(), pss_kib=sum(p["pss_kib"] for p in processes),
                              processes=processes)), file=stream)


def trace(command, output, **kwargs):
    with (output / "memory.jsonl").open("w") as samples:
        with subprocess.Popen(command, start_new_session=True, **kwargs) as process:
            started = time.monotonic()
            while process.poll() is None:
                if time.monotonic() - started > 300:
                    os.killpg(process.pid, 9)
                    raise subprocess.TimeoutExpired(command, 300)
                sample_memory(process.pid, samples)
                time.sleep(0.05)
            if process.returncode:
                raise subprocess.CalledProcessError(process.returncode, command)


def main():
    executable, output = (Path(value).resolve() for value in sys.argv[1:3])
    workload = sys.argv[3] if len(sys.argv) > 3 else "switched-operators"
    spec = json.loads((ROOT / "benchmarks/workloads.json").read_text())[workload]
    output.mkdir()
    workspace = output / "fixture"
    shutil.copytree(ROOT / spec["source"], workspace)
    with (output / "rmut.log").open("w") as log:
        trace(
            ["strace", "-f", "-ttt", "-T", "-s", "256", "-e",
             "trace=execve,execveat,wait4,exit_group,mkdir,mkdirat,copy_file_range,sendfile",
             "-o", output / "process-tree.log",
             executable, "run", "--project", spec["project"], "--test-project",
             spec["test_project"], *spec["rmut_args"],
             "--json", output / "rmut.json"], output,
            env={**ENV, "DOTNET_HOST_PATH": str(Path(__file__).resolve()),
                 "RMUT_PROFILE_DOTNET": shutil.which("dotnet"), "RMUT_PROFILE_OUTPUT": str(output)},
            cwd=workspace, stdout=log, stderr=subprocess.STDOUT,
        )


if __name__ == "__main__":
    if "RMUT_PROFILE_DOTNET" in os.environ:
        sys.exit(child())
    main()
