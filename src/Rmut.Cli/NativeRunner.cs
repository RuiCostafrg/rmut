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
    private sealed record Prepare(InstrumentedFile[] Files) : Request;
    private sealed record EvaluateSwitched(string Id) : Request;
    private sealed record Evaluate(string Id, int Index, string MutatedSource) : Request;
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

        using var session = new EvaluationSession(options, timeout, cancellation);
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

            var report = await RunEngineAsync(binary, session, log, cancellation);
            if (session.ReusedOutcomesStillValid(cancellation))
            {
                session.Save(report, cancellation);
                return report;
            }

            log("Outcome inputs changed during execution. Retrying with fresh execution.");
            using var retry = new EvaluationSession(options with { OutcomeCache = null }, timeout, cancellation);
            return await RunEngineAsync(binary, retry, log, cancellation);
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
                        preparation.Files.All(file => file is not null && (uint)file.Index < (uint)sourceCount &&
                            file.Content is not null):
                        var ready = await session.PrepareSwitchedAsync(preparation.Files, cancellation);
                        log(ready ? "Switched build ready." : "Switched build unavailable. Using fresh execution.");
                        await ReplyAsync(ready);
                        break;
                    case EvaluateSwitched evaluation when evaluation.Id is { Length: 64 }:
                        var switched = await session.EvaluateSwitchedAsync(evaluation.Id, cancellation);
                        if (switched is not null)
                            log($"Switched execution {evaluation.Id}");
                        await ReplyAsync(switched);
                        break;
                    case Evaluate evaluation when evaluation.Id is { Length: 64 } &&
                        (uint)evaluation.Index < (uint)sourceCount &&
                        evaluation.MutatedSource is not null:
                        var key = evaluation.Id;
                        if (session.TryReuse(key, out var reused))
                        {
                            log($"Reused outcome {evaluation.Id}");
                            session.Remember(key, reused);
                            await ReplyAsync(reused);
                            break;
                        }
                        var result = await session.EvaluateMutantAsync(
                            evaluation.Index, evaluation.MutatedSource, cancellation);
                        session.Remember(key, result);
                        await ReplyAsync(result);
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
