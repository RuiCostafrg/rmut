#!/usr/bin/env python3
"""Run Stryker.NET 5.1.0 and Rmut on frozen inputs with per-run cgroup accounting."""

import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import signal
import statistics
import subprocess
import sys
import time
import uuid

from reports import differential, rmut_records, stryker_records


ROOT = Path(__file__).resolve().parents[1]
STRYKER_VERSION = "5.1.0"
TOOLS = ("stryker", "rmut")
GATES = {"elapsed_seconds": 0.50, "peak_memory_bytes": 0.80, "cpu_seconds": 0.80}
IGNORED_MUTATORS = [
    "statement", "block", "boolean", "assignment", "initializer", "unary",
    "update", "checked", "linq", "string", "stringmethod", "bitwise", "regex",
    "math", "nullcoalescing", "conditional", "collectionexpression",
]
ENV = {
    **os.environ,
    "DOTNET_CLI_USE_MSBUILD_SERVER": "0",
    "MSBUILDDISABLENODEREUSE": "1",
    "UseSharedCompilation": "false",
    "DOTNET_CLI_TELEMETRY_OPTOUT": "1",
}


def write_json(path, data):
    path.write_text(json.dumps(data, indent=2) + "\n")


def counters(group, file):
    return dict(line.split() for line in (group / file).read_text().splitlines())


class Cgroup:
    def __init__(self, parent):
        self.path = parent / f"rmut-{uuid.uuid4().hex}"
        self.path.mkdir()

    def __enter__(self):
        try:
            for file in ("cgroup.procs", "cgroup.events", "cgroup.kill", "cpu.stat", "memory.peak"):
                if not (self.path / file).is_file():
                    raise ValueError(f"Required cgroup v2 file unavailable: {file}")
        except (OSError, ValueError):
            self.path.rmdir()
            raise
        return self

    def __exit__(self, *_):
        if self.populated():
            (self.path / "cgroup.kill").write_text("1")
            deadline = time.monotonic() + 10
            while self.populated() and time.monotonic() < deadline:
                time.sleep(0.01)
        self.path.rmdir()

    def populated(self):
        return counters(self.path, "cgroup.events")["populated"] == "1"

    def measure(self, elapsed):
        usage = counters(self.path, "cpu.stat")
        cpu = (int(usage["user_usec"]) + int(usage["system_usec"])) / 1_000_000
        return {
            "elapsed_seconds": elapsed,
            "peak_memory_bytes": int((self.path / "memory.peak").read_text()),
            "cpu_seconds": cpu,
            "average_cpu_percent": 100 * cpu / elapsed,
        }


def execute(command, cwd, log, timeout, parent=None):
    if parent is None:
        with log.open("w") as stream:
            with subprocess.Popen(command, cwd=cwd, env=ENV, stdout=stream,
                                  stderr=subprocess.STDOUT, start_new_session=True) as process:
                try:
                    return {"exit_code": process.wait(timeout=timeout)}
                except (subprocess.TimeoutExpired, KeyboardInterrupt):
                    os.killpg(process.pid, signal.SIGKILL)
                    raise
    with Cgroup(parent) as group, log.open("w") as stream:
        # Join before exec so all child work, including startup, inherits the cgroup.
        launcher = [
            "/bin/sh", "-c", 'printf "%s" "$$" > "$1/cgroup.procs" || exit 125; shift; exec "$@"',
            "attach-cgroup", str(group.path), *command,
        ]
        start = time.monotonic()
        deadline = start + timeout
        with subprocess.Popen(launcher, cwd=cwd, env=ENV, stdout=stream,
                              stderr=subprocess.STDOUT) as process:
            try:
                code = process.wait(timeout=timeout)
                while group.populated():
                    if time.monotonic() >= deadline:
                        raise TimeoutError("Process tree stayed alive past timeout")
                    time.sleep(0.01)
            except (subprocess.TimeoutExpired, TimeoutError, KeyboardInterrupt):
                (group.path / "cgroup.kill").write_text("1")
                process.kill()
                raise
        return {"exit_code": code, **group.measure(time.monotonic() - start)}


def preflight(parent, output):
    probe = execute(["/bin/true"], output, output / "cgroup-probe.log", 10, parent)
    if probe["exit_code"] or not probe["peak_memory_bytes"]:
        raise ValueError(f"Cgroup probe failed, see {output / 'cgroup-probe.log'}")


