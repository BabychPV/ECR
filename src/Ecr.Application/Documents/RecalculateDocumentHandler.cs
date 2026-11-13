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
    IWorkflowStore workflow,
    // ✎ F13 p218: копії аркушів (RS) — «рахувати нічого» лише коли затверджені оригінал І всі копії аркуша. Необов'язковий
    // заради тестів, що конструюють обробник вручну (без нього копій немає — як і доти).
    IDocumentSheetCopyStore? sheetCopies = null,
    // ✎ LAND-YEAR B24: розкладки версій (в пам'яті) і версія періоду — закритий якір річної розкладки у відкритому році не
    // відмовляє. Без них (тести, що конструюють обробник вручну) — відмова, як доти.
    ITemplateLayoutRegistry? layouts = null,
    IProjectVersionTimeline? timeline = null)
{
    /// <summary>Право на перерахунок СВОГО документа: лише читання (`02-contracts.md` §9).</summary>
    /// <remarks>
    /// ✎ 2026-10-02: функціональне <c>Document.View</c> у проєкті документа плюс видимість
    /// документа (грант не нижче <c>Read</c>), як <c>GetTableSliceHandler</c>. Інакше виконавець
    /// (<c>DataEntry</c>) застрягав на «methodology results are stale». <c>Calculation.Recalculate</c>
    /// лишається для проєктного/масового перерахунку (<c>RunCalculationHandler</c>,
    /// <c>RecalculateImpactedHandler</c>).
    /// </remarks>
    public const string Permission = "Document.View";

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
        var outcome = await RequestAsync(documentId, periodKey, sheetDefId, ct).ConfigureAwait(false);

        // ⚠ Старий виклик (без переліку): «рахувати нічого» лишається відмовою 4221, як і до UAT-recalc-ux.
        return outcome.JobId
            ?? throw Calculations.RecalculationWritePolicy.Reject(
                Calculations.RecalculationWriteDenial.SheetsSubmitted, periodKey.Value, documentId);
    }

    /// <summary>
    /// UAT-recalc-ux: ставить перерахунок у чергу, пропускаючи подані/затверджені аркуші, і повертає їх перелік.
    /// </summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="sheetDefId">Аркуш; <c>null</c> — увесь документ.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>
    /// Задача і пропущені аркуші; <c>JobId = null</c> — подано/затверджено все, що просили перерахувати (аркуш
    /// області чи всі аркуші складу): не помилка, а відповідь із переліком.
    /// </returns>
    /// <exception cref="Errors.BusinessRuleException"><c>ECR-CALC-4221</c> <c>periodClosed</c> — період закритий.</exception>
    public async Task<Calculations.RecalculationRequestOutcome> RequestAsync(
        long documentId, PeriodKey periodKey, int? sheetDefId, CancellationToken ct)
    {
        var profile = await Security.PermissionCheck
            .RequireInAnyProjectAsync(access, currentUser, Permission, ct)
            .ConfigureAwait(false);

        // ⛔ І ГРАНТ на проєкт документа (Q-174, аудит фази 2). Право саме по
        // собі каже «цей користувач узагалі запускає перерахунки», а не «для
        // ЦЬОГО документа». Без цієї перевірки користувач із
        // `Calculation.Recalculate` на власний проєкт міг поставити в чергу
        // перезапис обчислених значень чужого документа.
        // ⛔ B-08: невидимий документ — 404, як і `GET /documents/{id}`, а не 403
        // «NoGrant»: різниця відповідей сама розкривала б, що документ існує.
        // ФВ-6.14: і право — у проєкті документа.
        await DocumentVisibility.RequireVisibleAsync(access, profile, documentId, Permission, ct).ConfigureAwait(false);

        // ⛔ D-214: роль, ЗВУЖЕНА аркушами/періодами, перераховувати документ не може — перерахунок
        // пише в усі аркуші й періоди, тож обійшов би звуження (раніше це давало невхід
        // `Calculation.Recalculate`, який не звужується). Потрібне читання БЕЗ звуження:
        // з ролі без області або з області проєкту, але не лише з шару звуження.
        if (!HasUnnarrowedRead(profile, await access.DocumentProjectIdAsync(documentId, ct).ConfigureAwait(false)))
        {
            throw new Errors.AccessDeniedException(
                "ECR-AUTH-0403", $"Потрібне право {Permission} без звуження.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-AUTH-0403.permission",
                    ["permission"] = Permission,
                });
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

        // ⛔ Схований від читача аркуш — та сама відмова, що й аркуш поза складом.
        if (sheetDefId is { } hiddenTarget)
        {
            await DocumentVisibility
                .RequireSheetVisibleAsync(documents, access, profile, documentId, hiddenTarget, periodKey, ct)
                .ConfigureAwait(false);
        }

        // ⛔ N1-06: перерахунок УСЬОГО документа (`sheetDefId = null`) пише і в таблиці схованого від
        // читача аркуша, а відповідь (202/4221) за станом цього аркуша розкривала б його стан. Роль,
        // що не бачить хоч одного аркуша складу, цілий документ не перераховує: та сама відмова
        // («потрібне повне читання») і ДО перевірки стану, тож у Draft і в Submitted вона однакова.
        // Область одного аркуша (вище) відсікається `RequireSheetVisibleAsync`.
        IReadOnlyList<int>? included = null;
        if (sheetDefId is null)
        {
            included = await documents.GetIncludedSheetIdsAsync(documentId, ct).ConfigureAwait(false);

            if (DocumentSheetVisibility.HasRestrictions(profile))
            {
                var scope = (await access.ReadScopeAsync(profile, documentId, ct).ConfigureAwait(false))
                    .InPeriod(periodKey);

                // ⚠ Склад невідомий (рядків `doc.DocumentSheet` немає) — задача рахує всі таблиці документа,
                // тож консервативно: заборона на будь-який аркуш структури відмовляє.
                var hasHidden = included is { Count: > 0 }
                    ? included.Any(id => !scope.CanReadSheet(id))
                    : scope.HiddenSheetIds().Count > 0;

                if (hasHidden)
                {
                    throw new Errors.AccessDeniedException(
                        "ECR-AUTH-0403",
                        $"Перерахунок усього документа потребує читання всіх його аркушів: {Permission}.",
                        new Dictionary<string, object?>
                        {
                            ["messageKey"] = "err.ECR-AUTH-0403.recalcNeedsFullRead",
                        });
                }
            }
        }

        // ⛔ B69: архівований проєкт — відмова до будь-якого стану періоду. `FindPeriodStateAsync` піднімає закритий місяць
        // до Grace за винятком «відкрито для документа», і виняток, що пережив архівацію, інакше ставив задачу в чергу.
        if (await periods.IsDocumentProjectArchivedOrArchivingAsync(documentId, ct).ConfigureAwait(false))
        {
            throw Calculations.RecalculationWritePolicy.Reject(
                Calculations.RecalculationWriteDenial.ProjectArchived, periodKey.Value, documentId);
        }

        // ⛔ Стан періоду і робочого процесу — ДО черги (ФВ-9.7, ФВ-9.17).
        // ✎ 07.11 (D-468): закритий період не перераховується жодним маршрутом — шлях один, Reopen.
        var state = await periods
            .FindPeriodStateAsync(documentId, periodKey.Value, ct)
            .ConfigureAwait(false);

        // ✎ UAT-recalc-ux: перелік пропущених — подані/затверджені аркуші ОБЛАСТІ (аркуш або склад документа). Читач
        // тут бачить усі аркуші області (перевірки видимості вище), тож перелік нічого схованого не називає.
        var skippedRows = await workflow
            .ListApprovedSheetsAsync(projectId: null, documentId, periodKey.Value, Calculations.RecalculationSkips.MaxListed + 1, ct)
            .ConfigureAwait(false);
        var (skipped, truncated) = Calculations.RecalculationSkips.Cap(
            [.. skippedRows.Where(r => sheetDefId is { } only
                ? r.SheetDefId == only
                : included is not { Count: > 0 } || included.Contains(r.SheetDefId))]);
        var nothing = false;

        if (state is { } periodState)
        {
            var sheets = await workflow
                .GetSheetsAsync(documentId, periodKey, ct)
                .ConfigureAwait(false);

            // ⛔ RC14 (приймальна №10, P2-3): область ОДНОГО аркуша відхиляється лише коли ПОДАНИЙ САМ цей
            // аркуш. Подані СУСІДНІ аркуші перерахунок області не чіпає (цілі запису — таблиці цього аркуша,
            // ФВ-9.17 про зріз аркуша), а без цього чернетка поруч із затвердженим аркушем не могла ні
            // перерахуватися, ні бути поданою («Recalculate before submitting» — глухий кут). Перерахунок
            // усього документа (`sheetDefId = null`) і закритий період — як і раніше.
            //
            // ⛔ RC15 (P2-A): перерахунок УСЬОГО документа (`sheetDefId = null`) подані/затверджені аркуші
            // ПРОПУСКАЄ (задача рахує лише чернеткові/відкриті й не чіпає ні їхніх комірок, ні результатів), а
            // не відхиляє цілий запит; відмова (той самий 4221) — коли подані/затверджені ВСІ аркуші складу
            // документа (рахувати нічого). Стан має лише аркуш, який хоч раз подавали, тож склад — з порту.
            // Задача (`RecalculationJob.RefusedPeriodsAsync`) перевіряє те саме ще раз на своєму шляху запису.
            // ✎ UAT-recalc-ux (слово людини 07.11: «тільки погоджені не мають перераховуватися»): пропускається лише
            // ЗАТВЕРДЖЕНИЙ аркуш; поданий перераховується (задача пише його журналом і позначкою для погоджувача).
            // ✎ F13 p218 (правило людини 07.11): аркуш «нічого рахувати» лише коли затверджений його ОРИГІНАЛ і КОЖНА копія
            // (RS: стан копії свій, `SheetCopyId`). Доти будь-який затверджений рядок аркуша (і оригінал, і копія) робив
            // увесь аркуш «затвердженим», і копії-чернетки лишались без перерахунку.
            var copiesBySheet = sheetCopies is null
                ? new Dictionary<int, IReadOnlyList<SheetCopyOrdinal>>()
                : (await sheetCopies.ListGroupsAsync(documentId, ct).ConfigureAwait(false))
                    .ToDictionary(g => g.SheetDefId, g => g.Copies);

            bool Approved(int sheetId, long? copyId) => sheets.Any(s =>
                s.SheetDefId == sheetId
                && s.SheetCopyId == copyId
                && s.Status is Domain.Enums.DocumentStatus.Approved);

            bool IsSubmitted(int sheetId)
                => Approved(sheetId, null)
                   && (copiesBySheet.GetValueOrDefault(sheetId) ?? []).All(c => Approved(sheetId, c.SheetCopyId));

            bool submitted;
            if (sheetDefId is { } scopeSheetId)
            {
                submitted = IsSubmitted(scopeSheetId);
            }
            else
            {
                // ⚠ Склад невідомий (рядків `doc.DocumentSheet` немає) — поводимось як раніше: будь-який
                // поданий аркуш відхиляє, а не пропускається (консервативно; задача перевірить за екземплярами).
                submitted = included is { Count: > 0 }
                    ? included.All(IsSubmitted)
                    : sheets.Any(s => IsSubmitted(s.SheetDefId));
            }

            // ✎ UAT-recalc-ux: закритий період — відмова, як і раніше; «подано все» — не відмова, а відповідь без
            // задачі з переліком (людина бачить, ЩО подано і куди перейти).
            var denial = Calculations.RecalculationWritePolicy.Check(
                periodState, hasSubmittedSheets: false);

            // ✎ LAND-YEAR B24 (D-NEW-3): закритий ЯКІР річної розкладки у відкритому році — не відмова: задача пише
            // не-місячні цілі під агрегатом року (`RecalculationJob.AnnualOnlyAnchorsAsync` — справжній гейт, за
            // ефективними станами й винятками документа; тут — швидка оцінка за збереженими станами).
            if (denial != Calculations.RecalculationWriteDenial.None
                && !(denial == Calculations.RecalculationWriteDenial.PeriodClosed
                     && await ClosedYearAnchorWritesAsync(documentId, periodKey, ct).ConfigureAwait(false)))
            {
                throw Calculations.RecalculationWritePolicy.Reject(denial, periodKey.Value, documentId);
            }

            nothing = submitted;
        }

        if (nothing)
        {
            return new Calculations.RecalculationRequestOutcome(null, skipped, truncated);
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
        // ⛔ Витіснення (Exclusive скасовує ВИКОНУВАНУ задачу) — лише власникам
        // `Calculation.Recalculate`. Виконавець із самим `Document.View` ставить без витіснення
        // (Coalesced): інакше будь-хто з читанням у циклі POST обривав би чужий довгий
        // перерахунок (DoS). Його перерахунок усе одно відбудеться після наявного.
        // ✎ LAND-YEAR B59: документ РІЧНОЇ розкладки — «Перерахувати» розходиться на всі місяці року (одна задача
        // «увесь рік», звужена до цього календарного року: `RecalculationRequest.Year`). Прогін одного місяця писав би
        // лише цілі, якими той володіє (якір — не-місячне + C_M01), і решта C_Mm лишалась би застарілою. Гейт стану на
        // шляху запису — той самий (`RecalculationJob.RefusedPeriodsAsync`: закриті місяці вилучаються, закритий якір —
        // лише не-місячне, B24). Поза річною розкладкою — названий період, як доти.
        var year = await YearLayoutYearAsync(documentId, periodKey, ct).ConfigureAwait(false);
        object payload = year is { } scopeYear
            ? new
            {
                DocumentId = documentId,
                PeriodKey = (int?)null,
                Year = scopeYear,
                TriggeredByUserId = currentUser.UserId,
                SheetDefId = sheetDefId,
            }
            : new
            {
                DocumentId = documentId,
                PeriodKey = periodKey.Value,
                TriggeredByUserId = currentUser.UserId,
                SheetDefId = sheetDefId,
            };
        var target = year is { } targetYear
            ? YearScopedTargetOf(documentId, targetYear, sheetDefId)
            : ScopedTargetOf(documentId, periodKey, sheetDefId);
        var projectId = await access.DocumentProjectIdAsync(documentId, ct).ConfigureAwait(false);
        var mayPreempt = projectId is { } pid
            ? Security.PermissionCheck.IsGrantedIn(profile, PreemptPermission, pid)
            : Security.PermissionCheck.IsGranted(profile, PreemptPermission);

        var jobId = mayPreempt
            ? await jobs
                .EnqueueExclusiveAsync<IRecalculationJob>(target, payload, ct, currentUser.UserId)
                .ConfigureAwait(false)
            : await jobs
                .EnqueueCoalescedAsync<IRecalculationJob>(target, payload, ct, currentUser.UserId)
                .ConfigureAwait(false);

        return new Calculations.RecalculationRequestOutcome(jobId, skipped, truncated);
    }

    /// <summary>
    /// ✎ LAND-YEAR B24: закритий <paramref name="periodKey"/> — якір року (найменший ключ календаря року) документа
    /// річної розкладки, а рік ще не закритий (<see cref="Security.AnnualPeriodRule.AnchorWritesAnnualTargets"/>).
    /// </summary>
    /// <remarks>
    /// Без реєстру розкладок (чи жодної річної версії) — <c>false</c> без жодного запиту (відмова, як доти). Версія — ПЕРІОДУ
    /// (<see cref="IProjectVersionTimeline"/>), не «чинна» проєкту. Запити — лише на шляху відмови закритим періодом:
    /// проєкт документа (кеш доступу), версія періоду (кеш шкали), стани періодів проєкту.
    /// </remarks>
    private async Task<bool> ClosedYearAnchorWritesAsync(long documentId, PeriodKey periodKey, CancellationToken ct)
    {
        if (layouts is not { HasAny: true }
            || timeline is null
            || await access.DocumentProjectIdAsync(documentId, ct).ConfigureAwait(false) is not { } projectId
            || !layouts.IsYearVersion(await timeline.VersionOfAsync(projectId, periodKey.Value, ct).ConfigureAwait(false)))
        {
            return false;
        }

        var year = (await periods.GetPeriodStatesAsync(projectId, periodKey: null, ct).ConfigureAwait(false))
            .Where(p => p.PeriodKey / 100 == periodKey.Year)
            .ToList();

        return year.Count > 0
               && year.Min(p => p.PeriodKey) == periodKey.Value
               && Security.AnnualPeriodRule.AnchorWritesAnnualTargets(
                   Domain.Enums.PeriodState.Closed, year.Select(p => p.State));
    }

    /// <summary>
    /// ✎ LAND-YEAR B59: рік <paramref name="periodKey"/>, якщо версія ПЕРІОДУ (<see cref="IProjectVersionTimeline"/>) має
    /// річну розкладку (<c>YearWithMonthColumns</c>); інакше <c>null</c>.
    /// </summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="periodKey">Період кнопки.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>Без реєстру розкладок (чи жодної річної версії) — <c>null</c> без жодного запиту.</remarks>
    private async Task<int?> YearLayoutYearAsync(long documentId, PeriodKey periodKey, CancellationToken ct)
    {
        if (layouts is not { HasAny: true }
            || timeline is null
            || await access.DocumentProjectIdAsync(documentId, ct).ConfigureAwait(false) is not { } projectId)
        {
            return null;
        }

        return layouts.IsYearVersion(await timeline.VersionOfAsync(projectId, periodKey.Value, ct).ConfigureAwait(false))
            ? periodKey.Year
            : null;
    }

    /// <summary>
    /// ✎ LAND-YEAR B59: ціль перерахунку документа річної розкладки за календарний рік (з областю аркуша чи без).
    /// </summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="year">Рік.</param>
    /// <param name="sheetDefId">Аркуш області; <c>null</c> — увесь документ.</param>
    /// <returns>
    /// <c>doc{id}-y{year}</c> (+ <c>-s{sheet}</c>): окремо від нічної <see cref="YearTargetOf"/> (усі роки) і від
    /// цілей одного періоду — злиття постановок не поглинає запит іншої області чи іншого обсягу.
    /// </returns>
    public static string YearScopedTargetOf(long documentId, int year, int? sheetDefId)
    {
        var target = string.Create(
            System.Globalization.CultureInfo.InvariantCulture, $"doc{documentId}-y{year}");
        return sheetDefId is { } sheet
            ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{target}-s{sheet}")
            : target;
    }

    /// <summary>Право, що дає витіснення виконуваного перерахунку того самого документа.</summary>
    internal const string PreemptPermission = "Calculation.Recalculate";

    private static bool HasUnnarrowedRead(Security.AccessProfile profile, int? projectId)
        => profile.Permissions.Contains(Permission)
           || (projectId is { } id
               && profile.Scoped.TryGetValue(id, out var scoped)
               && scoped.Permissions.Contains(Permission));

    /// <summary>Ціль перерахунку: документ і період.</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="periodKey">Період.</param>
    /// <returns>Ключ цілі для витіснення.</returns>
    /// <remarks>
    /// ⚠ Саме пара, а не самий документ: перерахунок різних періодів одного
    /// документа — це різна робота над різними партиціями, і витісняти одне
    /// одним означало б, що заповнення грудня скасовує перерахунок листопада.
    /// <para>
    /// ⚠ Q-331 / AN-91: ця ціль — перерахунок УСЬОГО документа за період. Аркуш входить у ціль лише
    /// в <see cref="ScopedTargetOf"/>: інакше постановку аркуша B поглинала б
    /// наявна <c>Queued</c> аркуша A (payload черга не змінює). Одночасність прогонів різних
    /// областей знімає лок документа в <c>RecalculationJob</c> (як для цілей періоду й року), а не
    /// збіг цілі.
    /// </para>
    /// </remarks>
    public static string TargetOf(long documentId, PeriodKey periodKey)
        => string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"doc{documentId}-p{periodKey.Value}");

    /// <summary>Ціль перерахунку з областю: увесь документ (<c>null</c>) або один аркуш.</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="sheetDefId">Аркуш області; <c>null</c> — увесь документ.</param>
    /// <returns>
    /// Для <c>null</c> — рівно <see cref="TargetOf"/> (її ж ставлять автоперерахунок,
    /// правка шапки й нічний розклад); для аркуша — окрема ціль «документ × період × аркуш».
    /// </returns>
    /// <remarks>
    /// ⛔ AN-91 (аудит 2026-10-09, N2): ціль без області давала злиття запитів з різною областю.
    /// Черга поглинає постановку наявною <c>Queued</c> на ту саму ціль, лишаючи її payload як є
    /// (<c>DbJobQueue.EnqueueSql</c>, <c>QuartzJobScheduler.CoalesceAsync</c>), тож запит аркуша B —
    /// або автоперерахунок усього документа після збору PI — тихо зливався в чергову задачу аркуша A
    /// і не виконувався: B лишався застарілим, поки хтось не перерахує ще раз.
    /// <para>
    /// ⚠ Ціна: перерахунок усього документа не витісняє виконуваний перерахунок аркуша — іде ПІСЛЯ
    /// нього (лок документа, відкладення при зайнятому).
    /// </para>
    /// </remarks>
    public static string ScopedTargetOf(long documentId, PeriodKey periodKey, int? sheetDefId)
        => sheetDefId is { } sheet
            ? string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"{TargetOf(documentId, periodKey)}-s{sheet}")
            : TargetOf(documentId, periodKey);

    /// <summary>Ціль перерахунку документа за ВЕСЬ рік (нічний розклад, P4 ФВ-9.8).</summary>
    /// <param name="documentId">Документ.</param>
    /// <returns>Ключ цілі для злиття постановок.</returns>
    /// <remarks>
    /// ⚠ Окрема від <see cref="TargetOf"/>: річна задача рахує всі періоди документа
    /// по черзі (<c>[Period:-1]</c>), і розкласти її на пари «документ + період»
    /// означало б втратити порядок. Два нічні запуски, поки перший ще в черзі,
    /// зливаються саме за цим ключем.
    /// </remarks>
    public static string YearTargetOf(long documentId)
        => string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"doc{documentId}-year");
}
