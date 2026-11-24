// src/Ecr.Infrastructure/Persistence/DocumentSourceMonthGate.cs
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ecr.Infrastructure.Persistence;

/// <summary>
/// B80 (N2b, R3.1 A3): реалізація <see cref="ISourceMonthApprovalGate"/> — шаблон документа є споживачем зв'язки в
/// стані <see cref="DocumentSourceState.ApprovedOnly"/>, читання з документа діє, документ-джерело року є, а місяць
/// джерела не затверджено (оригінал аркуша) — тоді місяць названо.
/// </summary>
/// <remarks>
/// <para>
/// Перший запит — зв'язки споживача, і лише в стані <c>ApprovedOnly</c>: за <c>Any</c> і без зв'язки це єдиний запит
/// (порожній результат). Далі — ті самі правила, що в завантажувачі: таблиці й обраний документ
/// (<see cref="DocumentSourceBindingStore"/>, найбільший id за рік, без проєкту золотих тестів), затверджені місяці
/// (<see cref="DocumentSourceGate.ApprovedMonthsAsync"/>, лише оригінал аркуша, період-власник місяця).
/// </para>
/// <para>
/// Місяці споживача — місяці, якими володіє період аркуша за періодичністю ЙОГО проєкту
/// (<see cref="DocumentSourceGate.OwnedMonths"/>): місячний — один <c>yyyyMM</c>; річний якір — усі 12, тобто
/// затвердження цілого року вимагає затвердження кожного місяця джерела.
/// </para>
/// <para>
/// ⚠ Місяць джерела вважається затвердженим, якщо затверджено аркуш БУДЬ-ЯКОЇ таблиці зв'язки обраного документа (як у
/// фактах здоров'я, <c>SystemHealthStore</c>); у HSE400 усі потоки на одному аркуші.
/// </para>
/// </remarks>
/// <param name="db">Контекст.</param>
/// <param name="readOverride">Перекриття для тестів; у DI — <c>null</c>: біт зв'язки (ключ виведено, P-0118-31/33).</param>
/// <param name="graph">Граф залежностей документів: споживачі зв'язки (B179 L6; ручний перелік не читається).</param>
public sealed class DocumentSourceMonthGate(EcrDbContext db, bool? readOverride, IDependencyGraphProvider graph) : ISourceMonthApprovalGate, ISourceIssueReader
{
    /// <summary>Ворота без перекриття: вирішує біт <c>ReadFromDocument</c> зв'язки (P-0118-31/33, ключ виведено).</summary>
    /// <param name="sp">Провайдер.</param>
    public static DocumentSourceMonthGate Create(IServiceProvider sp)
    {
        ArgumentNullException.ThrowIfNull(sp);
        return new DocumentSourceMonthGate(sp.GetRequiredService<EcrDbContext>(), readOverride: null, sp.GetRequiredService<IDependencyGraphProvider>());
    }

    /// <inheritdoc />
    public async Task<UnapprovedSourceMonth?> FindUnapprovedSourceMonthAsync(
        long documentId, int periodKey, CancellationToken ct)
    {
        // Явне «не читати з документа» — Land читає довідник, джерело не потрібне: жодного запиту.
        if (readOverride == false)
        {
            return null;
        }

        var bindings = await ConsumerBindingsAsync(db, graph, documentId, periodKey, ct).ConfigureAwait(false);

        var year = periodKey / 100;
        var store = new DocumentSourceBindingStore(db, graph);
        foreach (var binding in bindings)
        {
            if (!(readOverride ?? binding.ReadFromDocument))
            {
                continue;
            }

            var tableIds = await store.BoundTableIdsAsync(binding.Id, ct).ConfigureAwait(false);
            var chosen = await store.ChosenSourceDocumentAsync(tableIds, year, ct).ConfigureAwait(false);

            // A5: документа-джерела за рік немає — рядки довідника, як доти; перешкоди немає.
            if (chosen is null)
            {
                continue;
            }

            var approved = (await DocumentSourceGate
                    .ApprovedMonthsAsync(db, [chosen.DocumentId], tableIds, ct).ConfigureAwait(false))
                .Where(a => a.Year == year)
                .Select(a => a.Month)
                .ToHashSet();

            foreach (var month in DocumentSourceGate.OwnedMonths(binding.PeriodKind, periodKey % 100))
            {
                if (approved.Contains(month))
                {
                    continue;
                }

                var source = await (
                        from d in db.Documents.AsNoTracking()
                        where d.Id == chosen.DocumentId
                        join p in db.Projects.AsNoTracking() on d.ProjectId equals p.Id
                        select new { ProjectCode = p.Code, d.BusinessKey })
                    .FirstAsync(ct).ConfigureAwait(false);

                return new UnapprovedSourceMonth(
                    chosen.DocumentId, (year * 100) + month, source.ProjectCode, source.BusinessKey);
            }
        }

        return null;
    }

