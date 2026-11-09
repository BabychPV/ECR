using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Documents;

/// <summary>
/// SHEETNAME(): постановка перерахунку формул аркуша, коли змінилась його показувана назва або з'явились нові
/// екземпляри (перше відкриття копії). Один шлях для <see cref="DocumentSheetCopyHandler"/> і
/// <see cref="GetDocumentTablesHandler"/>: задача <see cref="IRecalculationJob"/> на ціль «документ × період»,
/// без витіснення (<c>Coalesced</c>) — та сама, що після правки шапки.
/// </summary>
public static class SheetNameRecalculation
{
    /// <summary>Аркуші версії, формули яких згадують <c>SHEETNAME</c> (за текстом виразу).</summary>
    /// <param name="snapshot">Структура версії.</param>
    public static IReadOnlyList<SheetDef> SheetsUsing(TemplateVersionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return
        [
            .. snapshot.Sheets.Where(s => !s.IsDeleted
                                          && Recalculation.SheetDisplayNames.MayUse(
                                              s.Tables.SelectMany(t => t.Formulas).Select(f => f.Expression))),
        ];
    }

    /// <summary>Ставить перерахунок документа за період, звужений до аркуша (<c>null</c> — увесь документ).</summary>
    /// <param name="jobs">Черга задач.</param>
    /// <param name="documentId">Документ.</param>
    /// <param name="period">Період.</param>
    /// <param name="sheetDefId">Аркуш; <c>null</c> — кілька аркушів, увесь документ (злиття постановок на одну ціль
    /// інакше лишило б лише перший аркуш).</param>
    /// <param name="userId">Хто спричинив; <c>null</c> — системна.</param>
    /// <param name="ct">Токен скасування.</param>
    public static Task<string> EnqueueAsync(
        IBackgroundJobScheduler jobs, long documentId, PeriodKey period, int? sheetDefId, int? userId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(jobs);

        return jobs.EnqueueCoalescedAsync<IRecalculationJob>(
            RecalculateDocumentHandler.TargetOf(documentId, period),
            new
            {
                DocumentId = documentId,
                PeriodKey = period.Value,
                TriggeredByUserId = userId,
                SheetDefId = sheetDefId,
            },
            ct,
            userId);
    }
}
