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

## Incremental build candidate

The [hosted evidence](https://github.com/RuiCostafrg/rmut/actions/runs/46042907049/artifacts/15220887890)
contains the six raw pairs, runtime logs, TRX timing summaries, process trace,
and memory samples for revision `2d04589`. One pair was an excluded warmup.
Both tools exited successfully with identical mutation identities and four
kills plus one survivor in every pair. Rmut logged incremental build success
and used the switched binary for every mutant. The run used the same fixture,
SDK, versions, CPU affinity and benchmark settings as the DLL rerun above,
with a one-minute starting host load of 1.71.

| Metric | Stryker median (range) | Rmut median (range) | Paired median ratio (range) | Gate |
| --- | ---: | ---: | ---: | ---: |
| Wall time | 12.90 s (12.76–13.61) | 11.40 s (10.49–12.95) | 0.883× (0.771–0.952) | 0.50×, fail |
| Peak cgroup memory | 248.36 MiB (247.21–249.38) | 218.81 MiB (218.22–220.55) | 0.883× (0.876–0.892) | 0.80×, fail |
| Process-tree CPU | 14.67 CPU-s (14.62–14.80) | 11.89 CPU-s (11.84–11.97) | 0.809× (0.806–0.814) | 0.80×, fail |

The CPU and peak-memory ratios were below both prior DLL candidates across
all observed pairs. Wall time was lower than Stryker in all five measured
pairs. The wall-ratio range overlaps measurements from both prior DLL runs,
so the size of the incremental wall improvement across hosted VMs is
uncertain. The time saved fits within the eliminated restore and compiler
work, and both tools' CPU usage fits within the two-core budget.

In the separate instrumented run, the baseline build ran restore (1.047 s
inclusive) and two C# compiler tasks (4.449 s combined). The incremental
build ran one C# compiler task (1.815 s) and skipped restore. The trace
showed seven separate testhosts. The six switched VSTest command invocations
took 1.417–1.520 s apiece under tracing. Saving their roughly 0.5 s launch
interval alone would leave a substantial wall-time gap to the gate.

The maximum sampled proportional set size (PSS) was 305.18 MiB across 427
samples, during the original baseline build. MSBuild accounted for 179.18 MiB
and its second C# compiler process for 93.88 MiB at that sample. This
trace-session diagnostic includes the tracer and shim and may miss shorter
peaks. PSS apportions shared mappings and excludes the cgroup's unmapped
page cache. The timed memory gate uses the separate, untraced cgroup peak.

## Direct VSTest console experiment

The [first direct-console run](https://github.com/RuiCostafrg/rmut/actions/runs/46074562230/artifacts/15234573339)
measured revision `3b8bc26` on AMD EPYC 9V74. The preceding incremental run
used Intel Xeon 6975P-C. The first direct-console run used two pinned
logical CPUs and had a starting load of 1.75. All five measured pairs and
the excluded warmup matched the mutation identities and outcomes, confirmed
incremental preparation, and used switched execution for all five mutants.

| Metric | Stryker median (range) | Rmut median (range) | Paired median ratio (range) | Gate |
| --- | ---: | ---: | ---: | ---: |
| Wall time | 16.21 s (16.17–16.32) | 11.86 s (11.85–11.95) | 0.733× (0.726–0.737) | 0.50×, fail |
| Peak cgroup memory | 230.90 MiB (230.74–231.25) | 179.64 MiB (179.56–180.30) | 0.778× (0.777–0.781) | 0.80×, pass |
| Process-tree CPU | 20.31 CPU-s (20.29–20.37) | 15.11 CPU-s (15.09–15.19) | 0.744× (0.741–0.749) | 0.80×, pass |

The direct-console trace measured seven new testhosts. Median command
startup to testhost execution was 0.383 s for the six direct invocations,
compared with 0.510 s in the earlier incremental wrapper trace. The runs
used different VM types, and the direct run's absolute Rmut CPU use and
wall time were higher than the preceding incremental run. The paired
results establish the gates for each hosted run. They leave the size of
the direct launcher's end-to-end contribution unconfirmed.

The trace peaked at 264.90 MiB sampled PSS in the original baseline build,
with MSBuild at 139.83 MiB and the compiler at 93.47 MiB. The same baseline
phase dominated the previous trace. Rmut needs to save about 3.75 s on
this VM to reach the wall gate, while the observed launcher interval
accounts for about 2.30 s across the six switched invocations.

## Direct-console replication and parallel pilot

All three runs completed five equivalent measured pairs and one excluded
warmup. Each verified direct-console selection and all five switched
mutants. Each was pinned to two logical CPUs. The two serial runs used
Intel Xeon 6975P-C VMs, and the parallel pilot used AMD EPYC 9V45.

| Rmut configuration | Wall ratio (range) | Peak memory ratio (range) | CPU ratio (range) |
| --- | ---: | ---: | ---: |
| [Direct launch with SDK host forwarding](https://github.com/RuiCostafrg/rmut/actions/runs/46074808513/artifacts/15235140089) | 0.803× (0.726–0.874), fail | 0.883× (0.874–0.889), fail | 0.771× (0.770–0.792), pass |
| [Direct launch and workstation GC in the build processes](https://github.com/RuiCostafrg/rmut/actions/runs/46074988025/artifacts/15234778337) | 0.910× (0.778–0.980), fail | 0.887× (0.882–0.891), fail | 0.776× (0.771–0.782), pass |
| [Two Rmut workers against the serial Stryker reference](https://github.com/RuiCostafrg/rmut/actions/runs/46075358751/artifacts/15234870897) | 0.740× (0.703–0.818), fail | 0.785× (0.784–0.793), pass | 0.779× (0.754–0.806), inconclusive |

The workstation GC trial failed to reduce build memory. The sampled compiler
at the peak used 95.67 MiB PSS with workstation GC and 94.33 MiB in the
direct-launch replication. The experiment was reverted. The parallel
pilot retained workstation GC and used a different CPU model. Its two
pairs of mutant testhosts overlapped by 1.01 s and 0.93 s, respectively,
and the trace showed seven distinct testhosts. The pilot confirms overlapping
execution in private workspaces. The parallel gate workload uses two
workers on both tools.

## Matched two-worker runs

Both tools had two workers on two pinned logical CPUs. Each run used an
Intel Xeon 6975P-C VM with starting loads of 1.67 and 2.11 respectively.
All five measured pairs and the excluded warmup agreed on every mutation
and used the direct console for all Rmut mutants.

| Rmut configuration | Wall ratio (range) | Peak memory ratio (range) | CPU ratio (range) |
| --- | ---: | ---: | ---: |
| [Control first, then two workers](https://github.com/RuiCostafrg/rmut/actions/runs/46076076304/artifacts/15235636478) | 0.728× (0.701–0.803), fail | 0.746× (0.734–0.763), pass | 0.746× (0.735–0.751), pass |
| [Control overlaps the first mutant](https://github.com/RuiCostafrg/rmut/actions/runs/46076429944/artifacts/15234744786) | 0.631× (0.599–0.687), fail | 0.746× (0.735–0.762), pass | 0.748× (0.745–0.755), pass |

The latter run measured Rmut at 8.64 s median wall time (7.94–8.69 s)
and Stryker at 12.90 s (12.65–13.70 s). This leaves a 2.19 s gap
between the Rmut median and half the Stryker median. The diagnostic
confirmed three waves of two distinct testhosts after the original
baseline, with the paired testhosts overlapping by 0.91–1.04 s. The
highest sampled PSS was again in the original build, at 306.08 MiB.
The measured CPU use of 11.44 CPU-s implies a lower bound of 5.72 s
on two logical CPUs even if all work were parallelizable.

## Private Roslyn compiler

The private compiler runs across the two SDK builds under a unique pipe
name, while each test invocation retains its own testhost. Both hosted
runs used two workers per tool on two pinned CPUs. Five measured pairs and
one excluded warmup matched every mutant identity and outcome in each run.

| Revision and host | Wall ratio (range) | Peak memory ratio (range) | CPU ratio (range) |
| --- | ---: | ---: | ---: |
| [Initial, AMD EPYC 7763](https://github.com/RuiCostafrg/rmut/actions/runs/46102611817/artifacts/15246327824) | 0.555× (0.531–0.561), fail | 0.727× (0.719–0.787), pass | 0.590× (0.583–0.591), pass |
| [Clean-fallback revision, AMD EPYC 9V45](https://github.com/RuiCostafrg/rmut/actions/runs/46102735349/artifacts/15245498816) | 0.489× (0.463–0.499), pass | 0.728× (0.723–0.803), inconclusive | 0.578× (0.575–0.581), pass |

The clean-fallback revision finished in 6.49 s (6.38–6.54 s) against
Stryker's 13.16 s (13.00–14.11 s). Four memory pairs were at or below
0.735×. The last pair was 0.803×, so no run passed all three gates.
The initial run had an analogous final-pair increase from roughly
209 MiB to 224 MiB in Rmut. The sources of those individual cgroup peak
values are unverified.

The Roslyn logs show three successful server compilation requests and
the trace shows a single server PID, no standalone compiler, and a clean
exit before the switched tests. The second build's Csc task took 0.163 s
on the newer AMD VM. The sampled memory peak shifted to the original test
while the compiler server remained idle. The server accounted for
124.80 MiB PSS out of 301.45 MiB total sampled PSS in that run.
The tests and profiler each use separate processes. The untraced memory
gate uses cgroup accounting.

A [compact-heap trial](https://github.com/RuiCostafrg/rmut/actions/runs/46103175410/artifacts/15246058100)
on an Intel Xeon Platinum 8573C VM also matched all five pairs. Its median
ratios were 0.554× wall (0.523–0.560, fail), 0.815× peak memory
(0.812–0.845, fail), and 0.588× CPU (0.584–0.600, pass). The diagnostic
peak was 332.49 MiB PSS during the original build. The trial used a
different CPU model, so it cannot isolate the effect of the GC setting.
The setting was reverted because this run did not demonstrate a memory
gate improvement.
