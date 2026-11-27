using Xunit;

namespace Conformance;

public sealed class SubjectTests
{
    [Fact]
    public void ExercisesSharedOperators()
    {
        Assert.Equal(9, Subject.Combine(2, 3, 4));
        Assert.True(Subject.Same(2, 2));
        Assert.False(Subject.Choose(true, false));
    }
}
