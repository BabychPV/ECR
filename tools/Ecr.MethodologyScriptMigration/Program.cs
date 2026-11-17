namespace Ecr.MethodologyScriptMigration;

/// <summary>Точка входу. Команди convert/shadow/parity/pack додаються наступними задачами (план, розд. 8.1).</summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        Console.Error.WriteLine(
            "Ecr.MethodologyScriptMigration: наразі реалізовано лише ядро перетворення (Conversion/). "
            + "Команди convert/shadow/parity/pack — наступні задачі етапу 5.");
        return args.Length == 0 ? 0 : 64;
    }
}
