using System.Globalization;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Integration;
using Ecr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Jobs;

/// <summary>
/// Стежить за запасом порожніх партицій попереду.
/// </summary>
/// <remarks>
/// ⚠ Працює **на випередження**. <c>SPLIT</c> порожньої останньої партиції —
/// операція метаданих; <c>SPLIT</c> непорожньої переміщує дані з блокуванням.
/// Дізнатися про вичерпаний запас треба на старті або за розкладом, а не
/// вночі під час архівації.
/// </remarks>
public sealed class PartitionCheckJob(
    EcrDbContext db, ISqlCapabilities capabilities, IClock clock) : IBackgroundJob
{
    /// <summary>Код задачі в журналі обслуговування.</summary>
    public static string Code => "partition-check";

    /// <summary>
    /// Скільки меж попереду вважається достатнім запасом.
    /// </summary>
    /// <remarks>
    /// Дві — це мінімум, не комфорт: одна витрачається на поточний місяць,
    /// друга лишається запасом на час, поки хтось прочитає алерт.
    /// </remarks>
    public const int MinimumBoundariesAhead = 2;

    /// <inheritdoc />
    public async Task ExecuteAsync(object? payload, IJobProgress progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(progress);

        var run = new MaintenanceRun(Code, clock.UtcNow);
        db.MaintenanceRuns.Add(run);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        // ⛔ Функції партиціонування може не бути — тоді запасу не існує як
        // поняття, і мовчати про це не можна: «перевірка пройшла» на базі без
        // партицій означала б, що вичерпання диска не помітить ніхто.
        //
        // ⚠ Перевіряється НАЯВНІСТЬ функції, а не редакція: партиціонування
        // доступне в усіх редакціях від SQL Server 2016 SP1, включно з
        // Express, і судити про нього за іменем редакції означало б відмовити
        // там, де все працює (АРХ-7).
        if (!await PartitionFunctionExistsAsync(ct).ConfigureAwait(false))
        {
            run.Complete(
                "Degraded",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{{\"reason\":\"pf_ByPeriodKey не існує\",\"edition\":\"{capabilities.EditionName}\"}}"),
                clock.UtcNow);

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await progress.ReportAsync(100, "Партиціонування недоступне", ct).ConfigureAwait(false);
            return;
        }

        var currentKey = CurrentPeriodKey(clock.UtcNow);
        var ahead = await BoundariesAheadAsync(currentKey, ct).ConfigureAwait(false);

        var enough = ahead >= MinimumBoundariesAhead;

        // ⚠ Достатній запас НЕ породжує шуму: задача, що пише попередження
        // щоночі, привчає його не читати — і справжнє попередження губиться
        // серед звичних.
        run.Complete(
            enough ? "Succeeded" : "Degraded",
            string.Create(
                CultureInfo.InvariantCulture,
                $"{{\"boundariesAhead\":{ahead},\"minimum\":{MinimumBoundariesAhead}}}"),
            clock.UtcNow);

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        await progress
            .ReportAsync(100, enough ? $"Запас партицій: {ahead}" : $"НЕСТАЧА партицій: {ahead}", ct)
            .ConfigureAwait(false);

        // ⛔ Задача НЕ виконує DDL. `SPLIT` робить SQL Agent під окремим
        // principal (D-66): обліковий запис застосунку DDL-прав у PROD не має
        // і мати не повинен. Право створювати партиції — це право створювати
        // будь-що; задача, яка ним володіє, перестає бути безпечною.
    }

    /// <summary>
    /// Стеля вибірки меж партиціонування.
    /// </summary>
    /// <remarks>
    /// Меж у роках наперед — десятки. Тисяча означає, що обслуговування
    /// партицій колись зациклилося, і читати їх усі немає сенсу.
    /// </remarks>
    private const int MaxBoundaries = 1_000;

    /// <summary>Чи існує функція партиціонування за періодом.</summary>
    private async Task<bool> PartitionFunctionExistsAsync(CancellationToken ct)
    {
        var found = await db.Database
            .SqlQuery<int>($"""
                SELECT COUNT(*) AS Value
                FROM sys.partition_functions
                WHERE name = N'pf_ByPeriodKey'
                """)

            // Take(1) при COUNT — не оптимізація, а межа, якої вимагає
            // архітектурне правило 6: воно читає інструкцію, а не наміри.
            .Take(1)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return found is [> 0];
    }

    /// <summary>Скільки меж партиціонування лишилося попереду поточного періоду.</summary>
    private async Task<int> BoundariesAheadAsync(int currentKey, CancellationToken ct)
    {
        var boundaries = await db.Database
            .SqlQuery<int>($"""
                SELECT CAST(rv.value AS int) AS Value
                FROM sys.partition_range_values rv
                JOIN sys.partition_functions pf ON pf.function_id = rv.function_id
                WHERE pf.name = N'pf_ByPeriodKey'
                """)
            .Take(MaxBoundaries)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return boundaries.Count(b => b > currentKey);
    }

    /// <summary>Ключ поточного місяця (R-A6).</summary>
    /// <summary>Ключ поточного місяця для підрахунку запасу партицій.</summary>
    /// <param name="utcNow">Поточний момент у UTC.</param>
    /// <remarks>
    /// ⚠ UTC тут СВІДОМО, а не забутий переклад у пояс майданчика
    /// (<c>H-13</c>, <c>D2-78</c>). Задача обслуговує всю базу — проєкти всіх
    /// майданчиків одразу, — тож «поясу» в неї немає в принципі. А міряє вона
    /// не межу, а ЗАПАС: мінімум дві межі попереду
    /// (<see cref="MinimumBoundariesAhead"/>), тобто щонайменше місяць. Кілька
    /// годин розбіжності на межі місяця цього числа не міняють.
    /// </remarks>
    private static int CurrentPeriodKey(DateTime utcNow) => (utcNow.Year * 100) + utcNow.Month;
}
