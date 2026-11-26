// src/Ecr.Infrastructure/Persistence/RegistryImpactStore.cs
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>Реалізація <see cref="IRegistryImpactStore"/> над <see cref="EcrDbContext"/>.</summary>
/// <param name="db">Контекст.</param>
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

    /// <inheritdoc />
    /// <remarks>
    /// ⚠ Той самий запит (<see cref="QueryAsync"/>), лише без умови «прогін до <c>DataChangedAt</c>» і в межах року.
    /// </remarks>
    public Task<IReadOnlyList<RegistryImpactRow>> ListImpactedForYearAsync(int registryDefId, int year, CancellationToken ct)
        => QueryAsync(registryDefId, db.Documents.AsNoTracking(), limit: null, ct, (year * 100, (year * 100) + 99));

    /// <inheritdoc />
    /// <remarks>
    /// ⛔ Шаблон документа за період — <c>d.TemplateId</c>, інакше шаблон версії проєкту за період (B74:
    /// <c>VersionOf</c>/<c>TemplateVersionOf</c>, не <c>Project.TemplateVersionId</c>). «Документ × період є» — екземпляр
    /// таблиці оригіналу аркуша (<c>SheetCopyId = null</c>) у цьому періоді.
    /// <para>
    /// ✎ B179 L2: шаблони дає граф залежностей (а не ручний <c>cfg.DocumentSourceBindingConsumer</c>), і стан-ворота тут
    /// не перевіряється — його перевірив той, хто поставив задачу (хук завершення за <c>Any</c>, тригер затвердження за
    /// <c>ApprovedOnly</c>).
    /// </para>
    /// </remarks>
    public Task<IReadOnlyList<RegistryImpactRow>> ListTemplateConsumersAsync(
        IReadOnlyCollection<int> templateIds, int firstPeriod, int lastPeriod, int? month, CancellationToken ct)
        => ListTemplateConsumersRoutedAsync(templateIds, firstPeriod, lastPeriod, month, routedMonth: null, routedVersions: null, ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<RegistryImpactRow>> ListTemplateConsumersRoutedAsync(
        IReadOnlyCollection<int> templateIds, int firstPeriod, int lastPeriod, int? month, int? routedMonth,
        IReadOnlySet<int>? routedVersions, CancellationToken ct)
        => ListRoutedConsumersAsync(templateIds, firstPeriod, lastPeriod, month, routedMonth, routedVersions, anyPeriodState: false, ct);

    /// <summary>
    /// ✎ D-581: те саме відображення, але й у періодах БУДЬ-ЯКОГО стану (закритий, ще не відкритий) — для політики зв'язки
    /// D-554: затверджений закритий місяць споживача зміна джерела теж робить застарілим, хоч каскад його й не чіпає.
    /// </summary>
    /// <param name="templateIds">Шаблони-споживачі.</param>
    /// <param name="firstPeriod">Найменший ключ періоду (включно).</param>
    /// <param name="lastPeriod">Найбільший ключ періоду (включно).</param>
    /// <param name="month">Обсяг «місяць».</param>
    /// <param name="routedMonth">Місяць правки для ребра <c>MonthOwned</c>.</param>
    /// <param name="routedVersions">Версії, на яких звуження діє.</param>
    /// <param name="anyPeriodState"><c>true</c> — періоди будь-якого стану; <c>false</c> — лише <c>Open</c>/<c>Grace</c>.</param>
    /// <param name="ct">Токен скасування.</param>
    internal async Task<IReadOnlyList<RegistryImpactRow>> ListRoutedConsumersAsync(
        IReadOnlyCollection<int> templateIds, int firstPeriod, int lastPeriod, int? month, int? routedMonth,
        IReadOnlySet<int>? routedVersions, bool anyPeriodState, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(templateIds);
        if (templateIds.Count == 0)
        {
            return [];
        }

        var consumers = templateIds.Distinct().ToList();
        var rows = await (
                from instance in db.TableInstances.AsNoTracking()
                where instance.SheetCopyId == null
                      && instance.PeriodKeyValue >= firstPeriod && instance.PeriodKeyValue <= lastPeriod
                join d in db.Documents.AsNoTracking() on instance.DocumentId equals d.Id
                join project in db.Projects.AsNoTracking() on d.ProjectId equals project.Id
                join period in db.Periods.AsNoTracking()
                    on new { d.ProjectId, PeriodKey = instance.PeriodKeyValue }
                    equals new { period.ProjectId, PeriodKey = period.PeriodKeyValue }
                where anyPeriodState || period.State == PeriodState.Open || period.State == PeriodState.Grace
                let versionId = d.TemplateId == null
                    ? EcrDbContext.VersionOf(d.ProjectId, instance.PeriodKeyValue)
                    : EcrDbContext.TemplateVersionOf(d.ProjectId, d.TemplateId.Value, instance.PeriodKeyValue)
                where db.TemplateVersions.Any(v => consumers.Contains(v.TemplateId) && v.Id == versionId)
                select new
                {
                    d.Id, d.BusinessKey, d.ProjectId, PeriodKey = instance.PeriodKeyValue, period.State, project.PeriodKind,
                    VersionId = versionId,
                })
            .Distinct()
            .OrderBy(r => r.Id).ThenBy(r => r.PeriodKey)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // ✎ PERF-06b (D-572): ребро «комірка періоду-власника місяця» — у місячному проєкті лише період (рік, m), якщо він є в
        // календарі проєкту (будь-який стан: закритий власник не рахується, а інші періоди цю комірку не рахують). Без
        // рядка календаря маршрутизація віддає комірку екземпляру перегляду — тоді всі періоди, як доти. Рішення — спільне з
        // воротами F5 (MonthOwnerRouting). ✎ W15z M1: і лише для періоду на версії з routedVersions (версія за періодом — B74).
        HashSet<(int ProjectId, int PeriodKey)> owners = [];
        if (routedMonth is { } rm && rows.Exists(r => r.PeriodKind == PeriodKind.Monthly))
        {
            owners = await MonthOwnerRouting.ExistingOwnersAsync(
                    db,
                    [.. rows.Where(r => r.PeriodKind == PeriodKind.Monthly).Select(r => r.ProjectId).Distinct()],
                    [.. rows.Select(r => (r.PeriodKey / 100 * 100) + rm).Distinct()],
                    ct)
                .ConfigureAwait(false);
        }

        // ✎ Огляд b11-2 (C): копії аркушів маршрутизація не віддає власнику місяця (MonthColumnRouter — лише оригінали): документ із
        // копіями в діапазоні чи версія з аркушем, що копіюється, — увесь рік. (D): власник місяця — на версії, що звужує (рік
        // зі змішаними версіями — інакше увесь рік).
        HashSet<long> wholeYear = [];
        Dictionary<long, int?> ownerVersion = [];
        if (routedMonth is { } routed && routedVersions is { Count: > 0 })
        {
            var versions = routedVersions.ToList();
            var copyable = await db.SheetDefs.AsNoTracking()
                .Where(s => versions.Contains(s.TemplateVersionId) && s.IsCopyable)
                .Select(s => s.TemplateVersionId)
                .Distinct()
                .ToListAsync(ct)
                .ConfigureAwait(false);
            routedVersions = routedVersions.Except(copyable).ToHashSet();

            var docs = rows.Select(r => r.Id).Distinct().ToList();
            wholeYear = [.. await db.TableInstances.AsNoTracking()
                .Where(i => docs.Contains(i.DocumentId) && i.SheetCopyId != null
                            && i.PeriodKeyValue >= firstPeriod && i.PeriodKeyValue <= lastPeriod)
                .Select(i => i.DocumentId)
                .Distinct()
                .ToListAsync(ct)
                .ConfigureAwait(false)];

            foreach (var ownerKey in rows.Select(r => (r.PeriodKey / 100 * 100) + routed).Distinct().ToList())
            {
                var found = await db.Documents.AsNoTracking()
                    .Where(d => docs.Contains(d.Id))
                    .Select(d => new
                    {
                        d.Id,
                        Version = d.TemplateId == null
                            ? EcrDbContext.VersionOf(d.ProjectId, ownerKey)
                            : EcrDbContext.TemplateVersionOf(d.ProjectId, d.TemplateId.Value, ownerKey),
                    })
                    .ToListAsync(ct)
                    .ConfigureAwait(false);
                foreach (var f in found)
                {
                    ownerVersion[(f.Id * 1_000_000) + ownerKey] = f.Version;
                }
            }
        }

        bool OwnerOnRoutedVersion(long documentId, int ownerKey)
            => ownerVersion.TryGetValue((documentId * 1_000_000) + ownerKey, out var v) && v is { } version
               && routedVersions!.Contains(version);

        // B179 L2: обсяг «місяць» — лише період, що володіє місяцем (те саме правило, що ворота затверджених місяців).
        return [.. rows
            .Where(r => month is not { } m || DocumentSourceGate.OwnedMonths(r.PeriodKind, r.PeriodKey % 100).Contains(m))
            .Where(r => routedMonth is not { } owned
                        || routedVersions is null
                        || !routedVersions.Contains(r.VersionId)
                        || wholeYear.Contains(r.Id)
                        || !OwnerOnRoutedVersion(r.Id, (r.PeriodKey / 100 * 100) + owned)
                        || !MonthOwnerRouting.Narrows(r.PeriodKind, owners.Contains((r.ProjectId, (r.PeriodKey / 100 * 100) + owned)))
                        || r.PeriodKey % 100 == owned)
            .Select(r => new RegistryImpactRow(r.Id, r.BusinessKey, r.ProjectId, r.PeriodKey, r.State, string.Empty))];
    }

    /// <summary>Спільне тіло вибірок: документи — з <paramref name="documents"/>.</summary>
    /// <param name="registryDefId">Довідник.</param>
    /// <param name="documents">Документи, серед яких шукати.</param>
    /// <param name="limit">Стеля рядків; <c>null</c> — без стелі (межа — сам <paramref name="documents"/>).</param>
    /// <param name="ct">Токен скасування.</param>
    /// <param name="sourcePeriods">
    /// Зміна документа-джерела (HSE400 3b): межі ключів періодів результатів (включно), без умови
    /// <c>DataChangedAt</c>; <c>null</c> — правка самого довідника.
    /// </param>
    private async Task<IReadOnlyList<RegistryImpactRow>> QueryAsync(
        int registryDefId,
        IQueryable<Ecr.Domain.Entities.Documents.Document> documents,
        int? limit,
        CancellationToken ct,
        (int From, int To)? sourcePeriods = null)
    {
        var anyRun = sourcePeriods is not null;
        var changedAt = anyRun
            ? null
            : await db.RegistryDefs.AsNoTracking()
                .Where(r => r.Id == registryDefId)
                .Select(r => r.DataChangedAt)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);

        if (!anyRun && changedAt is null)
        {
            return [];
        }

        var firstPeriod = sourcePeriods?.From ?? int.MinValue;
        var lastPeriod = sourcePeriods?.To ?? int.MaxValue;

        var query =
            from use in db.RegistryUses.AsNoTracking()
            where use.SourceKind == RegistryUse.MethodologyVersionSource && use.RegistryDefId == registryDefId
            join result in db.CalculationResults.AsNoTracking()
                on use.SourceId equals result.MethodologyVersionId
            join run in db.CalculationRuns.AsNoTracking() on result.CalculationRunId equals run.Id
            where run.Status == CalculationRun.CurrentStatus
                  && (anyRun || (run.InputsAsOfUtc ?? run.StartedAt) < changedAt)
                  && result.PeriodKey >= firstPeriod && result.PeriodKey <= lastPeriod
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
