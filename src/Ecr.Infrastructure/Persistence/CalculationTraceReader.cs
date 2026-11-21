// src/Ecr.Infrastructure/Persistence/CalculationTraceReader.cs
using Ecr.Application.Ports;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>✎ D-565 (PERF-03): реалізація <see cref="ICalculationTraceReader"/> над <see cref="EcrDbContext"/>.</summary>
/// <remarks>
/// ⚠ Пошук кроків рядка — у партиції періоду за індексом прогону (<c>IX_CalculationStep_Run</c>), далі фільтр документа й
/// ключа рядка: кроків одного прогону — тисячі, рядка — одиниці. <c>TraceJson</c> розпаковує сама сутність (дві колонки:
/// старий текст і стиснений <c>TraceJsonGz</c>).
/// </remarks>
public sealed class CalculationTraceReader(EcrDbContext db) : ICalculationTraceReader
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<CalculationStepRecord>> ReadRowStepsAsync(
        long calculationRunId, int periodKey, long documentId, long tableInstanceId, string rowKey, int max,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rowKey);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(max);

        var steps = await db.CalculationSteps
            .AsNoTracking()
            .Where(s => s.PeriodKey == periodKey
                        && s.CalculationRunId == calculationRunId
                        && s.DocumentId == documentId
                        && s.SourceRowKey == rowKey
                        && (s.TableInstanceId == tableInstanceId || s.TableInstanceId == null))
            .OrderBy(s => s.StepOrder)
            .ThenBy(s => s.Id)
            .Take(max + 1)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return steps.ConvertAll(s => new CalculationStepRecord(
            s.Id, s.StepOrder, s.StepCode, s.Expression, s.Value, s.TraceJson, s.Masked, s.SubstanceEntryId));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<long, string?>> ReadRunProfilesAsync(
        IReadOnlyCollection<long> calculationRunIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(calculationRunIds);
        if (calculationRunIds.Count == 0)
        {
            return new Dictionary<long, string?>();
        }

        var ids = calculationRunIds.Distinct().ToList();
        return await db.CalculationRuns
            .AsNoTracking()
            .Where(r => ids.Contains(r.Id))
            .ToDictionaryAsync(r => r.Id, r => r.ModulesProfileJson, ct)
            .ConfigureAwait(false);
    }
}
