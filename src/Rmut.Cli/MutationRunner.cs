using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;
using System.Xml.Linq;
using Rmut.Engine;

namespace Rmut.Cli;

internal enum Outcome
{
    Passed, TestFailed, Killed, Survived, BuildFailed, TimedOut, RunnerError
}

internal sealed record RunResult(Outcome Outcome, string? Details = null);
internal sealed record MutantResult(
    string Id, string Path, int Line, string Original, string Replacement, Outcome Outcome, string? Details);
internal sealed record RunReport(
    RunResult Baseline, IReadOnlyList<MutantResult> Mutants,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RunResult? Error = null);

internal sealed class MutationRunner(RunOptions options, TimeSpan timeout, Action<string> log)
{
    public async Task<RunReport> RunAsync(CancellationToken cancellation)
    {
        var project = ResolveProject(options.Project);
        var testProject = ResolveProject(options.TestProject);
        var root = Workspace.FindRoot(project, testProject);
        var results = new List<MutantResult>();

        using var baselineWorkspace = new Workspace(root, cancellation);
        var baseline = await EvaluateAsync(baselineWorkspace.Map(testProject), cancellation);
        LogResult("Baseline", baseline);
        if (baseline.Outcome != Outcome.Passed)
            return new RunReport(baseline, results);

        var sources = await DiscoverSourcesAsync(
            baselineWorkspace.Map(project), baselineWorkspace.Root, cancellation);
        if (sources.Result.Outcome != Outcome.Passed)
        {
            LogResult("Source discovery", sources.Result);
            return new RunReport(baseline, results, sources.Result);
        }

        foreach (var source in sources.Paths)
        {
            cancellation.ThrowIfCancellationRequested();
            var relativePath = Path.GetRelativePath(baselineWorkspace.Root, source);
            var originalPath = Path.Combine(root, relativePath);
            if (!File.Exists(originalPath))
                continue;

            var displayPath = relativePath.Replace(Path.DirectorySeparatorChar, '/');
            var content = await File.ReadAllTextAsync(originalPath, cancellation);
            foreach (var mutant in MutationEngine.Discover(displayPath, content))
            {
                cancellation.ThrowIfCancellationRequested();
                RunResult result;
                try
                {
                    using var workspace = new Workspace(root, cancellation);
                    await File.WriteAllTextAsync(workspace.Map(originalPath), mutant.MutatedSource, cancellation);
                    result = await EvaluateAsync(workspace.Map(testProject), cancellation);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
                {
                    result = new RunResult(Outcome.RunnerError, ex.Message);
                }

                result = result with
                {
                    Outcome = result.Outcome switch
                    {
                        Outcome.TestFailed => Outcome.Killed,
                        Outcome.Passed => Outcome.Survived,
                        _ => result.Outcome
                    }
                };
                results.Add(new MutantResult(mutant.Id, mutant.Path, mutant.Line,
                    mutant.Original, mutant.Replacement, result.Outcome, result.Details));
                LogResult($"{mutant.Id} {mutant.Path}:{mutant.Line} {mutant.Original} → {mutant.Replacement}", result);
            }
        }

        log($"Mutants: {results.Count}. Killed: {results.Count(result => result.Outcome == Outcome.Killed)}. " +
            $"Survived: {results.Count(result => result.Outcome == Outcome.Survived)}.");
        return new RunReport(baseline, results);
    }

    private void LogResult(string label, RunResult result)
    {
        log($"{label}: {result.Outcome}");
        if (result.Details is not null)
            log(result.Details);
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

    private async Task<RunResult> EvaluateAsync(string testProject, CancellationToken cancellation)
    {
        var directory = Path.GetDirectoryName(testProject)!;
        var build = await DotnetProcess.RunAsync(directory, timeout, cancellation,
            "build", testProject, "--disable-build-servers", "--nologo");
        if (build.TimedOut)
            return new RunResult(Outcome.TimedOut, "Build timed out.");
        if (build.ExitCode != 0)
            return new RunResult(Outcome.BuildFailed, build.Diagnostics);

        var results = Path.Combine(directory, "TestResults", Guid.NewGuid().ToString("N"));
        var test = await DotnetProcess.RunAsync(directory, timeout, cancellation, "test", testProject,
            "--no-build", "--no-restore", "--logger", "trx;LogFilePrefix=rm", "--results-directory", results);
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
