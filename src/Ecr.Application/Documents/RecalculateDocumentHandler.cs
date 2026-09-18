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
    Common.ICurrentUser currentUser,
    IDocumentStore documents,
    IPeriodStore periods,
    IWorkflowStore workflow)
{
    /// <summary>Право на запуск перерахунку (`02-contracts.md` §9).</summary>
    public const string Permission = "Calculation.Recalculate";

    /// <summary>Ставить задачу в чергу і повертає її ідентифікатор.</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="sheetDefId">
    /// Аркуш; <c>null</c> — увесь документ (поведінка до Q-331). Реальний
    /// перерахунок ОДНОГО аркуша (директива паритету зі старою системою,
    /// прогалина 2, Q-327 → Q-331): звужує ЦІЛІ запису до таблиць цього
    /// аркуша, входи лишаються з усього документа (`RecalculationJob`,
    /// `RecalculationService.RunAsync`).
    /// </param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="Errors.NotFoundException"><c>ECR-DOC-0404</c> — аркуша немає в складі документа.</exception>
    /// <exception cref="Errors.BusinessRuleException">
    /// <c>ECR-CALC-4221</c> — період закритий або має поданий аркуш.
    /// </exception>
    /// <remarks>
    /// ⛔ ЗАКРИТІ періоди АВТОМАТИЧНО не перераховуються ніколи (ФВ-9.7), і
    /// поданий зріз — узагалі ніколи (ФВ-9.17).
    /// <para>
    /// ⚠ Тут раніше стояло твердження, що це «перевіряє
    /// <c>RunCalculationHandler</c>, якому задача передає керування». Воно
    /// було НЕПРАВДИВЕ, і саме на нього спирався дефект. Жодної передачі
    /// керування немає: цей обробник кладе <c>IRecalculationJob</c> у чергу
    /// САМ (нижче), а задача кличе з <c>RunCalculationHandler</c> лише
    /// <c>CompleteAsync</c> — завершення прогону, де періодів немає взагалі.
    /// <c>RunCalculationHandler.HandleAsync</c> із його перевіркою стану
    /// періоду на цьому маршруті не викликається НІКОЛИ, тож перерахунок
    /// документа переписував числа закритого періоду мовчки й «успішно».
    /// </para>
    /// <para>
    /// ⚠ Справжній гейт стоїть у <c>RecalculationJob</c> — на самому шляху
    /// запису, спільному для всіх трьох маршрутів. Перевірка нижче — це
    /// ШВИДКА ВІДМОВА заради людини: без неї користувач отримав би <c>202</c>
    /// і <c>jobId</c>, а причину відмови побачив би хвилиною пізніше в стані
    /// задачі. Обидві питають одне й те саме
    /// (<c>RecalculationWritePolicy</c>), тож розійтися не можуть.
    /// </para>
    /// </remarks>
    public async Task<string> HandleAsync(
        long documentId, PeriodKey periodKey, int? sheetDefId, CancellationToken ct)
    {
        var profile = await Security.PermissionCheck
            .RequireAsync(access, currentUser, Permission, ct)
            .ConfigureAwait(false);

        // ⛔ І ГРАНТ на проєкт документа (Q-174, аудит фази 2). Право саме по
        // собі каже «цей користувач узагалі запускає перерахунки», а не «для
        // ЦЬОГО документа». Без цієї перевірки користувач із
        // `Calculation.Recalculate` на власний проєкт міг поставити в чергу
        // перезапис обчислених значень чужого документа.
        var read = await access.CanReadDocumentAsync(profile, documentId, ct).ConfigureAwait(false);
        if (!read.IsAllowed)
        {
            throw new Errors.AccessDeniedException(
                "ECR-AUTH-0403", $"Немає доступу до документа {documentId}: {read.Reason}.");
        }

        // ⛔ Q-331: аркуш мусить входити в СКЛАД документа — той самий гейт,
        // що вже стоїть перед `SubmitSheetHandler` (`ФВ-3.2`). Без нього
        // перерахунок довільного `sheetDefId` (навіть чужого документа чи
        // такого, якого не існує взагалі) тихо повертав би `jobId`, чий прогін
        // не запише НІЧОГО: `RecalculationJob`/`RecalculationService` просто не
        // знайшли б жодної таблиці цього аркуша в документі — і людина
        // отримала б «перераховано» без жодного перерахунку.
        if (sheetDefId is { } targetSheetId
            && !await documents.HasSheetAsync(documentId, targetSheetId, ct).ConfigureAwait(false))
        {
            throw new Errors.NotFoundException(
                "ECR-DOC-0404",
                $"Аркуша {targetSheetId} немає в складі документа {documentId}.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-DOC-0404.sheetNotInDocument",
                    ["sheetDefId"] = targetSheetId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["documentId"] = documentId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                });
        }

        // ⛔ Стан періоду і робочого процесу — ДО черги (ФВ-9.7, ФВ-9.17).
        // Погодження на перерахунок закритого періоду цей маршрут не приймає
        // взагалі: його оформлює перерахунок ПРОЄКТУ
        // (`RunCalculationHandler` + `ClosedPeriodApproval`, причина й друга
        // людина), і вигадувати тут другий, безпогоджувальний вхід у закритий
        // період означало б обійти правило чотирьох очей кнопкою на документі.
        var state = await periods
            .FindPeriodStateAsync(documentId, periodKey.Value, ct)
            .ConfigureAwait(false);

        if (state is { } periodState)
        {
            var sheets = await workflow
                .GetSheetsAsync(documentId, periodKey, ct)
                .ConfigureAwait(false);

            var submitted = sheets.Any(s =>
                s.Status is Domain.Enums.DocumentStatus.Submitted
                         or Domain.Enums.DocumentStatus.Approved);

            var denial = Calculations.RecalculationWritePolicy.Check(
                periodState, submitted, hasClosedPeriodApproval: false);

            if (denial != Calculations.RecalculationWriteDenial.None)
            {
                throw new Errors.BusinessRuleException(
                    Calculations.RecalculationWritePolicy.ErrorCode,
                    Calculations.RecalculationWritePolicy.Explain(denial, periodKey.Value),
                    new Dictionary<string, object?>
                    {
                        ["documentId"] = documentId,
                        ["periodKey"] = periodKey.Value,
                        ["denial"] = denial.ToString(),
                    });
            }
        }

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
        // ⚠ `createdByUserId` — щоб автор прочитав стан ВЛАСНОЇ задачі без
        // System.ViewHealth (Q-156). Окремо від `TriggeredByUserId` у payload
        // вище: те поле бачить сама задача перерахунку, це — лише журнал
        // прогресу для перевірки прав при опитуванні.
        return await jobs
            .EnqueueExclusiveAsync<IRecalculationJob>(
                TargetOf(documentId, periodKey),
                new
                {
                    DocumentId = documentId,
                    PeriodKey = periodKey.Value,
                    TriggeredByUserId = currentUser.UserId,
                    SheetDefId = sheetDefId,
                },
                ct,
                currentUser.UserId)
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
    /// <para>
    /// ⚠ Q-331: НЕ пара «документ + аркуш» — умисно. Кожен прогін перемикає
    /// актуальність усього <c>CalculationRun</c> документа за період
    /// (<c>RunCalculationHandler.CompleteAsync</c>), а не лише свого аркуша;
    /// два одночасні прогони над тим самим документом і періодом, хай навіть
    /// різних аркушів, гонялися б за тим самим перемиканням актуальності —
    /// той самий клас проблеми, що вже описаний нижче для двох повних
    /// перерахунків (`H-23c`).
    /// </para>
    /// </remarks>
    public static string TargetOf(long documentId, PeriodKey periodKey)
        => string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"doc{documentId}-p{periodKey.Value}");
}
