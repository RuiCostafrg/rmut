#if MSTEST
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Test = Microsoft.VisualStudio.TestTools.UnitTesting.TestMethodAttribute;
#elif XUNIT
using Test = Xunit.FactAttribute;
#elif NUNIT
using Test = NUnit.Framework.TestAttribute;
#endif

namespace FrameworkMatrix;

#if MSTEST
[TestClass]
#endif
public sealed class CalculatorTests
{
    [Test]
    public void AddsTwoNumbers()
    {
        if (Calculator.Add(2, 3) != 5)
            throw new System.InvalidOperationException("Expected the sum to be 5.");
    }

    [Test]
    public void RecognizesPositiveNumbers()
    {
        if (!Calculator.IsPositive(1))
            throw new System.InvalidOperationException("Expected 1 to be positive.");
    }
}
