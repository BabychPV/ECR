using System.Globalization;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Workflow;

/// <summary>
/// Спільна перевірка «період закрито — спершу відкрийте період» для повернення
/// аркуша в <c>Draft</c> (<c>Reopen</c>, <c>Recall</c>; D-67, ФВ-5.20a).
/// </summary>
/// <remarks>
/// ⛔ R5-W1 / W1-04 (F-08 неповний). Обидва обробники порівнювали ЗБЕРЕЖЕНИЙ
/// стан періоду, який до години відстає від реальної межі: після
/// <c>ReopenedUntil</c> чи <c>ComputedCloseAt</c> рішення про запис уже бачить
/// <c>Closed</c>, а тут іще проходило <c>Grace</c>. Аркуш повертався в
/// <c>Draft</c> у фактично закритому періоді й застрягав: правити й подавати не
/// можна, затвердження втрачено. Тепер — ефективний стан, тим самим правилом,
/// під тим самим <c>UPDLOCK</c> періоду.
///
/// ⚠ Одне місце на двох: дві копії правила розійшлися б так само, як розійшлися
/// збережений і ефективний стан.
/// </remarks>
internal static class ClosedPeriodGuard
{
    /// <summary>Бере період під <c>UPDLOCK</c> і відмовляє, якщо він ЕФЕКТИВНО закритий.</summary>
    /// <param name="workflow">Сховище робочого процесу.</param>
    /// <param name="documentId">Документ.</param>
    /// <param name="key">Період.</param>
    /// <param name="utcNow">Поточний момент.</param>
    /// <param name="ct">Токен скасування (транзакції).</param>
    /// <exception cref="BusinessRuleException">Період закрито — <c>ECR-PRD-4223</c>.</exception>
    public static async Task RequireNotClosedAsync(
        IWorkflowStore workflow, long documentId, PeriodKey key, DateTime utcNow, CancellationToken ct)
    {
        var period = await workflow.LockPeriodAsync(documentId, key, ct).ConfigureAwait(false);

        // ⛔ B69: в архівованому (чи тому, що архівується) проєкті аркуш у Draft не повертається НІКОЛИ — і виняток
        // «місяць відкрито для документа» нижче цього не змінює. Виняток, що пережив архівацію, інакше пускав Recall.
        if (await workflow.IsDocumentProjectArchivedOrArchivingAsync(documentId, ct).ConfigureAwait(false))
        {
            throw new BusinessRuleException(
                "ECR-PRD-4223",
                $"Проєкт документа {documentId.ToString(CultureInfo.InvariantCulture)} архівований: аркуші в ньому в роботу не повертаються.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-PRD-4223.projectArchived",
                    ["periodKey"] = key.Value.ToString(CultureInfo.InvariantCulture),
                });
        }

        // ⚠ Збережений `Closed` — закрито без запиту: ефективний стан назад не
        // повертається ніколи (`PeriodStateCalculator.Effective`).
        var state = period.State == PeriodState.Closed
            ? PeriodState.Closed
            : await workflow.EffectivePeriodStateAsync(period, utcNow, ct).ConfigureAwait(false);

        // Приймальна 08.11 (i): місяць, відкритий лише для ЦЬОГО документа, — аркуш повертається, як у Grace.
        if (state == PeriodState.Closed
            && !await workflow.HasDocumentPeriodReopenAsync(documentId, key, utcNow, ct).ConfigureAwait(false))
        {
            throw new BusinessRuleException(
                "ECR-PRD-4223",
                $"Період {key.Value} закрито: спершу відкрийте період, потім аркуш.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-PRD-4223.reopenPeriodFirst",
                    ["periodKey"] = key.Value.ToString(CultureInfo.InvariantCulture),
                    ["periodState"] = state.ToString(),
                });
        }
    }
}
