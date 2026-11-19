// src/Ecr.Infrastructure/Jobs/StaleApprovedDependentCheck.cs
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Jobs;

/// <summary>
/// B80 (N4, R3.2): правило узгодженості <c>STALE_APPROVED_DEPENDENT</c> — затверджений місяць документа-споживача зв'язки
/// <c>ApprovedOnly</c>, чий місяць документа-джерела зараз не затверджено або затверджено ПІЗНІШЕ за споживача.
/// </summary>
/// <remarks>
/// <para>
/// ⛔ Затверджений місяць споживача ніколи не перераховується (R3.2, таблиця: <c>SkippedApproved</c>) — тож розбіжність
/// із джерелом лишається видимою лише тут і в панелі проблем розрахунку, доки місяць не повернуть у роботу.
/// Консервативно (рішення координатора 16.11): повторне затвердження з тими самими числами теж знахідка.
/// </para>
/// <para>
/// Правила — ті самі, що в панелі (<see cref="DocumentSourceIssues"/>). Кандидати — пари документ × період, де є
/// затверджений стан оригіналу аркуша, а шаблон документа за період — споживач зв'язки <c>ApprovedOnly</c>. Знахідка — на
/// документ × період; сутність — найменший id стану оригіналу аркуша (<c>wf.ApprovalState</c>) цієї пари.
/// </para>
/// <para>
/// ⛔ Жодної зв'язки <c>ApprovedOnly</c> (усі стенди за сідом — <c>Any</c>) або перекриття «не читати з документа» —
/// один дрібний запит і порожній результат; повний прохід, тож старі знахідки правила закриваються.
/// </para>
/// </remarks>
/// <param name="db">Контекст.</param>
/// <param name="readOverride">
/// Перекриття біта зв'язки <c>ReadFromDocument</c> (<see cref="Ecr.Application.Ports.IDocumentSourceFlagOverrides"/>;
/// у DI завжди <c>null</c> — ключ конфігурації виведено, P-0118-31/33); <c>null</c> — біт зв'язки.
/// </param>
/// <param name="graph">Граф залежностей документів: споживачі зв'язки (B179 L6; ручний перелік не читається).</param>
internal sealed class StaleApprovedDependentCheck(EcrDbContext db, bool? readOverride, IDependencyGraphProvider graph)
{
    /// <summary>Сутність знахідки (EntityId — найменший id стану оригіналу аркуша документа × періоду).</summary>
    public const string EntityType = "wf.ApprovalState";

    /// <summary>Знахідки правила, не більше <paramref name="limit"/>.</summary>
    /// <param name="limit">Стеля знахідок.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task<List<StaleApprovedDependent>> RunAsync(int limit, CancellationToken ct)
    {
        if (readOverride == false || !await DocumentSourceIssues.AnyApprovedOnlyAsync(db, ct).ConfigureAwait(false))
        {
            return [];
        }

        // ✎ B179 L6: споживачі — з графа залежностей, а не з ручного cfg.DocumentSourceBindingConsumer.
        var approvedOnly = await db.DocumentSourceBindings.AsNoTracking()
            .Where(b => b.SourceState == DocumentSourceState.ApprovedOnly
                        && b.MappingKind == DocumentSourceBinding.Hse400CompositionMapping)
            .Select(b => b.Id)
            .ToListAsync(ct).ConfigureAwait(false);
        var consumers = (await graph.GetAsync(ct).ConfigureAwait(false)).TemplateDependencies
            .Where(d => approvedOnly.Contains(d.BindingId))
            .Select(d => d.ConsumerTemplateId)
            .Distinct()
            .ToList();
        if (consumers.Count == 0)
        {
            return [];
        }

        // ⛔ Шаблон документа за період — d.TemplateId, інакше шаблон версії проєкту за період (B74:
        // VersionOf/TemplateVersionOf, не Project.TemplateVersionId) — як у RegistryImpactStore.ListTemplateConsumersAsync (B179 L2).
        var candidates = await (
                from s in db.ApprovalStates.AsNoTracking()
                where s.SheetCopyId == null && s.Status == DocumentStatus.Approved
                join d in db.Documents.AsNoTracking() on s.DocumentId equals d.Id
                where db.TemplateVersions.Any(v =>
                    consumers.Contains(v.TemplateId)
                    && v.Id == (d.TemplateId == null
                        ? EcrDbContext.VersionOf(d.ProjectId, s.PeriodKey)
                        : EcrDbContext.TemplateVersionOf(d.ProjectId, d.TemplateId.Value, s.PeriodKey)))
                select new { s.DocumentId, s.PeriodKey })
            .Distinct()
            .ToListAsync(ct).ConfigureAwait(false);

        var issues = new DocumentSourceIssues(db, readOverride, graph);
        var found = new List<StaleApprovedDependent>();
        foreach (var document in candidates.GroupBy(c => c.DocumentId).OrderBy(g => g.Key))
        {
            var findings = await issues
                .ListAsync(document.Key, [.. document.Select(c => c.PeriodKey)], ct).ConfigureAwait(false);

            // Одна знахідка на документ × період (перший місяць періоду з розбіжністю — річний якір має 12).
            foreach (var finding in findings
                         .Where(f => f.Row.Kind == ISourceIssueReader.SourceChangedAfterApproval && f.StateId is not null)
                         .GroupBy(f => f.Row.PeriodKey)
                         .Select(g => g.First()))
            {
                found.Add(new StaleApprovedDependent(
                    finding.StateId!.Value, document.Key, finding.Row.PeriodKey, finding.Row.SourceDocumentId,
                    finding.Row.SourcePeriodKey, finding.Reason ?? DocumentSourceIssues.ReasonNotApproved));
                if (found.Count >= limit)
                {
                    return found;
                }
            }
        }

        return found;
    }
}

/// <summary>Затверджений місяць споживача, чиє джерело змінилося після затвердження.</summary>
/// <param name="StateId">Найменший id стану оригіналу аркуша (сутність знахідки).</param>
/// <param name="DocumentId">Документ-споживач.</param>
/// <param name="PeriodKey">Період споживача.</param>
/// <param name="SourceDocumentId">Документ-джерело.</param>
/// <param name="SourcePeriodKey">Місяць джерела.</param>
/// <param name="Reason"><c>notApproved</c> чи <c>reapproved</c>.</param>
internal sealed record StaleApprovedDependent(
    long StateId, long DocumentId, int PeriodKey, long SourceDocumentId, int SourcePeriodKey, string Reason);
