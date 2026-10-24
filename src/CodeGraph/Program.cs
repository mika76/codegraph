namespace CodeGraph;

public static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            return Cli.Run(args);
        }
        catch (CodeGraphException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }
}
