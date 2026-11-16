// src/Ecr.Infrastructure/Persistence/WorkflowNoneQueries.cs
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.Enums;

namespace Ecr.Infrastructure.Persistence;

/// <summary>
/// ✎ B75 lane 1b (B138): відбір станів погодження документів, чия версія шаблону за період стану — без подання й
/// затвердження (<see cref="WorkflowMode.None"/>).
/// </summary>
/// <remarks>
/// <para>
/// Перенос документа на нову версію переносить <c>wf.ApprovalState</c> (Q4 дизайну B75), тож у версії
/// <see cref="WorkflowMode.None"/> можуть лишатися старі <c>Submitted</c>/<c>Approved</c>. Для такої версії вони нічого не
/// означають: черга погодження і «повернуті мені» їх не показують, перерахунок не вважає аркуш поданим чи затвердженим.
/// </para>
/// <para>
/// Версія — за тим самим правилом, що <c>SheetEditGate</c> (F13 D1 lane 1): документ без шаблону —
/// <c>doc.fn_VersionOf(проєкт, період)</c>, із шаблоном (B74) — <c>doc.fn_TemplateVersionOf(проєкт, шаблон, період)</c>.
/// Предикат іде в ТОЙ САМИЙ запит (<c>NOT EXISTS</c>), нового звернення не додає.
/// </para>
/// </remarks>
public static class WorkflowNoneQueries
{
    /// <summary>Лише стани документів, чия версія за період стану має подання й затвердження.</summary>
    /// <param name="source">Запит до <c>wf.ApprovalState</c>.</param>
    /// <param name="db">Контекст (для корельованого підзапиту версії).</param>
    /// <returns>Звужений запит.</returns>
    public static IQueryable<ApprovalState> ExceptWorkflowNone(this IQueryable<ApprovalState> source, EcrDbContext db)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(db);
        return source.Where(a => !db.Documents.Any(d =>
            d.Id == a.DocumentId
            && db.TemplateVersions.Any(v =>
                v.WorkflowMode == WorkflowMode.None
                && v.Id == (d.TemplateId == null
                    ? EcrDbContext.VersionOf(d.ProjectId, a.PeriodKey)
                    : EcrDbContext.TemplateVersionOf(d.ProjectId, d.TemplateId.Value, a.PeriodKey)))));
    }

    /// <summary>Ідентифікатори версій без подання й затвердження — один дрібний запит (версій одиниці).</summary>
    /// <param name="db">Контекст.</param>
    /// <param name="ct">Токен скасування.</param>
    public static Task<int[]> NoneVersionIdsAsync(this EcrDbContext db, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        return Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToArrayAsync(
            Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.AsNoTracking(db.TemplateVersions)
                .Where(v => v.WorkflowMode == WorkflowMode.None)
                .Select(v => v.Id),
            ct);
    }

    /// <summary>
    /// Те саме для ПЕРЕЛІКІВ через усі проєкти (черга погодження, «повернуті мені»), де станів багато: версії без
    /// погодження читаються заздалегідь (<see cref="NoneVersionIdsAsync"/>), і коли їх НЕМАЄ — фільтр не додається
    /// зовсім (текст запиту рівно той, що до B138).
    /// </summary>
    /// <param name="source">Запит до <c>wf.ApprovalState</c>.</param>
    /// <param name="db">Контекст (для корельованого підзапиту версії).</param>
    /// <param name="noneVersionIds">Версії без подання й затвердження.</param>
    /// <returns>Звужений запит.</returns>
    /// <remarks>
    /// ⚠ Замір (B144, 14 411 поданих станів, тестова база): корельований <c>NOT EXISTS</c> зі скалярною
    /// <c>doc.fn_VersionOf</c> на КОЖЕН стан коштував ~17 мкс/рядок і тоді, коли жодної версії без погодження немає
    /// (усі подані: 3 → 61–249 мс; сторінка 200: 15 → 27 мс). Некорельований запобіжник у самому SQL не допомагає
    /// (133 мс). Тому «чи є такі версії» вирішується до запиту; ціна — одне звернення до <c>cfg.TemplateVersion</c>.
    /// Запити ОДНОГО документа (перерахунок, стани аркушів) лишаються на однопараметровій формі: там рядків одиниці.
    /// </remarks>
    public static IQueryable<ApprovalState> ExceptWorkflowNone(
        this IQueryable<ApprovalState> source, EcrDbContext db, int[] noneVersionIds)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(noneVersionIds);

        if (noneVersionIds.Length == 0)
        {
            return source;
        }

        return source.Where(a => !db.Documents.Any(d =>
            d.Id == a.DocumentId
            && noneVersionIds.Contains(d.TemplateId == null
                ? EcrDbContext.VersionOf(d.ProjectId, a.PeriodKey)
                : EcrDbContext.TemplateVersionOf(d.ProjectId, d.TemplateId.Value, a.PeriodKey))));
    }
}
