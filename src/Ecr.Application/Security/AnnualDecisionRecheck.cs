using System.Globalization;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Security;

/// <summary>
/// ✎ B79 (Q9): повторна перевірка РІЧНОГО рішення про комірку під замками аркушів року — для записувачів, які беруть
/// рішення <see cref="IAccessDecisionService.CanEditCellAsync"/> поза транзакцією, але пишуть не через
/// <c>PatchCellsHandler</c> (історія дозволу рядка, частини поділу місяця).
/// </summary>
/// <remarks>
/// <para>
/// Те саме правило, що в <c>PatchCellsHandler.CheckLockedColumnPeriods</c>: стовпець із залежними розрахунками
/// (<see cref="EditDecision.DependentInput"/>) — <see cref="DependentMonthsRule"/> (затверджений місяць відхиляє,
/// поданий ні); решта річних комірок — агрегат <see cref="AnnualPeriodRule.AggregateStatus"/> (текст якоря пише й
/// поданий рік, AN-142). Місяць, затверджений між рішенням і комітом, запис відхиляє.
/// </para>
/// <para>
/// ⚠ Лише для річного рішення (<see cref="EditDecision.AnnualPeriods"/>): нерічні комірки цих записувачів — як і доти.
/// Замки — спільні, за зростанням ключа періоду, як у PATCH (затвердження бере той самий замок винятково).
/// </para>
/// </remarks>
public static class AnnualDecisionRecheck
{
    /// <summary>Бере спільні замки аркуша за місяці року рішення й повторює правило на прочитаних станах.</summary>
    /// <param name="gate">Замок аркуша; <c>null</c> при річному рішенні — відмова (fail-closed).</param>
    /// <param name="documentId">Документ.</param>
    /// <param name="sheetDefId">Аркуш таблиці комірки.</param>
    /// <param name="sheetCopyId">Копія аркуша; <c>null</c> — оригінал.</param>
    /// <param name="decision">Дозволене рішення про комірку, прийняте поза транзакцією.</param>
    /// <param name="deniedCount">Скільки комірок відхиляє відмова (для <c>deniedCount</c>).</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="AccessDeniedException"><c>ECR-ACCS-0403</c>, <c>deniedCells</c> з причиною під замком.</exception>
    public static async Task EnsureAsync(
        ISheetEditGate? gate, long documentId, int sheetDefId, long? sheetCopyId, EditDecision decision, int deniedCount,
        CancellationToken ct)
    {
        if (decision.AnnualPeriods is not { Count: > 0 } annual)
        {
            return;
        }

        // ⛔ fail-closed: рішення річне, а замка для повторної перевірки немає — писати не можна. У проді замок
        // вприснуто завжди (DependencyInjection); `null` можливий лише в тестових фабриках.
        if (gate is null)
        {
            throw new InvalidOperationException(
                "Річне рішення про комірку потребує повторної перевірки під замком аркуша, але замка немає.");
        }

        // ✎ B79 (квіз 16.11): аркуші-читачі стовпця з залежними — у каноничному порядку разом з аркушем правки.
        var readers = decision.DependentInput ? decision.DependentReaderSheets ?? [] : [];
        var locked = new Dictionary<int, DocumentStatus>(annual.Count);
        var readerStatuses = await DependentReaderLocks
            .EnterAsync(
                gate, documentId, DependentReaderLocks.Plan(sheetDefId, sheetCopyId, annual, readers, annual),
                async period => locked[period] = await gate
                    .EnterEditOfAsync(documentId, sheetDefId, new PeriodKey(period), sheetCopyId, ct)
                    .ConfigureAwait(false),
                ct)
            .ConfigureAwait(false);

        if (decision.DependentInput)
        {
            // ✎ Квіз 16.11: власні місяці — лише коли стовпець читає формула цього ж аркуша.
            if (!decision.DependentOnlyOtherSheets)
            {
                DependentMonthsRule.ThrowIfLocked(annual, locked, deniedCount);
            }
            else if (AnnualPeriodRule.AggregateStatus(annual.Select(period => locked[period])) == DocumentStatus.Approved)
            {
                // ✎ F13 AU: читає лише інший аркуш — власний рік, затверджений цілком (агрегат), лишається замкненим
                // (`DocumentApproved`), як у рішенні (`AccessDecisionService.Decide`, причина стану перед правилом
                // залежних) і в PATCH; поданий рік поблажку зберігає.
                throw Denied(EditDenyReason.DocumentApproved, deniedCount);
            }

            DependentReaderLocks.ThrowIfLocked(readers, annual, readerStatuses, deniedCount);
            return;
        }

        var aggregate = AnnualPeriodRule.AggregateStatus(annual.Select(period => locked[period]));
        if (decision.AnnualTextOnly && aggregate == DocumentStatus.Submitted)
        {
            return;
        }

        var reason = aggregate switch
        {
            DocumentStatus.Submitted => (EditDenyReason?)EditDenyReason.DocumentSubmitted,
            DocumentStatus.Approved => EditDenyReason.DocumentApproved,
            _ => null,
        };

        if (reason is { } denied)
        {
            throw Denied(denied, deniedCount);
        }
    }

    private static AccessDeniedException Denied(EditDenyReason reason, int deniedCount)
        => new(
            "ECR-ACCS-0403",
            $"Заборонених комірок у запиті: {deniedCount}. Причина першої: {reason}.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-ACCS-0403.deniedCells",
                ["deniedCount"] = deniedCount.ToString(CultureInfo.InvariantCulture),
                ["reason"] = reason.ToString(),
            });
}
