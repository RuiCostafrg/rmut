using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rmut.Cli;

internal static class NativeRunner
{
    private static readonly JsonSerializerOptions Protocol = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    [JsonPolymorphic(TypeDiscriminatorPropertyName = "command")]
    [JsonDerivedType(typeof(Baseline), "baseline")]
    [JsonDerivedType(typeof(Sources), "sources")]
    [JsonDerivedType(typeof(Prepare), "prepare")]
    [JsonDerivedType(typeof(EvaluateSwitched), "evaluateSwitched")]
    [JsonDerivedType(typeof(Evaluate), "evaluate")]
    [JsonDerivedType(typeof(Log), "log")]
    [JsonDerivedType(typeof(Finish), "finish")]
    private abstract record Request;
    private sealed record Baseline : Request;
    private sealed record Sources : Request;
    private sealed record Prepare(InstrumentedFile[] Files, string? FirstMutant) : Request;
    private sealed record EvaluateSwitched(string[] Ids) : Request;
    private sealed record Evaluate(int Index, string MutatedSource) : Request;
    private sealed record Log(string Text) : Request;
    private sealed record Finish(RunReport Report) : Request;

    public static async Task<RunReport> RunAsync(
        RunOptions options, TimeSpan timeout, Action<string> log, CancellationToken cancellation)
    {
        var assembly = typeof(NativeRunner).Assembly;
        var packagedRid = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "RmutNativeRid").Value;
        if (packagedRid != RuntimeInformation.RuntimeIdentifier)
            throw new IOException($"The installed Rmut engine targets {packagedRid}. This runtime is {RuntimeInformation.RuntimeIdentifier}.");

        await using var session = new EvaluationSession(options, timeout, cancellation);
        var binaryName = OperatingSystem.IsWindows() ? "rmut-native.exe" : "rmut-native";
        var packagedBinary = Path.Combine(Path.GetDirectoryName(assembly.Location)!, binaryName);
        // NuGet extraction may discard executable permissions. Restore them on a private copy.
        var directory = Directory.CreateTempSubdirectory("rmut-engine-");
        try
        {
            var binary = Path.Combine(directory.FullName, binaryName);
            File.Copy(packagedBinary, binary);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(binary, UnixFileMode.UserRead | UnixFileMode.UserExecute);

            return await RunEngineAsync(binary, session, log, cancellation);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static async Task<RunReport> RunEngineAsync(
        string binary, EvaluationSession session, Action<string> log, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(binary)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardInput = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8
        };
        using var engine = Process.Start(start) ?? throw new IOException("Could not start the Rust engine.");
        try
        {
            // Each line is one request. Only requests that invoke the SDK receive a reply.
            var sourceCount = 0;
            while (await engine.StandardOutput.ReadLineAsync(cancellation) is { } line)
            {
                var request = JsonSerializer.Deserialize<Request>(line, Protocol);
                switch (request)
                {
                    case Baseline:
                        await ReplyAsync(await session.BaselineAsync(cancellation));
                        break;
                    case Sources:
                        var sources = await session.DiscoverAsync(cancellation);
                        sourceCount = sources.Paths.Count;
                        await ReplyAsync(sources);
                        break;
                    case Prepare preparation when preparation.Files is { Length: > 0 } &&
                        (preparation.FirstMutant is null ||
                            (session.SwitchedWorkers == 2 && preparation.FirstMutant.Length == 64)) &&
                        preparation.Files.All(file => file is not null && (uint)file.Index < (uint)sourceCount &&
                            file.Content is not null):
                        var prepared = await session.PrepareSwitchedAsync(
                            preparation.Files, preparation.FirstMutant, cancellation);
                        log(prepared.Build switch
                        {
                            SwitchedBuild.Incremental or SwitchedBuild.IncrementalWithDirectConsole => "Incremental switched build ready.",
                            SwitchedBuild.Clean => "Switched build ready.",
                            _ => "Switched build unavailable. Using fresh execution."
                        });
                        if (prepared.Build == SwitchedBuild.IncrementalWithDirectConsole)
                            log("Direct VSTest console ready.");
                        if (prepared.FirstResult is not null)
                        {
                            log("Switched control completed with a speculative mutant.");
                            log($"Switched execution {preparation.FirstMutant}");
                        }
                        await ReplyAsync(new
                        {
                            Ready = prepared.Build != SwitchedBuild.Unavailable,
                            prepared.FirstResult
                        });
                        break;
                    case EvaluateSwitched evaluation when evaluation.Ids is { Length: > 0 } &&
                        evaluation.Ids.Length <= session.SwitchedWorkers &&
                        evaluation.Ids.All(id => id is { Length: 64 }):
                        var results = await Task.WhenAll(evaluation.Ids.Select(id =>
                            session.EvaluateSwitchedAsync(id, cancellation)));
                        if (results.Length > 1)
                            log($"Switched batch completed with {results.Length} workers.");
                        for (var index = 0; index < results.Length; index++)
                        {
                            if (results[index] is not null)
                                log($"Switched execution {evaluation.Ids[index]}");
                        }
                        await ReplyAsync(results);
                        break;
                    case Evaluate evaluation when (uint)evaluation.Index < (uint)sourceCount &&
                        evaluation.MutatedSource is not null:
                        await ReplyAsync(await session.EvaluateMutantAsync(
                            evaluation.Index, evaluation.MutatedSource, cancellation));
                        break;
                    case Log message when message.Text is not null:
                        log(message.Text);
                        break;
                    case Finish finish when finish.Report is not null:
                        engine.StandardInput.Close();
                        await engine.WaitForExitAsync(cancellation);
                        cancellation.ThrowIfCancellationRequested();
                        if (engine.ExitCode != 0)
                            throw new IOException($"The Rust engine exited with code {engine.ExitCode}.");
                        return finish.Report;
                    default:
                        throw new IOException("Invalid request from the Rust engine.");
                }
            }

            await engine.WaitForExitAsync(cancellation);
            throw new IOException($"The Rust engine exited without a report (code {engine.ExitCode}).");
        }
        catch (IOException) when (cancellation.IsCancellationRequested)
        {
            cancellation.ThrowIfCancellationRequested();
            throw;
        }
        finally
        {
            if (!engine.HasExited)
            {
                try
                {
                    engine.Kill();
                }
                catch (InvalidOperationException) when (engine.HasExited)
                {
                }
                catch (Win32Exception) when (engine.HasExited)
                {
                }
                await engine.WaitForExitAsync();
            }
        }

        Task ReplyAsync<T>(T response) =>
            engine.StandardInput.WriteLineAsync(JsonSerializer.Serialize(response, Protocol).AsMemory(), cancellation);
    }
}
