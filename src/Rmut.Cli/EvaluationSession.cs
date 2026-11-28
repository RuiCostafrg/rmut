using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace Rmut.Cli;

internal sealed record SourceFile(string Path, string Content);
internal sealed record SourceList(RunResult Result, IReadOnlyList<SourceFile> Paths, string? Selector, int SwitchedWorkers);
internal sealed record InstrumentedFile(int Index, string Content);
internal enum SwitchedBuild { Unavailable, Clean, Incremental, IncrementalWithDirectConsole }
internal sealed record SwitchedPreparation(SwitchedBuild Build, RunResult? FirstResult = null);

internal sealed class EvaluationSession : IAsyncDisposable
{
    private readonly string project;
    private readonly string testProject;
    private readonly string root;
    private readonly string? switchedTestAssembly;
    private readonly Workspace baselineWorkspace;
    private readonly TimeSpan timeout;
    private readonly bool reuseBaselineBuild;
    private readonly bool useCompilerServer;
    private CompilerServer? compilerServer;
    private Workspace? baselineBuildSnapshot;
    private readonly List<string> originals = [];
    private readonly string? selector;
    private Workspace? switchedWorkspace;
    private string? productionAssemblyName;
    private sealed record VSTestConsole(string Path, IReadOnlyDictionary<string, string> Environment);
    private VSTestConsole? vstestConsole;

    public int SwitchedWorkers { get; }

    public EvaluationSession(RunOptions options, TimeSpan timeout, CancellationToken cancellation)
    {
        project = ResolveProject(options.Project);
        testProject = ResolveProject(options.TestProject);
        root = Workspace.FindRoot(project, testProject);
        if (options.SwitchedTestAssembly is { } relativeAssembly)
        {
            var testDirectory = Path.GetDirectoryName(testProject)!;
            switchedTestAssembly = Path.GetFullPath(relativeAssembly, testDirectory);
            if (Path.IsPathRooted(relativeAssembly) || !Workspace.Contains(testDirectory, switchedTestAssembly))
                throw new IOException("The switched test assembly must be relative to and inside the test project directory.");
        }
        baselineWorkspace = new Workspace(root, cancellation);
        this.timeout = timeout;
        reuseBaselineBuild = options.ReuseBaselineBuild;
        useCompilerServer = options.UseCompilerServer;
        SwitchedWorkers = options.SwitchedWorkers;
        selector = options.Switched ? $"RMUT_MUTANT_{Guid.NewGuid():N}" : null;
    }

