using Rmut.Cli;
using Xunit;

namespace Rmut.Cli.Tests;

public sealed class MutationRunnerTests
{
    private const string Source = """
        // A supplementary character 😀 and operators + && outside any expression.
        public static class Calculator
        {
            public static int Tested() => 3 + 2;
            public static int Untested() => 3 + 2;
            public const int Constant = 3 * 0;
        }
        """;

    [Fact]
    public async Task RunReportsKillsSurvivorsAndBuildFailuresWithoutChangingInputs()
    {
        using var fixture = new Fixture("Assert.Equal(5, Calculator.Tested());");
        var reference = await fixture.RunAsync();
        var report = await fixture.RunNativeAsync();

        Assert.Equal(reference.Baseline.Outcome, report.Baseline.Outcome);
        Assert.Equal(reference.Mutants.Select(Identity), report.Mutants.Select(Identity));

        Assert.Equal(Outcome.Passed, report.Baseline.Outcome);
        Assert.Collection(report.Mutants,
            result => Assert.Equal(Outcome.Killed, result.Outcome),
            result => Assert.Equal(Outcome.Survived, result.Outcome),
            result => Assert.Equal(Outcome.BuildFailed, result.Outcome));
        Assert.All(report.Mutants, result => Assert.Equal("prod/Calculator.cs", result.Path));
        fixture.AssertInputsUnchanged();
    }

    [Fact]
    public async Task SwitchedExecutionMatchesFreshWithInitializationAndIsolatedState()
    {
        using var fixture = new Fixture("""
            const string marker = "mutation-test-marker";
            Assert.False(System.IO.File.Exists(marker));
            System.IO.File.WriteAllText(marker, "");
            var values = new int[32];
            System.Threading.Tasks.Parallel.For(0, values.Length, i =>
                values[i] = Calculator.Add(3, 2));
            Assert.All(values, value => Assert.Equal(5, value));
            Assert.Equal(5, Calculator.Initialized);
            Assert.Equal(1, Calculator.Next());
            """, """
            public static class Calculator
            {
                public static readonly int Initialized = Add(3, 2);
                private static int calls;
                public static int Add(int left, int right) => left + right;
                public static bool Untested(int value) => value > 0;
                public static int Next() => System.Threading.Interlocked.Increment(ref calls);
                public static readonly int UnsupportedInitializer = 4 + 2;
                public const int UnsupportedConstant = 3 * 0;
            }
            """);
        var log = new List<string>();
        var fresh = await fixture.RunNativeAsync();
        var switched = await fixture.RunNativeAsync(switched: true, log: log.Add);

        Assert.Equal(Outcome.Passed, switched.Baseline.Outcome);
        Assert.Equal(fresh.Mutants.Select(Identity), switched.Mutants.Select(Identity));
        Assert.Collection(switched.Mutants,
            mutant => Assert.Equal(Outcome.Killed, mutant.Outcome),
            mutant => Assert.Equal(Outcome.Survived, mutant.Outcome),
            mutant => Assert.Equal(Outcome.Survived, mutant.Outcome),
            mutant => Assert.Equal(Outcome.BuildFailed, mutant.Outcome));
        Assert.Equal(new[]
        {
            $"Switched execution {switched.Mutants[0].Id}",
            $"Switched execution {switched.Mutants[1].Id}"
        }, log.Where(line => line.StartsWith("Switched execution ", StringComparison.Ordinal)));
        fixture.AssertInputsUnchanged();
    }

    [Fact]
    public async Task FailedSwitchedBuildFallsBackToFreshForTheWholeBatch()
    {
        using var fixture = new Fixture("Assert.Equal(5, Calculator.Tested());", """
            public static class Calculator
            {
                public static int Tested() => 3 + 2;
                public static int Untested() => 3 * 0;
            }
            """);
        var log = new List<string>();
        var fresh = await fixture.RunNativeAsync();
        var switched = await fixture.RunNativeAsync(switched: true, log: log.Add);

        Assert.Equal(Outcome.Passed, switched.Baseline.Outcome);
        Assert.Equal(fresh.Mutants.Select(Identity), switched.Mutants.Select(Identity));
        Assert.Contains("Switched build unavailable. Using fresh execution.", log);
        Assert.Collection(switched.Mutants,
            mutant => Assert.Equal(Outcome.Killed, mutant.Outcome),
            mutant => Assert.Equal(Outcome.BuildFailed, mutant.Outcome));
    }

    [Fact]
    public async Task ConsumerEnvironmentTypeCannotInterceptTheMutationSelector()
    {
        using var fixture = new Fixture("Assert.Equal(5, Calculator.Tested());", """
            #pragma warning disable CS0436
            namespace System
            {
                public static class Environment
                {
                    public static string GetEnvironmentVariable(string name) => "";
                }
            }

            public static class Calculator
            {
                public static int Tested() => 3 + 2;
            }
            """);
        var log = new List<string>();
        var report = await fixture.RunNativeAsync(switched: true, log: log.Add);

        Assert.Equal(Outcome.Passed, report.Baseline.Outcome);
        Assert.Equal(Outcome.Killed, Assert.Single(report.Mutants).Outcome);
        Assert.DoesNotContain(log, line => line.StartsWith("Switched execution ", StringComparison.Ordinal));
    }

    private static object Identity(MutantResult mutant) =>
        new { mutant.Id, mutant.Path, mutant.Line, mutant.Original, mutant.Replacement, mutant.Outcome };

