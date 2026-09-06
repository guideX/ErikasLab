namespace ErikasLab.Platform.MonoGame;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var game = new ErikasLabHost();
        game.Run();
    }
}
