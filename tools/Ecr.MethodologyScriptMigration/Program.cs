using System.Text;
using Ecr.MethodologyScriptMigration.Cli;

namespace Ecr.MethodologyScriptMigration;

/// <summary>
/// Точка входу. Команди (план, розд. 8.1): <c>convert</c>, <c>shadow</c>, <c>pack</c>; <c>parity</c> — ще не підключено.
/// Коди виходу: 0 — усе гаразд; 1 — є невдалі версії (перетворення чи запис); 2 — помилка запуску чи середовища.
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        using var cancel = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancel.Cancel();
        };

        return await MigrationCli.RunAsync(args, Console.Out, Console.Error, cancel.Token).ConfigureAwait(false);
    }
}
