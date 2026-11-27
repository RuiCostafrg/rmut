using Xunit;

namespace SwitchedBenchmark;

public sealed class SubjectTests
{
    [Fact]
    public void ExercisesSwitchableOperators()
    {
        Assert.Equal(7, Subject.Add(3, 4));
        Assert.Equal(-1, Subject.Subtract(3, 4));
        Assert.True(Subject.Same(2, 2));
        Assert.True(Subject.Different(2, 3));
    }
}
