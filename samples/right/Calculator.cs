namespace Demo;

public static class Calculator
{
    public static long Add(int a, int b)
    {
        return (long)a + b;
    }

    public static int Multiply(int a, int b) => a * b;

    public static string Version => "2.0";
}
