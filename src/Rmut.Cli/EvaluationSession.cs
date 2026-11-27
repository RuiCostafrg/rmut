using System.ComponentModel;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace Rmut.Cli;

internal sealed record SourceFile(string Path, string Content);
internal sealed record SourceList(RunResult Result, IReadOnlyList<SourceFile> Paths);

internal sealed class EvaluationSession : IDisposable
{
    private readonly string project;
    private readonly string testProject;
    private readonly string root;
    private readonly Workspace baselineWorkspace;
    private readonly TimeSpan timeout;
    private readonly List<string> originals = [];

    public EvaluationSession(RunOptions options, TimeSpan timeout, CancellationToken cancellation)
    {
        project = ResolveProject(options.Project);
        testProject = ResolveProject(options.TestProject);
        root = Workspace.FindRoot(project, testProject);
        baselineWorkspace = new Workspace(root, cancellation);
        this.timeout = timeout;
    }

    public Task<RunResult> BaselineAsync(CancellationToken cancellation) =>
        EvaluateAsync(baselineWorkspace.Map(testProject), cancellation);

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

        return new SourceList(result, files);
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

    public void Dispose() => baselineWorkspace.Dispose();

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
