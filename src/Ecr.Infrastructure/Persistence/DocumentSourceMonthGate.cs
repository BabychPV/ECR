// src/Ecr.Infrastructure/Persistence/DocumentSourceMonthGate.cs
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
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
/// <param name="readOverride">Явний ключ <c>Calculations:Hse400CompositionFromDocument</c>; <c>null</c> — біт зв'язки.</param>
public sealed class DocumentSourceMonthGate(EcrDbContext db, bool? readOverride) : ISourceMonthApprovalGate
{
    /// <summary>Ворота з перекриттям ключем завантажувача (не задано — біт <c>ReadFromDocument</c> зв'язки).</summary>
    /// <param name="sp">Провайдер.</param>
    /// <param name="configuration">Конфігурація.</param>
    public static DocumentSourceMonthGate Create(IServiceProvider sp, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(sp);
        ArgumentNullException.ThrowIfNull(configuration);
        return new DocumentSourceMonthGate(
            sp.GetRequiredService<EcrDbContext>(),
            Hse400CompositionSnapshotLoader.Override(configuration, Hse400CompositionSnapshotLoader.EnabledKey));
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

        // Шаблон документа: власний (B74) або основний шаблон проєкту.
        var bindings = await (
                from d in db.Documents.AsNoTracking()
                where d.Id == documentId
                join p in db.Projects.AsNoTracking() on d.ProjectId equals p.Id
                join pv in db.TemplateVersions.AsNoTracking() on p.TemplateVersionId equals pv.Id
                from c in db.DocumentSourceBindingConsumers.AsNoTracking()
                where c.TemplateId == (d.TemplateId ?? pv.TemplateId)
                join b in db.DocumentSourceBindings.AsNoTracking() on c.BindingId equals b.Id
                where b.SourceState == DocumentSourceState.ApprovedOnly
                      && b.MappingKind == DocumentSourceBinding.Hse400CompositionMapping
                orderby b.Id
                select new { b.Id, b.ReadFromDocument, p.PeriodKind })
            .ToListAsync(ct).ConfigureAwait(false);

        var year = periodKey / 100;
        var store = new DocumentSourceBindingStore(db);
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
}
