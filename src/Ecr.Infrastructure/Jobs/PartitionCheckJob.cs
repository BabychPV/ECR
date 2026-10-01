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

        // ⛔ Q-240: див. `MaintenanceRunFailure`. Без цього catch виняток
        // лишав прогін `Running`/`FinishedAt = NULL`, а зведення бере збої за
        // `FinishedAt >= since` — тобто провал перевірки запасу партицій не
        // потрапляв у зведення й ні в чий лист.
        try
        {
            await RunAsync(run, progress, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await MaintenanceRunFailure.RecordAsync(db, run, ex, clock.UtcNow).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Власне перевірка; прогін уже відкрито.</summary>
    /// <param name="run">Відкритий прогін журналу обслуговування.</param>
    /// <param name="progress">Прогрес задачі.</param>
    /// <param name="ct">Токен скасування.</param>
    private async Task RunAsync(MaintenanceRun run, IJobProgress progress, CancellationToken ct)
    {
        // Аудит: межі pf_AuditByMonth продовжуються на AuditMonthsAhead місяців
        // уперед процедурою (EXECUTE AS OWNER, тож DDL-прав застосунку не треба —
        // D-66). Ідемпотентно: додає лише відсутні межі; SPLIT порожньої крайньої
        // партиції — операція метаданих. Збій (напр. таймаут блокування) падає
        // прогоном Failed (Q-240) і повторюється наступної ночі.
        var auditAdded = await ExtendAuditBoundariesAsync(ct).ConfigureAwait(false);

        // D-247: архівація старих партицій аудиту (SWITCH у arc.Audit*) тією ж нічною
        // задачею; процедура ідемпотентна й сама пише слід у itg.MaintenanceRun.
        // Збій архівації НЕ має ховати перевірку запасу партицій нижче: він іде в
        // подробиці прогону й опускає статус до Degraded (сам рядок Failed процедура
        // вже записала).
        var (archivedPartitions, archivedRows, archiveError) =
            await ArchiveAuditAsync(ct).ConfigureAwait(false);
        var archiveJson = string.Create(
            CultureInfo.InvariantCulture,
            $",\"auditArchivedPartitions\":{archivedPartitions},\"auditArchivedRows\":{archivedRows}{(archiveError is null ? string.Empty : ",\"auditArchiveFailed\":true")}");

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
                    $"{{\"reason\":\"pf_ByPeriodKey не існує\",\"edition\":\"{capabilities.EditionName}\",\"auditBoundariesAdded\":{auditAdded}{archiveJson}}}"),
                clock.UtcNow);

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await progress.ReportKeyAsync(100, "jobs.partitionUnavailable", ct).ConfigureAwait(false);
            return;
        }

        var currentKey = CurrentPeriodKey(clock.UtcNow);
        var ahead = await BoundariesAheadAsync(currentKey, ct).ConfigureAwait(false);

        var enough = ahead >= MinimumBoundariesAhead;

        // ⚠ Достатній запас НЕ породжує шуму: задача, що пише попередження
        // щоночі, привчає його не читати — і справжнє попередження губиться
        // серед звичних.
        run.Complete(
            enough && archiveError is null ? "Succeeded" : "Degraded",
            string.Create(
                CultureInfo.InvariantCulture,
                $"{{\"boundariesAhead\":{ahead},\"minimum\":{MinimumBoundariesAhead},\"auditBoundariesAdded\":{auditAdded}{archiveJson}}}"),
            clock.UtcNow);

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        await progress
            .ReportKeyAsync(
                100,
                enough ? "jobs.partitionAhead" : "jobs.partitionShortage",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["ahead"] = ahead.ToString(CultureInfo.InvariantCulture),
                },
                ct)
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

    /// <summary>Запас меж <c>pf_AuditByMonth</c> уперед, місяців.</summary>
    public const int AuditMonthsAhead = 12;

    /// <summary>Скільки місяців аудит лишається «гарячим» в <c>aud.*</c> (D-247; припущення).</summary>
    public const int AuditArchiveOlderThanMonths = 24;

    /// <summary>
    /// Архівує старі партиції аудиту процедурою <c>arc.usp_ArchiveAudit</c> (EXEC, без DDL у C#).
    /// </summary>
    private async Task<(int Partitions, long Rows, string? Error)> ArchiveAuditAsync(CancellationToken ct)
    {
        var partitions = new Microsoft.Data.SqlClient.SqlParameter("@PartitionsSwitched", System.Data.SqlDbType.Int)
        {
            Direction = System.Data.ParameterDirection.Output,
        };
        var rows = new Microsoft.Data.SqlClient.SqlParameter("@RowsSwitched", System.Data.SqlDbType.BigInt)
        {
            Direction = System.Data.ParameterDirection.Output,
        };

        try
        {
            await db.Database
                .ExecuteSqlRawAsync(
                    "EXEC arc.usp_ArchiveAudit @OlderThanMonths = {0}, @Today = {1}, @PartitionsSwitched = @PartitionsSwitched OUTPUT, @RowsSwitched = @RowsSwitched OUTPUT;",
                    new object[] { AuditArchiveOlderThanMonths, clock.UtcNow.Date, partitions, rows },
                    ct)
                .ConfigureAwait(false);
        }
        catch (Microsoft.Data.SqlClient.SqlException ex)
        {
            return (0, 0, ex.Message);
        }

        return (partitions.Value is int p ? p : 0, rows.Value is long r ? r : 0, null);
    }

    /// <summary>Продовжує межі аудиту; повертає, скільки меж додано.</summary>
    private async Task<int> ExtendAuditBoundariesAsync(CancellationToken ct)
    {
        var added = new Microsoft.Data.SqlClient.SqlParameter("@Added", System.Data.SqlDbType.Int)
        {
            Direction = System.Data.ParameterDirection.Output,
        };

        var today = clock.UtcNow.Date;
        await db.Database
            .ExecuteSqlRawAsync(
                "EXEC arc.usp_EnsureAuditPartitions @MonthsAhead = {0}, @Today = {1}, @Added = @Added OUTPUT;",
                new object[] { AuditMonthsAhead, today, added },
                ct)
            .ConfigureAwait(false);

        return added.Value is int n ? n : 0;
    }

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
            // `COUNT(*)` повертає рівно один рядок, тож порядок нічого не
            // вибирає — він тут лише для того, щоб `Take` не був без
            // `OrderBy` (EF 10102), а не щоб приглушити попередження.
            .OrderBy(v => v)
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
            // ⚠ Найстаріші межі нам не потрібні: рахуємо ті, що ПОПЕРЕДУ.
            // Без порядку стеля брала б довільну тисячу, і за понад тисячу
            // меж запас міг би вийти нулем при повній решті (EF 10102).
            .OrderByDescending(v => v)
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
