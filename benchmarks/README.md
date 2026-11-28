# Compare Rmut with Stryker.NET 5.1.0

Use Python 3.11+, Git, tar and the same .NET SDK and runtimes for both installed tools. The conformance projects target .NET 10. The harness uses Linux for both modes and needs a writable, delegated cgroup v2 subtree with CPU and memory accounting for measurement.

## Install and compare

From the repository root, use a new tool and output directory outside Git checkouts for each build.

```sh
run_root=$(mktemp -d)
dotnet tool install dotnet-stryker --version 5.1.0 --tool-path "$run_root/stryker"
dotnet pack src/Rmut.Cli -c Release -p:PackageVersion=0.0.1-benchmark -o "$run_root/feed"
dotnet tool install Rmut --version 0.0.1-benchmark --add-source "$run_root/feed" \
  --tool-path "$run_root/rmut"

python3 -B benchmarks/run.py compare shared-operators \
  --stryker "$run_root/stryker/dotnet-stryker" --rmut "$run_root/rmut/rmut" \
  --output "$run_root/comparison"
```

`compare` runs both tools once and emits a differential result without collecting timings. The harness normalizes edits using a relative source path, UTF-16 offset, original text, and replacement text. This distinguishes multiple edits on one line and lets a token edit match a reference report that replaces its containing expression. Status reasons are retained for inspection. The gate checks the reported status, including exclusions, for each matching edit. Some Stryker replacements discard inline trivia, and C# line directives can change Stryker report coordinates. Such edits may appear as mismatches and require inspection. It fails on missing or ambiguous identities, incomplete execution, and differing status sets.

| Workload | Scope |
| --- | --- |
| `switched-operators` | Five independent arithmetic and equality edits supported by the Rust switched runner. One xUnit test kills four mutants and leaves one survivor. |
| `switched-assembly` | Same fixture with Rmut opting into direct VSTest DLL execution. |
| `shared-operators` | Equivalent arithmetic, equality and logical mutants. Two edits share a line. The tests kill four mutants and leave one survivor with coverage analysis off. |
| `relational-gap` | Stryker has a second replacement for the same comparison expression. |
| `no-coverage-gap` | Stryker per-test coverage distinguishes an uncovered mutation from a survivor. |
| `operator-exclusion-gap` | A configured Stryker mutator exclusion changes the outcome for the logical edit. |
| `smart-enum` | Small real library and its unit tests. The pinned upstream project is multi-targeted. |
| `fluent-validation` | Larger real library and its unit tests. The pinned test project targets multiple runtimes. |

For a real project, clone the repository listed in [workloads.json](workloads.json), fetch the exact revision, and pass the checkout with `--checkout`. The harness extracts that revision with `git archive`. The checkout may contain unrelated local edits. The gap and real-project workloads are limited to compare mode. The current compatibility gaps are listed in the [feature inventory](STRYKER-5.1.0.md).

## Measure the switched runner

Run the [Paired mutation benchmark](../.github/workflows/paired-benchmark.yml) workflow
on the candidate branch through GitHub Actions. It installs the Release Rmut
package built from that revision and Stryker.NET 5.1.0, uses an isolated .NET
10.0.404 SDK, and delegates cgroup v2 accounting on an Ubuntu x64 runner. Download
the `paired-benchmark` artifact from the run for the raw evidence, including when
a gate fails. Select `switched-assembly` to measure the opt-in DLL path. The
optional `profile` input collects a separate Rmut trace after measurement with
process and file-copy syscalls, MSBuild performance summaries, and TRX timings.

Use a quiet Linux host with a delegated cgroup v2 parent whose child groups expose `cpu.stat`, `memory.peak` and `cgroup.kill`. Use a fixed CPU affinity, a stable SDK and package source, and the same environment and worker budget for both tools. Record any changes to the host, repository or tool settings with the results. Use separate output directories for clean and incremental scenarios.

```sh
python3 -B benchmarks/run.py measure switched-operators \
  --stryker "$run_root/stryker/dotnet-stryker" --rmut "$run_root/rmut/rmut" \
  --cgroup "$delegated_cgroup" --pairs 5 --warmups 1 \
  --output "$run_root/clean"
```

Each trial starts with a fresh source copy and uses the same shared NuGet package cache. Both tools build in Debug with the configured workload and the installed binaries themselves come from release packages. The switched workload uses one Stryker worker and the serial Rust-backed Rmut runner. Both require a passing baseline and run the full fixture suite for each eligible mutant. For both comparison and measurement, the switched workloads require every Rmut mutant to use switched execution. Tool-owned restore, build and test work runs inside the timed process tree. Fixture extraction, report comparison, warmups and incremental seeding are outside the aggregate. Pair order alternates across warmups and measured trials.

The child cgroup measures peak charged memory, including charged file cache and short-lived descendants, and total user plus system CPU seconds. Average CPU percentage is CPU seconds divided by elapsed seconds times 100 and can exceed 100% on a multicore host. Peak cgroup memory depends on kernel page-cache accounting. Compare runs on the same host with consistent cache preparation.

`summary.json` reports each metric's median and range, both ratio of medians and median paired ratio, and the gate verdict. The elapsed-time gate is 0.50× of Stryker. The memory and CPU gates are 0.80×. Observations crossing a gate are inconclusive. A run with any correctness mismatch or process failure emits no performance summary. Exit 0 means the requested gate passed, 1 means a comparison or performance gate failed or was inconclusive, and 2 means setup failed.

Incremental comparison uses `--scenario incremental` with a workload manifest that supplies both `stryker_seed_config` / `stryker_config` and `rmut_seed_args` / `rmut_args`. The seed and measured stages reuse the same source and output root per tool. Rmut arguments may reference `{workspace}` and `{run_dir}`. An optional `incremental_patch` relative to the workload manifest is applied after both tools have independently seeded from identical source. The seed Git commit is on branch `benchmark-base`. The harness compares each tool's incremental outcomes with its own fresh outcomes and checks
Rmut's cache hits. To exercise the shared fixture, use `compare incremental-operators --scenario
incremental` with the usual installed tool paths and a separate output directory. Run it with no
patch for unchanged inputs, then repeat with `--incremental-patch
benchmarks/incremental/test-only.patch` and `--incremental-patch
benchmarks/incremental/production.patch`. These runs validate correctness. The clean timing
workflow uses its existing workloads and output directories.

Keep `environment.json`, `summary.json`, and every `pair.json`, tool log and report for audit. The [baseline log](BASELINE.md) records current evidence.
