// src/Ecr.Infrastructure/Persistence/RegistryImpactStore.cs
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>Реалізація <see cref="IRegistryImpactStore"/> над <see cref="EcrDbContext"/>.</summary>
public sealed class RegistryImpactStore(EcrDbContext db) : IRegistryImpactStore
{
    /// <inheritdoc />
    /// <remarks>
    /// ⚠ Зв'язок «документ → довідник» виводиться, а не зберігається: актуальний прогін
    /// (<c>Status = Current</c>) дав результат за версією методології, а ребра версії
    /// (<c>cfg.RegistryUse</c>, <c>SourceKind = 1</c>) кажуть, що ця версія читає довідник.
    /// Формули ШАБЛОНУ (<c>SourceKind = 0</c>) сюди не входять — їх ребра пише публікація
    /// шаблону (RT-24), а результатів у <c>calc.CalculationResult</c> вони не лишають.
    /// <para>
    /// ⛔ Фільтр стану періоду — суть виміру: <c>Open</c>/<c>Grace</c>, без <c>Closed</c> (<c>D-39</c>).
    /// </para>
    /// <para>
    /// ⛔ Зачеплений — лише прогін, що ПОЧАВСЯ до правки довідника (<c>DataChangedAt &gt; StartedAt</c>),
    /// тим самим правилом, що й банер свіжості (<c>MethodologyStore.GetCalculationFreshnessAsync</c>).
    /// Доти перелік показував кожен документ, що колись читав довідник, і перерахований документ
    /// лишався в ньому назавжди. Довідник без жодної правки (<c>DataChangedAt = null</c>) не зачепив нічого.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<RegistryImpactRow>> ListImpactedAsync(
        int registryDefId, int take, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(take);
        var limit = Math.Min(take, IRegistryImpactStore.MaxRows);

        var changedAt = await db.RegistryDefs.AsNoTracking()
            .Where(r => r.Id == registryDefId)
            .Select(r => r.DataChangedAt)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (changedAt is null)
        {
            return [];
        }

        var query =
            from use in db.RegistryUses.AsNoTracking()
            where use.SourceKind == RegistryUse.MethodologyVersionSource && use.RegistryDefId == registryDefId
            join result in db.CalculationResults.AsNoTracking()
                on use.SourceId equals result.MethodologyVersionId
            join run in db.CalculationRuns.AsNoTracking() on result.CalculationRunId equals run.Id
            where run.Status == CalculationRun.CurrentStatus && run.StartedAt < changedAt
            join document in db.Documents.AsNoTracking() on result.DocumentId equals document.Id
            join period in db.Periods.AsNoTracking()
                on new { document.ProjectId, result.PeriodKey }
                equals new { period.ProjectId, PeriodKey = period.PeriodKeyValue }
            where period.State == PeriodState.Open || period.State == PeriodState.Grace
            join version in db.MethodologyVersions.AsNoTracking() on use.SourceId equals version.Id
            join methodology in db.Methodologies.AsNoTracking() on version.MethodologyId equals methodology.Id
            select new
            {
                document.Id,
                document.BusinessKey,
                document.ProjectId,
                result.PeriodKey,
                period.State,
                MethodologyCode = methodology.Code,
            };

        var rows = await query
            .Distinct()
            .OrderBy(r => r.Id).ThenBy(r => r.PeriodKey).ThenBy(r => r.MethodologyCode)
            .Take(limit)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return [.. rows.Select(r => new RegistryImpactRow(
            r.Id, r.BusinessKey, r.ProjectId, r.PeriodKey, r.State, r.MethodologyCode))];
    }
}
