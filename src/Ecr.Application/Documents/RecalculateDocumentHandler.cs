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

        // ⛔ Новий перерахунок ВИТІСНЯЄ попередній над тим самим документом і
        // періодом (`H-23c`). Дві причини, і жодна не про зручність.
        //
        // Перша: два повні перерахунки одного документа пишуть у
        // `calc.CalculationResult` одночасно і обидва перемикають актуальність
        // прогону. Числа лишаються правдоподібними, а який прогін переміг —
        // не скаже ніхто.
        //
        // Друга: доти зупинити довгий перерахунок було неможливо взагалі —
        // `IBackgroundJobScheduler.CancelAsync` не кликав НІХТО. Річний
        // перерахунок у чинній системі йде двадцять хвилин; наш із дворічною
        // звіркою буде довшим, і повторний запуск — єдиний спосіб, яким людина
        // може обірвати той, що пішов не туди.
        // ⚠ `TriggeredByUserId` — тут, а не виводиться з бази: хто натиснув
        // кнопку, знає лише HTTP-запит, і `currentUser` уже тут інжектований.
        // `ProjectId` НАВМИСНО не кладеться сюди — його визначає задача з
        // документа (`RecalculationJob`): payload не мусить нести те, що й так
        // виводиться з `DocumentId`, і друге джерело правди про проєкт
        // документа розійшлося б із першим на першій же помилці копіювання.
        return await jobs
            .EnqueueExclusiveAsync<IRecalculationJob>(
                TargetOf(documentId, periodKey),
                new { DocumentId = documentId, PeriodKey = periodKey.Value, TriggeredByUserId = currentUser.UserId },
                ct)
            .ConfigureAwait(false);
    }

    /// <summary>Ціль перерахунку: документ і період.</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="periodKey">Період.</param>
    /// <returns>Ключ цілі для витіснення.</returns>
    /// <remarks>
    /// ⚠ Саме пара, а не самий документ: перерахунок різних періодів одного
    /// документа — це різна робота над різними партиціями, і витісняти одне
    /// одним означало б, що заповнення грудня скасовує перерахунок листопада.
    /// </remarks>
    public static string TargetOf(long documentId, PeriodKey periodKey)
        => string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"doc{documentId}-p{periodKey.Value}");
}
