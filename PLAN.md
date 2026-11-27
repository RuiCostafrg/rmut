# Rmut implementation plan

Rmut aims to replace Stryker.NET with a Rust mutation engine delivered as the NuGet tool `rmut`. The existing C# implementation provides an executable correctness reference. Rust will own mutant generation, scheduling, resource accounting and report assembly. Keep managed code at the boundaries that require MSBuild, test discovery or the .NET tool host.

## Release acceptance

Pin [Stryker.NET 5.1.0](https://github.com/stryker-mutator/stryker-net/releases/tag/dotnet-stryker%405.1.0) for the first comparison. Inventory its languages, mutation operators, project and runner support, config and CLI options, coverage and selection, incremental runs, thresholds, exit codes, dashboards and report formats. Track each supported behavior with a fixture and differential result. Document any gap before claiming parity.

Use a frozen corpus of small and large real projects plus conformance fixtures. Run both installed release tools on the same machine with identical SDK, tests, eligible mutants, worker budget and cache conditions. Compare mutant identities, outcomes and exclusions before comparing costs. Report mismatches and failures. Split clean and incremental runs, alternate tool order, record warmups, at least five measured pairs, raw results and variation. Include restore/build costs consistently.

For each agreed workload, require median elapsed time at most 0.50 times Stryker.NET, peak process-tree memory at most 0.80 times, and total user plus system CPU seconds at most 0.80 times. Use isolated Linux cgroups for peak memory and CPU accounting including short-lived child processes. Record average CPU utilization separately. Report an inconclusive result if noise crosses a gate. These are release targets pending measurement.

## Next implementation tasks

1. Build a Rust vertical slice that discovers mutations and drives the existing fixture through the installed NuGet tool. Check per-mutant outcomes against the managed reference and ship the native executable for each validated runtime.
2. Build the Stryker.NET feature inventory, differential fixtures and reproducible process-tree benchmark harness. Establish baseline data before optimizing.
3. Close repository CI and release gaps against typescript-crap, including dependency audits, workflow lint, platform checks, coverage, mutation checks, release provenance and verification of the published package. Keep GitHub protections active throughout.

## Completed managed prototype

1. **Engine** owns src/Rmut.Engine and tests/Rmut.Engine.Tests. Expose Rmut.Engine.MutationEngine.Discover(string path, string source) returning an ordered sequence of mutants with Id, Path, Line, Original, Replacement and MutatedSource properties. Use Roslyn to replace exactly one supported arithmetic, equality, comparison or Boolean token per candidate. Verify stable IDs, one-based locations, comments, strings and distinct mutations on one line.
2. **Runner** owns src/Rmut.Cli and tests/Rmut.Cli.Tests. Reference ../Rmut.Engine/Rmut.Engine.csproj. Implement rmut run --project PATH --test-project PATH and optional --json PATH. Build and run a clean baseline, then run each mutant in an isolated copy. Preserve relative project references and original files. Distinguish test failures, build failures, timeouts and runner errors. Verify killed and survived fixtures plus failing baseline and cancellation. Set PackAsTool, PackageId Rmut and ToolCommandName rmut on the CLI project.
3. **Distribution and integration** owns fixtures, scripts, README and .github/workflows. Add a small consumer fixture, CI build/tests and a Release pack, local-feed tool install and end-to-end invocation. Configure version-tagged NuGet publication only after tests and package smoke checks pass. Document feed identity, credential setup, version mapping and the exact install command. A real release requires the maintainer to configure the NuGet identity and cut a tag.

Integrate engine, runner, then distribution. The first delivery passes when an installed package reports the fixture’s expected kills and survivors from outside the repository. Check all three components together before tagging.

## Follow-on milestones

| Milestone | Depends on | Acceptance gate |
| --- | --- | --- |
| M2 instrumented execution | Fresh runner | Compile switched mutants once. Compare every outcome against isolated fresh execution on static state, initialization and parallel tests. Fall back on unsupported constructs. |
| M3 test selection | M2 and runner discovery | Attribute baseline hits to tests. Run the full suite on ambiguous attribution and compare outcomes with selection disabled. |
| M4 cache | Stable IDs and reports | Separate compilation and outcome caches. Validate baseline each run. Exercise invalidation for source, tests, references, SDK, settings and declared external inputs. |
| M5 resource limits | Reliable cancellation | Bound concurrent workers and process-tree resources. Exercise hung tests and memory pressure. Report exhaustion separately from kills. |
| M6 release evaluation | M2–M5 | Compare fresh, optimized and Stryker.NET on matched fixtures and operators. Record repeated clean and incremental timings, peak memory, mutant counts, outcome differences and environment. Use evidence to choose defaults and document compatibility before stable NuGet release. |