    public async Task<RunResult> BaselineAsync(CancellationToken cancellation)
    {
        var privateProject = baselineWorkspace.Map(testProject);
        if (useCompilerServer && !HasWorkspaceSdkOverride(privateProject, baselineWorkspace.Root))
            compilerServer = await CompilerServer.TryCreateAsync(Path.GetDirectoryName(privateProject)!,
                timeout, cancellation);
        var build = await BuildAsync(privateProject, cancellation, server: compilerServer);
        if (build.Outcome != Outcome.Passed && compilerServer is not null)
        {
            await StopCompilerServerAsync();
            using var clean = new Workspace(root, cancellation);
            baselineWorkspace.RestoreSnapshot(clean);
            build = await BuildAsync(privateProject, cancellation);
        }
        if (build.Outcome != Outcome.Passed)
            return build;

        if (reuseBaselineBuild)
        {
            try
            {
                baselineBuildSnapshot = new Workspace(baselineWorkspace.Root, cancellation, includeBuildOutputs: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The original baseline can still run, and preparation can use a clean workspace.
            }
        }

        if (baselineBuildSnapshot is null)
            await StopCompilerServerAsync();

        return await TestAsync(privateProject, cancellation);
    }

    public async Task<SourceList> DiscoverAsync(CancellationToken cancellation)
    {
        var (result, paths) = await DiscoverSourcesAsync(
            baselineWorkspace.Map(project), baselineWorkspace.Root, cancellation);
        var files = new List<SourceFile>();
        originals.Clear();
        foreach (var source in paths)
        {
            cancellation.ThrowIfCancellationRequested();
            var relativePath = Path.GetRelativePath(baselineWorkspace.Root, source);
            var originalPath = Path.Combine(root, relativePath);
            if (!File.Exists(originalPath))
                continue;

            var displayPath = relativePath.Replace(Path.DirectorySeparatorChar, '/');
            var content = await File.ReadAllTextAsync(originalPath, cancellation);
            originals.Add(originalPath);
            files.Add(new SourceFile(displayPath, content));
        }

        return new SourceList(result, files, selector, SwitchedWorkers);
    }

    public async Task<RunResult> EvaluateMutantAsync(int index, string mutatedSource, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        try
        {
            using var workspace = new Workspace(root, cancellation);
            await File.WriteAllTextAsync(workspace.Map(originals[index]), mutatedSource, cancellation);
            return await EvaluateAsync(workspace.Map(testProject), cancellation);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        {
            return new RunResult(Outcome.RunnerError, ex.Message);
        }
    }

    public async Task<SwitchedPreparation> PrepareSwitchedAsync(IReadOnlyList<InstrumentedFile> files,
        string? firstMutant, CancellationToken cancellation)
    {
        if (selector is null)
            return new(SwitchedBuild.Unavailable);

        if (baselineBuildSnapshot is { } snapshot)
        {
            try
            {
                // MSBuild and NuGet intermediates can contain absolute paths.
                // Restore the pre-test contents at the exact path where they were built.
                baselineWorkspace.RestoreSnapshot(snapshot);
                var (ready, firstResult) = await ValidateSwitchedBuildAsync(
                    baselineWorkspace, files, firstMutant, cancellation, skipRestore: true);
                if (ready)
                {
                    switchedWorkspace = baselineWorkspace;
                    return new(vstestConsole is null ? SwitchedBuild.Incremental : SwitchedBuild.IncrementalWithDirectConsole,
                        firstResult);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
            {
                // Retry from clean inputs if the incremental build cannot be used.
            }
        }

        await StopCompilerServerAsync();
        Workspace? template = null;
        try
        {
            template = new Workspace(root, cancellation);
            var (ready, firstResult) = await ValidateSwitchedBuildAsync(
                template, files, firstMutant, cancellation, skipRestore: false);
            if (!ready)
                return new(SwitchedBuild.Unavailable);

            switchedWorkspace = template;
            template = null;
            return new(SwitchedBuild.Clean, firstResult);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        {
            return new(SwitchedBuild.Unavailable);
        }
        finally
        {
            template?.Dispose();
        }
    }

    private async Task<(bool Ready, RunResult? First)> ValidateSwitchedBuildAsync(
        Workspace template, IReadOnlyList<InstrumentedFile> files, string? firstMutant,
        CancellationToken cancellation, bool skipRestore)
    {
        if (switchedTestAssembly is { } assembly && File.Exists(template.Map(assembly)))
            File.Delete(template.Map(assembly));

        foreach (var file in files)
            await File.WriteAllTextAsync(template.Map(originals[file.Index]), file.Content, cancellation);

        var build = await BuildAsync(template.Map(testProject), cancellation, rejectFrameworkTypeConflicts: true,
            skipRestore: skipRestore, server: skipRestore ? compilerServer : null);
        if (skipRestore)
            await StopCompilerServerAsync();
        if (build.Outcome != Outcome.Passed)
            return (false, null);

        // An incremental target can decide to skip compilation. Check that the
        // production DLL loaded from the test output contains this run's selector.
        if (skipRestore)
        {
            if (productionAssemblyName is null || switchedTestAssembly is null || selector is null)
                return (false, null);

            var productionAssembly = Path.Combine(Path.GetDirectoryName(template.Map(switchedTestAssembly))!,
                productionAssemblyName);
            if (!File.Exists(productionAssembly) ||
                (await File.ReadAllBytesAsync(productionAssembly, cancellation)).AsSpan()
                    .IndexOf(Encoding.Unicode.GetBytes(selector)) < 0)
                return (false, null);
        }

        // Only publish the speculative mutant result if the unselected control passes.
        using var control = new Workspace(template.Root, cancellation, includeBuildOutputs: true);
        var controlRun = TestSwitchedAsync(template, control, cancellation);
        if (firstMutant is null)
            return ((await controlRun).Outcome == Outcome.Passed, null);

        var firstRun = EvaluateSwitchedInTemplateAsync(template, firstMutant, cancellation);
        await Task.WhenAll((Task)controlRun, firstRun);
        return (await controlRun).Outcome == Outcome.Passed
            ? (true, await firstRun)
            : (false, null);
    }

    public Task<RunResult?> EvaluateSwitchedAsync(string id, CancellationToken cancellation) =>
        switchedWorkspace is { } template
            ? EvaluateSwitchedInTemplateAsync(template, id, cancellation)
            : Task.FromResult<RunResult?>(null);

    private async Task<RunResult?> EvaluateSwitchedInTemplateAsync(Workspace template, string id,
        CancellationToken cancellation)
    {
        if (selector is null)
            return null;

        try
        {
            using var workspace = new Workspace(template.Root, cancellation, includeBuildOutputs: true);
            var result = await TestSwitchedAsync(template, workspace, cancellation, new(selector, id));
            return result.Outcome is Outcome.Passed or Outcome.TestFailed ? result : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        {
            return null;
        }
    }

    private async Task StopCompilerServerAsync()
    {
        if (compilerServer is not { } server)
            return;

        await server.DisposeAsync();
        compilerServer = null;
    }

    public async ValueTask DisposeAsync()
    {
        using var baseline = baselineWorkspace;
        using var snapshot = baselineBuildSnapshot;
        try
        {
            await StopCompilerServerAsync();
        }
        finally
        {
            if (switchedWorkspace != baselineWorkspace)
                switchedWorkspace?.Dispose();
        }
    }

    private async Task<(RunResult Result, string[] Paths)> DiscoverSourcesAsync(
        string project, string workspace, CancellationToken cancellation)
    {
        var propertiesToQuery = reuseBaselineBuild
            ? "TargetFrameworks,TargetFileName,MSBuildBinPath,DOTNET_HOST_PATH,NETCoreSdkRuntimeIdentifier" : "TargetFrameworks";
        var process = await DotnetProcess.RunAsync(Path.GetDirectoryName(project)!, timeout, cancellation,
            "msbuild", project, "-nologo", $"-getProperty:{propertiesToQuery}", "-getItem:Compile");
        if (process.TimedOut)
            return (new RunResult(Outcome.TimedOut, "Production source discovery timed out."), []);
        if (process.ExitCode != 0)
            return (new RunResult(Outcome.RunnerError, process.Diagnostics), []);

        try
        {
            using var document = JsonDocument.Parse(process.Output);
            var properties = document.RootElement.GetProperty("Properties");
            var frameworks = properties.GetProperty("TargetFrameworks").GetString();
            if (reuseBaselineBuild)
            {
                var targetFile = properties.GetProperty("TargetFileName").GetString();
                productionAssemblyName = !string.IsNullOrEmpty(targetFile) && Path.GetFileName(targetFile) == targetFile
                    ? targetFile : null;
                var sdk = properties.GetProperty("MSBuildBinPath").GetString();
                var host = properties.GetProperty("DOTNET_HOST_PATH").GetString();
                var sdkRid = properties.GetProperty("NETCoreSdkRuntimeIdentifier").GetString();
                var architecture = RuntimeInformation.ProcessArchitecture.ToString();
                if (sdkRid?.EndsWith($"-{architecture}", StringComparison.OrdinalIgnoreCase) == true &&
                    sdk is not null && Path.IsPathFullyQualified(sdk) &&
                    host is not null && Path.IsPathFullyQualified(host) && File.Exists(host) &&
                    Path.GetDirectoryName(host) is { } dotnetRoot &&
                    Environment.GetEnvironmentVariable("VSTEST_CONSOLE_PATH") is null &&
                    Environment.GetEnvironmentVariable("DOTNET_TEST_RUNNER") is null &&
                    baselineBuildSnapshot is { } snapshot &&
                    !HasWorkspaceSdkOverride(project, workspace) &&
                    !HasWorkspaceSdkOverride(baselineWorkspace.Map(testProject), workspace) &&
                    !HasWorkspaceSdkOverride(snapshot.Map(project), snapshot.Root) &&
                    !HasWorkspaceSdkOverride(snapshot.Map(baselineWorkspace.Map(testProject)), snapshot.Root))
                {
                    var console = Path.Combine(sdk, "vstest.console.dll");
                    if (File.Exists(console))
                    {
                        vstestConsole = new VSTestConsole(console, new Dictionary<string, string>
                        {
                            ["VSTEST_DOTNET_ROOT_PATH"] = dotnetRoot,
                            ["VSTEST_DOTNET_ROOT_ARCHITECTURE"] = architecture
                        });
                    }
                }
            }
            if (!string.IsNullOrWhiteSpace(frameworks))
                return (new RunResult(Outcome.RunnerError, "The production project must use TargetFramework."), []);

            var paths = document.RootElement.GetProperty("Items").GetProperty("Compile").EnumerateArray()
                .Select(item => item.GetProperty("FullPath").GetString()!)
                .Where(path => Path.GetExtension(path).Equals(".cs", StringComparison.OrdinalIgnoreCase))
                .Distinct()
                .Order(StringComparer.Ordinal)
                .ToArray();
            var externalSource = paths.FirstOrDefault(path => !Workspace.Contains(workspace, path));
            if (externalSource is not null)
                return (new RunResult(Outcome.RunnerError, $"Production source is outside the workspace: {externalSource}"), []);

            return (new RunResult(Outcome.Passed), paths);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return (new RunResult(Outcome.RunnerError, $"Invalid MSBuild source list: {ex.Message}"), []);
        }
    }

    private static bool HasWorkspaceSdkOverride(string projectPath, string workspace)
    {
        for (var directory = Path.GetDirectoryName(projectPath);
            directory is not null && Workspace.Contains(workspace, directory);
            directory = Path.GetDirectoryName(directory))
        {
            if (File.Exists(Path.Combine(directory, "global.json")))
                return true;
        }

        return false;
    }

    private async Task<RunResult> EvaluateAsync(string testProject, CancellationToken cancellation)
    {
        var build = await BuildAsync(testProject, cancellation);
        return build.Outcome == Outcome.Passed ? await TestAsync(testProject, cancellation) : build;
    }

    private async Task<RunResult> BuildAsync(string testProject, CancellationToken cancellation,
        bool rejectFrameworkTypeConflicts = false, bool skipRestore = false, CompilerServer? server = null)
    {
        string[] arguments = server is null
            ? ["build", testProject, "--disable-build-servers", "--nologo"]
            : ["build", testProject, "--nologo", .. server.BuildArguments];
        if (skipRestore)
            arguments = [.. arguments, "--no-restore"];
        if (rejectFrameworkTypeConflicts)
            arguments = [.. arguments, "-warnaserror:CS0436"];
        var environment = server is null ? null : new Dictionary<string, string>
        {
            ["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0",
            ["MSBUILDUSESERVER"] = "0",
            ["MSBUILDDISABLENODEREUSE"] = "1"
        };
        if (environment is not null &&
            Environment.GetEnvironmentVariable("DOTNET_GCConserveMemory") is null &&
            Environment.GetEnvironmentVariable("COMPlus_GCConserveMemory") is null)
            environment["DOTNET_GCConserveMemory"] = "7";
        var build = await DotnetProcess.RunAsync(Path.GetDirectoryName(testProject)!, timeout, cancellation,
            environment, arguments);
        if (build.TimedOut)
            return new RunResult(Outcome.TimedOut, "Build timed out.");
        return build.ExitCode == 0 ? new RunResult(Outcome.Passed) : new RunResult(Outcome.BuildFailed, build.Diagnostics);
    }

    private Task<RunResult> TestSwitchedAsync(Workspace template, Workspace workspace,
        CancellationToken cancellation, KeyValuePair<string, string>? selection = null)
    {
        var privateProject = workspace.Map(template.Map(testProject));
        var privateAssembly = switchedTestAssembly is null ? null : workspace.Map(template.Map(switchedTestAssembly));
        if (privateAssembly is not null && !File.Exists(privateAssembly))
            return Task.FromResult(new RunResult(Outcome.RunnerError, "Switched test assembly is missing from the build copy."));

        var console = template == baselineWorkspace ? vstestConsole : null;
        return TestAsync(privateProject, cancellation, selection, privateAssembly, console);
    }

    private async Task<RunResult> TestAsync(string testProject, CancellationToken cancellation,
        KeyValuePair<string, string>? selection = null, string? assembly = null, VSTestConsole? console = null)
    {
        var directory = Path.GetDirectoryName(testProject)!;
        var results = Path.Combine(directory, "TestResults", Guid.NewGuid().ToString("N"));
        string[] arguments;
        if (assembly is null)
        {
            arguments = ["test", testProject, "--no-build", "--no-restore",
                "--logger", "trx;LogFilePrefix=rm", "--results-directory", results];
        }
        else if (console is null)
        {
            arguments = ["test", assembly, "--logger", "trx;LogFilePrefix=rm", "--results-directory", results];
        }
        else
        {
            arguments = ["exec", console.Path, assembly, "--logger:trx;LogFilePrefix=rm", $"--resultsdirectory:{results}"];
        }

        if (selection is { } selector)
        {
            var assignment = $"{selector.Key}={selector.Value}";
            arguments = console is null
                ? [.. arguments, "--environment", assignment]
                : [.. arguments, $"--environment:{assignment}"];
        }
        var test = await DotnetProcess.RunAsync(directory, timeout, cancellation, console?.Environment, arguments);
        if (test.TimedOut)
            return new RunResult(Outcome.TimedOut, "Tests timed out.");

        try
        {
            if (!Directory.Exists(results))
                return new RunResult(Outcome.RunnerError, test.Diagnostics);

            var total = 0;
            var failed = 0;
            foreach (var path in Directory.EnumerateFiles(results, "*.trx"))
            {
                var summary = XDocument.Load(path).Root?.Elements()
                    .SingleOrDefault(element => element.Name.LocalName == "ResultSummary");
                var counters = summary?.Elements().SingleOrDefault(element => element.Name.LocalName == "Counters");
                if (counters is null || (string?)summary?.Attribute("outcome") is not ("Completed" or "Failed" or "Passed") ||
                    summary.Descendants().Any(IsInfrastructureError) ||
                    counters.Attributes().Any(attribute =>
                        attribute.Name.LocalName is ("error" or "timeout" or "aborted" or "notRunnable" or
                            "passedButRunAborted" or "disconnected") &&
                        (int)attribute != 0))
                    return new RunResult(Outcome.RunnerError, test.Diagnostics);

                total += (int)counters.Attribute("executed")!;
                failed += (int)counters.Attribute("failed")!;
            }

            return (test.ExitCode, total, failed) switch
            {
                (0, > 0, 0) => new RunResult(Outcome.Passed),
                (1, > 0, > 0) => new RunResult(Outcome.TestFailed, test.Diagnostics),
                _ => new RunResult(Outcome.RunnerError, test.Diagnostics)
            };
        }
        catch (Exception ex) when (ex is XmlException or InvalidOperationException or FormatException or ArgumentNullException)
        {
            return new RunResult(Outcome.RunnerError, $"Invalid test results: {ex.Message}");
        }
    }

    private static bool IsInfrastructureError(XElement element)
    {
        if (element.Name.LocalName != "RunInfo" ||
            (string?)element.Attribute("outcome") is not ("Error" or "Aborted" or "Timeout"))
            return false;

        // The xUnit adapter logs an error for each failed test in addition to its result.
        var message = element.Elements().FirstOrDefault(child => child.Name.LocalName == "Text")?.Value.Trim();
        return (string?)element.Attribute("outcome") != "Error" ||
            message is null || !message.StartsWith("[xUnit.net ", StringComparison.Ordinal) ||
            !message.EndsWith(" [FAIL]", StringComparison.Ordinal);
    }

    private static string ResolveProject(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!Path.GetExtension(fullPath).Equals(".csproj", StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath))
            throw new IOException($"C# project not found: {path}");
        return fullPath;
    }
}
