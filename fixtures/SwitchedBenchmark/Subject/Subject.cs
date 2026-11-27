namespace SwitchedBenchmark;

public static class Subject
{
    public static int Add(int a, int b) => a + b;
    public static int Subtract(int a, int b) => a - b;
    public static bool Same(int a, int b) => a == b;
    public static bool Different(int a, int b) => a != b;
    public static int Unasserted(int a, int b) => a * b;
}
