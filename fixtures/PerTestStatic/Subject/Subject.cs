namespace PerTestStatic;

public static class Subject
{
    public static readonly int Initialized = Add(3, 2);
    public static int Add(int a, int b) => a + b;
}
