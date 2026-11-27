using System.Diagnostics.CodeAnalysis;

namespace Rmut.Cli;

internal sealed record RunOptions(string Project, string TestProject, string? Json, bool Switched = false,
    string? SwitchedTestAssembly = null, bool ReuseBaselineBuild = false)
{
    public static bool TryParse(string[] args, [NotNullWhen(true)] out RunOptions? options)
    {
        options = null;
        if (args.Length == 0 || args[0] != "run" || args.Length % 2 == 0)
            return false;

        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 1; i < args.Length; i += 2)
        {
            if (args[i] is not ("--project" or "--test-project" or "--json" or "--execution" or "--switched-test-assembly") ||
                string.IsNullOrWhiteSpace(args[i + 1]) || args[i + 1].StartsWith("--") ||
                !paths.TryAdd(args[i], args[i + 1]))
                return false;
        }

        if (!paths.TryGetValue("--project", out var project) ||
            !paths.TryGetValue("--test-project", out var testProject))
            return false;

        var execution = paths.GetValueOrDefault("--execution", "fresh");
        if (execution is not ("fresh" or "switched" or "switched-incremental"))
            return false;

        var assembly = paths.GetValueOrDefault("--switched-test-assembly");
        if (assembly is not null && (execution == "fresh" || Path.IsPathRooted(assembly) ||
            !Path.GetExtension(assembly).Equals(".dll", StringComparison.OrdinalIgnoreCase)))
            return false;

        if (execution == "switched-incremental" && assembly is null)
            return false;

        options = new RunOptions(project, testProject, paths.GetValueOrDefault("--json"), execution != "fresh", assembly,
            execution == "switched-incremental");
        return true;
    }
}
