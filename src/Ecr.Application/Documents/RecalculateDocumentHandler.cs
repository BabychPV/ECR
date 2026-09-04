using Ecr.Application.Ports;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Documents;

/// <summary>
/// Ставить перерахунок документа в чергу.
/// </summary>
/// <remarks>
/// ⚠ Перерахунок НІКОЛИ не виконується синхронно, навіть на малому документі:
/// відповідь із результатом означала б, що HTTP-запит тримає з'єднання на весь
/// нічний перерахунок. Клієнт отримує <c>jobId</c> і слухає прогрес.
///
/// Обробник існує окремо від контролера не для симетрії: постановка в чергу —
/// це прикладне рішення (яка задача, з яким payload, за яких умов), і в
/// HTTP-шарі воно перетворило б контролер на другий прикладний шар.
/// </remarks>
public sealed class RecalculateDocumentHandler(
    IBackgroundJobScheduler jobs,
    Security.IAccessDecisionService access,
    Common.ICurrentUser currentUser)
{
    /// <summary>Право на запуск перерахунку (`02-contracts.md` §9).</summary>
    public const string Permission = "Calculation.Recalculate";

    /// <summary>Ставить задачу в чергу і повертає її ідентифікатор.</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// ⚠ ЗАКРИТІ періоди АВТОМАТИЧНО не перераховуються ніколи (ФВ-9.7) —
    /// це перевіряє <c>RunCalculationHandler</c>, якому задача передає
    /// керування. Тут — право і чергування.
    /// </remarks>
    public async Task<string> HandleAsync(long documentId, PeriodKey periodKey, CancellationToken ct)
    {
        await Templates.ListTemplatesHandler
            .RequireAsync(access, currentUser, Permission, ct)
            .ConfigureAwait(false);

        return await jobs
            .EnqueueAsync<IRecalculationJob>(
                new { DocumentId = documentId, PeriodKey = periodKey.Value }, ct)
            .ConfigureAwait(false);
    }
}
