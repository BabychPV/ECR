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

        if (evaluatedTableInstanceIds.Count == 0)
        {
            return 0;
        }

        // Період — ключ проходу; прогін без періоду (рік без екземплярів) нічого не перераховував.
        var period = await db.CalculationRuns
            .AsNoTracking()
            .Where(r => r.Id == calculationRunId)
            .Select(r => r.PeriodKey ?? 0)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (period == 0)
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
                    issue.RowKey, issue.RowNumber, issue.MethodologyCode, calculationRunId, now));
            }
        }

        return dropped;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CalculationIssueRow>> ListAsync(
        long documentId, int periodKey, long? sheetCopyId, CancellationToken ct)
        => await (
                from i in db.CalculationIssues.AsNoTracking()
                where i.DocumentId == documentId && i.PeriodKey == periodKey && i.SheetCopyId == sheetCopyId
                join t in db.TableDefs.AsNoTracking() on i.TableDefId equals t.Id
                orderby t.Id, i.RowNumber
                select new CalculationIssueRow(
                    i.TableDefId, t.Code, i.SheetCopyId, i.RowNumber, i.RowKey, i.Kind, i.MethodologyCode, i.DetectedAt))
            .ToListAsync(ct)
            .ConfigureAwait(false);
}
