// src/Ecr.Infrastructure/Jobs/OrphanScanJob.cs

using System.Globalization;
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

        await progress.ReportKeyAsync(0, "jobs.orphanScanChecking", ct).ConfigureAwait(false);

        var summary = await scanner.ScanAllAsync(ct).ConfigureAwait(false);

        await progress
            .ReportKeyAsync(
                100,
                "jobs.orphanScanDone",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["changed"] = summary.Changed.ToString(CultureInfo.InvariantCulture),

                    // ⛔ Разом зі «скільки змінено» йде «скільки оглянуто» і
                    // «чи замкнувся обхід». Саме лише `changed` описує три
                    // різні стани однаковим нулем: «оглянув усе, міняти не
                    // було чого», «оглянув шматок, бюджет вичерпано», «не
                    // оглянув нічого». Перший — здорова система, третій —
                    // сканер, що стоїть, і відрізнити їх у журналі було
                    // неможливо.
                    ["examined"] = summary.ExaminedRows.ToString(CultureInfo.InvariantCulture),
                    ["cycles"] = summary.CyclesCompleted.ToString(CultureInfo.InvariantCulture),
                },
                ct)
            .ConfigureAwait(false);

        // ⚠ Підсумок у журнал ЗАВЖДИ, зокрема нульовий. Задача, яка мовчить,
        // коли нічого не знайшла, і мовчить, коли не запустилася, — це задача,
        // про зупинку якої дізнаються з першого заблокованого Submit.
        //
        // Ненульове зняття — нормально: хтось виправив довідник. Ненульова
        // постановка — привід подивитися, що з ним сталося.
        LogSummary(logger, summary.Changed, summary.ExaminedRows, summary.CyclesCompleted);

        // ⚠ Замикання обходу — окремий запис, а не поле в попередньому: це
        // подія, яка трапляється раз на кілька ночей, і саме за нею видно, що
        // сканер ВСТИГАЄ за зростанням таблиці. Якщо її немає тижнями —
        // покриття відстає, хоч щоночі й писалося «успіх».
        if (summary.CycleCompleted)
        {
            LogCycle(logger, summary.CyclesCompleted, summary.LastCycleCompletedAt);
        }
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "orphan-scan: змінено ознаку IsOrphaned — {Changed}; оглянуто рядків — {Examined}; "
                  + "повних обходів набору — {Cycles}.")]
    private static partial void LogSummary(ILogger logger, int changed, int examined, int cycles);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "orphan-scan: обхід набору ЗАМКНУВСЯ (всього {Cycles}); мітка часу — {CompletedAt}.")]
    private static partial void LogCycle(ILogger logger, int cycles, DateTime? completedAt);
}
