// src/Ecr.Infrastructure/Persistence/CalculationIssueStore.cs
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Calculations;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>Реалізація <see cref="ICalculationIssueStore"/> над <see cref="EcrDbContext"/> (AN-69).</summary>
/// <remarks>
/// ⛔ Заміна — «видалити попередні рядки проходу, додати поточні»: таблиця тримає лише останній стан (рішення людини
/// 28.10). Видалення виконується одразу (у транзакції викликача), нові рядки лише додаються в контекст — їх
/// зберігає викликач тим самим <c>SaveChanges</c>, що й перемикання актуальності прогону.
/// </remarks>
public sealed class CalculationIssueStore(EcrDbContext db, IClock clock) : ICalculationIssueStore
{
    /// <inheritdoc />
    public async Task<int> ReplaceAsync(
        long calculationRunId,
        IReadOnlyCollection<long> evaluatedTableInstanceIds,
        IReadOnlyCollection<CalculationIssueInput> issues,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(evaluatedTableInstanceIds);
        ArgumentNullException.ThrowIfNull(issues);

        // Період — ключ проходу; прогін без періоду (рік без екземплярів) нічого не перераховував.
        var run = await db.CalculationRuns
            .AsNoTracking()
            .Where(r => r.Id == calculationRunId)
            .Select(r => new { Period = r.PeriodKey ?? 0, r.DocumentId })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        var period = run?.Period ?? 0;
        if (period == 0)
        {
            return 0;
        }

        // AN-160: самолікування - проблеми таблиць без жодної активної прив'язки методики (прив'язки зняли, а прохід цю
        // таблицю вже не відвідує) прибираються на кожному проході того самого документа й періоду.
        _ = await DeleteForUnboundTablesAsync(run!.DocumentId, period, null, ct).ConfigureAwait(false);

        if (evaluatedTableInstanceIds.Count == 0)
        {
            return 0;
        }

        var ids = evaluatedTableInstanceIds.Distinct().ToList();
        var instances = await db.TableInstances
            .AsNoTracking()
            .Where(t => t.PeriodKeyValue == period && ids.Contains(t.Id))
            .Select(t => new { t.Id, t.DocumentId, t.TableDefId, t.SheetCopyId })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // 1. Попередні проблеми цих (документ × таблиця × копія) - геть, навіть якщо поточних немає.
        foreach (var scope in instances.GroupBy(i => (i.DocumentId, i.SheetCopyId)))
        {
            var documentId = scope.Key.DocumentId;
            var copyId = scope.Key.SheetCopyId;
            var tableDefIds = scope.Select(i => i.TableDefId).Distinct().ToList();

            await (copyId is { } copy
                    ? db.CalculationIssues.Where(x => x.DocumentId == documentId && x.PeriodKey == period
                        && x.SheetCopyId == copy && tableDefIds.Contains(x.TableDefId))
                    : db.CalculationIssues.Where(x => x.DocumentId == documentId && x.PeriodKey == period
                        && x.SheetCopyId == null && tableDefIds.Contains(x.TableDefId)))
                .ExecuteDeleteAsync(ct)
                .ConfigureAwait(false);
        }

        // 2. Поточні - до стелі на таблицю, за номером рядка.
        var byInstance = instances.ToDictionary(i => i.Id);
        var now = clock.UtcNow;
        var dropped = 0;
        foreach (var table in issues.Where(i => byInstance.ContainsKey(i.TableInstanceId)).GroupBy(i => i.TableInstanceId))
        {
            var instance = byInstance[table.Key];
            var ordered = table.OrderBy(i => i.RowNumber).ToList();
            dropped += Math.Max(0, ordered.Count - ICalculationIssueStore.MaxPerTable);

            foreach (var issue in ordered.Take(ICalculationIssueStore.MaxPerTable))
            {
                db.CalculationIssues.Add(new CalculationIssue(
                    instance.DocumentId, period, instance.TableDefId, instance.SheetCopyId,
                    issue.RowKey, issue.RowNumber, issue.MethodologyId, calculationRunId, now));
            }
        }

        return dropped;
    }

    /// <inheritdoc />
    public async Task<int> DeleteForUnboundTablesAsync(
        long? documentId, int? periodKey, IReadOnlyCollection<int>? tableDefIds, CancellationToken ct)
    {
        if (tableDefIds is { Count: 0 })
        {
            return 0;
        }

        // Набір - один ExecuteDelete з NOT EXISTS по активних прив'язках; історії й аудиту тут немає (лише поточні рядки).
        var query = db.CalculationIssues
            .Where(i => !db.CalculationBindings.Any(b => b.TableDefId == i.TableDefId && b.IsActive));
        if (documentId is { } doc)
        {
            query = query.Where(i => i.DocumentId == doc);
        }

        if (periodKey is { } period)
        {
            query = query.Where(i => i.PeriodKey == period);
        }

        if (tableDefIds is not null)
        {
            var ids = tableDefIds.Distinct().ToList();
            query = query.Where(i => ids.Contains(i.TableDefId));
        }

        return await query.ExecuteDeleteAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CalculationIssueRow>> ListAsync(
        long documentId, int periodKey, long? sheetCopyId, CancellationToken ct)
        => await (
                from i in db.CalculationIssues.AsNoTracking()
                where i.DocumentId == documentId && i.PeriodKey == periodKey && i.SheetCopyId == sheetCopyId
                join t in db.TableDefs.AsNoTracking() on i.TableDefId equals t.Id
                join m in db.Methodologies.AsNoTracking() on i.MethodologyId equals (int?)m.Id into methods
                from m in methods.DefaultIfEmpty()
                orderby t.Id, i.RowNumber
                select new CalculationIssueRow(
                    i.TableDefId, t.Code, i.SheetCopyId, i.RowNumber, i.RowKey, i.Kind,
                    m == null ? null : m.Code, i.DetectedAt))
            .ToListAsync(ct)
            .ConfigureAwait(false);
}
