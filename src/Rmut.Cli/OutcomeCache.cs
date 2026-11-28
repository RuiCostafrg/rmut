using System.Collections;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace Rmut.Cli;

internal sealed class OutcomeCache
{
    private sealed record InputManifest(bool Complete, string[] ExternalInputs);
    private sealed record Entry(string Fingerprint, Dictionary<string, Outcome> Outcomes);

    private readonly string path;
    private readonly string manifestPath;
    private readonly string root;
    private readonly string project;
    private readonly string testProject;
    private readonly Dictionary<string, Outcome> recorded = new(StringComparer.Ordinal);
    private Entry? previous;
    private bool reused;
    private string? fingerprint;
    private string[] dependencies = [];
    private string? snapshotRoot;
    private string restoreGraph = "";

    private OutcomeCache(string path, string manifestPath, string root, string project, string testProject)
    {
        this.path = path;
        this.manifestPath = manifestPath;
        this.root = root;
        this.project = project;
        this.testProject = testProject;
    }

    public static OutcomeCache? Open(OutcomeCacheOptions? options, string root, string project, string testProject)
    {
        if (options is null)
            return null;

        var path = Path.GetFullPath(options.CachePath);
        var manifest = Path.GetFullPath(options.InputsPath);
        if (Workspace.Contains(root, path) || Workspace.Contains(path, manifest))
            throw new IOException("The outcome cache must be outside the input workspace and separate from the input manifest.");
        return new OutcomeCache(path, manifest, root, project, testProject);
    }

    public async Task LoadAsync(string snapshot, string baselineRoot, string baselineProject, string baselineProduction, TimeSpan timeout,
        CancellationToken cancellation)
    {
        try
        {
            var manifestData = File.ReadAllBytes(manifestPath);
            var manifest = JsonSerializer.Deserialize<InputManifest>(manifestData, JsonSerializerOptions.Web);
            if (manifest is not { Complete: true, ExternalInputs: not null } ||
                manifest.ExternalInputs.Any(string.IsNullOrWhiteSpace))
                return;

            var sdk = await DotnetProcess.RunAsync(Path.GetDirectoryName(baselineProject)!, timeout, cancellation,
                "msbuild", baselineProject, "-nologo", "-getProperty:MSBuildSDKsPath",
                "-getProperty:ProjectAssetsFile");
            if (sdk.ExitCode != 0 || sdk.TimedOut)
                return;

            using var evaluation = JsonDocument.Parse(sdk.Output);
            var properties = evaluation.RootElement.GetProperty("Properties");
            var sdkPath = properties.GetProperty("MSBuildSDKsPath").GetString()!;
            var install = Directory.GetParent(sdkPath)?.Parent?.Parent;
            var assetsPath = properties.GetProperty("ProjectAssetsFile").GetString()!;
            if (install is null || !Path.IsPathFullyQualified(assetsPath) ||
                !Workspace.Contains(baselineRoot, assetsPath))
                return;

            var binary = OperatingSystem.IsWindows() ? "rmut-native.exe" : "rmut-native";
            var assets = File.ReadAllText(assetsPath);
            restoreGraph = assets.Replace(JsonEncodedText.Encode(baselineRoot).ToString(), "<workspace>",
                StringComparison.Ordinal);
            using var restore = JsonDocument.Parse(assets);
            dependencies = [manifestPath,
                typeof(OutcomeCache).Assembly.Location, Path.Combine(AppContext.BaseDirectory, binary),
                Path.Combine(install.FullName, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"),
                Path.Combine(install.FullName, "host"), Path.Combine(install.FullName, "shared"),
                Path.Combine(install.FullName, "packs"), Directory.GetParent(sdkPath)!.FullName,
                .. manifest.ExternalInputs.Select(input => Path.GetFullPath(input, Path.GetDirectoryName(manifestPath)!)),
                .. Packages(restore.RootElement, baselineRoot, baselineProduction)];
            var current = Fingerprint(snapshot, cancellation);
            if (!manifestData.AsSpan().SequenceEqual(File.ReadAllBytes(manifestPath)))
                return;
            fingerprint = current;
            snapshotRoot = snapshot;
        }
        catch (Exception ex) when (Unavailable(ex))
        {
            return;
        }

        try
        {
            var data = File.ReadAllBytes(path);
            if (data.Length <= 32 || !SHA256.HashData(data.AsSpan(32)).AsSpan().SequenceEqual(data.AsSpan(0, 32)))
                return;
            var entry = JsonSerializer.Deserialize<Entry>(data.AsSpan(32));
            if (entry?.Fingerprint == fingerprint && entry.Outcomes is not null &&
                entry.Outcomes.Values.All(IsReusable))
                previous = entry;
        }
        catch (Exception ex) when (Unavailable(ex))
        {
            // An incomplete or unreadable entry is a cache miss.
        }
    }

    public RunResult? Find(string key)
    {
        if (previous?.Outcomes.TryGetValue(key, out var outcome) != true)
            return null;

        reused = true;
        return new RunResult(outcome);
    }

    public bool ReusedOutcomesStillValid(CancellationToken cancellation)
    {
        try
        {
            return !reused || Fingerprint(snapshotRoot!, cancellation) == fingerprint;
        }
        catch (Exception ex) when (Unavailable(ex))
        {
            return false;
        }
    }

    public void Remember(string key, RunResult result)
    {
        if (fingerprint is not null && IsReusable(result.Outcome))
            recorded[key] = result.Outcome;
    }

    public void Save(RunReport report, CancellationToken cancellation)
    {
        if (fingerprint is null || report.Baseline.Outcome != Outcome.Passed || report.Error is not null ||
            report.Mutants.Count == 0 || report.Mutants.Count != recorded.Count)
            return;

        try
        {
            if (previous is not null && previous.Outcomes.Count == recorded.Count &&
                recorded.All(row => previous.Outcomes.TryGetValue(row.Key, out var outcome) && outcome == row.Value))
                return;
            if (Fingerprint(snapshotRoot!, cancellation) != fingerprint)
                return;

            var payload = JsonSerializer.SerializeToUtf8Bytes(new Entry(fingerprint, recorded));
            var directory = Path.GetDirectoryName(path)!;
            var temporary = Path.Combine(directory, $".rmut-outcomes-{Guid.NewGuid():N}.tmp");
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
                {
                    stream.Write(SHA256.HashData(payload));
                    stream.Write(payload);
                    stream.Flush(flushToDisk: true);
                }
                cancellation.ThrowIfCancellationRequested();
                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                File.Delete(temporary);
            }
        }
        catch (Exception ex) when (Unavailable(ex))
        {
            // The completed run is still valid if the optional cache cannot be published.
        }
    }

