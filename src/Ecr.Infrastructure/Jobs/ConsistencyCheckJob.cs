using System.Globalization;
using Ecr.Application.Ports;
using Ecr.Application.Registries;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Integration;
using Ecr.Domain.Enums;
using Ecr.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Jobs;

/// <summary>
/// Перевірка узгодженості даних; знахідки пише в <c>aud.ConsistencyIssue</c>.
/// </summary>
/// <remarks>
/// ⚠ Задача **нічого не виправляє**. Автоматичне «полагодження» неузгодженості
/// приховало б її причину, а причина тут завжди важливіша за наслідок:
/// осиротілий рядок означає, що десь видалили запис довідника, на який
/// посилаються подані документи.
/// <para>
/// Єдиний виняток — <c>IsOrphaned</c>: його задача переставляє через
/// <see cref="IOrphanScanner"/>, бо це не «виправлення», а ПЕРЕРАХУНОК
/// похідної ознаки. Причина лишається на місці й лишається видимою.
/// </para>
/// </remarks>
public sealed class ConsistencyCheckJob(
    EcrDbContext db, IOrphanScanner scanner, IClock clock) : IBackgroundJob
{
    /// <summary>Код задачі в журналі обслуговування.</summary>
    public static string Code => "consistency-check";

    /// <summary>Стеля знахідок одного проходу.</summary>
    /// <remarks>
    /// Тисяча знахідок — це вже не «знахідки», а зламані дані: далі писати
    /// нема сенсу, треба дивитися на причину. Межа не дає перевірці
    /// перетворити інцидент на кількагодинний запис у журнал.
    /// </remarks>
    private const int MaxIssues = 1_000;

    /// <inheritdoc />
    public async Task ExecuteAsync(object? payload, IJobProgress progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(progress);

        var run = new MaintenanceRun(Code, clock.UtcNow);
        db.MaintenanceRuns.Add(run);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        var issues = new List<ConsistencyIssue>();

        await progress.ReportAsync(10, "Осиротілі комірки", ct).ConfigureAwait(false);
        issues.AddRange(await OrphanedCellsAsync(ct).ConfigureAwait(false));

        await progress.ReportAsync(40, "Порушені посилання", ct).ConfigureAwait(false);
        issues.AddRange(await BrokenReferencesAsync(ct).ConfigureAwait(false));

        await progress.ReportAsync(60, "Звірка архіву", ct).ConfigureAwait(false);
        issues.AddRange(await ArchiveChecksumsAsync(ct).ConfigureAwait(false));

        // ⚠ Перерахунок ознаки IsOrphaned — В ОБИДВА боки (ФВ-8.13a). Задача
        // симетрична: те, що ставить ознаку, її ж і знімає. Асиметрія тут не
        // половина функції, а пастка — виправлення довідника не розблокувало б
        // Submit, і користувач лишився б із помилкою, причину якої вже усунуто.
        await progress.ReportAsync(80, "Перерахунок IsOrphaned", ct).ConfigureAwait(false);
        var rescanned = await scanner.ScanAllAsync(ct).ConfigureAwait(false);

        await WriteIssuesAsync(issues, ct).ConfigureAwait(false);

        // ⚠ Підсумок пишеться ЗАВЖДИ, зокрема нульовий. Знахідка — баг, а не
        // шум (ФВ-7.7): якщо перевірка регулярно щось знаходить і це вважають
        // нормою, вона перестає працювати як сигнал.
        run.Complete(
            issues.Count == 0 ? "Succeeded" : "Degraded",
            $"{{\"issues\":{issues.Count},\"orphanFlagsChanged\":{rescanned}}}",
            clock.UtcNow);

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await progress.ReportAsync(100, $"Знахідок: {issues.Count}", ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Комірки, що посилаються на записи довідника, яких немає.
    /// </summary>
    /// <remarks>
    /// ⛔ Закриті періоди тут теж перевіряються — і це не суперечить
    /// <see cref="OrphanScanPlan"/>. Різниця в тому, ЩО робиться: ознаку
    /// <c>IsOrphaned</c> у закритому періоді не чіпають (вона нічого не
    /// розблокує), а от знайдене порушення посилання записують — саме там
    /// воно найнебезпечніше, бо дані вже подані.
    /// </remarks>
    private async Task<List<ConsistencyIssue>> OrphanedCellsAsync(CancellationToken ct)
    {
        var query =
            from cell in db.CellValues.AsNoTracking()
            where cell.ValueRegistryEntryId != null
                  && !db.RegistryEntries.Any(e => e.Id == cell.ValueRegistryEntryId)
            select new OrphanRow(cell.PeriodKeyValue, cell.TableRowId, cell.ValueRegistryEntryId!.Value);

        var found = await query.Take(MaxIssues).ToListAsync(ct).ConfigureAwait(false);

        return found.ConvertAll(f => new ConsistencyIssue(
            "ORPHANED_CELL",
            Severity: 2,
            "doc.CellValue",
            f.TableRowId,
            $"Комірка рядка {f.TableRowId} періоду {f.PeriodKey} посилається на запис довідника "
            + $"{f.RegistryEntryId}, якого не існує."));
    }

    /// <summary>
    /// Порушені посилання в гібридному режимі зберігання.
    /// </summary>
    /// <remarks>
    /// ⚠ У гібридній моделі (D-21) частина значень лежить у JSON, і зовнішній
    /// ключ їх не тримає: перевірити посилання може лише ця задача. У
    /// нормалізованій моделі те саме тримає FK, і знахідок тут не буває — саме
    /// тому ненульовий результат означає або гібрид, або зламане обмеження.
    /// </remarks>
    private async Task<List<ConsistencyIssue>> BrokenReferencesAsync(CancellationToken ct)
    {
        var query =
            from row in db.TableRows.AsNoTracking()
            where !db.TableInstances.Any(
                i => i.Id == row.TableInstanceId && i.PeriodKeyValue == row.PeriodKeyValue)
            select new BrokenRow(row.PeriodKeyValue, row.Id, row.TableInstanceId);

        var found = await query.Take(MaxIssues).ToListAsync(ct).ConfigureAwait(false);

        return found.ConvertAll(f => new ConsistencyIssue(
            "BROKEN_FK",
            Severity: 3,
            "doc.TableRow",
            f.RowId,
            $"Рядок {f.RowId} посилається на екземпляр таблиці {f.TableInstanceId} "
            + $"періоду {f.PeriodKey}, якого не існує."));
    }

    /// <summary>
    /// Звіряє архів із джерелом за контрольними сумами.
    /// </summary>
    /// <remarks>
    /// ⚠ Звіряються **суми прогону**, а не рядки: перечитати десятки мільйонів
    /// рядків архіву щоночі неможливо. Три суми (<c>COUNT</c>,
    /// <c>CHECKSUM_AGG</c>, <c>SUM</c>) записав сам прогін архівації — тут
    /// перевіряється, що вони збіглися і що прогін дійшов до кінця.
    /// </remarks>
    private async Task<List<ConsistencyIssue>> ArchiveChecksumsAsync(CancellationToken ct)
    {
        var runs = await db.ArchiveRuns
            .AsNoTracking()
            .Where(r => r.Status != "Running")
            .OrderByDescending(r => r.StartedAt)
            .Take(MaxIssues)
            .Select(r => new ArchiveRow(r.Id, r.Status, r.ChecksumSourceJson, r.ChecksumTargetJson))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var issues = new List<ConsistencyIssue>();

        foreach (var run in runs)
        {
            // ⛔ Розбіжність сум — найважча знахідка: вона означає, що архів і
            // джерело кажуть різне про ті самі дані, і жодне з двох чисел не
            // можна вважати правильним.
            if (!string.Equals(run.SourceJson, run.TargetJson, StringComparison.Ordinal))
            {
                issues.Add(new ConsistencyIssue(
                    "ARCHIVE_CHECKSUM",
                    Severity: 3,
                    "itg.ArchiveRun",
                    run.Id,
                    $"Контрольні суми прогону архівації {run.Id} не збіглися: "
                    + $"джерело {run.SourceJson ?? "—"}, архів {run.TargetJson ?? "—"}."));
            }
        }

        return issues;
    }

    /// <summary>
    /// Записує знахідки, не плодячи дублікатів.
    /// </summary>
    /// <remarks>
    /// Повторна знахідка тієї самої проблеми зіставляється за трійкою
    /// <c>(RuleCode, EntityType, EntityId)</c>. Без цього щонічний прохід
    /// перетворив би журнал на копії однієї проблеми — і справжня нова
    /// знахідка потонула б серед них.
    /// </remarks>
    private async Task WriteIssuesAsync(IReadOnlyList<ConsistencyIssue> issues, CancellationToken ct)
    {
        if (issues.Count == 0)
        {
            return;
        }

        var now = clock.UtcNow;

        foreach (var issue in issues)
        {
            await db.Database.ExecuteSqlRawAsync(
                """
                IF NOT EXISTS (
                    SELECT 1 FROM aud.ConsistencyIssue
                    WHERE RuleCode = @rule AND EntityType = @type AND EntityId = @id
                      AND ResolvedAt IS NULL)
                INSERT INTO aud.ConsistencyIssue
                    (DetectedAt, Severity, RuleCode, EntityType, EntityId, Message)
                VALUES (@now, @severity, @rule, @type, @id, @message);
                """,
                [
                    new SqlParameter("@now", now),
                    new SqlParameter("@severity", issue.Severity),
                    new SqlParameter("@rule", issue.RuleCode),
                    new SqlParameter("@type", issue.EntityType),
                    new SqlParameter("@id", issue.EntityId),
                    new SqlParameter("@message", issue.Message),
                ],
                ct).ConfigureAwait(false);
        }
    }

    /// <summary>Знахідка перевірки.</summary>
    /// <param name="RuleCode">Код правила.</param>
    /// <param name="Severity">Вага: 1 інформація, 2 попередження, 3 помилка.</param>
    /// <param name="EntityType">Тип сутності.</param>
    /// <param name="EntityId">Ідентифікатор сутності.</param>
    /// <param name="Message">Текст для людини.</param>
    private sealed record ConsistencyIssue(
        string RuleCode, byte Severity, string EntityType, long EntityId, string Message);

    private sealed record OrphanRow(int PeriodKey, long TableRowId, long RegistryEntryId);

    private sealed record BrokenRow(int PeriodKey, long RowId, long TableInstanceId);

    private sealed record ArchiveRow(long Id, string Status, string? SourceJson, string? TargetJson);
}