    [Fact]
    public async Task FailingBaselineStopsBeforeRunningMutants()
    {
        using var fixture = new Fixture("Assert.Equal(99, Calculator.Tested());");
        var report = await fixture.RunNativeAsync();

        Assert.Equal(Outcome.TestFailed, report.Baseline.Outcome);
        Assert.Empty(report.Mutants);
        fixture.AssertInputsUnchanged();
    }

    [Fact]
    public async Task UnsupportedConditionalCompilationDoesNotProduceFalseSurvivors()
    {
        using var fixture = new Fixture("Assert.Equal(5, Calculator.Tested());", Source + """

            #if false
            public class Disabled { public int Add() => 3 + 2; }
            #endif
            """);
        var report = await fixture.RunNativeAsync();

        Assert.Equal(Outcome.Passed, report.Baseline.Outcome);
        Assert.Equal(Outcome.RunnerError, report.Error?.Outcome);
        Assert.Empty(report.Mutants);
        fixture.AssertInputsUnchanged();
    }

    [Fact]
    public async Task TestHostExitIsARunnerError()
    {
        using var fixture = new Fixture("System.Environment.Exit(42);");
        var report = await fixture.RunNativeAsync();

        Assert.Equal(Outcome.RunnerError, report.Baseline.Outcome);
        Assert.Empty(report.Mutants);
        fixture.AssertInputsUnchanged();
    }

    [Fact]
    public async Task DeadlineExpiryIsATimeout()
    {
        using var fixture = new Fixture("Assert.Equal(5, Calculator.Tested());");
        var report = await fixture.RunNativeAsync(timeout: TimeSpan.FromMilliseconds(1));

        Assert.Equal(Outcome.TimedOut, report.Baseline.Outcome);
        Assert.Empty(report.Mutants);
        fixture.AssertInputsUnchanged();
    }

    [Fact]
    public async Task CancelingAnActiveRunStopsItAndPreservesInputs()
    {
        using var cancellation = new CancellationTokenSource();
        using var markerDirectory = new TemporaryDirectory();
        var marker = Path.Combine(markerDirectory.Directory.FullName, "started");
        using var fixture = new Fixture($$"""
            System.IO.File.WriteAllText({{System.Text.Json.JsonSerializer.Serialize(marker)}}, "");
            System.Threading.Thread.Sleep(System.TimeSpan.FromMinutes(10));
            """);
        var run = fixture.RunNativeAsync(cancellation.Token);
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            while (!File.Exists(marker))
            {
                if (run.IsCompleted)
                {
                    var report = await run;
                    Assert.Fail($"Run ended before the test started: {report.Baseline}");
                }
                await Task.Delay(50, deadline.Token);
            }
        }
        finally
        {
            cancellation.Cancel();
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await run.WaitAsync(TimeSpan.FromSeconds(15)));
        fixture.AssertInputsUnchanged();
    }

    private sealed class Fixture : IDisposable
    {
        private readonly DirectoryInfo root = Directory.CreateTempSubdirectory("rmut-fixture-");
        private readonly RunOptions options;
        private readonly Dictionary<string, string> inputs;

        public Fixture(string assertion, string source = Source)
        {
            inputs = new()
            {
                ["Directory.Build.props"] = """
                    <Project>
                      <PropertyGroup>
                        <TargetFramework>net10.0</TargetFramework>
                        <NuGetAudit>false</NuGetAudit>
                      </PropertyGroup>
                    </Project>
                    """,
                ["prod/Calculator.csproj"] = """<Project Sdk="Microsoft.NET.Sdk" />""",
                ["prod/Calculator.cs"] = source,
                ["test/Calculator.Tests.csproj"] = """
                    <Project Sdk="Microsoft.NET.Sdk">
                      <PropertyGroup><IsTestProject>true</IsTestProject></PropertyGroup>
                      <ItemGroup>
                        <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
                        <PackageReference Include="xunit" Version="2.9.3" />
                        <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" />
                        <ProjectReference Include="../prod/Calculator.csproj" />
                      </ItemGroup>
                    </Project>
                    """,
                ["test/CalculatorTests.cs"] = $$"""
                    using Xunit;
                    public sealed class CalculatorTests
                    {
                        [Fact]
                        public void Check()
                        {
                            {{assertion}}
                        }
                    }
                    """
            };

            foreach (var (relative, content) in inputs)
            {
                var path = Path.Combine(root.FullName, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, content);
            }

            options = new RunOptions(Path.Combine(root.FullName, "prod/Calculator.csproj"),
                Path.Combine(root.FullName, "test/Calculator.Tests.csproj"), null);
        }

        public Task<RunReport> RunAsync(CancellationToken cancellation = default, TimeSpan? timeout = null) =>
            new ManagedReferenceRunner(options, timeout ?? TimeSpan.FromMinutes(2), _ => { }).RunAsync(cancellation);

        public Task<RunReport> RunNativeAsync(CancellationToken cancellation = default, TimeSpan? timeout = null,
            bool switched = false, Action<string>? log = null) =>
            NativeRunner.RunAsync(options with { Switched = switched }, timeout ?? TimeSpan.FromMinutes(2),
                log ?? (_ => { }), cancellation);

        public void AssertInputsUnchanged()
        {
            Assert.Equal(inputs.Count, root.GetFiles("*", SearchOption.AllDirectories).Length);
            foreach (var (path, content) in inputs)
                Assert.Equal(content, File.ReadAllText(Path.Combine(root.FullName, path)));
        }

        public void Dispose() => root.Delete(recursive: true);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public DirectoryInfo Directory { get; } = System.IO.Directory.CreateTempSubdirectory("rmut-marker-");
        public void Dispose() => Directory.Delete(recursive: true);
    }
}