    private string Fingerprint(string snapshot, CancellationToken cancellation)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Add(string value) => hash.AppendData(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));

        void AddTree(string name, string file)
        {
            cancellation.ThrowIfCancellationRequested();
            if (Directory.Exists(file))
            {
                var directory = new DirectoryInfo(file);
                if (directory.LinkTarget is not null)
                    throw new IOException("Linked directory in outcome inputs.");
                Add("directory");
                Add(name);
                foreach (var child in directory.EnumerateFileSystemInfos().OrderBy(entry => entry.Name, StringComparer.Ordinal))
                    AddTree(child.Name, child.FullName);
                Add("end");
                return;
            }

            Add("file");
            Add(name);
            using var stream = File.OpenRead(file);
            hash.AppendData(SHA256.HashData(stream));
        }

        Add("rmut-outcomes-v1");
        Add(root);
        Add(project);
        Add(testProject);
        Add(RuntimeInformation.OSDescription);
        Add(RuntimeInformation.RuntimeIdentifier);
        AddTree("workspace", snapshot);
        foreach (DictionaryEntry variable in Environment.GetEnvironmentVariables().Cast<DictionaryEntry>()
                     .OrderBy(entry => (string)entry.Key, StringComparer.Ordinal))
        {
            Add((string)variable.Key);
            Add((string)variable.Value!);
        }
        Add("dependencies");
        Add(restoreGraph);
        foreach (var dependency in dependencies.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            AddTree(dependency, dependency);

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static IEnumerable<string> Packages(JsonElement assets, string baselineRoot, string baselineProduction)
    {
        var restore = assets.GetProperty("project").GetProperty("restore");
        var projectDirectory = Path.GetDirectoryName(restore.GetProperty("projectPath").GetString()!)!;
        var foundProduction = false;
        var folders = assets.GetProperty("packageFolders").EnumerateObject().Select(folder => folder.Name).ToArray();
        foreach (var library in assets.GetProperty("libraries").EnumerateObject().OrderBy(library => library.Name))
        {
            var relative = library.Value.GetProperty("path").GetString()!;
            switch (library.Value.GetProperty("type").GetString())
            {
                case "package" when !Path.IsPathRooted(relative):
                    var locations = folders.Select(folder => Path.GetFullPath(relative, folder))
                        .Where(Directory.Exists).ToArray();
                    if (locations.Length != 1)
                        throw new IOException("Ambiguous or unavailable restored package.");
                    yield return locations[0];
                    break;
                case "project":
                    var reference = Path.GetFullPath(relative, projectDirectory);
                    if (!Workspace.Contains(baselineRoot, reference) || !File.Exists(reference))
                        throw new IOException("Project reference is outside the baseline workspace.");
                    foundProduction |= reference.Equals(baselineProduction,
                        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
                    break;
                default:
                    throw new IOException("Untracked library in the restore graph.");
            }
        }
        if (!foundProduction)
            throw new IOException("The test restore graph does not reference the production project.");
    }

    private static bool IsReusable(Outcome outcome) => outcome is Outcome.Passed or Outcome.TestFailed;

    private static bool Unavailable(Exception exception) => exception is IOException or UnauthorizedAccessException or
        JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException or Win32Exception;
}
