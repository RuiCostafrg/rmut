# Reference baseline

## Local verification

The harness unit tests cover distinct edits on the same source line, UTF-16 offsets, excluded files, identity and outcome mismatches, and gate-crossing variation.

The installed tools were Stryker.NET 5.1.0 and the managed Rmut prototype from commit `afd4cdd`, packaged as `0.0.1-benchmark`. The local ARM64 Termux/proot host did not delegate writable cgroup v2 controllers. The harness stopped at cgroup setup and produced no performance data.

A correctness run with the Linux ARM64 SDK 10.0.404 needed `DOTNET_GCRegionRange=400000000` for Roslyn to build on the host. Both tools then hit VSTest's ARM64 dotnet-host lookup error under proot. See [the upstream VSTest issue](https://github.com/microsoft/vstest/issues/16446).

## Hosted conformance

The Stryker.NET conformance workflow runs the four differential cases using installed NuGet tools on hosted Ubuntu x64. Each matrix job exports its version information, reports, tool logs and differential as an artifact. The real-project and incremental workloads remain pending a compatible candidate and Linux benchmark host.

## Hosted Rust switched baseline

[Run 46004075790](https://github.com/RuiCostafrg/rmut/actions/runs/46004075790)
measured Rmut `0.0.1-benchmark` from `6265c3c` against Stryker.NET 5.1.0.
The [raw artifact](https://github.com/RuiCostafrg/rmut/actions/runs/46004075790/artifacts/15199592826)
contains the package hashes, environment, per-pair counters, tool logs and
reports. This run used Ubuntu 24.04 x64, .NET SDK 10.0.404, two logical CPUs
on a four-CPU Intel Xeon 6975P-C runner, and a shared NuGet cache. Host load
at measurement start was 1.82 over one minute. Tool packages were built in
Release and both tools built the fixture in Debug with serial mutant execution.

All five measured pairs and the excluded warmup pair agreed on the same five
edits and outcomes, with four killed mutants and one survivor per tool in each
pair. The twelve tool processes exited successfully. Every Rmut report ID
matched one switched execution log entry, including all 25 measured mutants.
Each pair started with fresh source copies, and tool order alternated.

| Metric | Stryker median (min–max) | Rmut median (min–max) | Median paired ratio (min–max) | Gate |
| --- | ---: | ---: | ---: | ---: |
| Wall time | 12.86 s (12.71–13.67) | 14.61 s (13.87–15.36) | 1.124× (1.087–1.149) | 0.50×, missed |
| Peak cgroup memory | 248.69 MiB (247.62–250.31) | 229.46 MiB (228.13–230.55) | 0.921× (0.920–0.930) | 0.80×, missed |
| Process-tree CPU time | 14.64 CPU-s (14.54–14.84) | 16.23 CPU-s (16.07–16.33) | 1.109× (1.090–1.117) | 0.80×, missed |

The ranges show observed variation on one host. The wall time includes
tool-owned restore, build, initial tests, and mutant execution. For example,
the Stryker warmup log spent roughly 11 of 14 seconds on analysis, restore,
build and initial testing before announcing the five mutants. Rmut also runs
an instrumented control before testing the mutants. The CPU counters stayed
within the two-core budget. The run did not collect per-process profiles, so
it does not isolate Rmut's limiting phase. The fixture measures small-project
startup and mutation testing end to end. Larger projects, incremental runs
and other concurrency settings remain unmeasured.

The [first hosted attempt](https://github.com/RuiCostafrg/rmut/actions/runs/46003649852)
failed at cgroup migration. Putting the supervisor inside the delegated
subtree resolved it. The [next attempt](https://github.com/RuiCostafrg/rmut/actions/runs/46003747480)
matched outcomes on the conformance fixture, but only two of its five edits
used switched execution. The gate rejected that pair. The baseline above
uses the dedicated `switched-operators` fixture.
