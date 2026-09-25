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
            orderby cell.TableRowId, cell.ColumnDefId
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
                ORDER BY c.TableRowId, c.ColumnDefId
                """)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return rows.ConvertAll(Map);
    }

    /// <summary>
    /// Комірки зрізу з архіву за самим ідентифікатором екземпляра — без
    /// відомого наперед періоду.
    /// </summary>
    /// <remarks>
    /// ⚠ На відміну від <see cref="ReadAsync"/>, викликач тут (F-13,
    /// <c>NormalizedCellStore.ReadSliceAsync</c>) НЕ знає <c>PeriodKey</c>:
    /// гарячий шлях бере його з <c>doc.TableInstance</c> через join, а той
    /// рядок truncate'ний разом із партицією тим самим
    /// <c>arc.usp_ArchiveYear</c>, що й <c>doc.CellValue</c>. Тому період
    /// читається окремим дешевим запитом до <c>arc.TableInstance</c>, перш
    /// ніж іти в <c>arc.CellValue</c> — а сам запит комірок УЖЕ є
    /// (<see cref="ArchivedAsync"/>), і дублювати його тут не потрібно.
    /// </remarks>
    public async Task<IReadOnlyList<CellRecord>> ReadArchivedSliceAsync(long tableInstanceId, CancellationToken ct)
    {
        var periodKeys = await db.Database
            .SqlQuery<int>($"""
                SELECT TOP (1) PeriodKey AS Value
                FROM arc.TableInstance
                WHERE Id = {tableInstanceId}
                """)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (periodKeys.Count == 0)
        {
            return [];
        }

        return await ArchivedAsync(tableInstanceId, new PeriodKey(periodKeys[0]), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Те саме кількома екземплярами; екземпляр без жодного рядка в архіві в
    /// результат не потрапляє — той самий контракт, що й у
    /// <c>NormalizedCellStore.ReadSlicesAsync</c>.
    /// </summary>
    public async Task<IReadOnlyDictionary<long, IReadOnlyList<CellRecord>>> ReadArchivedSlicesAsync(
        IReadOnlyList<long> tableInstanceIds, CancellationToken ct)
    {
        var result = new Dictionary<long, IReadOnlyList<CellRecord>>();

        foreach (var tableInstanceId in tableInstanceIds)
        {
            var cells = await ReadArchivedSliceAsync(tableInstanceId, ct).ConfigureAwait(false);
            if (cells.Count > 0)
            {
                result[tableInstanceId] = cells;
            }
        }

        return result;
    }

    /// <summary>Екземпляри таблиць документа з архіву за період.</summary>
    /// <remarks>
    /// ⚠ Сирий SQL, а не LINQ: <c>arc.*</c> немає в моделі EF навмисно (той
    /// самий коментар, що й на <see cref="ArchivedAsync"/>). Джойн на
    /// <c>doc.Document</c>/<c>doc.Project</c>, а не на <c>arc.*</c>-дзеркала
    /// цих таблиць — їх і не існує: документ і проєкт не архівуються, лише
    /// табличні дані (<c>12-archive-tables.sql</c>).
    /// </remarks>
    public async Task<IReadOnlyList<TableInstanceRef>> ReadArchivedTableInstancesAsync(
        long documentId, PeriodKey periodKey, CancellationToken ct)
        => await db.Database
            .SqlQuery<TableInstanceRef>($"""
                SELECT t.Id AS TableInstanceId, t.DocumentId, t.TableDefId,
                       p.TemplateVersionId, t.PeriodKey
                FROM arc.TableInstance t
                JOIN doc.Document d ON d.Id = t.DocumentId
                JOIN doc.Project p ON p.Id = d.ProjectId
                WHERE t.DocumentId = {documentId} AND t.PeriodKey = {periodKey.Value}
                ORDER BY t.Id
                """)
            .ToListAsync(ct)
            .ConfigureAwait(false);

    /// <summary>Чи має документ хоч один архівний екземпляр таблиці в цьому періоді.</summary>
    /// <remarks>
    /// ⚠ Дешевий <c>EXISTS</c> (<c>TOP (1)</c>), а не повний
    /// <see cref="ReadArchivedTableInstancesAsync"/>: викликач
    /// (<c>RowStore.EnsureTableInstancesAsync</c>) хоче лише «архівовано чи
    /// ні», а не самі рядки.
    /// </remarks>
    public async Task<bool> HasArchivedTableInstancesAsync(long documentId, PeriodKey periodKey, CancellationToken ct)
    {
        var found = await db.Database
            .SqlQuery<int>($"""
                SELECT TOP (1) 1 AS Value
                FROM arc.TableInstance
                WHERE DocumentId = {documentId} AND PeriodKey = {periodKey.Value}
                """)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return found.Count > 0;
    }

    /// <summary>Ключі й ідентифікатори рядків з архіву одного екземпляра таблиці.</summary>
    /// <remarks>
    /// ⚠ Спільна точка для <see cref="ReadArchivedRowVersionsAsync"/> і
    /// <see cref="ReadArchivedOrphanFlagsAsync"/> нижче: обидва — той самий
    /// перелік рядків із дефолтним значенням замість поля, якого немає в
    /// <c>arc.TableRow</c>. Дублювати запит під кожен із них означало б три
    /// копії того самого <c>WHERE</c>.
    /// </remarks>
    public async Task<IReadOnlyList<(string RowKey, long Id)>> ReadArchivedRowIdsAsync(
        long tableInstanceId, PeriodKey periodKey, CancellationToken ct)
    {
        var rows = await db.Database
            .SqlQuery<ArchivedRowIdentity>($"""
                SELECT r.RowKey, r.Id
                FROM arc.TableRow r
                WHERE r.TableInstanceId = {tableInstanceId} AND r.PeriodKey = {periodKey.Value}
                ORDER BY r.Id
                """)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return [.. rows.Select(r => (r.RowKey, r.Id))];
    }

    /// <summary>Версії рядків з архіву — завжди порожній рядок.</summary>
    /// <remarks>
    /// ⚠ <c>arc.TableRow</c> НЕ МАЄ колонки <c>RowVersion</c>
    /// (<c>12-archive-tables.sql</c>): архів копіює лише поля, потрібні для
    /// читання даних, а не для оптимістичного блокування запису. Заархівовані
    /// проєкти не редагуються (<c>EditRules.cs</c>,
    /// <c>EditDenyReason.ProjectArchived</c>), тож версії рядка немає кому
    /// звіряти. Порожній рядок — свідомий дефолт, а не забутий стовпець: він
    /// НЕ є валідним Base64 <c>rowversion</c> (той завжди 8 байт і
    /// непорожній), тож звірка <c>baseVersion</c> на архівному рядку
    /// провалиться явно, а не збіжиться випадково.
    /// </remarks>
    public async Task<IReadOnlyList<(string RowKey, string RowVersion)>> ReadArchivedRowVersionsAsync(
        long tableInstanceId, PeriodKey periodKey, CancellationToken ct)
    {
        var rows = await ReadArchivedRowIdsAsync(tableInstanceId, periodKey, ct).ConfigureAwait(false);
        return [.. rows.Select(r => (r.RowKey, string.Empty))];
    }

    /// <summary>Ознаки «осиротілості» рядків з архіву — завжди <c>false</c>.</summary>
    /// <remarks>
    /// ⚠ <c>arc.TableRow</c> НЕ МАЄ колонки <c>IsOrphaned</c>
    /// (<c>12-archive-tables.sql</c>): нічний <c>OrphanScanJob</c> архіву не
    /// торкається (архівні проєкти не редагуються), тож ставити прапорець
    /// там нема кому і споживати його нема кому — перегляд архівного
    /// документа не пропонує дію «видалити осиротілий рядок» узагалі.
    /// <c>false</c> — свідомий дефолт, а не обчислення.
    /// </remarks>
    public async Task<IReadOnlyList<(long Id, bool IsOrphaned)>> ReadArchivedOrphanFlagsAsync(
        long tableInstanceId, PeriodKey periodKey, CancellationToken ct)
    {
        var rows = await ReadArchivedRowIdsAsync(tableInstanceId, periodKey, ct).ConfigureAwait(false);
        return [.. rows.Select(r => (r.Id, false))];
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
        long? ValueRegistryEntryId,
        int? ValueUnitId,
        bool IsCalculated,
        bool IsEmpty);

    /// <summary>Ключ і ідентифікатор рядка — форма проєкції архівного <c>arc.TableRow</c>.</summary>
    private sealed record ArchivedRowIdentity(string RowKey, long Id);
}
