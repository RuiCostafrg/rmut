using Xunit;

namespace PerTestStatic;

public sealed class SubjectTests
{
    [Fact]
    public void ReadsStaticInitializer() => Assert.Equal(5, Subject.Initialized);

    [Fact]
    public void RunsWithoutReadingInitializer() => Assert.True(string.IsNullOrEmpty(""));
}
