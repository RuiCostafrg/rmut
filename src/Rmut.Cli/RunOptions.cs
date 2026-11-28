using System.Diagnostics.CodeAnalysis;

namespace Rmut.Cli;

internal enum BaselineBuildMode { Default, SingleNode, SeparateRestore }

internal sealed record RunOptions(string Project, string TestProject, string? Json, bool Switched = false,
    string? SwitchedTestAssembly = null, bool ReuseBaselineBuild = false, int SwitchedWorkers = 1, bool UseCompilerServer = false,
    BaselineBuildMode BaselineBuild = BaselineBuildMode.Default)
{
    public static bool TryParse(string[] args, [NotNullWhen(true)] out RunOptions? options)
    {
        options = null;
        if (args.Length == 0 || args[0] != "run" || args.Length % 2 == 0)
            return false;

        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 1; i < args.Length; i += 2)
        {
            if (args[i] is not ("--project" or "--test-project" or "--json" or "--execution" or "--switched-test-assembly" or "--switched-workers" or "--switched-compiler" or "--switched-baseline-build") ||
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

        var workers = paths.GetValueOrDefault("--switched-workers", "1");
        if (workers is not ("1" or "2") || (workers == "2" && execution != "switched-incremental"))
            return false;

        var compiler = paths.GetValueOrDefault("--switched-compiler", "standalone");
        if (compiler is not ("standalone" or "private-server") ||
            (compiler == "private-server" && execution != "switched-incremental"))
            return false;

        var baselineBuild = paths.GetValueOrDefault("--switched-baseline-build", "default") switch
        {
            "default" => BaselineBuildMode.Default,
            "single-node" => BaselineBuildMode.SingleNode,
            "separate-restore" => BaselineBuildMode.SeparateRestore,
            _ => (BaselineBuildMode?)null
        };
        if (baselineBuild is null ||
            (baselineBuild != BaselineBuildMode.Default && compiler != "private-server"))
            return false;

        options = new RunOptions(project, testProject, paths.GetValueOrDefault("--json"), execution != "fresh", assembly,
            execution == "switched-incremental", workers == "2" ? 2 : 1, compiler == "private-server", baselineBuild.Value);
        return true;
    }
}
