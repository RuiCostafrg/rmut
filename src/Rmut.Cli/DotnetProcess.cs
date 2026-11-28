using System.ComponentModel;
using System.Diagnostics;

namespace Rmut.Cli;

internal sealed record ProcessResult(int ExitCode, string Output, string Error, bool TimedOut = false)
{
    public string Diagnostics => string.Join(Environment.NewLine,
        new[] { Output.Trim(), Error.Trim() }.Where(value => value.Length != 0));
}

internal static class DotnetProcess
{
    public static Task<ProcessResult> RunAsync(
        string directory, TimeSpan timeout, CancellationToken cancellation, params string[] arguments) =>
        RunAsync(directory, timeout, cancellation, null, arguments);

    public static async Task<ProcessResult> RunAsync(
        string directory, TimeSpan timeout, CancellationToken cancellation,
        IReadOnlyDictionary<string, string>? environment, string[] arguments)
    {
        cancellation.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
            WorkingDirectory = directory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);

        if (environment is not null)
        {
            foreach (var (name, value) in environment)
                start.Environment[name] = value;
        }

        using var process = Process.Start(start) ?? throw new IOException("Could not start dotnet.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(timeout);
        var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
        var error = process.StandardError.ReadToEndAsync(deadline.Token);
        try
        {
            await Task.WhenAll(process.WaitForExitAsync(deadline.Token), output, error);
            return new ProcessResult(process.ExitCode, await output, await error);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) when (process.HasExited)
            {
            }
            catch (Win32Exception) when (process.HasExited)
            {
            }

            await process.WaitForExitAsync();
            cancellation.ThrowIfCancellationRequested();
            return new ProcessResult(-1, "", "", TimedOut: true);
        }
    }
}
