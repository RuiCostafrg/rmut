using System.Text.Json.Serialization;

namespace Rmut.Cli;

internal enum Outcome
{
    Passed, TestFailed, Killed, Survived, BuildFailed, TimedOut, RunnerError
}

internal sealed record RunResult(Outcome Outcome, string? Details = null);
internal sealed record MutantResult(
    string Id, string Path, int Line, string Original, string Replacement, Outcome Outcome, string? Details,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Offset = null);
internal sealed record RunReport(
    RunResult Baseline, IReadOnlyList<MutantResult> Mutants,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RunResult? Error = null);
