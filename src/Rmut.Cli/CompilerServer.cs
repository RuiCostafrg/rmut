namespace Rmut.Cli;

internal sealed class CompilerServer(string directory, string assembly) : IAsyncDisposable
{
    private readonly string pipe = $"rmut-{Guid.NewGuid():N}";

    public string[] BuildArguments => ["-nodeReuse:false", "-p:UseSharedCompilation=true",
        $"-p:SharedCompilationId={pipe}"];

    public static async Task<CompilerServer?> TryCreateAsync(string directory, TimeSpan timeout,
        CancellationToken cancellation)
    {
        try
        {
            var versionTask = DotnetProcess.RunAsync(directory, timeout, cancellation, "--version");
            var installedTask = DotnetProcess.RunAsync(directory, timeout, cancellation, "--list-sdks");
            await Task.WhenAll(versionTask, installedTask);
            var version = await versionTask;
            var installed = await installedTask;
            if (version.ExitCode != 0 || installed.ExitCode != 0)
                return null;

            var selected = version.Output.Trim();
            var prefix = selected + " [";
            var matches = installed.Output.Split('\n', StringSplitOptions.TrimEntries)
                .Where(line => line.StartsWith(prefix, StringComparison.Ordinal) && line.EndsWith(']'))
                .ToArray();
            if (matches.Length != 1)
                return null;

            var compiler = Path.Combine(matches[0][prefix.Length..^1], selected, "Roslyn", "bincore", "VBCSCompiler.dll");
            return Path.IsPathFullyQualified(compiler) && File.Exists(compiler)
                ? new CompilerServer(directory, compiler)
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        var shutdown = await DotnetProcess.RunAsync(directory, TimeSpan.FromSeconds(15), CancellationToken.None,
            "exec", assembly, $"-pipename:{pipe}", "-shutdown");
        if (shutdown.ExitCode != 0)
            throw new IOException($"Could not stop the private compiler server. {shutdown.Diagnostics}");
    }
}
