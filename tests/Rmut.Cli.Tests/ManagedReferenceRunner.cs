using Rmut.Cli;
using Rmut.Engine;

namespace Rmut.Cli.Tests;

internal sealed class ManagedReferenceRunner(RunOptions options, TimeSpan timeout, Action<string> log)
{
    public async Task<RunReport> RunAsync(CancellationToken cancellation)
    {
        using var session = new EvaluationSession(options, timeout, cancellation);
        var results = new List<MutantResult>();
        var baseline = await session.BaselineAsync(cancellation);
        LogResult("Baseline", baseline);
        if (baseline.Outcome != Outcome.Passed)
            return new RunReport(baseline, results);

        var sources = await session.DiscoverAsync(cancellation);
        if (sources.Result.Outcome != Outcome.Passed)
        {
            LogResult("Source discovery", sources.Result);
            return new RunReport(baseline, results, sources.Result);
        }

        for (var index = 0; index < sources.Paths.Count; index++)
        {
            var source = sources.Paths[index];
            foreach (var mutant in MutationEngine.Discover(source.Path, source.Content))
            {
                var result = await session.EvaluateMutantAsync(index, mutant.MutatedSource, cancellation);
                result = result with
                {
                    Outcome = result.Outcome switch
                    {
                        Outcome.TestFailed => Outcome.Killed,
                        Outcome.Passed => Outcome.Survived,
                        _ => result.Outcome
                    }
                };
                results.Add(new MutantResult(mutant.Id, mutant.Path, mutant.Line,
                    mutant.Original, mutant.Replacement, result.Outcome, result.Details));
                LogResult($"{mutant.Id} {mutant.Path}:{mutant.Line} {mutant.Original} → {mutant.Replacement}", result);
            }
        }

        log($"Mutants: {results.Count}. Killed: {results.Count(result => result.Outcome == Outcome.Killed)}. " +
            $"Survived: {results.Count(result => result.Outcome == Outcome.Survived)}.");
        return new RunReport(baseline, results);
    }

    private void LogResult(string label, RunResult result)
    {
        log($"{label}: {result.Outcome}");
        if (result.Details is not null)
            log(result.Details);
    }
}
