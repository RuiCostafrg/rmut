# Rmut implementation plan

Build a C# mutation tester delivered as the NuGet .NET tool Rmut, with command name rmut. Start with SDK-style projects and one xUnit test project referencing one production project. Record the SDK, runner and platforms actually verified. Oximut’s [implementation plan](https://github.com/RuiCostafrg/oximut/blob/main/PLAN.md) supplies the milestone sequence. Roslyn, MSBuild and dotnet test supply the .NET implementation.

## First delivery and parallel ownership

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
