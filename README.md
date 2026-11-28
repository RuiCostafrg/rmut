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

## Verify a local package

Run these commands from the repository root with the .NET 10 SDK, Bash, and jq.

```sh
feed="$(mktemp -d)"
dotnet pack src/Rmut.Cli/Rmut.Cli.csproj --configuration Release \
  -p:PackageVersion=0.0.0-ci --output "$feed"
bash scripts/smoke-package.sh "$feed" 0.0.0-ci
```

The smoke test installs the package from the local feed and runs it from a temporary consumer directory. The fixture expects `+` changed to `-` to be killed and `>` changed to `>=` to survive.

The installed-package smoke test passed with .NET SDK 10.0.404, xUnit 2.9.3, and VSTest 18.12.0 on Linux ARM64. The local PRoot environment required setting `VSTestConsolePath` to the 18.12.0 console from `Microsoft.TestPlatform.CLI` to work around SDK-bundled VSTest host detection. [VSTest host detection issue](https://github.com/microsoft/vstest/issues/16446)

## CI checks

The required `verify` check joins workflow lint, NuGet advisory audit, line coverage,
mutation testing, and build and installed-tool tests on Linux x64 and ARM64,
macOS ARM64, and Windows x64. The advisory audit checks direct and transitive
packages and also runs weekly. CI retains the Cobertura and Stryker.NET reports.
Stryker.NET 5.1.0 mutates the managed discovery engine, and the installed-tool
fixture checks CLI behavior for one killed mutant and one survivor.

## Publish a release

The GitHub Actions workflow publishes package `Rmut` to the NuGet.org V3 feed. A tag of the form `vMAJOR.MINOR.PATCH` sets the NuGet version by dropping the leading `v`. For example, `v0.1.0` publishes `Rmut` version `0.1.0`. The publish job uses the package that passed the build, tests, and installed-tool
smoke check. GitHub Actions attests the original package before publishing. After
NuGet.org indexes the release, CI verifies the attestation and NuGet signature,
checks that every package file matches the attested payload, and smoke-tests the
download from the public feed.

Before cutting the first tag, configure a NuGet.org account authorized to publish `Rmut`. In NuGet.org, add a GitHub Actions trusted publishing policy for repository `RuiCostafrg/rmut`, workflow file `ci.yml`, and environment `nuget`. Select the user or organization that will own the package. Scope the policy to `Rmut` and allow publishing a new package for the first release and new versions for subsequent releases. Create the `nuget` environment in GitHub and set its `NUGET_USER` secret to the account's NuGet.org profile name. The workflow exchanges its GitHub OIDC token for a short-lived NuGet API key. [NuGet trusted publishing setup](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing)

Created in [T3 Code](https://t3.codes).
