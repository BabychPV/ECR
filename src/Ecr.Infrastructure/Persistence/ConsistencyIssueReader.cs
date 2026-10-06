using Ecr.Application.Common;
using Ecr.Application.Ports;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>Читання <c>aud.ConsistencyIssue</c> прямим ADO.</summary>
/// <remarks>
/// ⚠ Та сама причина, що й у <see cref="AuditReader"/>: таблиці <c>aud.*</c>
/// живуть поза моделлю EF (журнал append-only, обліковий запис застосунку має
/// на ньому лише <c>INSERT</c> і <c>SELECT</c>). Сама задача
/// <c>ConsistencyCheckJob</c> пише туди теж сирим <c>MERGE</c>, не через
/// <c>DbSet</c>, — читач мусить бути з нею з одного боку межі.
/// </remarks>
public sealed class ConsistencyIssueReader(EcrDbContext db) : IConsistencyIssueReader
{
    /// <inheritdoc />
    public async Task<ConsistencyIssuePage> ReadIssuesAsync(
        string? ruleCode, bool openOnly, byte? severity, string? query, CursorRequest page, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(page);

        await using var connection = new SqlConnection(db.Database.GetConnectionString());
        await connection.OpenAsync(ct).ConfigureAwait(false);

        await using var command = connection.CreateCommand();

        // ⚠ Курсор іде ВНИЗ (`Id < @before`), бо й порядок спадний: журнал
        // читають із кінця — «що знайшлося цієї ночі», а не «що знайшлося
        // першого дня». Початок відліку — `long.MaxValue`, а не 0: із нулем
        // перша сторінка була б порожня завжди.
        //
        // ⚠ `TOP (@take)` бере на один більше за розмір сторінки — рівно так
        // само, як `AuditReader`: це і є ознака «є ще», без окремого COUNT.
        command.CommandText = $"""
            SELECT TOP (@take)
                   Id, DetectedAt, Severity, RuleCode, EntityType, EntityId,
                   Message, ResolvedAt, ResolvedByUserId
              FROM aud.ConsistencyIssue
             WHERE Id < @before
                   {(ruleCode is null ? string.Empty : "AND RuleCode = @ruleCode")}
                   {(openOnly ? "AND ResolvedAt IS NULL" : string.Empty)}
                   {(severity is null ? string.Empty : "AND Severity = @severity")}
                   {(query is null ? string.Empty : QueryPredicate)}
             ORDER BY Id DESC;
            """;

        command.Parameters.AddWithValue("@take", page.Limit + 1);
        command.Parameters.AddWithValue("@before", DecodeBefore(page.Cursor));
        if (ruleCode is not null)
        {
            command.Parameters.AddWithValue("@ruleCode", ruleCode);
        }

        if (severity is not null)
        {
            command.Parameters.AddWithValue("@severity", severity.Value);
        }

        if (query is not null)
        {
            command.Parameters.AddWithValue("@q", "%" + EscapeLike(query) + "%");
        }

        var rows = new List<(long Id, ConsistencyIssueView View)>();
        await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var id = reader.GetInt64(0);

                rows.Add((
                    id,
                    new ConsistencyIssueView(
                        id,
                        DateTime.SpecifyKind(reader.GetDateTime(1), DateTimeKind.Utc),
                        reader.GetByte(2),
                        reader.GetString(3),
                        reader.IsDBNull(4) ? null : reader.GetString(4),
                        reader.IsDBNull(5) ? null : reader.GetInt64(5),
                        reader.GetString(6),
                        reader.IsDBNull(7)
                            ? null
                            : DateTime.SpecifyKind(reader.GetDateTime(7), DateTimeKind.Utc),
                        reader.IsDBNull(8) ? null : reader.GetInt32(8))));
            }
        }

        var hasMore = rows.Count > page.Limit;
        var items = rows.Take(page.Limit).Select(r => r.View).ToList();

        // ⚠ Лічильники — за тими самими фільтрами, КРІМ ваги: вкладки ваги на
        // екрані мають показувати, скільки було б у кожній, а не нулі в усіх,
        // крім обраної. Журнал системний (право `System.ViewHealth`), стеля
        // знахідок за прохід — тисяча: один агрегат, без індексу.
        await using var totalsCommand = connection.CreateCommand();
        totalsCommand.CommandText = $"""
            SELECT COALESCE(SUM(CASE WHEN Severity = 1 THEN 1 ELSE 0 END), 0),
                   COALESCE(SUM(CASE WHEN Severity = 2 THEN 1 ELSE 0 END), 0),
                   COALESCE(SUM(CASE WHEN Severity = 3 THEN 1 ELSE 0 END), 0),
                   COUNT(*)
              FROM aud.ConsistencyIssue
             WHERE 1 = 1
                   {(ruleCode is null ? string.Empty : "AND RuleCode = @ruleCode")}
                   {(openOnly ? "AND ResolvedAt IS NULL" : string.Empty)}
                   {(query is null ? string.Empty : QueryPredicate)};
            """;
        if (ruleCode is not null)
        {
            totalsCommand.Parameters.AddWithValue("@ruleCode", ruleCode);
        }

        if (query is not null)
        {
            totalsCommand.Parameters.AddWithValue("@q", "%" + EscapeLike(query) + "%");
        }

        ConsistencySeverityTotals totals;
        int allMatching;
        await using (var totalsReader = await totalsCommand.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            await totalsReader.ReadAsync(ct).ConfigureAwait(false);
            totals = new ConsistencySeverityTotals(
                Info: totalsReader.GetInt32(0),
                Warnings: totalsReader.GetInt32(1),
                Errors: totalsReader.GetInt32(2));
            allMatching = totalsReader.GetInt32(3);
        }

        var totalCount = severity switch
        {
            1 => totals.Info,
            2 => totals.Warnings,
            3 => totals.Errors,
            _ => allMatching,
        };

        return new ConsistencyIssuePage(
            items,
            hasMore ? Cursor.Encode(rows[page.Limit - 1].Id) : null,
            totalCount,
            totals);
    }

    /// <inheritdoc />
    public async Task<ConsistencySummary> ReadSummaryAsync(bool openOnly, CancellationToken ct)
    {
        await using var connection = new SqlConnection(db.Database.GetConnectionString());
        await connection.OpenAsync(ct).ConfigureAwait(false);

        await using var command = connection.CreateCommand();

        // ⚠ Один прохід по таблиці, без індексу: стеля знахідок за прохід —
        // тисяча, і журнал читає лише `System.ViewHealth`. `Total` рахується
        // окремо від трьох відомих ваг, щоб невідома вага (якщо правило
        // колись її введе) не зникла зі суми мовчки.
        command.CommandText = $"""
            SELECT COALESCE(SUM(CASE WHEN Severity = 1 THEN 1 ELSE 0 END), 0),
                   COALESCE(SUM(CASE WHEN Severity = 2 THEN 1 ELSE 0 END), 0),
                   COALESCE(SUM(CASE WHEN Severity = 3 THEN 1 ELSE 0 END), 0),
                   COUNT(*),
                   MAX(DetectedAt)
              FROM aud.ConsistencyIssue
             WHERE 1 = 1
                   {(openOnly ? "AND ResolvedAt IS NULL" : string.Empty)};
            """;

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        await reader.ReadAsync(ct).ConfigureAwait(false);

        return new ConsistencySummary(
            reader.GetInt32(0),
            reader.GetInt32(1),
            reader.GetInt32(2),
            reader.GetInt32(3),
            reader.IsDBNull(4) ? null : DateTime.SpecifyKind(reader.GetDateTime(4), DateTimeKind.Utc));
    }

    /// <inheritdoc />
    /// <remarks>
    /// ⛔ Лише КОДИ й ідентифікатори для рішення про видимість: жодного значення комірки
    /// (<c>doc.CellValue</c> читається не далі рядка), жодної назви. Один запит на тип
    /// сутності, і його розмір обмежений сторінкою (<c>Take</c> = кількість ключів).
    /// ⚠ <c>doc.CellValue.EntityId</c> — це <c>TableRowId</c>, а не комірка: колонка не
    /// визначається (в рядку може бути кілька осиротілих комірок). <c>doc.TableRow</c>
    /// (<c>BROKEN_FK</c>) — екземпляра таблиці вже немає, документ невідомий; <c>itg.ArchiveRun</c>
    /// не має місця в структурі документа. Для них — порожньо.
    /// </remarks>
    public async Task<IReadOnlyDictionary<(string EntityType, long EntityId), ConsistencyLocation>> ResolveLocationsAsync(
        IReadOnlyCollection<(string EntityType, long EntityId)> entities, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entities);

        var result = new Dictionary<(string EntityType, long EntityId), ConsistencyLocation>();

        var rowIds = entities
            .Where(e => e.EntityType == "doc.CellValue")
            .Select(e => e.EntityId)
            .Distinct()
            .ToList();

        if (rowIds.Count > 0)
        {
            var rows = await (
                from row in db.TableRows.AsNoTracking()
                where rowIds.Contains(row.Id)
                join instance in db.TableInstances.AsNoTracking()
                    on new { row.PeriodKeyValue, Id = row.TableInstanceId }
                    equals new { instance.PeriodKeyValue, instance.Id }
                join document in db.Documents.AsNoTracking() on instance.DocumentId equals document.Id
                join project in db.Projects.AsNoTracking() on document.ProjectId equals project.Id
                join table in db.TableDefs.AsNoTracking() on instance.TableDefId equals table.Id
                join sheet in db.SheetDefs.AsNoTracking() on table.SheetDefId equals sheet.Id
                orderby row.Id
                select new
                {
                    RowId = row.Id,
                    row.PeriodKeyValue,
                    RowKey = row.RowKeyValue,
                    DocumentId = document.Id,
                    document.BusinessKey,
                    document.ProjectId,
                    project.TemplateVersionId,
                    SheetDefId = sheet.Id,
                    SheetCode = sheet.Code,
                    TableDefId = table.Id,
                    TableCode = table.Code,
                })
                .Take(rowIds.Count)
                .ToListAsync(ct)
                .ConfigureAwait(false);

            foreach (var r in rows)
            {
                result[("doc.CellValue", r.RowId)] = new ConsistencyLocation(
                    r.ProjectId,
                    r.TemplateVersionId,
                    r.PeriodKeyValue,
                    r.SheetDefId,
                    r.TableDefId,
                    new ConsistencyIssueWhere(r.DocumentId, r.BusinessKey, r.SheetCode, r.TableCode, r.RowKey, null));
            }
        }

        var columnIds = entities
            .Where(e => e.EntityType == "cfg.ColumnDef" && e.EntityId <= int.MaxValue)
            .Select(e => (int)e.EntityId)
            .Distinct()
            .ToList();

        if (columnIds.Count > 0)
        {
            var columns = await (
                from column in db.ColumnDefs.AsNoTracking()
                where columnIds.Contains(column.Id)
                join table in db.TableDefs.AsNoTracking() on column.TableDefId equals table.Id
                join sheet in db.SheetDefs.AsNoTracking() on table.SheetDefId equals sheet.Id
                orderby column.Id
                select new
                {
                    ColumnId = column.Id,
                    ColumnCode = column.Code,
                    TableDefId = table.Id,
                    TableCode = table.Code,
                    SheetDefId = sheet.Id,
                    SheetCode = sheet.Code,
                    sheet.TemplateVersionId,
                })
                .Take(columnIds.Count)
                .ToListAsync(ct)
                .ConfigureAwait(false);

            foreach (var c in columns)
            {
                result[("cfg.ColumnDef", c.ColumnId)] = new ConsistencyLocation(
                    null,
                    c.TemplateVersionId,
                    null,
                    c.SheetDefId,
                    c.TableDefId,
                    new ConsistencyIssueWhere(null, null, c.SheetCode, c.TableCode, null, c.ColumnCode));
            }
        }

        return result;
    }

    /// <summary>Умова пошуку: текст, код правила або номер сутності.</summary>
    private const string QueryPredicate = """
        AND (Message LIKE @q ESCAPE N'\'
             OR RuleCode LIKE @q ESCAPE N'\'
             OR CAST(EntityId AS nvarchar(20)) LIKE @q ESCAPE N'\')
        """;

    /// <summary>Екранує <c>% _ [ \</c>, щоб підрядок шукався буквально.</summary>
    private static string EscapeLike(string value)
        => value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal)
            .Replace("[", "\\[", StringComparison.Ordinal);

    /// <summary>
    /// Межа спадного курсора: <c>long.MaxValue</c> на першій сторінці.
    /// </summary>
    /// <remarks>
    /// ⛔ <see cref="Cursor.Decode"/> віддає <c>0</c> і на порожньому, і на
    /// зіпсованому курсорі — це правильно для ЗРОСТАЮЧОГО порядку, де нуль
    /// означає «з початку». Тут порядок спадний, і нуль означав би «раніше за
    /// найперший рядок», тобто порожню відповідь назавжди. Перетворення живе
    /// в одному місці саме тому, що помилка мовчазна: сторінка просто порожня.
    /// </remarks>
    private static long DecodeBefore(string? cursor)
    {
        var decoded = Cursor.Decode(cursor);

        return decoded == 0 ? long.MaxValue : decoded;
    }
}
