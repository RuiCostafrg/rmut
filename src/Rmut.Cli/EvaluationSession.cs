using System.ComponentModel;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace Rmut.Cli;

internal sealed record SourceFile(string Path, string Content);
internal sealed record SourceList(RunResult Result, IReadOnlyList<SourceFile> Paths, string? Selector, string? CoveragePath, string? CoverageScope);
internal sealed record InstrumentedFile(int Index, string Content);
internal sealed record ExecutedTest(Guid Id, string? FilterName);
internal sealed record SwitchedPreparation(bool Ready, IReadOnlyList<string>? Hits = null);

internal sealed class EvaluationSession : IDisposable
{
    private readonly string project;
    private readonly string testProject;
    private readonly string root;
    private readonly string? switchedTestAssembly;
    private readonly Workspace baselineWorkspace;
    private readonly TimeSpan timeout;
    private readonly List<string> originals = [];
    private readonly string? selector;
    private readonly string? coveragePath;
    private readonly List<ExecutedTest> baselineTests = [];
    private readonly string? coverageScope;
    private Dictionary<string, IReadOnlyList<ExecutedTest>>? coveringTests;
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
        this.timeout = timeout;
        selector = options.Switched ? $"RMUT_MUTANT_{Guid.NewGuid():N}" : null;
        coverageScope = options.Coverage == CoverageMode.PerTest ? $"{selector}_COVERAGE" : null;
        coveragePath = options.Coverage != CoverageMode.Off ? Path.Combine(baselineWorkspace.Root, $".rmut-coverage-{Guid.NewGuid():N}") : null;
    }

    public Task<RunResult> BaselineAsync(CancellationToken cancellation) =>
        EvaluateAsync(baselineWorkspace.Map(testProject), cancellation, coveragePath is null ? null : baselineTests);

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

        return new SourceList(result, files, selector, coveragePath, coverageScope);
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
        bool collectCoverage, CancellationToken cancellation)
    {
        if (selector is null)
            return new SwitchedPreparation(false);

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
                return new SwitchedPreparation(false);

            // Run a control with every switch off in the same kind of private copy
            // used for mutants. Keep the compiled template pristine.
            using var control = new Workspace(template.Root, cancellation, includeBuildOutputs: true);
            IReadOnlyList<string>? hits = null;
            if (collectCoverage && coveragePath is not null && baselineTests.Count > 0)
            {
                hits = await CollectCoverageAsync(template, control, coveragePath, baselineTests, cancellation);
                if (hits is not null && coverageScope is not null)
                    coveringTests = await AttributeCoverageAsync(template, coveragePath, hits, cancellation);
            }
            if (hits is null)
            {
                // A failed collection control may have changed files in its private copy.
                using var fallback = collectCoverage
                    ? new Workspace(template.Root, cancellation, includeBuildOutputs: true)
                    : null;
                var tests = baselineTests.Count > 0 ? new List<ExecutedTest>() : null;
                KeyValuePair<string, string>? disableProbes = collectCoverage ? new(selector, "off") : null;
                var result = await TestSwitchedAsync(template, fallback ?? control, cancellation, disableProbes, tests);
                if (result.Outcome != Outcome.Passed || (tests is not null && !SameTests(tests, baselineTests)))
                    return new SwitchedPreparation(false);
            }

            switchedWorkspace = template;
            template = null;
            return new SwitchedPreparation(true, hits);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        {
            return new SwitchedPreparation(false);
        }
        finally
        {
            template?.Dispose();
        }
    }

    private async Task<IReadOnlyList<string>?> CollectCoverageAsync(Workspace template, Workspace control,
        string markerPath, IReadOnlyList<ExecutedTest> expected, CancellationToken cancellation, string? filter = null)
    {
        try
        {
            var markers = Directory.CreateDirectory(markerPath);
            var tests = new List<ExecutedTest>();
            KeyValuePair<string, string>? capture = filter is null || coverageScope is null ? null : new(coverageScope, markerPath);
            var result = await TestSwitchedAsync(template, control, cancellation, capture, tests, filter);
            if (result.Outcome != Outcome.Passed || !SameTests(tests, expected))
                return null;

            var hits = markers.EnumerateFileSystemInfos().ToArray();
            return hits.All(hit => hit is DirectoryInfo &&
                (hit.Attributes & FileAttributes.ReparsePoint) == 0 && hit.Name != "probe-error" &&
                hit.Name.Length == 64 && hit.Name.All(char.IsAsciiHexDigitUpper))
                ? hits.Select(hit => hit.Name).ToArray()
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        {
            return null;
        }
    }

    private async Task<Dictionary<string, IReadOnlyList<ExecutedTest>>?> AttributeCoverageAsync(
        Workspace template, string markerPath, IReadOnlyList<string> suiteHits, CancellationToken cancellation)
    {
        if (suiteHits.Count == 0 || baselineTests.Count < 2 ||
            baselineTests.Any(test => test.FilterName is null) ||
            baselineTests.Select(test => test.FilterName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != baselineTests.Count)
            return null;

        var hitsByTest = new Dictionary<string, List<ExecutedTest>>(StringComparer.Ordinal);
        Directory.Move(markerPath, $"{markerPath}-suite");
        Directory.CreateDirectory(markerPath);
        foreach (var test in baselineTests)
        {
            using var control = new Workspace(template.Root, cancellation, includeBuildOutputs: true);
            var isolatedPath = $"{markerPath}-{Guid.NewGuid():N}";
            var hits = await CollectCoverageAsync(template, control, isolatedPath, [test], cancellation, TestFilter([test]));
            if (hits is null)
                return null;
            foreach (var hit in hits)
            {
                if (!hitsByTest.TryGetValue(hit, out var covering))
                    hitsByTest.Add(hit, covering = []);
                covering.Add(test);
            }
        }

        return !Directory.EnumerateFileSystemEntries(markerPath).Any() &&
            hitsByTest.Keys.ToHashSet().SetEquals(suiteHits)
            ? hitsByTest.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<ExecutedTest>)pair.Value)
            : null;
    }

    private static bool SameTests(IReadOnlyList<ExecutedTest> observed, IReadOnlyList<ExecutedTest> expected) =>
        observed.Count == expected.Count && observed.ToHashSet().SetEquals(expected);

    private static string TestFilter(IEnumerable<ExecutedTest> tests) =>
        string.Join('|', tests.Select(test => $"FullyQualifiedName={test.FilterName}"));

    public async Task<RunResult?> EvaluateSwitchedAsync(string id, Action<string> log, CancellationToken cancellation)
    {
        if (switchedWorkspace is not { } template || selector is null)
            return null;

        try
        {
            var selection = new KeyValuePair<string, string>(selector, id);
            if (coveringTests?.GetValueOrDefault(id) is { } covered && covered.Count < baselineTests.Count)
            {
                using var filteredWorkspace = new Workspace(template.Root, cancellation, includeBuildOutputs: true);
                var executed = new List<ExecutedTest>();
                var filtered = await TestSwitchedAsync(template, filteredWorkspace, cancellation, selection,
                    executed, TestFilter(covered));
                if (filtered.Outcome is (Outcome.Passed or Outcome.TestFailed) && SameTests(executed, covered))
                {
                    log($"Selected {covered.Count} of {baselineTests.Count} tests for {id}");
                    return filtered;
                }
            }

            using var workspace = new Workspace(template.Root, cancellation, includeBuildOutputs: true);
            var result = await TestSwitchedAsync(template, workspace, cancellation, selection);
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

    private async Task<(RunResult Result, string[] Paths)> DiscoverSourcesAsync(
        string project, string workspace, CancellationToken cancellation)
    {
        var process = await DotnetProcess.RunAsync(Path.GetDirectoryName(project)!, timeout, cancellation,
            "msbuild", project, "-nologo", "-getProperty:TargetFrameworks", "-getItem:Compile");
        if (process.TimedOut)
            return (new RunResult(Outcome.TimedOut, "Production source discovery timed out."), []);
        if (process.ExitCode != 0)
            return (new RunResult(Outcome.RunnerError, process.Diagnostics), []);

        try
        {
            using var document = JsonDocument.Parse(process.Output);
            var frameworks = document.RootElement.GetProperty("Properties").GetProperty("TargetFrameworks").GetString();
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

    private async Task<RunResult> EvaluateAsync(string testProject, CancellationToken cancellation,
        List<ExecutedTest>? executedTests = null)
    {
        var build = await BuildAsync(testProject, cancellation);
        return build.Outcome == Outcome.Passed ? await TestAsync(testProject, cancellation, executedTests: executedTests) : build;
    }

    private async Task<RunResult> BuildAsync(string testProject, CancellationToken cancellation,
        bool rejectFrameworkTypeConflicts = false)
    {
        string[] arguments = ["build", testProject, "--disable-build-servers", "--nologo"];
        if (rejectFrameworkTypeConflicts)
            arguments = [.. arguments, "-warnaserror:CS0436"];
        var build = await DotnetProcess.RunAsync(Path.GetDirectoryName(testProject)!, timeout, cancellation, arguments);
        if (build.TimedOut)
            return new RunResult(Outcome.TimedOut, "Build timed out.");
        return build.ExitCode == 0 ? new RunResult(Outcome.Passed) : new RunResult(Outcome.BuildFailed, build.Diagnostics);
    }

    private Task<RunResult> TestSwitchedAsync(Workspace template, Workspace workspace,
        CancellationToken cancellation, KeyValuePair<string, string>? selection = null, List<ExecutedTest>? executedTests = null, string? filter = null)
    {
        var privateProject = workspace.Map(template.Map(testProject));
        var privateAssembly = switchedTestAssembly is null ? null : workspace.Map(template.Map(switchedTestAssembly));
        if (privateAssembly is not null && !File.Exists(privateAssembly))
            return Task.FromResult(new RunResult(Outcome.RunnerError, "Switched test assembly is missing from the build copy."));

        return TestAsync(privateProject, cancellation, selection, privateAssembly, executedTests, filter);
    }

    private async Task<RunResult> TestAsync(string testProject, CancellationToken cancellation,
        KeyValuePair<string, string>? selection = null, string? assembly = null, List<ExecutedTest>? executedTests = null, string? filter = null)
    {
        var directory = Path.GetDirectoryName(testProject)!;
        var results = Path.Combine(directory, "TestResults", Guid.NewGuid().ToString("N"));
        string[] arguments = assembly is null
            ? ["test", testProject, "--no-build", "--no-restore",
                "--logger", "trx;LogFilePrefix=rm", "--results-directory", results]
            : ["test", assembly, "--logger", "trx;LogFilePrefix=rm", "--results-directory", results];
        if (selection is { } selector)
            arguments = [.. arguments, "--environment", $"{selector.Key}={selector.Value}"];
        if (filter is not null)
            arguments = [.. arguments, "--filter", filter];
        var test = await DotnetProcess.RunAsync(directory, timeout, cancellation, arguments);
        if (test.TimedOut)
            return new RunResult(Outcome.TimedOut, "Tests timed out.");

        try
        {
            if (!Directory.Exists(results))
                return new RunResult(Outcome.RunnerError, test.Diagnostics);

            var total = 0;
            var failed = 0;
            var observedTests = executedTests is null ? null : new HashSet<ExecutedTest>();
            var complete = true;
            foreach (var path in Directory.EnumerateFiles(results, "*.trx"))
            {
                var run = XDocument.Load(path).Root;
                var summary = run?.Elements()
                    .SingleOrDefault(element => element.Name.LocalName == "ResultSummary");
                var counters = summary?.Elements().SingleOrDefault(element => element.Name.LocalName == "Counters");
                if (counters is null || (string?)summary?.Attribute("outcome") is not ("Completed" or "Failed" or "Passed") ||
                    summary.Descendants().Any(IsInfrastructureError) ||
                    counters.Attributes().Any(attribute =>
                        attribute.Name.LocalName is ("error" or "timeout" or "aborted" or "notRunnable" or
                            "passedButRunAborted" or "disconnected") &&
                        (int)attribute != 0))
                    return new RunResult(Outcome.RunnerError, test.Diagnostics);

                var executed = (int)counters.Attribute("executed")!;
                total += executed;
                failed += (int)counters.Attribute("failed")!;
                if (observedTests is not null)
                    complete &= ReadExecutedTests(run!, executed, observedTests);
            }

            if (observedTests is not null && complete && observedTests.Count == total)
                executedTests!.AddRange(observedTests);

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

    private bool ReadExecutedTests(XElement run, int executed, HashSet<ExecutedTest> tests)
    {
        var ns = run.Name.Namespace;
        var results = run.Element(ns + "Results")?.Elements().ToArray() ?? [];
        if (results.Length != executed)
            return false;

        var definitions = run.Element(ns + "TestDefinitions")?.Elements().ToLookup(
            definition => (string?)definition.Attribute("id"));
        foreach (var result in results)
        {
            var rawId = (string?)result.Attribute("testId");
            if (result.Name != ns + "UnitTestResult" ||
                (string?)result.Attribute("outcome") is not ("Passed" or "Failed") ||
                !Guid.TryParse(rawId, out var id) || id == Guid.Empty)
                return false;

            string? filterName = null;
            if (coverageScope is not null && definitions?[rawId].ToArray() is [var definition] &&
                definition.Element(ns + "TestMethod") is { } method &&
                ((string?)method.Attribute("adapterTypeName"))?.StartsWith("executor://xunit/",
                    StringComparison.OrdinalIgnoreCase) == true)
            {
                var name = $"{(string?)method.Attribute("className")}.{(string?)method.Attribute("name")}";
                if (name == (string?)result.Attribute("testName") &&
                    name.Split('.').All(part => part.Length > 0 && part.All(c => char.IsAsciiLetterOrDigit(c) || c == '_')))
                    filterName = name;
            }
            if (!tests.Add(new ExecutedTest(id, filterName)))
                return false;
        }
        return true;
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
