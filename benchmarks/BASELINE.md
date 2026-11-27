# Reference baseline

## Local verification

The harness unit tests cover distinct edits on the same source line, UTF-16 offsets, excluded files, identity and outcome mismatches, and gate-crossing variation.

The installed tools were Stryker.NET 5.1.0 and the managed Rmut prototype from commit `afd4cdd`, packaged as `0.0.1-benchmark`. The local ARM64 Termux/proot host did not delegate writable cgroup v2 controllers. The harness stopped at cgroup setup and produced no performance data.

A correctness run with the Linux ARM64 SDK 10.0.404 needed `DOTNET_GCRegionRange=400000000` for Roslyn to build on the host. Both tools then hit VSTest's ARM64 dotnet-host lookup error under proot. See [the upstream VSTest issue](https://github.com/microsoft/vstest/issues/16446).

## Hosted conformance

The Stryker.NET conformance workflow runs the four differential cases using installed NuGet tools on hosted Ubuntu x64. Each matrix job exports its version information, reports, tool logs and differential as an artifact. The real-project and incremental workloads remain pending a compatible candidate and Linux benchmark host.
