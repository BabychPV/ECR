// src/Ecr.Infrastructure/Persistence/FormulaErrorStore.cs
using System.Text.Json;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>✎ C-1 / D-475: реалізація <see cref="IFormulaErrorStore"/> над <see cref="EcrDbContext"/>.</summary>
/// <remarks>
/// ⛔ Таблиця тримає лише ПОТОЧНИЙ стан (як <c>calc.CalculationIssue</c>): заміна — «видалити рядки адрес, додати нові».
/// Видалення виконується одразу (у транзакції викликача — тій самій, що пише значення), нові рядки лише додаються в
/// контекст і зберігаються викликачем тим самим <c>SaveChanges</c>.
/// ⚠ <c>db.TableInstances</c> тут не використовується навмисно: <c>SheetCopyId</c> береться з самого рядка стану.
/// </remarks>
public sealed class FormulaErrorStore(EcrDbContext db) : IFormulaErrorStore
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<FormulaErrorRow>> ReadInstancesAsync(
        IReadOnlyCollection<long> tableInstanceIds, PeriodKey periodKey, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tableInstanceIds);
        if (tableInstanceIds.Count == 0)
        {
            return [];
        }

        var ids = tableInstanceIds.Distinct().ToList();
        var period = periodKey.Value;
        return await db.FormulaErrors
            .AsNoTracking()
            .Where(e => e.PeriodKey == period && ids.Contains(e.TableInstanceId))
            .Select(e => new FormulaErrorRow(
                e.DocumentId, e.PeriodKey, e.TableInstanceId, e.TableDefId, e.SheetCopyId, e.TableRowId, e.RowKey,
                e.ColumnDefId, e.FormulaDefId, e.Kind, e.ErrorCode, e.InputsJson, e.SourcePeriodKey, e.SourceTableDefId,
                e.SourceRowKey, e.SourceColumnDefId, e.Since))
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task ApplyAsync(
        IReadOnlyCollection<FormulaErrorRow> upserts, IReadOnlyCollection<CellAddress> deletes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(upserts);
        ArgumentNullException.ThrowIfNull(deletes);
        if (upserts.Count == 0 && deletes.Count == 0)
        {
            return;
        }

        // ОДИН DELETE за адресами upserts ∪ deletes: стан комірки замінюється, унікальний індекс не порушується.
        var keys = JsonSerializer.Serialize(
            upserts.Select(u => u.Address).Concat(deletes).Distinct()
                .Select(a => new { p = a.PeriodKey.Value, r = a.TableRowId, c = a.ColumnDefId }));
        await db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                DELETE e FROM calc.FormulaError AS e
                JOIN OPENJSON({keys}) WITH (PeriodKey int '$.p', TableRowId bigint '$.r', ColumnDefId int '$.c') AS k
                  ON k.PeriodKey = e.PeriodKey AND k.TableRowId = e.TableRowId AND k.ColumnDefId = e.ColumnDefId;
                """,
                ct)
            .ConfigureAwait(false);

        foreach (var row in upserts)
        {
            db.FormulaErrors.Add(new FormulaError(
                row.DocumentId, row.PeriodKey, row.TableInstanceId, row.TableDefId, row.SheetCopyId, row.TableRowId,
                row.RowKey, row.ColumnDefId, row.FormulaDefId, row.Kind, row.ErrorCode, row.InputsJson,
                row.SourcePeriodKey, row.SourceTableDefId, row.SourceRowKey, row.SourceColumnDefId, row.Since));
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<(int ColumnDefId, long EntryId), LocalizedText>> LookupNamesAsync(
        IReadOnlyCollection<(int ColumnDefId, long EntryId)> refs, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(refs);
        var result = new Dictionary<(int ColumnDefId, long EntryId), LocalizedText>();
        if (refs.Count == 0)
        {
            return result;
        }

        var columnIds = refs.Select(r => r.ColumnDefId).Distinct().ToList();
        var entryIds = refs.Select(r => r.EntryId).Distinct().ToList();

        // ✎ B78: запис має належати САМЕ довіднику колонки входу — «37» числової колонки записом не є.
        var rows = await (
                from c in db.ColumnDefs.AsNoTracking()
                where columnIds.Contains(c.Id) && c.LookupRegistryDefId != null
                join e in db.RegistryEntries.AsNoTracking() on c.LookupRegistryDefId equals (int?)e.RegistryDefId
                where entryIds.Contains(e.Id)
                select new { ColumnId = c.Id, EntryId = e.Id, e.DisplayL10n })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var wanted = refs.ToHashSet();
        foreach (var row in rows.Where(r => wanted.Contains((r.ColumnId, r.EntryId))))
        {
            result[(row.ColumnId, row.EntryId)] = row.DisplayL10n;
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<FormulaErrorView>> ListAsync(
        long documentId, int periodKey, long? sheetCopyId, CancellationToken ct)
    {
        // ✎ B78: номер рядка — ПОЗИЦІЯ в сітці (з 1), а не `doc.TableRow.Ordinal`: у динамічних таблицях (Land T3_00)
        // Ordinal у всіх рядків однаковий (0), і банер показував «рядок 0» на кожній комірці. Порядок — той самий, що в
        // `GetTableSliceHandler`: Ordinal опису рядка шаблону за RowKey (немає — у кінець), далі `TableRow.Id`.
        // ✎ F13 P3-1 (замір: 1000 помилок у динамічній таблиці на 1000 рядків — медіана 20,3 с з корельованим
        // COUNT, O(n²) підзапитів): позиції — ОДИН прохід ROW_NUMBER() по рядках екземплярів із помилками, у тій самій
        // команді (EF вкладає сирий запит підзапитом). ⛔ Ключ партиції — у предикаті обох таблиць документа (WR-05).
        var positions = db.Database.SqlQueryRaw<RowPositionRow>(
            RowPositionsSql,
            new Microsoft.Data.SqlClient.SqlParameter("@positionsDocumentId", documentId),
            new Microsoft.Data.SqlClient.SqlParameter("@positionsPeriodKey", periodKey));

        return await (
                from e in db.FormulaErrors.AsNoTracking()
                where e.DocumentId == documentId && e.PeriodKey == periodKey && e.SheetCopyId == sheetCopyId
                join r in db.TableRows.AsNoTracking() on new { P = e.PeriodKey, Id = e.TableRowId } equals new { P = r.PeriodKeyValue, r.Id }
                join p in positions on new { P = e.PeriodKey, Id = e.TableRowId } equals new { P = p.PeriodKey, Id = p.RowId }
                where !r.IsDeleted
                join t in db.TableDefs.AsNoTracking() on e.TableDefId equals t.Id
                join c in db.ColumnDefs.AsNoTracking() on e.ColumnDefId equals c.Id
                join f in db.FormulaDefs.AsNoTracking() on e.FormulaDefId equals f.Id into fs
                from f in fs.DefaultIfEmpty()
                join st in db.TableDefs.AsNoTracking() on e.SourceTableDefId equals (int?)st.Id into sts
                from st in sts.DefaultIfEmpty()
                join sc in db.ColumnDefs.AsNoTracking() on e.SourceColumnDefId equals (int?)sc.Id into scs
                from sc in scs.DefaultIfEmpty()
                join rd in db.RowDefs.AsNoTracking() on r.RowDefId equals (int?)rd.Id into rds
                from rd in rds.DefaultIfEmpty()
                orderby t.Id, p.Position, c.Ordinal
                select new FormulaErrorView(
                    new FormulaErrorRow(
                        e.DocumentId, e.PeriodKey, e.TableInstanceId, e.TableDefId, e.SheetCopyId, e.TableRowId, e.RowKey,
                        e.ColumnDefId, e.FormulaDefId, e.Kind, e.ErrorCode, e.InputsJson, e.SourcePeriodKey,
                        e.SourceTableDefId, e.SourceRowKey, e.SourceColumnDefId, e.Since),
                    t.Code, c.Code, c.HeaderL10n, p.Position,
                    f == null ? null : f.Expression,
                    st == null ? null : st.Code,
                    sc == null ? null : sc.Code,
                    rd == null ? null : rd.LabelL10n))
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// ✎ F13 P3-1: позиції рядків екземплярів із помилками документа за період — один прохід <c>ROW_NUMBER()</c> у порядку
    /// зрізу сітки (Ordinal опису рядка шаблону за RowKey, немає — у кінець; далі Id). Ключ партиції — в обох таблицях.
    /// </summary>
    private const string RowPositionsSql = """
        SELECT r.PeriodKey AS PeriodKey, r.Id AS RowId,
               CAST(ROW_NUMBER() OVER (PARTITION BY r.TableInstanceId
                                       ORDER BY ISNULL(d.Ordinal, 2147483647), r.Id) AS int) AS Position
          FROM doc.TableRow AS r
          JOIN (SELECT DISTINCT fe.TableInstanceId, fe.TableDefId
                  FROM calc.FormulaError AS fe
                 WHERE fe.DocumentId = @positionsDocumentId AND fe.PeriodKey = @positionsPeriodKey) AS i
            ON i.TableInstanceId = r.TableInstanceId
         OUTER APPLY (SELECT MIN(rd.Ordinal) AS Ordinal
                        FROM cfg.RowDef AS rd
                       WHERE rd.TableDefId = i.TableDefId AND rd.RowKey = r.RowKey AND rd.IsDeleted = 0) AS d
         WHERE r.PeriodKey = @positionsPeriodKey AND r.IsDeleted = 0
        """;

    /// <summary>✎ F13 P3-1: позиція рядка в сітці — рядок сирого запиту <c>ROW_NUMBER()</c> у <see cref="ListAsync"/>.</summary>
    private sealed class RowPositionRow
    {
        public int PeriodKey { get; init; }

        public long RowId { get; init; }

        public int Position { get; init; }
    }
}
