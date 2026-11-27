namespace Conformance;

public static class Subject
{
    public static int Combine(int a, int b, int c) => a + b + c;
    public static bool Same(int a, int b) => a == b;
    public static bool Choose(bool a, bool b) => a && b;
    public static int Unasserted(int a, int b) => a * b;
}