def extract_source(spec, workspace, checkout):
    if "source" in spec:
        shutil.copytree(ROOT / spec["source"], workspace, symlinks=True, ignore=shutil.ignore_patterns(
            "bin", "obj", "TestResults", "StrykerOutput", ".git",
        ))
    else:
        workspace.mkdir()
        source = checkout.resolve(strict=True)
        with subprocess.Popen(["git", "-C", source, "archive", spec["revision"]],
                              stdout=subprocess.PIPE) as archive:
            subprocess.run(["tar", "-x", "-C", workspace], stdin=archive.stdout, check=True)
            archive.stdout.close()
            if archive.wait():
                raise ValueError(f"Unable to extract pinned revision {spec['revision']}")


def tool_command(tool, args, spec, workspace, directory, *, seed=False):
    run_dir = workspace.parent
    if tool == "rmut":
        report = directory / "rmut.json"
        command = [str(args.rmut), "run", "--project", spec["project"],
                   "--test-project", spec["test_project"], "--json", str(report)]
        command.extend(
            arg.replace("{workspace}", str(workspace)).replace("{run_dir}", str(run_dir))
            for arg in spec.get("rmut_seed_args" if seed else "rmut_args", [])
        )
        return command, workspace, report

    config = {
        "project": Path(spec["project"]).name,
        "reporters": ["json"],
        "configuration": "Debug",
        "concurrency": 1,
        "coverage-analysis": "off",
        "disable-mix-mutants": True,
        "disable-bail": True,
        "break-on-initial-test-failure": True,
        "test-runner": "vstest",
    }
    if spec.get("shared_operators"):
        config["ignore-mutations"] = [*IGNORED_MUTATORS, *spec.get("additional_ignored_mutators", [])]
    config.update(spec.get("stryker_seed_config" if seed else "stryker_config", {}))
    config_file = directory / "stryker-config.json"
    write_json(config_file, {"stryker-config": config})
    return (
        [str(args.stryker), "--config-file", str(config_file), "--skip-version-check", "--output", str(run_dir)],
        (workspace / spec["test_project"]).parent,
        None,
    )


def run_tool(tool, args, spec, run_dir):
    workspace = run_dir / "source"
    run_dir.mkdir()
    result = {}
    records = {}
    try:
        shutil.copytree(args.output / "input", workspace, symlinks=True)
        if args.scenario == "incremental":
            subprocess.run(["git", "init", "-q", "-b", "benchmark-base"], cwd=workspace,
                           check=True)
            subprocess.run(["git", "add", "-f", "."], cwd=workspace, check=True)
            subprocess.run(["git", "-c", "user.name=Benchmark", "-c",
                            "user.email=benchmark@example.invalid", "-c", "commit.gpgsign=false",
                            "commit", "-qm", "Frozen source"], cwd=workspace, check=True)
            seed_dir = run_dir / "seed"
            seed_dir.mkdir()
            seed = invoke(tool, args, spec, workspace, seed_dir, seed=True)
            result["seed"] = seed[0]
            if seed[0].get("error") or not seed[1] or differential(seed[1], seed[1])["unfinished"]:
                raise ValueError("Incremental seed did not produce a completed mutation report")
            if "incremental_patch" in spec:
                patch = args.manifest.resolve().parent / spec["incremental_patch"]
                subprocess.run(["git", "apply", str(patch)], cwd=workspace, check=True)
        sample, records = invoke(tool, args, spec, workspace, run_dir)
        result.update(sample)
    except (OSError, ValueError, KeyError, IndexError, TypeError, subprocess.SubprocessError) as error:
        result["error"] = str(error)
    write_json(run_dir / "result.json", result)
    return result, records


def invoke(tool, args, spec, workspace, directory, *, seed=False):
    command, cwd, report = tool_command(tool, args, spec, workspace, directory, seed=seed)
    result = {"command": command, "cwd": str(cwd)}
    records = {}
    try:
        result.update(execute(command, cwd, directory / "tool.log", args.timeout,
                              args.cgroup if args.measure and not seed else None))
        if tool == "stryker":
            report = workspace.parent / "reports" / "mutation-report.json"
            if not report.is_file():
                raise ValueError("Stryker did not write a JSON report")
            if seed:
                report = report.rename(directory / "mutation-report.json")
            data = json.loads(report.read_text())
            records = stryker_records(data, workspace, spec["project"])
        else:
            data = json.loads(report.read_text())
            records = rmut_records(data, workspace)
            if args.measure and spec.get("require_switched") and not seed:
                switched_ids = re.findall(r"^Switched execution ([a-fA-F0-9]{64})$",
                                          (directory / "tool.log").read_text(), re.M)
                expected_ids = [mutant["id"] for mutant in data["mutants"]]
                result["switched_mutants"] = len(switched_ids)
                if sorted(switched_ids) != sorted(expected_ids):
                    raise ValueError("Rmut did not use switched execution for every mutant")
        if records and not any(row["outcome"] != "ignored" for row in records.values()):
            raise ValueError("Mutation report contained only exclusions")
        if not records:
            raise ValueError("Mutation report contained no mutants or exclusions")
        result["report"] = str(report)
        result["mutants"] = [
            {"identity": list(key), **record} for key, record in sorted(records.items())
        ]
        if result["exit_code"]:
            result["error"] = f"Tool exited with status {result['exit_code']}"
    except (OSError, ValueError, KeyError, IndexError, TypeError, TimeoutError,
            subprocess.SubprocessError) as error:
        result["error"] = str(error)
    return result, records