    /// <inheritdoc />
    /// <remarks>
    /// ✎ B80 N4: спільне тіло з консистентністю — <see cref="DocumentSourceIssues"/> (ті самі зв'язки споживача, обраний
    /// документ і затверджені місяці, що в <see cref="FindUnapprovedSourceMonthAsync"/>).
    /// </remarks>
    public async Task<IReadOnlyList<SourceIssueRow>> ListSourceIssuesAsync(
        long documentId, IReadOnlyCollection<int> periodKeys, CancellationToken ct)
        => [.. (await new DocumentSourceIssues(db, readOverride, graph).ListAsync(documentId, periodKeys, ct).ConfigureAwait(false))
            .Select(f => f.Row)];

    /// <inheritdoc />
    /// <remarks>
    /// ✎ F5-R (D-570): один екземпляр <see cref="DocumentSourceIssues"/> на всю сторінку — обраний документ-джерело року
    /// й таблиці зв'язки кешуються між документами; граф без жодного ребра «джерело → споживач» — жодного запиту.
    /// </remarks>
    public async Task<IReadOnlyDictionary<long, IReadOnlyList<int>>> ListSourceChangedPeriodsAsync(
        IReadOnlyDictionary<long, IReadOnlyList<int>> approved, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(approved);
        var result = new Dictionary<long, IReadOnlyList<int>>();
        if (readOverride == false || approved.All(a => a.Value.Count == 0)
            || (await graph.GetAsync(ct).ConfigureAwait(false)).TemplateDependencies.Count == 0)
        {
            return result;
        }

        var issues = new DocumentSourceIssues(db, readOverride, graph);
        foreach (var (documentId, periods) in approved.Where(a => a.Value.Count > 0).OrderBy(a => a.Key))
        {
            var changed = (await issues.ListAsync(documentId, periods, ct).ConfigureAwait(false))
                .Where(f => f.Row.Kind == ISourceIssueReader.SourceChangedAfterApproval)
                .Select(f => f.Row.PeriodKey)
                .Distinct()
                .Order()
                .ToList();
            if (changed.Count > 0)
            {
                result[documentId] = changed;
            }
        }

        return result;
    }

    /// <summary>
    /// Зв'язки в стані <c>ApprovedOnly</c> (склад HSE400), споживачем яких є шаблон документа за період: власний шаблон
    /// документа (B74) або шаблон версії ПЕРІОДУ проєкту (шкала, <c>EcrDbContext.VersionOf</c> — сторож B74).
    /// </summary>
    /// <param name="db">Контекст.</param>
    /// <param name="graph">Граф залежностей документів (споживачі зв'язки).</param>
    /// <param name="documentId">Документ-споживач.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>За зростанням id зв'язки; з періодичністю проєкту споживача.</returns>
    internal static async Task<List<ConsumerBinding>> ConsumerBindingsAsync(
        EcrDbContext db, IDependencyGraphProvider graph, long documentId, int periodKey, CancellationToken ct)
        => (await ConsumerBindingsByPeriodAsync(db, graph, documentId, [periodKey], ct).ConfigureAwait(false))
            .GetValueOrDefault(periodKey) ?? [];

    /// <summary>
    /// ✎ B80 N5 (F13): <see cref="ConsumerBindingsAsync"/> для ВСІХ періодів одним проходом — запит шаблонів документа за
    /// кожен період (одне приєднання до списку періодів) і один запит зв'язок, а не пара запитів на період. Кількість
    /// звернень до БД від кількості періодів не залежить (граф — з кешу).
    /// </summary>
    /// <param name="db">Контекст.</param>
    /// <param name="graph">Граф залежностей документів (споживачі зв'язки).</param>
    /// <param name="documentId">Документ-споживач.</param>
    /// <param name="periodKeys">Періоди.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Період → зв'язки за зростанням id; періоду без зв'язок у словнику немає.</returns>
    /// <param name="includeAny">
    /// ✎ F5-R (D-570): і зв'язки в стані <c>Any</c> (проблема «джерело змінено після затвердження»); ворота подання їх не
    /// беруть — за <c>Any</c> подання не залежить від затвердження джерела.
    /// </param>
    internal static async Task<Dictionary<int, List<ConsumerBinding>>> ConsumerBindingsByPeriodAsync(
        EcrDbContext db, IDependencyGraphProvider graph, long documentId, IReadOnlyCollection<int> periodKeys, CancellationToken ct,
        bool includeAny = false)
    {
        var keys = periodKeys.Distinct().ToList();
        if (keys.Count == 0)
        {
            return [];
        }

        // ✎ B179 L6: споживач зв'язки — шаблон, якого граф залежностей називає споживачем (а не ручний перелік).
        var own = await OwnTemplatesAsync(db, documentId, keys, ct).ConfigureAwait(false);
        if (own.Count == 0)
        {
            return [];
        }

        var snapshot = await graph.GetAsync(ct).ConfigureAwait(false);
        var templateBindings = snapshot.TemplateDependencies
            .GroupBy(d => d.ConsumerTemplateId)
            .ToDictionary(g => g.Key, g => g.Select(d => d.BindingId).Distinct().ToList());
        var bindingIds = own
            .SelectMany(o => templateBindings.GetValueOrDefault(o.TemplateId) ?? [])
            .Distinct()
            .ToList();
        if (bindingIds.Count == 0)
        {
            return [];
        }

        var bindings = await (
                from b in db.DocumentSourceBindings.AsNoTracking()
                where bindingIds.Contains(b.Id)
                      && (includeAny || b.SourceState == DocumentSourceState.ApprovedOnly)
                      && b.MappingKind == DocumentSourceBinding.Hse400CompositionMapping
                orderby b.Id
                select new { b.Id, b.ReadFromDocument, b.SourceState })
            .ToListAsync(ct).ConfigureAwait(false);

        var result = new Dictionary<int, List<ConsumerBinding>>();
        foreach (var o in own)
        {
            var ids = templateBindings.GetValueOrDefault(o.TemplateId);
            var found = bindings
                .Where(b => ids is not null && ids.Contains(b.Id))
                .Select(b => new ConsumerBinding(b.Id, b.ReadFromDocument, o.PeriodKind, b.SourceState))
                .ToList();
            if (found.Count > 0)
            {
                result[o.PeriodKey] = found;
            }
        }

        return result;
    }


