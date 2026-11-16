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
}
