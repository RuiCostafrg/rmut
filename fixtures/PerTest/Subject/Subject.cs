namespace PerTest;

public static class Subject
{
    public static int Shared(int a, int b) => a + b;
    public static bool Exclusive(int a, int b) => a == b;
    public static int Uncovered(int a, int b) => a * b;
}
