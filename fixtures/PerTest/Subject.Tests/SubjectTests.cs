using System.Threading.Tasks;
using Xunit;

namespace PerTest;

public sealed class FirstTests
{
    [Fact]
    public void ExercisesSharedAndExclusiveOperators()
    {
        Parallel.For(0, 8, _ =>
        {
            Assert.Equal(5, Subject.Shared(3, 2));
            Assert.True(Subject.Exclusive(2, 2));
        });
    }
}

public sealed class SecondTests
{
    [Fact]
    public void AlsoExercisesSharedOperator() => Assert.Equal(5, Subject.Shared(3, 2));
}

public sealed class UnrelatedTests
{
    [Fact]
    public void RunsWithoutCallingSubject() => Assert.Equal(3, int.Parse("3"));
}
