using System.Diagnostics.CodeAnalysis;

namespace Rmut.Cli;

internal sealed record RunOptions(string Project, string TestProject, string? Json, bool Switched = false)
{
    public static bool TryParse(string[] args, [NotNullWhen(true)] out RunOptions? options)
    {
        options = null;
        if (args.Length == 0 || args[0] != "run" || args.Length % 2 == 0)
            return false;

        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 1; i < args.Length; i += 2)
        {
            if (args[i] is not ("--project" or "--test-project" or "--json" or "--execution") ||
                string.IsNullOrWhiteSpace(args[i + 1]) || args[i + 1].StartsWith("--") ||
                !paths.TryAdd(args[i], args[i + 1]))
                return false;
        }

        if (!paths.TryGetValue("--project", out var project) ||
            !paths.TryGetValue("--test-project", out var testProject))
            return false;

        var execution = paths.GetValueOrDefault("--execution", "fresh");
        if (execution is not ("fresh" or "switched"))
            return false;

        options = new RunOptions(project, testProject, paths.GetValueOrDefault("--json"), execution == "switched");
        return true;
    }
}
