// src/Ecr.Worker/ChildStub.cs

using System.Globalization;
using Microsoft.Extensions.Hosting;

namespace Ecr.Worker;

/// <summary>Поведінка заглушки дочірнього процесу (аргументи після <c>--child</c>).</summary>
/// <param name="EatMegabytes"><c>--eat-mb N</c>: закомітити N МБ (перевірка межі Job Object).</param>
/// <param name="Hang"><c>--hang</c>: не реагувати на сигнал зупинки.</param>
/// <param name="ExitAfter"><c>--exit-after-ms N</c>: завершитися через N мс (імітація падіння).</param>
/// <param name="ExitCode"><c>--exit-code C</c>: код виходу для <c>--exit-after-ms</c>.</param>
internal sealed record ChildStubOptions(int EatMegabytes, bool Hang, TimeSpan? ExitAfter, int ExitCode)
{
    /// <summary>Розбирає аргументи; <c>null</c> — невідомий аргумент.</summary>
    public static ChildStubOptions? Parse(IReadOnlyList<string> args)
    {
        var result = new ChildStubOptions(0, false, null, 1);
        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--hang":
                    result = result with { Hang = true };
                    break;
                case "--eat-mb" when TryInt(args, ++i, out var mb):
                    result = result with { EatMegabytes = mb };
                    break;
                case "--exit-after-ms" when TryInt(args, ++i, out var ms):
                    result = result with { ExitAfter = TimeSpan.FromMilliseconds(ms) };
                    break;
                case "--exit-code" when TryInt(args, ++i, out var code):
                    result = result with { ExitCode = code };
                    break;
                default:
                    return null;
            }
        }

        return result;
    }

    private static bool TryInt(IReadOnlyList<string> args, int index, out int value)
    {
        value = 0;
        return index < args.Count
            && int.TryParse(args[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }
}

/// <summary>
/// Заглушка циклу дочірнього воркера (P1): чекає сигналу зупинки. Реальний
/// цикл над <c>IJobQueue</c> — крок I1.
/// </summary>
internal sealed class ChildStub(ChildStubOptions options) : BackgroundService
{
    /// <summary>Код виходу, коли пам'ять не видано (межа Job Object).</summary>
    public const int ExitOutOfMemory = 5;

    /// <summary>Тримає закомічену пам'ять живою.</summary>
    private static List<byte[]>? eaten;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.EatMegabytes > 0)
        {
            var thread = new Thread(() => EatOrExit(options.EatMegabytes)) { IsBackground = true };
            thread.Start();
        }

        if (options.ExitAfter is { } exitAfter)
        {
            await Task.Delay(exitAfter, stoppingToken).ConfigureAwait(false);
            Environment.Exit(options.ExitCode);
        }

        if (options.Hang)
        {
            await Task.Run(() => Thread.Sleep(Timeout.Infinite), CancellationToken.None).ConfigureAwait(false);
        }

        await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
    }

    /// <remarks>
    /// ⚠ OutOfMemoryException від межі Job Object не лишається необробленою:
    /// тоді процес помирав би через звіт WER, і тривалість падіння гуляла від 5
    /// до понад 30 с. Власний код виходу ще й називає причину наглядачеві.
    /// </remarks>
    private static void EatOrExit(int megabytes)
    {
        try
        {
            Eat(megabytes);
        }
        catch (OutOfMemoryException)
        {
            eaten = null;
            Environment.Exit(ExitOutOfMemory);
        }
    }

    private static void Eat(int megabytes)
    {
        var chunks = new List<byte[]>(megabytes);
        for (var i = 0; i < megabytes; i++)
        {
            var chunk = new byte[1024 * 1024];
            for (var page = 0; page < chunk.Length; page += 4096)
            {
                chunk[page] = 1;
            }

            chunks.Add(chunk);
        }

        eaten = chunks;
    }
}
