using Subject;
using Xunit;

public sealed class CounterTests
{
    [Fact]
    public void CountsItemsThroughGeneratedMethod() =>
        Assert.Equal(2, Counter.Count(["first", "second"]));
}
