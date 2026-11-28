<img src="assets/icon.svg" width="64" height="64" alt="">

# Rmut

Rmut is a .NET tool that checks how well your tests detect changes to C# code. It runs the original tests first, then runs them with one operator changed at a time. A mutant is killed when the change causes a test to fail. It survives when the tests still pass.

## Install and run

Install the .NET 10 SDK, then install the `Rmut` package from NuGet.org once a release is available.

```sh
dotnet tool install --global Rmut --source https://api.nuget.org/v3/index.json
```

From your project directory, point `rmut` at the production and xUnit test projects.

```sh
rmut run --project src/MyApp/MyApp.csproj --test-project tests/MyApp.Tests/MyApp.Tests.csproj
```

Add `--json report.json` to save the baseline and individual mutant outcomes.

### Execution modes

Fresh execution is the default. It copies the workspace, restores and builds the test project and its references, and runs the full suite for each mutant.

Use `--execution switched` to opt into compiling supported mutations together. Rmut currently switches single-line binary expressions that form the entire body of an expression-bodied `int` or `bool` method, with operands drawn from `int` parameters and decimal integer literals. A separate test process selects one mutation at startup and runs the full suite in a private copy of the build. Unsupported mutations, failed instrumented builds or control runs, and indeterminate switched results use fresh execution.

For single-target VSTest projects with a self-contained test configuration, pass
`--switched-test-assembly bin/Debug/net10.0/Example.Tests.dll` to run the switched
control and mutants directly from a built DLL. The path is relative to the test
project directory and must point to a DLL written by the build. Use this option when running the DLL directly executes the
same suite as running the project. Projects that configure runsettings, filters,
adapters, or test targets through MSBuild should use project-based execution.
The original baseline and fresh fallback still run through the test project.

Use switched mode with relocatable SDK-style builds and tests that exercise runtime behavior. Use fresh execution if your build inspects source bodies, embeds absolute workspace paths, or your tests rely on uninstrumented source or assembly contents.

## Verify a local package

Run these commands from the repository root with the .NET 10 SDK, Bash, and jq.

```sh
feed="$(mktemp -d)"
dotnet pack src/Rmut.Cli/Rmut.Cli.csproj --configuration Release \
  -p:PackageVersion=0.0.0-ci --output "$feed"
bash scripts/smoke-package.sh "$feed" 0.0.0-ci
```

The smoke test installs the package from the local feed and runs it from a temporary consumer directory. It compares fresh and switched execution and checks that both mutants used the switched build. The fixture expects `+` changed to `-` to be killed and `>` changed to `>=` to survive.

The installed-package smoke test passed with .NET SDK 10.0.404, xUnit 2.9.3, and VSTest 18.12.0 on Linux ARM64. The local PRoot environment required setting `VSTestConsolePath` to the 18.12.0 console from `Microsoft.TestPlatform.CLI` to work around SDK-bundled VSTest host detection. GitHub Actions is configured for Ubuntu 24.04. [VSTest host detection issue](https://github.com/microsoft/vstest/issues/16446)

## Publish a release

The GitHub Actions workflow publishes package `Rmut` to the NuGet.org V3 feed. A tag of the form `vMAJOR.MINOR.PATCH` sets the NuGet version by dropping the leading `v`. For example, `v0.1.0` publishes `Rmut` version `0.1.0`. The publish job uploads the same package that passed the build, tests, and installed-tool smoke check.

Before cutting the first tag, configure a NuGet.org account authorized to publish `Rmut`. In NuGet.org, add a GitHub Actions trusted publishing policy for repository `RuiCostafrg/rmut`, workflow file `ci.yml`, and environment `nuget`. Select the user or organization that will own the package. Scope the policy to `Rmut` and allow publishing a new package for the first release and new versions for subsequent releases. Create the `nuget` environment in GitHub and set its `NUGET_USER` secret to the account's NuGet.org profile name. The workflow exchanges its GitHub OIDC token for a short-lived NuGet API key. [NuGet trusted publishing setup](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing)

Created in [T3 Code](https://t3.codes).

### Reuse mutation outcomes

For deterministic builds and tests, opt in with `--outcome-cache PATH --cache-inputs MANIFEST`.
Use a writable cache location outside the source workspace and create its parent directory first.
The manifest is JSON with `"complete": true` and an `"externalInputs"` array of files or
directories, resolved relative to the manifest. Include every external file the build or tests
read, such as imported MSBuild settings, native libraries, test data, and user-level tool
configuration. Use an empty array only when all dependencies are covered by the workspace,
resolved NuGet packages, and the .NET installation. Leave reuse off for time-dependent tests,
network dependencies, or any build or test inputs you cannot enumerate.

Rmut always runs the unmodified baseline and rediscovers mutants. Any change to the input
fingerprint invalidates all outcomes. Only completed kills and survivors from fresh execution
are eligible. An unavailable dependency, an unresolved project reference, or a damaged cache
causes fresh execution. Cache entries are local to the workspace and can be deleted at any time.