def summarize(pairs):
    summary = {}
    for metric, limit in GATES.items():
        by_tool = {tool: [pair[tool][metric] for pair in pairs] for tool in TOOLS}
        ratios = [rmut / stryker for stryker, rmut in zip(*by_tool.values(), strict=True)]
        observations = {
            tool: {"median": statistics.median(values), "min": min(values), "max": max(values)}
            for tool, values in by_tool.items()
        }
        median_ratio = observations["rmut"]["median"] / observations["stryker"]["median"]
        evidence = [median_ratio, *ratios]
        summary[metric] = {
            "observations": observations,
            "ratio_of_medians": median_ratio,
            "median_paired_ratio": statistics.median(ratios),
            "ratio_range": [min(ratios), max(ratios)],
            "gate": limit,
            "verdict": "inconclusive" if min(evidence) <= limit < max(evidence)
            else "pass" if max(evidence) <= limit else "fail",
        }
    return summary


def source_hash(root):
    entries = []
    for file in sorted(root.rglob("*")):
        if file.is_symlink():
            kind, value = "link", os.readlink(file)
        elif file.is_file():
            kind = "file"
            with file.open("rb") as stream:
                value = hashlib.file_digest(stream, "sha256").hexdigest()
        else:
            continue
        entries.append((file.relative_to(root).as_posix(), kind, value))
    return hashlib.sha256(json.dumps(entries).encode()).hexdigest()


