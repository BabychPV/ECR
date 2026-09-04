// src/Ecr.Infrastructure/Jobs/OrphanScanJob.cs

using Ecr.Application.Ports;
using Microsoft.Extensions.Logging;

namespace Ecr.Infrastructure.Jobs;

/// <summary>
/// Знаходить рядки, що посилаються на записи реєстру, які перестали бути
/// чинними у своєму періоді (<c>ФВ-8.13</c>, <c>D-98</c>).
/// </summary>
/// <remarks>
/// Чому це задача, а не перевірка при читанні: бюджет зрізу — 400 мс p95 на
/// ~5 000 комірок. Темпоральна перевірка на кожен рядок при кожному відкритті
/// таблиці зжерла б його цілком. Ознака зберігається, а не рахується щоразу.
/// <para>
/// Задача **симетрична**: вона так само знімає ознаку з рядків, що знову стали
/// чинними. Інакше виправлення довідника не розблокувало б <c>Submit</c>, і
/// користувач лишився б із помилкою, причину якої вже усунуто.
/// </para>
/// <para>
/// Сама логіка — в <see cref="IOrphanScanner"/>: та сама, якою користується
/// точковий перерахунок при зміні вікна дії. Дві реалізації розійшлися б, і
/// нічний прохід скасовував би те, що зробив денний.
/// </para>
/// </remarks>
public sealed partial class OrphanScanJob(IOrphanScanner scanner, ILogger<OrphanScanJob> logger) : IBackgroundJob
{
    /// <summary>Код задачі в черзі.</summary>
    public static string Code => "orphan-scan";

    /// <inheritdoc />
    public async Task ExecuteAsync(object? payload, IJobProgress progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(progress);

        await progress.ReportAsync(0, "Перевірка посилань на довідники", ct).ConfigureAwait(false);

        var changed = await scanner.ScanAllAsync(ct).ConfigureAwait(false);

        await progress.ReportAsync(100, $"Змінено рядків: {changed}", ct).ConfigureAwait(false);

        // ⚠ Підсумок у журнал ЗАВЖДИ, зокрема нульовий. Задача, яка мовчить,
        // коли нічого не знайшла, і мовчить, коли не запустилася, — це задача,
        // про зупинку якої дізнаються з першого заблокованого Submit.
        //
        // Ненульове зняття — нормально: хтось виправив довідник. Ненульова
        // постановка — привід подивитися, що з ним сталося.
        LogSummary(logger, changed);
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "orphan-scan: рядків зі зміненою ознакою IsOrphaned — {Changed}.")]
    private static partial void LogSummary(ILogger logger, int changed);
}
