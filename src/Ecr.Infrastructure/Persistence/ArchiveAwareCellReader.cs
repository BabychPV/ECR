using Ecr.Application.Ports;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>
/// Читає комірки з гарячої схеми або з архіву — **прозоро для викликача**.
/// </summary>
/// <remarks>
/// ⚠ Викликач не знає, де лежать дані, і не повинен знати: питання «чи
/// заархівований цей рік» не має жодного стосунку до питання «яке число в
/// комірці». Перекласти вибір на викликача означало б, що кожне місце читання
/// рано чи пізно забуде про архів — і покаже порожній звіт замість
/// торішнього.
/// <para>
/// ⛔ Вибір робиться **за станом проєкту**, а не за датою. «Рік старший за
/// два» — здогадка: проєкт може бути закритий і не заархівований, або
/// заархівований достроково. Стан <c>doc.Project.Status</c> і прогони
/// <c>itg.ArchiveRun</c> кажуть це напевно.
/// </para>
/// </remarks>
public sealed class ArchiveAwareCellReader(EcrDbContext db)
{
    /// <summary>Стеля вибірки одного зрізу.</summary>
    private const int MaxCells = 200_000;

    /// <summary>Чи період уже перенесений в архів.</summary>
    /// <param name="projectId">Проєкт.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task<bool> IsArchivedAsync(int projectId, PeriodKey periodKey, CancellationToken ct)
    {
        // ⚠ Останній ЗАВЕРШЕНИЙ прогін, що покрив цей період. Провалений
        // прогін не рахується: він міг зупинитися на попередній партиції, і
        // дані цього періоду ще в гарячій схемі.
        var archived = await db.ArchiveRuns
            .AsNoTracking()
            .Where(r => r.ProjectId == projectId
                        && r.Status == "Completed"
                        && r.FromPeriodKey <= periodKey.Value
                        && r.ToPeriodKey >= periodKey.Value)
            .OrderByDescending(r => r.StartedAt)
            .Select(r => r.Direction)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        // Напрям ОСТАННЬОГО прогону і вирішує: рік могли заархівувати, потім
        // повернути для перерахунку, потім заархівувати знову.
        return archived == Domain.Entities.Integration.ArchiveRun.ToArchive;
    }

    /// <summary>Читає комірки періоду звідти, де вони зараз лежать.</summary>
    /// <param name="projectId">Проєкт.</param>
    /// <param name="tableInstanceId">Екземпляр таблиці.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task<IReadOnlyList<CellRecord>> ReadAsync(
        int projectId, long tableInstanceId, PeriodKey periodKey, CancellationToken ct)
    {
        if (!await IsArchivedAsync(projectId, periodKey, ct).ConfigureAwait(false))
        {
            return await HotAsync(tableInstanceId, periodKey, ct).ConfigureAwait(false);
        }

        return await ArchivedAsync(tableInstanceId, periodKey, ct).ConfigureAwait(false);
    }

    /// <summary>Комірки з гарячої схеми.</summary>
    private async Task<List<CellRecord>> HotAsync(
        long tableInstanceId, PeriodKey periodKey, CancellationToken ct)
    {
        var query =
            from cell in db.CellValues.AsNoTracking()
            join row in db.TableRows.AsNoTracking()
                on new { P = cell.PeriodKeyValue, I = cell.TableRowId }
                equals new { P = row.PeriodKeyValue, I = row.Id }
            where row.TableInstanceId == tableInstanceId && row.PeriodKeyValue == periodKey.Value
            select new ArchivedCell(
                cell.PeriodKeyValue, cell.TableRowId, cell.ColumnDefId, cell.TableDefId,
                cell.ValueString, cell.ValueNumeric, cell.ValueDate, cell.ValueBool,
                cell.ValueRegistryEntryId, cell.ValueUnitId, cell.IsCalculated, cell.IsEmpty);

        var rows = await query.Take(MaxCells).ToListAsync(ct).ConfigureAwait(false);
        return rows.ConvertAll(Map);
    }

    /// <summary>
    /// Комірки з архіву.
    /// </summary>
    /// <remarks>
    /// Читається сирим SQL: <c>arc.*</c> немає в моделі EF навмисно (різниця
    /// фізична — columnstore і окрема файлова група), і заводити дзеркальні
    /// сутності означало б мати два описи однієї структури, які розійдуться.
    /// </remarks>
    private async Task<List<CellRecord>> ArchivedAsync(
        long tableInstanceId, PeriodKey periodKey, CancellationToken ct)
    {
        var rows = await db.Database
            .SqlQuery<ArchivedCell>($"""
                SELECT TOP ({MaxCells})
                       c.PeriodKey, c.TableRowId, c.ColumnDefId, c.TableDefId,
                       c.ValueString, c.ValueNumeric, c.ValueDate, c.ValueBool,
                       c.ValueRegistryEntryId, c.ValueUnitId, c.IsCalculated, c.IsEmpty
                FROM arc.CellValue c
                JOIN arc.TableRow r
                  ON r.PeriodKey = c.PeriodKey AND r.Id = c.TableRowId
                WHERE r.TableInstanceId = {tableInstanceId}
                  AND r.PeriodKey = {periodKey.Value}
                """)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return rows.ConvertAll(Map);
    }

    /// <summary>Приводить рядок будь-якої зі схем до контрактного вигляду.</summary>
    /// <remarks>
    /// ⚠ Одна функція на обидва джерела — саме це й робить читання прозорим.
    /// Дві копії розійшлися б на першому ж новому полі, і архівні числа
    /// почали б відрізнятися від гарячих без жодного повідомлення.
    /// </remarks>
    private static CellRecord Map(ArchivedCell cell)
        => new(
            new CellAddress(new PeriodKey(cell.PeriodKey), cell.TableRowId, cell.ColumnDefId),
            cell.TableDefId,
            new CellValueData
            {
                ValueString = cell.ValueString,
                ValueNumeric = cell.ValueNumeric,
                ValueDate = cell.ValueDate,
                ValueBool = cell.ValueBool,
                ValueRegistryEntryId = cell.ValueRegistryEntryId,
                ValueUnitId = cell.ValueUnitId,
                IsCalculated = cell.IsCalculated,
                IsEmpty = cell.IsEmpty,
            });

    /// <summary>Комірка в тому вигляді, в якому вона лежить в обох схемах.</summary>
    public sealed record ArchivedCell(
        int PeriodKey,
        long TableRowId,
        int ColumnDefId,
        int TableDefId,
        string? ValueString,
        decimal? ValueNumeric,
        DateTime? ValueDate,
        bool? ValueBool,
        int? ValueRegistryEntryId,
        int? ValueUnitId,
        bool IsCalculated,
        bool IsEmpty);
}
