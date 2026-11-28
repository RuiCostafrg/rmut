using Xunit;

namespace Rmut.Engine.Tests;

public sealed class MutationEngineTests
{
    [Theory]
    [InlineData("+", "-")]
    [InlineData("-", "+")]
    [InlineData("*", "/")]
    [InlineData("/", "*")]
    [InlineData("%", "*")]
    [InlineData("==", "!=")]
    [InlineData("!=", "==")]
    [InlineData("&&", "||")]
    [InlineData("||", "&&")]
    public void ReplacesOneSupportedOperator(string original, string replacement)
    {
        var mutant = Assert.Single(MutationEngine.Discover("Sample.cs", $"var answer = a {original} b;"));

        Assert.Equal("Sample.cs", mutant.Path);
        Assert.Equal(1, mutant.Line);
        Assert.Equal(original, mutant.Original);
        Assert.Equal(replacement, mutant.Replacement);
        Assert.Equal($"var answer = a {replacement} b;", mutant.MutatedSource);
    }

    [Theory]
    [InlineData("<", ">", "<=")]
    [InlineData("<=", ">", "<")]
    [InlineData(">", "<", ">=")]
    [InlineData(">=", "<", ">")]
    public void DiscoversBothRelationalAlternativesAtTheSameOperator(
        string original, string opposite, string boundary)
    {
        var mutants = MutationEngine.Discover("Sample.cs", $"var answer = a {original} b;").ToArray();

        Assert.Equal(new[] { opposite, boundary }, mutants.Select(mutant => mutant.Replacement));
        Assert.Equal(new[] { $"var answer = a {opposite} b;", $"var answer = a {boundary} b;" },
            mutants.Select(mutant => mutant.MutatedSource));
        Assert.NotEqual(mutants[0].Id, mutants[1].Id);
    }

    [Fact]
    public void DiscoversDistinctMutantsInSourceOrderWithStableIds()
    {
        const string source = "// + ==\r\nvar text = \"- &&\";\r\nvar result = a + b + c;\r\n";
        var mutants = MutationEngine.Discover("Sample.cs", source).ToArray();

        Assert.Collection(mutants,
            first =>
            {
                Assert.Equal(3, first.Line);
                Assert.Equal("// + ==\r\nvar text = \"- &&\";\r\nvar result = a - b + c;\r\n", first.MutatedSource);
            },
            second =>
            {
                Assert.Equal(3, second.Line);
                Assert.Equal("// + ==\r\nvar text = \"- &&\";\r\nvar result = a + b - c;\r\n", second.MutatedSource);
            });
        Assert.NotEqual(mutants[0].Id, mutants[1].Id);
        Assert.Equal(mutants.Select(mutant => mutant.Id),
            MutationEngine.Discover("Sample.cs", source).Select(mutant => mutant.Id));
        Assert.NotEqual(mutants[0].Id, MutationEngine.Discover("Other.cs", source).First().Id);
    }

    [Fact]
    public void SkipsTypeDelimitersAndUnaryOperators()
    {
        const string source = "List<int> values = new(); var value = -1; var flag = !true;";
        Assert.Empty(MutationEngine.Discover("Sample.cs", source));
    }

    [Theory]
    [InlineData("a+-b", "a- -b")]
    [InlineData("a+++b", "a++-b")]
    [InlineData("a---b", "a--+b")]
    [InlineData("a*/* keep */b", "a/ /* keep */b")]
    [InlineData("a**b", "a/ *b")]
    public void PreservesAdjacentTokens(string expression, string mutatedExpression)
    {
        var mutant = Assert.Single(MutationEngine.Discover("Sample.cs", $"var result = {expression};"));
        Assert.Equal($"var result = {mutatedExpression};", mutant.MutatedSource);
    }
}
