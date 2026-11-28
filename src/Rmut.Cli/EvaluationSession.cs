using System.ComponentModel;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Microsoft.CodeAnalysis.CSharp;

namespace Rmut.Cli;

internal sealed record SourceFile(string Path, string Content, string? ActiveContent);
internal sealed record SourceList(RunResult Result, IReadOnlyList<SourceFile> Paths, string? Selector);
internal sealed record InstrumentedFile(int Index, string Content);

internal sealed class EvaluationSession : IDisposable
{
    private readonly string project;
    private readonly string testProject;
    private readonly string root;
    private readonly string? switchedTestAssembly;
    private readonly Workspace baselineWorkspace;
    private readonly TimeSpan timeout;
    private readonly string compilerCapture;
    private readonly List<string> originals = [];
    private readonly string? selector;
    private Workspace? switchedWorkspace;

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
        compilerCapture = Path.Combine(baselineWorkspace.Root, $"rmut-compile-{Guid.NewGuid():N}.json");
        this.timeout = timeout;
        selector = options.Switched ? $"RMUT_MUTANT_{Guid.NewGuid():N}" : null;
    }

    public async Task<RunResult> BaselineAsync(CancellationToken cancellation)
    {
        await File.WriteAllTextAsync(compilerCapture, baselineWorkspace.Map(project), cancellation);
        var loggerAssembly = typeof(EvaluationSession).Assembly.Location;
        var test = baselineWorkspace.Map(testProject);
        var build = await BuildAsync(test, cancellation, additionalArguments:
        [
            "-property:ProvideCommandLineArgs=true",
            // Enable task-parameter events without printing a diagnostic console log.
            "-verbosity:diagnostic",
            "-consoleloggerparameters:Verbosity=minimal",
            $"-logger:Rmut.Cli.CompileInputsLogger,{loggerAssembly};{compilerCapture}"
        ]);
        return build.Outcome == Outcome.Passed ? await TestAsync(test, cancellation) : build;
    }

    public async Task<SourceList> DiscoverAsync(CancellationToken cancellation)
    {
        var (result, paths, parseOptions) = await DiscoverSourcesAsync(cancellation);
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
            var tree = CSharpSyntaxTree.ParseText(content, parseOptions, cancellationToken: cancellation);
            var syntax = tree.GetRoot(cancellation);
            if (SourceProjection.IsGenerated(source, syntax))
                continue;

            originals.Add(originalPath);
            files.Add(new SourceFile(displayPath, content, SourceProjection.ActiveText(content, syntax)));
        }

        return new SourceList(result, files, selector);
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

    public async Task<bool> PrepareSwitchedAsync(IReadOnlyList<InstrumentedFile> files, CancellationToken cancellation)
    {
        if (selector is null)
            return false;

        Workspace? template = null;
        try
        {
            template = new Workspace(root, cancellation);
            if (switchedTestAssembly is { } assembly)
            {
                // Custom output directories may already contain artifacts in the input.
                var templateAssembly = template.Map(assembly);
                if (File.Exists(templateAssembly))
                    File.Delete(templateAssembly);
            }

            foreach (var file in files)
                await File.WriteAllTextAsync(template.Map(originals[file.Index]), file.Content, cancellation);

            var build = await BuildAsync(template.Map(testProject), cancellation, rejectFrameworkTypeConflicts: true);
            if (build.Outcome != Outcome.Passed)
                return false;

            // Run a control with every switch off in the same kind of private copy
            // used for mutants. Keep the compiled template pristine.
            using var control = new Workspace(template.Root, cancellation, includeBuildOutputs: true);
            var result = await TestSwitchedAsync(template, control, cancellation);
            if (result.Outcome != Outcome.Passed)
                return false;

            switchedWorkspace = template;
            template = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        {
            return false;
        }
        finally
        {
            template?.Dispose();
        }
    }

    public async Task<RunResult?> EvaluateSwitchedAsync(string id, CancellationToken cancellation)
    {
        if (switchedWorkspace is not { } template || selector is null)
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

    public void Dispose()
    {
        using var baseline = baselineWorkspace;
        switchedWorkspace?.Dispose();
    }

    private async Task<(RunResult Result, string[] Paths, CSharpParseOptions? ParseOptions)> DiscoverSourcesAsync(
        CancellationToken cancellation)
    {
        try
        {
            using var stream = File.OpenRead(compilerCapture);
            var compilations = await JsonSerializer.DeserializeAsync<CompilerInputs[]>(stream, cancellationToken: cancellation);
            if (compilations is not [{ Arguments.Length: > 0 } inputs])
                return (new RunResult(Outcome.RunnerError,
                    "The test build must compile the production project exactly once and expose its compiler inputs."), [], null);

            var directory = Path.GetDirectoryName(baselineWorkspace.Map(project))!;
            var compiler = CSharpCommandLineParser.Default.Parse(inputs.Arguments, directory, null);
            var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            var generated = inputs.GeneratedSources.Select(path => Path.GetFullPath(path, directory)).ToHashSet(comparer);
            var paths = compiler.SourceFiles
                .Select(file => file.Path)
                .Where(path => !generated.Contains(path) && Path.GetExtension(path).Equals(".cs", StringComparison.OrdinalIgnoreCase))
                .Distinct(comparer)
                .Order(StringComparer.Ordinal)
                .ToArray();
            var externalSource = paths.FirstOrDefault(path => !Workspace.Contains(baselineWorkspace.Root, path));
            if (externalSource is not null)
                return (new RunResult(Outcome.RunnerError, $"Production source is outside the workspace: {externalSource}"), [], null);

            return (new RunResult(Outcome.Passed), paths, compiler.ParseOptions);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return (new RunResult(Outcome.RunnerError, $"Invalid MSBuild source list: {ex.Message}"), [], null);
        }
    }

    private async Task<RunResult> EvaluateAsync(string testProject, CancellationToken cancellation)
    {
        var build = await BuildAsync(testProject, cancellation);
        return build.Outcome == Outcome.Passed ? await TestAsync(testProject, cancellation) : build;
    }

    private async Task<RunResult> BuildAsync(string testProject, CancellationToken cancellation,
        bool rejectFrameworkTypeConflicts = false, string[]? additionalArguments = null)
    {
        string[] arguments = ["build", testProject, "--disable-build-servers", "--nologo"];
        if (additionalArguments is not null)
            arguments = [.. arguments, .. additionalArguments];
        if (rejectFrameworkTypeConflicts)
            arguments = [.. arguments, "-warnaserror:CS0436"];
        var build = await DotnetProcess.RunAsync(Path.GetDirectoryName(testProject)!, timeout, cancellation, arguments);
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

        return TestAsync(privateProject, cancellation, selection, privateAssembly);
    }

    private async Task<RunResult> TestAsync(string testProject, CancellationToken cancellation,
        KeyValuePair<string, string>? selection = null, string? assembly = null)
    {
        var directory = Path.GetDirectoryName(testProject)!;
        var results = Path.Combine(directory, "TestResults", Guid.NewGuid().ToString("N"));
        string[] arguments = assembly is null
            ? ["test", testProject, "--no-build", "--no-restore",
                "--logger", "trx;LogFilePrefix=rm", "--results-directory", results]
            : ["test", assembly, "--logger", "trx;LogFilePrefix=rm", "--results-directory", results];
        if (selection is { } selector)
            arguments = [.. arguments, "--environment", $"{selector.Key}={selector.Value}"];
        var test = await DotnetProcess.RunAsync(directory, timeout, cancellation, arguments);
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
