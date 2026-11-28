using System.Text.Json;
using Microsoft.Build.Framework;

namespace Rmut.Cli;

internal sealed record CompilerInputs(string[] Arguments, string[] GeneratedSources);

// Runs inside the SDK's MSBuild process during the baseline build.
public sealed class CompileInputsLogger : ILogger
{
    public LoggerVerbosity Verbosity { get; set; }
    public string? Parameters { get; set; }

    private readonly Dictionary<BuildEventContext, Capture> compilations = [];
    private string output = "";

    public void Initialize(IEventSource eventSource)
    {
        output = Parameters ?? throw new LoggerException("A compile input capture path is required.");
        var project = File.ReadAllText(output);
        ((IEventSource3)eventSource).IncludeTaskInputs();
        eventSource.TaskStarted += (_, task) =>
        {
            if (task.TaskName == "Csc" && task.BuildEventContext is { } context &&
                string.Equals(task.ProjectFile, project,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                compilations.Add(context, new Capture());
        };
        eventSource.MessageRaised += (_, message) =>
        {
            if (message is not TaskParameterEventArgs parameter ||
                parameter.BuildEventContext is not { } context ||
                !compilations.TryGetValue(context, out var capture))
                return;

            if (parameter.Kind == TaskParameterMessageKind.TaskInput && parameter.ParameterName == "Sources")
                capture.GeneratedSources = parameter.Items.OfType<ITaskItem>()
                    .Where(item => string.Equals(item.GetMetadata("AutoGen"), "true", StringComparison.OrdinalIgnoreCase))
                    .Select(item => item.ItemSpec)
                    .ToArray();
            if (parameter.Kind == TaskParameterMessageKind.TaskOutput && parameter.ParameterName == "CommandLineArgs")
                capture.Arguments = parameter.Items.OfType<ITaskItem>().Select(item => item.ItemSpec).ToArray();
        };
    }

    public void Shutdown() => File.WriteAllText(output, JsonSerializer.Serialize(compilations.Values
        .Select(capture => new CompilerInputs(capture.Arguments, capture.GeneratedSources))));

    private sealed class Capture
    {
        public string[] Arguments { get; set; } = [];
        public string[] GeneratedSources { get; set; } = [];
    }
}
