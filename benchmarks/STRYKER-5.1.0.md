# Stryker.NET 5.1.0 feature inventory

The reference is the released `dotnet-stryker` NuGet tool **5.1.0** and [its tagged source](https://github.com/stryker-mutator/stryker-net/tree/7052fa6c646a6bdfb143608e695d11542e2be815). Rmut refers to the managed prototype. The [baseline log](BASELINE.md) records which comparisons have actually run.

## Language, projects, and runners

| Reference behavior | Fixture or validation needed | Rmut status |
| --- | --- | --- |
| Mutates C# source, including language-version selection. F# support was abandoned. VB is outside the C# mutation path. | SDK C# conformance, language-version matrix, unsupported-project exit | SDK C# exercised by the conformance workload. Other cases pending. |
| Resolves source, test-project, and solution contexts. Selects a production project from references and can combine multiple test projects. | One reference in both conformance fixtures, ambiguous references, >1 test project, solution context | Explicit pair of project paths. Automatic resolution and multiple test projects are gaps. |
| Builds modern SDK-style projects and .NET Framework projects. The latter require solution context and the appropriate Windows MSBuild and NuGet setup. Selects a valid target for multi-target projects. | SmartEnum multi-target, SDK/TFM matrix, .NET Framework on Windows | Single production TFM. Framework and multi-target support pending. |
| VSTest and Microsoft.Testing.Platform (MTP). Relevant adapters include xUnit, NUnit, MSTest and MTP-native TUnit. Test-case filtering can differ by runner. | xUnit/VSTest in conformance, adapter matrix, MTP filtering and fallback paths | VSTest xUnit exercised. Other adapters, MTP and test filters pending. |
| Control-flow, asynchronous, compiler-generated and linked source can affect injection and build rollback. | Linked files in FluentValidation, async/static and compile-rollback conformance cases | MSBuild Compile item discovery. Stryker injection and rollback semantics pending. |

Sources include [operating modes](https://github.com/stryker-mutator/stryker-net/blob/7052fa6c646a6bdfb143608e695d11542e2be815/docs/operating-modes.md), [configuration](https://github.com/stryker-mutator/stryker-net/blob/7052fa6c646a6bdfb143608e695d11542e2be815/docs/configuration.md), [language history](https://github.com/stryker-mutator/stryker-net/blob/7052fa6c646a6bdfb143608e695d11542e2be815/docs/technical-reference/project-components.md).

## Mutation operators

Each row names a 5.1.0 `ignore-mutations` family. A fixture name links the family to current differential coverage. The remaining rows identify fixture inputs still needed.

| Family | Fixture or input | Rmut status |
| --- | --- | --- |
| `arithmetic` | `shared-operators`, `relational-gap`; also exercise subtraction, division, remainder and string concatenation | Partial. Fixtures cover addition and multiplication. |
| `equality` | `shared-operators` (`==`), `relational-gap` (`>`); add other relational operators and relational patterns | Partial. Rmut emits one replacement per comparison. |
| `logical` | `shared-operators`, `operator-exclusion-gap`; add `\|\|`, logical XOR and binary patterns | Partial. |
| `bitwise` | AND, OR, XOR and signed/unsigned shifts | Gap. |
| `boolean` | Both Boolean literals | Gap. |
| `assignment` | Compound arithmetic, bitwise and coalescing assignment | Gap. |
| `statement` | Replaceable statement and compile rollback | Gap. |
| `block` | Block deletion with value and void return paths | Gap. |
| `initializer` | Arrays, collection/object initializers and construction | Gap. |
| `unary` | Sign, Boolean negation and other prefix operators | Gap. |
| `update` | Pre/post increment and decrement | Gap. |
| `checked` | Checked/unchecked expressions and blocks | Gap. |
| `linq` | Enumerable predicate and aggregate substitutions, including filtering individual LINQ names | Gap. |
| `string` | String literals, interpolated strings and empty strings | Gap. |
| `stringmethod` | Case conversion, string predicates and constant replacements | Gap. |
| `regex` | Anchors, classes, groups, lookaround and quantifiers | Gap. |
| `math` | Math method substitutions | Gap. |
| `nullcoalescing` | Left/right replacement and coalescing behavior | Gap. |
| `conditional` | Ternary expressions, condition negation and patterns | Gap. |
| `collectionexpression` | Collection expressions and their elements | Gap. |

The reference supports Basic, Standard (default), Advanced and Complete levels. Mutators may emit multiple replacements at one location. Compare the individual replacements and compile-rejected mutants. Rmut currently has a fixed operator set.

Sources include [mutator enum](https://github.com/stryker-mutator/stryker-net/blob/7052fa6c646a6bdfb143608e695d11542e2be815/src/Stryker.Abstractions/Mutator.cs), [operator docs](https://github.com/stryker-mutator/stryker-net/blob/7052fa6c646a6bdfb143608e695d11542e2be815/docs/mutations.md), [regex operator docs](https://github.com/stryker-mutator/stryker-net/blob/7052fa6c646a6bdfb143608e695d11542e2be815/docs/regex-mutations.md).

## Execution, selection, and reporting

| Reference behavior | Fixture or validation needed | Rmut status |
| --- | --- | --- |
| Reads JSON/YAML config, `--config-file`, `init`, command-line overrides. | Config syntax, precedence, invalid options and generated config | Explicit CLI flags only. |
| Build selection via `solution`, `project`, `test-projects` (`--test-project`), `target-framework`, `configuration`, `language-version`, `msbuild-path`. | SDK and Framework project matrix above | Partial explicit selection. |
| Mutant selection via `mutate` glob/pattern ranges, `ignore-mutations`, `ignore-methods`, and inline disable/restore, once and reasons. | `operator-exclusion-gap`; add range, method, inline, and excluded-file cases | Gap. |
| `coverage-analysis` modes off, all, perTest and perTestInIsolation. Per-test selection, explicit NoCoverage, static handling. | `no-coverage-gap`; add tests whose coverage overlaps and static initialization cases | Runs all tests for every mutant. |
| `concurrency`, early bail, VSTest mutant batching, timeouts from baseline test timing and configuration `additional-timeout` / `timeout-ratio`. | Parallel/bail/mixing matrix; slow and hanging mutations | Serial runs with fixed process timeout. |
| Incremental Git `since` with target and ignored change globs. Experimental baseline reuses results with fallback versions and Disk, Dashboard, Azure Files, and S3 storage. | Seed, source edit, test edit and unchanged repeat for each supported cache provider | Gap. The harness can run explicitly configured seed and measured stages once Rmut exposes incremental arguments. |
| Initial failing-test handling via `break-on-initial-test-failure`; score thresholds high/low/break. Reference exits 0 for success, 1 for general errors and 2 for break threshold. | Baseline fail, mixed initial outcomes and threshold boundary matrix | Stops on failing baseline; usage exit 2 and run error exit 1; no score thresholds. |
| JSON mutation testing report includes locations, mutations, statuses, reasons, coverage/killing test IDs and test metadata. Statuses include Killed, Survived, NoCoverage, Timeout, CompileError, Ignored, Pending and RuntimeError. | Conformance and exact JSON schema checks including error/timeout/ignored cases | Own JSON schema with a subset of statuses. Differential adapter compares edits and outcomes. |
| HTML, progress, dots, cleartext, cleartexttree, Dashboard, realtime Dashboard, Markdown, GitLab, baseline and all reporter options. `open-report`, `report-file-name`, `output`. | Reporter artifact/snapshot matrix and dashboard test endpoint | Console text and own JSON only. |
| Project info name/module/version, Dashboard endpoint/credential, optional SourceLink. Baseline-specific Azure SAS/fileshare URL and S3 endpoint/region/bucket. | Local mock endpoints plus controlled provider integration | Gap. |
| `verbosity`, `log-to-file`, `diag`, `skip-version-check`. | CLI diagnostics and error fixtures | No corresponding configuration. |

Sources include [configuration](https://github.com/stryker-mutator/stryker-net/blob/7052fa6c646a6bdfb143608e695d11542e2be815/docs/configuration.md), [CLI inputs](https://github.com/stryker-mutator/stryker-net/blob/7052fa6c646a6bdfb143608e695d11542e2be815/src/Stryker.CLI/Stryker.CLI/CommandLineConfig/CommandLineConfigReader.cs), [reporter enum](https://github.com/stryker-mutator/stryker-net/blob/7052fa6c646a6bdfb143608e695d11542e2be815/src/Stryker.Abstractions/Options/Reporter.cs), [exit codes](https://github.com/stryker-mutator/stryker-net/blob/7052fa6c646a6bdfb143608e695d11542e2be815/src/Stryker.Configuration/ExitCodes.cs).

## Pinned upstream comparisons

The [hosted conformance run at `7f00fb5`](https://github.com/RuiCostafrg/rmut/actions/runs/46043654813)
installed both tools and captured the two comparisons. Download
`conformance-smart-enum` and `conformance-fluent-validation` for the raw reports,
tool logs, package fingerprints and normalized differences.

- **SmartEnum.** Both baselines passed. Stryker discovered 79 tests and reported
  396 edits. Rmut reported 63 edits, each with a Stryker counterpart. The 333
  Stryker-only identities establish a mutation inventory gap before test outcome
  comparisons. Of the 63 shared edits, 61 outcomes match. In `SmartEnum.cs`,
  changing `==` to `!=` at UTF-16 offset 16521 causes recursive operator calls
  and a test-host stack overflow in Rmut (`runnerError`). Stryker reports
  `timedOut` for the same edit. The pinned [Stryker VsTest runner](https://github.com/stryker-mutator/stryker-net/blob/7052fa6c646a6bdfb143608e695d11542e2be815/src/Stryker.TestRunner.VsTest/VsTestRunner.cs#L189-L222)
  classifies an uncancelled session with fewer test results than expected as a
  timeout. That status alone cannot distinguish a crash from deadline expiry.
  In `SmartFlagEngine.cs`, changing `-` to `+`
  at offset 7299 survives Rmut's source rebuild, while Stryker reports a compile
  error. The neighboring LINQ mutation replaces `enumList.Count` with
  `enumList.Sum` and also receives a compile error. Debug logging is enabled
  for this comparison to identify which diagnostics caused Stryker to roll
  back each edit. Track crash classification and compile rollback separately
  from the mutation inventory gap.
- **FluentValidation.** The explicit project reference let Stryker select and
  build the library and discover 843 tests. It failed to build the mutated
  assembly in generated `AbstractValidator<T>` code and could not restore it
  to a buildable state. Stryker exited zero without a mutation report. Rmut
  passed the baseline and stopped at conditional compilation in
  `DefaultValidatorExtensions.cs`. Rmut needs discovery using the evaluated
  target's preprocessor symbols. The Stryker generator failure needs an
  upstream reproduction or a reference version that can run the pinned
  source and generator unchanged.

Both workloads remain compare-only. Green capture jobs indicate that the
workflow saved evidence. Neither workload established parity, so no elapsed
time, CPU or memory comparison was collected.
