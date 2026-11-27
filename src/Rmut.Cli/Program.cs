using System.Text.Json;
using System.Text.Json.Serialization;
using Rmut.Cli;

const string usage = "Usage: rmut run --project PATH --test-project PATH [--json PATH]";
if (args is ["--help"] or ["-h"] or ["run", "--help"])
{
    Console.WriteLine(usage);
    return 0;
}

if (!RunOptions.TryParse(args, out var options))
{
    Console.Error.WriteLine(usage);
    return 2;
}

using var cancellation = new CancellationTokenSource();
ConsoleCancelEventHandler cancel = (_, e) =>
{
    e.Cancel = true;
    cancellation.Cancel();
};
Console.CancelKeyPress += cancel;
try
{
    var reportPath = options.Json is null ? null : Path.GetFullPath(options.Json);
    if (reportPath is not null && Path.Exists(reportPath))
        throw new IOException($"Report path already exists: {reportPath}");

    var report = await NativeRunner.RunAsync(options, TimeSpan.FromMinutes(5), Console.WriteLine, cancellation.Token);
    if (reportPath is not null)
    {
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        jsonOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        var json = JsonSerializer.Serialize(report, jsonOptions);
        var temporaryPath = Path.Combine(Path.GetDirectoryName(reportPath)!, $".rmut-{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(temporaryPath, json + Environment.NewLine, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            File.Move(temporaryPath, reportPath);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    return report.Baseline.Outcome != Outcome.Passed || report.Error is not null ||
        report.Mutants.Any(mutant => mutant.Outcome is not (Outcome.Killed or Outcome.Survived))
        ? 1 : 0;
}
catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
{
    Console.Error.WriteLine("Run canceled.");
    return 130;
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
    System.ComponentModel.Win32Exception or JsonException or ArgumentException)
{
    Console.Error.WriteLine($"Runner error: {ex.Message}");
    return 1;
}
finally
{
    Console.CancelKeyPress -= cancel;
}
