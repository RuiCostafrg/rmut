namespace Rmut.Engine;

public sealed record Mutant(
    string Id,
    string Path,
    int Line,
    string Original,
    string Replacement,
    string MutatedSource);