def package_hash(launcher, package, version):
    folder = launcher.parent / ".store" / package / version / package / version
    archive = folder / f"{package}.{version}.nupkg"
    with archive.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("mode", choices=["compare", "measure"])
    parser.add_argument("workload")
    parser.add_argument("--manifest", type=Path, default=ROOT / "benchmarks/workloads.json")
    parser.add_argument("--stryker", type=Path, required=True)
    parser.add_argument("--rmut", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--cgroup", type=Path)
    parser.add_argument("--checkout", type=Path)
    parser.add_argument("--scenario", choices=["clean", "incremental"], default="clean")
    parser.add_argument("--pairs", type=int, default=5)
    parser.add_argument("--warmups", type=int, default=1)
    parser.add_argument("--timeout", type=int, default=900)
    args = parser.parse_args()
    args.measure = args.mode == "measure"
    if args.measure and (args.cgroup is None or args.pairs < 5 or args.warmups < 1):
        parser.error("measure requires --cgroup, at least five pairs and at least one warmup pair")
    if args.timeout <= 0:
        parser.error("--timeout must be positive")
    args.stryker = args.stryker.resolve(strict=True)
    args.rmut = args.rmut.resolve(strict=True)
    if args.stryker.name != "dotnet-stryker" or args.rmut.name != "rmut":
        parser.error("Pass the installed dotnet-stryker and rmut launchers")
    args.output = args.output.resolve()
    if args.cgroup is not None:
        args.cgroup = args.cgroup.resolve(strict=True)
    spec = json.loads(args.manifest.read_text())[args.workload]
    if any((parent / ".git").exists() for parent in args.output.parents):
        parser.error("--output must be outside Git checkouts so the workload has its own project root")
    if args.measure and spec.get("comparison_only"):
        parser.error(f"Workload is limited to compare mode: {spec['comparison_only']}")
    if "source" in spec and args.output.is_relative_to((ROOT / spec["source"]).resolve()):
        parser.error("--output must be outside the fixture source")
    if "source" not in spec and args.checkout is None:
        parser.error(f"Provide --checkout containing the frozen revision {spec['revision']}")
    if args.scenario == "incremental":
        if args.measure:
            parser.error("incremental measurement requires verified Rmut cache reuse")
        if not all(spec.get(key) for key in (
            "stryker_seed_config", "stryker_config", "rmut_seed_args", "rmut_args",
        )):
            parser.error("incremental comparison requires seed and measured configuration for both tools")

    args.output.mkdir(parents=True)
    list_command = ["dotnet", "tool", "list", "--tool-path"]
    version = subprocess.check_output([*list_command, args.stryker.parent],
                                      env=ENV, text=True, timeout=30)
    if not re.search(r"^dotnet-stryker\s+5\.1\.0\s+dotnet-stryker\s*$", version, re.M):
        raise ValueError(f"Install dotnet-stryker {STRYKER_VERSION} with --tool-path")
    rmut_version = subprocess.check_output([*list_command, args.rmut.parent],
                                           env=ENV, text=True, timeout=30)
    candidate_version = re.search(r"^rmut\s+(\S+)\s+rmut\s*$", rmut_version, re.I | re.M)
    if not candidate_version:
        raise ValueError("Install Rmut with --tool-path")
    extract_source(spec, args.output / "input", args.checkout)
    write_json(args.output / "environment.json", {
        "workload": args.workload, "spec": spec, "scenario": args.scenario,
        "source_sha256": source_hash(args.output / "input"),
        "stryker_tool": version.strip(), "rmut_tool": rmut_version.strip(),
        "dotnet": subprocess.check_output(["dotnet", "--info"], env=ENV, text=True, timeout=30),
        "host": list(os.uname()), "affinity": sorted(os.sched_getaffinity(0)),
        "load_average": os.getloadavg(), "cache": "shared NuGet cache, fresh source per run",
        "dotnet_environment": {key: ENV[key] for key in (
            "DOTNET_CLI_USE_MSBUILD_SERVER", "MSBUILDDISABLENODEREUSE", "UseSharedCompilation",
        )},
        "dotnet_runtime_overrides": {key: ENV[key] for key in (
            "DOTNET_ROOT", "DOTNET_GCRegionRange", "DOTNET_GCHeapHardLimit",
            "DOTNET_SYSTEM_GLOBALIZATION_INVARIANT", "DOTNET_ROLL_FORWARD",
        ) if key in ENV},
        "cgroup": str(args.cgroup) if args.measure else None,
        "package_sha256": {
            "stryker": package_hash(args.stryker, "dotnet-stryker", STRYKER_VERSION),
            "rmut": package_hash(args.rmut, "rmut", candidate_version[1]),
        },
    })

    if args.measure:
        try:
            preflight(args.cgroup, args.output)
        except (OSError, ValueError) as error:
            write_json(args.output / "summary.json", {
                "equivalent": None, "warmup_pairs": 0, "measured_pairs": 0,
                "performance": None, "error": str(error),
            })
            raise

    all_pairs = []
    reference = None
    warmups = args.warmups if args.measure else 0
    repeats = args.pairs if args.measure else 1
    for index in range(warmups + repeats):
        pair_dir = args.output / f"pair-{index + 1:02d}"
        pair_dir.mkdir()
        order = TOOLS if index % 2 == 0 else tuple(reversed(TOOLS))
        samples, records = {}, {}
        for tool in order:
            samples[tool], records[tool] = run_tool(tool, args, spec, pair_dir / tool)
        diff = differential(records["stryker"], records["rmut"])
        stable = reference is None or all(
            {key: row["outcome"] for key, row in records[tool].items()} == reference[tool]
            for tool in TOOLS
        )
        same = stable and not any(diff.values()) and all("error" not in row for row in samples.values())
        pair = {"warmup": index < warmups, "order": order, "equivalent": same,
                "stable": stable, "differential": diff, **samples}
        write_json(pair_dir / "pair.json", pair)
        all_pairs.append(pair)
        if not same:
            break
        reference = {tool: {key: row["outcome"] for key, row in records[tool].items()} for tool in TOOLS}

    equivalent = all(pair["equivalent"] for pair in all_pairs)
    summary = {
        "equivalent": equivalent,
        "warmup_pairs": sum(pair["warmup"] for pair in all_pairs),
        "measured_pairs": sum(not pair["warmup"] for pair in all_pairs) if args.measure else 0,
        "performance": None,
    }
    if args.measure and equivalent:
        measured = [pair for pair in all_pairs if not pair["warmup"]]
        summary["performance"] = summarize(measured)
    write_json(args.output / "summary.json", summary)
    print(json.dumps(summary, indent=2))
    passed = equivalent and (
        not args.measure or all(row["verdict"] == "pass" for row in summary["performance"].values())
    )
    return 0 if passed else 1


if __name__ == "__main__":
    try:
        sys.exit(main())
    except (OSError, ValueError, KeyError, subprocess.SubprocessError) as error:
        print(f"Benchmark failed: {error}", file=sys.stderr)
        sys.exit(2)
