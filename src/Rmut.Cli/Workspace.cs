namespace Rmut.Cli;

internal sealed class Workspace : IDisposable
{
    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", "bin", "obj", "TestResults"
    };

    public string SourceRoot { get; }
    public string Root { get; }

    public Workspace(string sourceRoot, CancellationToken cancellation, bool includeBuildOutputs = false)
    {
        SourceRoot = sourceRoot;
        Root = Directory.CreateTempSubdirectory("rmut-").FullName;
        try
        {
            if (Contains(SourceRoot, Root))
                throw new IOException("The temporary directory is inside the input workspace.");
            CopyDirectory(SourceRoot, Root, cancellation, includeBuildOutputs);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void RestoreSnapshot(Workspace snapshot)
    {
        Directory.Delete(Root, recursive: true);
        Directory.Move(snapshot.Root, Root);
    }

    public string Map(string original) => Path.Combine(Root, Path.GetRelativePath(SourceRoot, original));

    public static string FindRoot(string project, string testProject)
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(project)!);
        while (!Contains(directory.FullName, testProject))
            directory = directory.Parent ?? throw new IOException("Projects must share a workspace.");

        var root = directory;
        for (var ancestor = directory; ancestor is not null; ancestor = ancestor.Parent)
        {
            if (File.Exists(Path.Combine(ancestor.FullName, ".git")) ||
                Directory.Exists(Path.Combine(ancestor.FullName, ".git")))
                return ancestor.FullName;

            if (new[] { "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props",
                "global.json", "NuGet.Config", "nuget.config", "NuGet.config" }
                .Any(file => File.Exists(Path.Combine(ancestor.FullName, file))))
                root = ancestor;
        }

        if (root.Parent is null)
            throw new IOException("Projects must share a workspace below the filesystem root.");

        return root.FullName;
    }

    public static bool Contains(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return !Path.IsPathRooted(relative) && relative != ".." &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static void CopyDirectory(string source, string destination, CancellationToken cancellation, bool includeBuildOutputs)
    {
        foreach (var entry in new DirectoryInfo(source).EnumerateFileSystemInfos())
        {
            cancellation.ThrowIfCancellationRequested();
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                continue;

            var target = Path.Combine(destination, entry.Name);
            if (entry is DirectoryInfo)
            {
                if (!includeBuildOutputs && ExcludedDirectories.Contains(entry.Name))
                    continue;
                Directory.CreateDirectory(target);
                CopyDirectory(entry.FullName, target, cancellation, includeBuildOutputs);
            }
            else if (entry.Name != ".git")
            {
                File.Copy(entry.FullName, target);
            }
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(Root))
            Directory.Delete(Root, recursive: true);
    }
}
