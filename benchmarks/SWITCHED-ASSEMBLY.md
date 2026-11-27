# Switched assembly execution

The process profile identified repeated test launch work as a cost that can
be removed while keeping the compile-once template, private copies, and a
fresh testhost per invocation. The DLL path is opt-in because project-based
VSTest can supply MSBuild settings and target hooks. The original project
baseline still runs first. The switched control and mutants use the
assembly produced by the instrumented build, with the same full-suite TRX
checks and fresh-project fallback.

## Diagnostic profile

The [baseline diagnostic run](https://github.com/RuiCostafrg/rmut/actions/runs/46006327894/artifacts/15202297497)
traced the installed project-based runner after its five timed pairs. The
trace includes two builds, one source-discovery invocation, and seven
testhosts for the original baseline, switched control, and five mutants.
The [initial assembly diagnostic](https://github.com/RuiCostafrg/rmut/actions/runs/46007176860/artifacts/15201013914)
also shows seven fresh testhosts, with the test command targeting a private
DLL for the switched control and all five mutants.

| Work in the traced runs | Project-based | Direct DLL |
| --- | ---: | ---: |
| Restore entry point, per build, inclusive MSBuild summary | 1.023–1.030 s | 0.999–1.011 s |
| C# compiler tasks, per build, summed over two projects | 4.448–4.490 s | 4.252–4.293 s |
| Private build copy, per switched invocation | 19–34 ms | 29–33 ms |
| SDK command start to testhost launch, switched median | 1.125 s | 0.492 s |
| Testhost launch to TRX finish, switched median | 0.918 s | 0.913 s |
| Test case duration recorded in TRX | 11–24 ms | 11–27 ms |
| Complete SDK test command, switched median | 2.090 s | 1.449 s |

These figures describe instrumented runs. The phase estimates use
MSBuild's inclusive restore entry point and summed compiler task timings.
The copy interval spans workspace creation to the last observed successful
copy syscall and excludes cleanup. Both traces moved about 5.36 MiB per
switched copy. The interval after testhost launch includes runtime and
adapter startup, discovery, test execution, and result transport. The test
case itself took only tens of milliseconds. The launch saving of about
0.63 s per invocation across six switched invocations bounds the expected
opportunity at roughly 3.8 s on the traced run. Tracing materially
inflates timings, so the untraced paired measurements are the end-to-end
comparison.

## Hosted paired measurements

The [candidate rerun](https://github.com/RuiCostafrg/rmut/actions/runs/46039046714/artifacts/15217709540)
used the package from `5d35e09`, which requires the private build to produce
the selected DLL. The [PR 16 reference](https://github.com/RuiCostafrg/rmut/actions/runs/46004075790/artifacts/15199592826),
project-based replication, initial DLL candidate, and candidate rerun each
completed five measured pairs and one excluded warmup. All tool invocations
exited successfully with identical edit identities and outcomes, four killed
and one survived per pair. All five Rmut mutations in each pair used switched
execution.

Each run used Ubuntu 24.04 x64, .NET SDK 10.0.404, two pinned logical CPUs,
the same Stryker.NET 5.1.0 package and fixture hash, a shared NuGet cache
within the run, and fresh input copies. Tools were packaged in Release
and built the fixture in Debug. Stryker used one worker with coverage
analysis, mutant mixing, and bail disabled to match the serial full-suite
workload. Order alternated between pairs. Initial one-minute host load
was 1.82, 1.61, 2.10, 2.29 respectively.
Hosted runs used separate VMs.

Median paired Rmut-to-Stryker ratios with observed pair ranges follow.
Lower ratios indicate less cost.

| Metric | PR 16 | Project replication | Initial DLL | DLL rerun | Gate |
| --- | ---: | ---: | ---: | ---: | ---: |
| Wall time | 1.124× (1.087–1.149) | 1.103× (1.070–1.141) | 0.956× (0.831–1.037) | 1.073× (0.920–1.094) | 0.50×, fail |
| Peak cgroup memory | 0.921× (0.920–0.930) | 0.921× (0.919–0.931) | 0.923× (0.917–0.928) | 0.921× (0.914–0.928) | 0.80×, fail |
| Process-tree CPU | 1.109× (1.090–1.117) | 1.105× (1.097–1.119) | 0.986× (0.980–0.992) | 1.000× (0.984–1.011) | 0.80×, fail |

The DLL rerun had the following absolute medians and ranges.

| Metric | Stryker | Rmut |
| --- | ---: | ---: |
| Wall time | 13.01 s (12.91–13.66) | 13.96 s (12.57–14.12) |
| Peak cgroup memory | 249.04 MiB (247.08–249.93) | 229.20 MiB (228.32–230.72) |
| Process-tree CPU | 14.90 CPU-s (14.84–14.96) | 14.89 CPU-s (14.71–15.00) |

All three gates still failed. The median paired wall ratio moved toward
parity compared with PR 16, but both DLL runs had pairs on either side of
parity and the rerun's range overlapped PR 16's. The end-to-end wall
improvement is inconclusive across hosted runs. The rerun's trace
reproduced the launch reduction at 0.524 s to testhost start and 1.466 s
for the full switched test command. Both DLL runs used less CPU relative
to Stryker than either project-based run. The final CPU ratio was near
parity, and memory ratios stayed in the same range. CPU counters stayed
within the two-core budget. These runs cover one small, serial fixture
and leave larger suites and incremental runs unmeasured.

The [installed-package differential](https://github.com/RuiCostafrg/rmut/actions/runs/46039227637)
confirmed the candidate's five matching outcomes and checked that every
Rmut mutant used switched execution. The runner test compares the opt-in
mode with fresh execution using static initialization, a mutable static
counter, and a filesystem marker. It also verifies fresh fallback when
the requested DLL is missing.