    /// <summary>
    /// Шаблон документа й періодичність проєкту за кожен період. Періоди проєкту, що існують у <c>doc.Period</c>, —
    /// ОДНИМ запитом (приєднання до <c>doc.Period</c> із <c>Contains</c> по ключах; шкала версій <c>VersionOf</c> /
    /// <c>TemplateVersionOf</c> залежить від періоду). Ключ, якого в <c>doc.Period</c> немає (рядок не заведено), —
    /// окремий запит на такий ключ, як було доти: поведінка не змінюється, а кількість звернень росте лише з кількістю
    /// ЗАВЕДЕНИХ-НЕ-ІСНУЮЧИХ ключів (у продукті періоди документа завжди є в <c>doc.Period</c>).
    /// </summary>
    private static async Task<List<OwnTemplate>> OwnTemplatesAsync(
        EcrDbContext db, long documentId, List<int> keys, CancellationToken ct)
    {
        var found = await (
                from d in db.Documents.AsNoTracking()
                where d.Id == documentId
                join p in db.Projects.AsNoTracking() on d.ProjectId equals p.Id
                join per in db.Periods.AsNoTracking() on d.ProjectId equals per.ProjectId
                where keys.Contains(per.PeriodKeyValue)
                from pv in db.TemplateVersions.AsNoTracking()
                where pv.Id == (d.TemplateId == null ? EcrDbContext.VersionOf(d.ProjectId, per.PeriodKeyValue) : EcrDbContext.TemplateVersionOf(d.ProjectId, d.TemplateId.Value, per.PeriodKeyValue))
                select new { PeriodKey = per.PeriodKeyValue, TemplateId = d.TemplateId ?? pv.TemplateId, p.PeriodKind })
            .ToListAsync(ct).ConfigureAwait(false);

        var result = found.Select(r => new OwnTemplate(r.PeriodKey, r.TemplateId, r.PeriodKind)).ToList();
        foreach (var key in keys.Except(result.Select(r => r.PeriodKey)))
        {
            var periodKey = key;
            var single = await (
                    from d in db.Documents.AsNoTracking()
                    where d.Id == documentId
                    join p in db.Projects.AsNoTracking() on d.ProjectId equals p.Id
                    from pv in db.TemplateVersions.AsNoTracking()
                    where pv.Id == (d.TemplateId == null ? EcrDbContext.VersionOf(d.ProjectId, periodKey) : EcrDbContext.TemplateVersionOf(d.ProjectId, d.TemplateId.Value, periodKey))
                    select new { TemplateId = d.TemplateId ?? pv.TemplateId, p.PeriodKind })
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);
            if (single is not null)
            {
                result.Add(new OwnTemplate(periodKey, single.TemplateId, single.PeriodKind));
            }
        }

        return result;
    }

    private sealed record OwnTemplate(int PeriodKey, int TemplateId, PeriodKind PeriodKind);
}

/// <summary>Зв'язка документа-споживача (<c>ApprovedOnly</c>; ✎ F5-R — і <c>Any</c>, коли запитано).</summary>
/// <param name="Id">Зв'язка.</param>
/// <param name="ReadFromDocument">Біт «читати з документа».</param>
/// <param name="PeriodKind">Періодичність проєкту СПОЖИВАЧА (які місяці володіє його період).</param>
/// <param name="State">Стан-ворота зв'язки.</param>
internal sealed record ConsumerBinding(
    int Id, bool ReadFromDocument, PeriodKind PeriodKind, DocumentSourceState State = DocumentSourceState.ApprovedOnly);
