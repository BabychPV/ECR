using Ecr.Application.Ports;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>✎ D-461: реалізація <see cref="IFormulaOverrideStore"/> над <see cref="EcrDbContext"/>.</summary>
public sealed class FormulaOverrideStore(EcrDbContext db) : IFormulaOverrideStore
{
    /// <inheritdoc />
    public async Task<FormulaOverrideTarget?> FindFormulaAsync(int templateVersionId, int formulaId, CancellationToken ct)
    {
        var row = await (
                from f in db.FormulaDefs.AsNoTracking()
                join t in db.TableDefs.AsNoTracking() on f.TableDefId equals t.Id
                join s in db.SheetDefs.AsNoTracking() on t.SheetDefId equals s.Id
                where f.Id == formulaId && !f.IsDeleted && s.TemplateVersionId == templateVersionId
                select new { f.Id, f.TableDefId, f.Scope, f.ColumnDefId, f.RowDefId, f.AllowManualOverride })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (row is null)
        {
            return null;
        }

        string? rowKey = null;
        if (row.RowDefId is { } rowDefId)
        {
            rowKey = await db.RowDefs.AsNoTracking()
                .Where(r => r.Id == rowDefId)
                .Select(r => r.RowKeyValue)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);
        }

        return new FormulaOverrideTarget(
            row.Id, templateVersionId, row.TableDefId, row.Scope, row.ColumnDefId, rowKey, row.AllowManualOverride);
    }

    /// <inheritdoc />
    /// <remarks>
    /// ⚠ Сирий UPDATE лише одного стовпця: тригер незмінності <c>TR_FormulaDef_Immutable</c> пропускає в
    /// Published/Deprecated рівно таку зміну (усі структурні поля незмінні) — і відхилив би будь-яку іншу.
    /// </remarks>
    public async Task SetAllowManualOverrideAsync(int formulaId, bool allow, CancellationToken ct)
        => await db.Database
            .ExecuteSqlInterpolatedAsync(
                $"UPDATE cfg.FormulaDef SET AllowManualOverride = {allow} WHERE Id = {formulaId};", ct)
            .ConfigureAwait(false);

    /// <inheritdoc />
    /// <remarks>
    /// ⚠ <c>ColumnDefId</c> належить версії шаблону, тож лічильник — рівно документи ЦІЄЇ версії. Лише наявні рядки
    /// (видалений рядок своєї позначки вже не показує).
    /// </remarks>
    public async Task<FormulaOverrideCounts> CountAsync(FormulaOverrideTarget target, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);

        var marks =
            from o in db.CellFormulaOverrides.AsNoTracking()
            join r in db.TableRows.AsNoTracking()
                on new { o.PeriodKeyValue, o.TableRowId } equals new { r.PeriodKeyValue, TableRowId = r.Id }
            join i in db.TableInstances.AsNoTracking()
                on new { r.PeriodKeyValue, r.TableInstanceId } equals new { i.PeriodKeyValue, TableInstanceId = i.Id }
            where !r.IsDeleted && i.TableDefId == target.TableDefId
            select new { i.DocumentId, r.RowKeyValue, o.ColumnDefId };

        if (target.ColumnDefId is { } columnId)
        {
            marks = marks.Where(m => m.ColumnDefId == columnId);
        }

        if (target.Scope != FormulaScope.Column && target.RowKey is { } rowKey)
        {
            marks = marks.Where(m => m.RowKeyValue == rowKey);
        }

        var cells = await marks.CountAsync(ct).ConfigureAwait(false);
        var documents = cells == 0
            ? 0
            : await marks.Select(m => m.DocumentId).Distinct().CountAsync(ct).ConfigureAwait(false);

        return new FormulaOverrideCounts(documents, cells);
    }

    /// <inheritdoc />
    /// <remarks>⚠ Колонка належить рівно одній версії, тож «позначка на колонці версії» = «позначка в документі версії».</remarks>
    public async Task<bool> AnyForVersionAsync(int templateVersionId, CancellationToken ct)
        => await (
                from o in db.CellFormulaOverrides.AsNoTracking()
                join c in db.ColumnDefs.AsNoTracking() on o.ColumnDefId equals c.Id
                join t in db.TableDefs.AsNoTracking() on c.TableDefId equals t.Id
                join s in db.SheetDefs.AsNoTracking() on t.SheetDefId equals s.Id
                where s.TemplateVersionId == templateVersionId
                select o.TableRowId)
            .AnyAsync(ct)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IReadOnlyList<FormulaOverrideMark>> ReadInstanceAsync(
        long tableInstanceId, PeriodKey periodKey, CancellationToken ct)
    {
        var key = periodKey.Value;

        // ⚠ Рівність за PeriodKey на ОБОХ таблицях — відсічка партицій (як у CellStyleStore). LEFT JOIN автора:
        // видалений користувач не ховає позначку.
        var rows = await (
                from o in db.CellFormulaOverrides.AsNoTracking()
                where o.PeriodKeyValue == key
                      && o.TableInstanceId == tableInstanceId
                      && db.TableRows.Any(r => r.PeriodKeyValue == key && r.Id == o.TableRowId && !r.IsDeleted)
                join u in db.Users.AsNoTracking() on o.SetByUserId equals u.Id into users
                from u in users.DefaultIfEmpty()
                select new { o.TableRowId, o.ColumnDefId, o.Reason, o.SetByUserId, Name = u == null ? null : u.DisplayName, o.SetAt })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return [.. rows.Select(r => new FormulaOverrideMark(
            r.TableRowId, r.ColumnDefId, r.Reason, r.SetByUserId, r.Name,
            DateTime.SpecifyKind(r.SetAt, DateTimeKind.Utc)))];
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<CellAddress, CellFormulaOverride>> ReadAsync(
        IReadOnlyCollection<CellAddress> addresses, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(addresses);

        var result = new Dictionary<CellAddress, CellFormulaOverride>();
        if (addresses.Count == 0)
        {
            return result;
        }

        var wanted = addresses.ToHashSet();
        foreach (var byPeriod in addresses.GroupBy(a => a.PeriodKey.Value))
        {
            var key = byPeriod.Key;

            // ⚠ Чанки по 1000 рядків: колекція розгортається в параметри (стеля SQL Server — 2100).
            foreach (var chunk in byPeriod.Select(a => a.TableRowId).Distinct().Chunk(1000))
            {
                var ids = chunk.ToList();
                var found = await db.CellFormulaOverrides.AsNoTracking()
                    .Where(o => o.PeriodKeyValue == key && ids.Contains(o.TableRowId))
                    .ToListAsync(ct)
                    .ConfigureAwait(false);

                foreach (var mark in found.Where(m => wanted.Contains(m.Address)))
                {
                    result[mark.Address] = mark;
                }
            }
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<IReadOnlySet<CellAddress>> ReadKeysAsync(
        IReadOnlyCollection<long> tableInstanceIds, PeriodKey periodKey, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tableInstanceIds);

        var result = new HashSet<CellAddress>();
        if (tableInstanceIds.Count == 0)
        {
            return result;
        }

        var key = periodKey.Value;
        foreach (var chunk in tableInstanceIds.Distinct().Chunk(1000))
        {
            var ids = chunk.ToList();
            var rows = await db.CellFormulaOverrides.AsNoTracking()
                .Where(o => o.PeriodKeyValue == key && ids.Contains(o.TableInstanceId))
                .Select(o => new { o.TableRowId, o.ColumnDefId })
                .ToListAsync(ct)
                .ConfigureAwait(false);

            foreach (var row in rows)
            {
                result.Add(new CellAddress(periodKey, row.TableRowId, row.ColumnDefId));
            }
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ProjectFormulaOverride>> ReadProjectAsync(int projectId, CancellationToken ct)
    {
        var rows = await (
                from o in db.CellFormulaOverrides.AsNoTracking()
                join r in db.TableRows.AsNoTracking()
                    on new { o.PeriodKeyValue, o.TableRowId } equals new { r.PeriodKeyValue, TableRowId = r.Id }
                join i in db.TableInstances.AsNoTracking()
                    on new { r.PeriodKeyValue, r.TableInstanceId } equals new { i.PeriodKeyValue, TableInstanceId = i.Id }
                join d in db.Documents.AsNoTracking() on i.DocumentId equals d.Id
                where d.ProjectId == projectId && !r.IsDeleted
                join c in db.CellValues.AsNoTracking()
                    on new { o.PeriodKeyValue, o.TableRowId, o.ColumnDefId }
                    equals new { c.PeriodKeyValue, c.TableRowId, c.ColumnDefId } into cells
                from c in cells.DefaultIfEmpty()
                select new
                {
                    o.PeriodKeyValue, o.TableRowId, o.ColumnDefId, i.DocumentId, r.RowKeyValue,
                    Numeric = c == null ? null : c.ValueNumeric,
                    Text = c == null ? null : c.ValueString,
                })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return [.. rows.Select(r => new ProjectFormulaOverride(
            new CellAddress(new PeriodKey(r.PeriodKeyValue), r.TableRowId, r.ColumnDefId),
            r.DocumentId,
            r.RowKeyValue,
            r.Numeric?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? r.Text))];
    }

    /// <inheritdoc />
    /// <remarks>
    /// ⚠ ОДНА команда на будь-яку кількість адрес (адреси — JSON-параметром, <c>OPENJSON</c>): число звернень не
    /// росте з кількістю комірок (храповик звернень).
    /// </remarks>
    public async Task ReleaseAsync(IReadOnlyList<CellAddress> addresses, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(addresses);
        if (addresses.Count == 0)
        {
            return;
        }

        var json = AddressesJson(addresses);
        await db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                 DECLARE @a TABLE (PeriodKey int NOT NULL, TableRowId bigint NOT NULL, ColumnDefId int NOT NULL,
                                   PRIMARY KEY (PeriodKey, TableRowId, ColumnDefId));
                 INSERT @a SELECT DISTINCT p, r, c FROM OPENJSON({json}) WITH (p int, r bigint, c int);
                 DELETE o FROM doc.CellFormulaOverride o
                   JOIN @a a ON a.PeriodKey = o.PeriodKey AND a.TableRowId = o.TableRowId AND a.ColumnDefId = o.ColumnDefId;
                 UPDATE v SET v.IsCalculated = 1 FROM doc.CellValue v
                   JOIN @a a ON a.PeriodKey = v.PeriodKey AND a.TableRowId = v.TableRowId AND a.ColumnDefId = v.ColumnDefId;
                 """,
                ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// ⚠ Сирий SQL у ПОТОЧНІЙ транзакції контексту (як сховище комірок): позначка, значення й журнал — один коміт.
    /// Оновлення наявної позначки міняє причину, автора й час (нова причина — новий запис журналу).
    /// ⚠ Набором: не більше ДВОХ команд на батч (зняття й постановка), хоч би скільки комірок — JSON-параметр і
    /// <c>OPENJSON</c>, без параметра на комірку (стеля 2100) і без команди на комірку.
    /// </remarks>
    public async Task ApplyAsync(
        IReadOnlyList<CellFormulaOverride> upserts, IReadOnlyList<CellAddress> removals, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(upserts);
        ArgumentNullException.ThrowIfNull(removals);

        if (removals.Count > 0)
        {
            var json = AddressesJson(removals);
            await db.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                     DELETE o FROM doc.CellFormulaOverride o
                       JOIN OPENJSON({json}) WITH (p int, r bigint, c int) a
                         ON a.p = o.PeriodKey AND a.r = o.TableRowId AND a.c = o.ColumnDefId;
                     """,
                    ct)
                .ConfigureAwait(false);
        }

        if (upserts.Count > 0)
        {
            var json = System.Text.Json.JsonSerializer.Serialize(
                upserts
                    .GroupBy(m => m.Address)
                    .Select(g => g.Last())
                    .Select(m => new
                    {
                        p = m.PeriodKeyValue,
                        r = m.TableRowId,
                        c = m.ColumnDefId,
                        i = m.TableInstanceId,
                        reason = m.Reason,
                        u = m.SetByUserId,
                        at = m.SetAt.ToString("yyyy-MM-ddTHH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture),
                    }));

            await db.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                     MERGE doc.CellFormulaOverride WITH (HOLDLOCK) AS t
                     USING (SELECT p, r, c, i, reason, u, at
                              FROM OPENJSON({json})
                                   WITH (p int, r bigint, c int, i bigint, reason nvarchar(1000), u int, at datetime2(3))) AS s
                        ON t.PeriodKey = s.p AND t.TableRowId = s.r AND t.ColumnDefId = s.c
                     WHEN MATCHED THEN UPDATE SET Reason = s.reason, SetByUserId = s.u, SetAt = s.at, TableInstanceId = s.i
                     WHEN NOT MATCHED THEN INSERT (PeriodKey, TableRowId, ColumnDefId, TableInstanceId, Reason, SetByUserId, SetAt)
                          VALUES (s.p, s.r, s.c, s.i, s.reason, s.u, s.at);
                     """,
                    ct)
                .ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// ⛔ <c>UPDLOCK, HOLDLOCK</c> з пошуком на КОЖНУ адресу (<c>LOOP JOIN</c> по PK): діапазонний замок і на відсутній
    /// ключ, тож вставка позначки паралельною правкою чекає на коміт перерахунку. Одна команда на батч.
    /// </remarks>
    public async Task<IReadOnlySet<CellAddress>> LockKeysAsync(IReadOnlyCollection<CellAddress> addresses, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(addresses);

        var result = new HashSet<CellAddress>();
        if (addresses.Count == 0)
        {
            return result;
        }

        var json = AddressesJson([.. addresses]);
        var rows = await db.Database
            .SqlQuery<LockedKey>(
                $"""
                 SELECT o.PeriodKey, o.TableRowId, o.ColumnDefId
                   FROM OPENJSON({json}) WITH (p int, r bigint, c int) AS a
                  INNER LOOP JOIN doc.CellFormulaOverride AS o WITH (UPDLOCK, HOLDLOCK)
                     ON o.PeriodKey = a.p AND o.TableRowId = a.r AND o.ColumnDefId = a.c
                 """)
            .OrderBy(k => k.PeriodKey).ThenBy(k => k.TableRowId).ThenBy(k => k.ColumnDefId)
            .Take(addresses.Count)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        foreach (var row in rows)
        {
            result.Add(new CellAddress(new PeriodKey(row.PeriodKey), row.TableRowId, row.ColumnDefId));
        }

        return result;
    }

    private sealed record LockedKey(int PeriodKey, long TableRowId, int ColumnDefId);

    private static string AddressesJson(IReadOnlyList<CellAddress> addresses)
        => System.Text.Json.JsonSerializer.Serialize(
            addresses.Distinct().Select(a => new { p = a.PeriodKey.Value, r = a.TableRowId, c = a.ColumnDefId }));
}
