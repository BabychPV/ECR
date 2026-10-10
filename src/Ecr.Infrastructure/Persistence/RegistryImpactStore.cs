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
    /// <para>
    /// ⛔ N2-03: «почався» міряється від <c>COALESCE(InputsAsOfUtc, StartedAt)</c> — прогін, що переніс результати
    /// старішого прогону, лишає їх на довіднику того прогону. Інакше банер свідчив би «застаріло», а документа
    /// в переліку для перерахунку не було б.
    /// </para>
    /// </remarks>
    public Task<IReadOnlyList<RegistryImpactRow>> ListImpactedAsync(
        int registryDefId, IReadOnlyCollection<int>? projectIds, int take, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(take);
        var limit = Math.Min(take, IRegistryImpactStore.MaxRows);

        if (projectIds is { Count: 0 })
        {
            return Task.FromResult<IReadOnlyList<RegistryImpactRow>>([]);
        }

        return QueryAsync(registryDefId, Visible(projectIds), limit, ct);
    }

    /// <inheritdoc />
    /// <remarks>
    /// ⛔ D2-04: той самий запит (<see cref="QueryAsync"/>), що й перелік, лише звужений до
    /// названих документів у SQL і без <c>TOP</c>: вибраний документ не випадає через те, що перша
    /// тисяча рядків довідника належить іншим документам.
    /// </remarks>
    public Task<IReadOnlyList<RegistryImpactRow>> ListImpactedForDocumentsAsync(
        int registryDefId, IReadOnlyCollection<long> documentIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(documentIds);

        if (documentIds.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<RegistryImpactRow>>([]);
        }

        var ids = documentIds.Distinct().ToList();
        return QueryAsync(
            registryDefId, db.Documents.AsNoTracking().Where(d => ids.Contains(d.Id)), limit: null, ct);
    }

    /// <summary>Спільне тіло обох вибірок: документи — з <paramref name="documents"/>.</summary>
    /// <param name="registryDefId">Довідник.</param>
    /// <param name="documents">Документи, серед яких шукати.</param>
    /// <param name="limit">Стеля рядків; <c>null</c> — без стелі (межа — сам <paramref name="documents"/>).</param>
    /// <param name="ct">Токен скасування.</param>
    private async Task<IReadOnlyList<RegistryImpactRow>> QueryAsync(
        int registryDefId, IQueryable<Ecr.Domain.Entities.Documents.Document> documents, int? limit, CancellationToken ct)
    {
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
            where run.Status == CalculationRun.CurrentStatus
                  && (run.InputsAsOfUtc ?? run.StartedAt) < changedAt
            join document in documents on result.DocumentId equals document.Id
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

        var ordered = query
            .Distinct()
            .OrderBy(r => r.Id).ThenBy(r => r.PeriodKey).ThenBy(r => r.MethodologyCode);

        var rows = await (limit is { } top ? ordered.Take(top) : ordered)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return [.. rows.Select(r => new RegistryImpactRow(
            r.Id, r.BusinessKey, r.ProjectId, r.PeriodKey, r.State, r.MethodologyCode))];
    }

    /// <summary>Документи дозволених проєктів — фільтр іде в SQL, до <c>TOP</c> (S18).</summary>
    private IQueryable<Ecr.Domain.Entities.Documents.Document> Visible(IReadOnlyCollection<int>? projectIds)
    {
        var documents = db.Documents.AsNoTracking();
        if (projectIds is null)
        {
            return documents;
        }

        var ids = projectIds.ToList();
        return documents.Where(d => ids.Contains(d.ProjectId));
    }
}
