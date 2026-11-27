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
0.63 s across each of six switched invocations bounds the expected
opportunity at roughly 3.8 s on the traced run. Tracing materially
inflates timings, so the untraced paired measurements are the end-to-end
comparison.
