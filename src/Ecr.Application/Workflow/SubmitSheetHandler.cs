using System.Security.Cryptography;
using System.Text;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Workflow;

/// <summary>
/// Подання аркуша за період — гранулярність `аркуш × період` (D-38).
/// Створює **іммутабельний зріз** вхідних даних (ФВ-5.7, ФВ-9.4).
/// </summary>
public sealed class SubmitSheetHandler(
    ICellStore cellStore,
    IRowStore rowStore,
    IWorkflowStore workflow,
    IDocumentStore documents,
    IMetadataCache metadata,
    IAccessDecisionService access,

    // ⛔ `validation` тут ЧИТАЄТЬСЯ (директива №09 `W8` п.5, `S-28`). Доти
    // параметр був упорснутий і не читаний — навмисно, під точковим
    // придушенням `CS9113`, щоб незакрита вимога `ФВ-5.4` («рівні
    // рядка, таблиці й документа блокують `Submit`») лишалася видимою при
    // кожному складанні, а не зникла разом із прибраним аргументом (`Q-146`).
    // Подання перевіряло лише осиротілі рядки — при тому, що документація
    // методу вже обіцяла `BusinessRuleException` «валідація або осиротілі
    // рядки». Тепер обіцянка виконується, і придушення прибране.
    Validation.ValidationEngine validation,
    IDocumentHeaderStore headers,
    Reporting.ReportSnapshotSync reports,
    IUnitOfWork uow,
    ICurrentUser currentUser,
    IClock clock,
    ISheetEditGate sheetGate,

    // ⛔ Формули аркуша рахуються ТУТ, під винятковим блокуванням і до
    // валідації та зрізу (`SubmitRecalculationRaceTests`): каскадна задача
    // після правки стоїть у черзі й після подання поданий аркуш пропускає.
    Recalculation.ISubmitRecalculation recalculation,

    // ⛔ Застарілість результатів методологій (F-02/F-05, коміт `c98c90a8`).
    // Доти `IsStale` був ЛИШЕ візуальною позначкою в сітці й експорті:
    // `SubmitSheetHandler`/`ApproveSheetHandler` про неї не знали, і аркуш із
    // застарілим прив'язаним числом методології подавався й погоджувався так
    // само, як і свіжий. Перерахунок формул аркуша (`recalculation` вище) її
    // не закриває — це інший механізм (ФОРМУЛИ ШАБЛОНУ), методологічних
    // прив'язок (`calc.CalculationResult`, `cfg.CalculationBinding`) він не
    // чіпає (`D-69`).
    IMethodologyStore methodologies)
{
    /// <summary>Подає аркуш на погодження.</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="sheetDefId">Аркуш.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="NotFoundException">Аркуша немає в складі документа.</exception>
    /// <exception cref="AccessDeniedException">Немає рівня <c>Submit</c>.</exception>
    /// <exception cref="BusinessRuleException">
    /// Валідація, осиротілі рядки або застарілі результати методологій.
    /// </exception>
    public async Task HandleAsync(long documentId, int sheetDefId, int periodKey, CancellationToken ct)
    {
        var userId = currentUser.UserId
                     ?? throw new AccessDeniedException(
                         "ECR-AUTH-0401", "Анонімний запит не може подавати аркуші.",
                         new Dictionary<string, object?> { ["messageKey"] = "err.ECR-AUTH-0401.signInRequired" });

        // B-16 (UX-аудит, четвертий раунд): було `new PeriodKey(periodKey)` —
        // первинний конструктор нічого не перевіряє (він же матеріалізує
        // збережені значення), тож `periodKey=0` чи від'ємний проходив далі,
        // не 422. `Parse` — той самий спільний валідатор зовнішнього ключа
        // періоду, що вже стоїть у `CanRecallAsync` того самого модуля
        // (`RecallSheetHandler`) і в `GetDocumentTablesHandler`/
        // `GetWorkflowHistoryHandler`.
        var key = PeriodKey.Parse(periodKey);

        // ⛔ Аркуш мусить входити в СКЛАД документа. Без цієї перевірки
        // `POST …/submit` на довільний `sheetDefId` — навіть той, якого в
        // документі ніколи не було, — проходив кодом `204`:
        // `IWorkflowStore.GetOrCreateAsync` нижче створює новий рядок стану
        // для БУДЬ-ЯКОГО ідентифікатора, а `CanSubmitAsync` перевіряє права,
        // не існування (виміряно живим прогоном, директива №09 §6.4, `S-17`).
        if (!await documents.HasSheetAsync(documentId, sheetDefId, ct).ConfigureAwait(false))
        {
            throw new NotFoundException(
                "ECR-DOC-0404",
                $"Аркуша {sheetDefId} немає в складі документа {documentId}.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-DOC-0404.sheetNotInDocument",
                    ["sheetDefId"] = sheetDefId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["documentId"] = documentId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                });
        }

        // ⛔ Уся перевірка й сам зріз — ПІД винятковим блокуванням аркуша × періоду,
        // однією транзакцією (`ISheetEditGate`, `SubmitEditRaceTests`). Доти
        // транзакція відкривалась лише навколо запису зрізу і не брала жодного
        // блокування до `SaveChanges`: правка того самого аркуша, що приходила,
        // поки подання ще не зафіксоване, бачила під RCSI стан `Draft` і
        // проходила — у зріз не потрапляла, а в живій комірці й журналі лишалась
        // (`docs/build/UX-PASS-2026-09-23.md`).
        //
        // ⚠ Блокування береться ДО перевірки прав і валідації, а не лише навколо
        // зрізу: інакше правка між валідацією і зрізом дала б зріз, якого
        // валідація не бачила. Ціна — правки ЦЬОГО аркуша за ЦЕЙ період чекають
        // секунди подання; правки сусідніх аркушів і інших періодів — ні.
        await uow.ExecuteInTransactionAsync(
            async innerCt =>
            {
                await sheetGate.EnterSubmitAsync(documentId, sheetDefId, key, innerCt).ConfigureAwait(false);
                await SubmitUnderLockAsync(documentId, sheetDefId, periodKey, key, userId, innerCt)
                    .ConfigureAwait(false);
            },
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Права, осиротілі рядки, валідація і зріз — під блокуванням, яке взяв
    /// <see cref="HandleAsync"/>.
    /// </summary>
    private async Task SubmitUnderLockAsync(
        long documentId, int sheetDefId, int periodKey, PeriodKey key, int userId, CancellationToken ct)
    {
        var profile = await access.BuildProfileAsync(userId, ct).ConfigureAwait(false);

        var decision = await access.CanSubmitAsync(profile, documentId, sheetDefId, key, ct)
                                   .ConfigureAwait(false);
        if (!decision.IsAllowed)
        {
            throw new AccessDeniedException(
                "ECR-ACCS-0403",
                $"Подання аркуша {sheetDefId} відхилено: {decision.Reason}.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-ACCS-0403.submitDenied",
                    ["sheetDefId"] = sheetDefId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["reason"] = decision.Reason.ToString(),
                });
        }

        // ⚠ Рядки з IsOrphaned блокують подання (ФВ-8.13). До Етапу 4 прапорець
        // ніхто не ставить — перевірка коректна і завжди пропускає; це не
        // несправність, а порядок робіт.
        //
        // ⚠ Саме GetOrphanedRowIdsAsync, а не GetOrphanFlagsAsync: у другого
        // перший аргумент — TableInstanceId, і передача documentId туди
        // компілювалася, але не знаходила нічого ніколи.
        var orphaned = await rowStore.GetOrphanedRowIdsAsync(documentId, key, ct).ConfigureAwait(false);
        if (orphaned.Count > 0)
        {
            throw new BusinessRuleException(
                "ECR-SUB-4221",
                $"Подання неможливе: рядків із втраченим посиланням на реєстр — {orphaned.Count}.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-SUB-4221.orphanedRows",
                    ["rowCount"] = orphaned.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["rowIds"] = orphaned,
                });
        }

        // ⛔ ЗАСТАРІЛІСТЬ РЕЗУЛЬТАТІВ МЕТОДОЛОГІЙ (F-02/F-05, пряме інженерне
        // рішення). Факт: `IsStale` — стан, що НЕ минає сам. Його дає
        // `GetCalculationFreshnessAsync`: чи є після початку поточного
        // («Current») прогону запис у `aud.CellChange` для цього документа й
        // періоду. Прогін стає «Current» лише через явний запуск розрахунку
        // (`RunCalculationHandler`/`RecalculateDocumentHandler`, право
        // `Calculation.Recalculate`) — жодної фонової задачі, що сама б це
        // зняла, у системі немає. Отже, застаріле число лишається застарілим,
        // доки хтось не перерахує вручну, — точнісінько той клас стану, для
        // якого директива вимагає БЛОКУВАННЯ, а не попередження.
        //
        // ⚠ Гранулярність — ДОКУМЕНТ × ПЕРІОД, а не аркуш: сам порт
        // (`IMethodologyStore.GetCalculationFreshnessAsync`) іншої не дає, і
        // це не новий компроміс цього фікса — та сама гранулярність уже
        // сьогодні йде на дисплей (`GetCalculationResultsHandler`, F-05):
        // кожне число на панелі документа несе ОДИН прапорець свіжості на
        // весь документ+період, не по аркушу. Наслідок чесно називаю: подання
        // аркуша БЕЗ жодної прив'язки методології може заблокуватися через
        // застарілість чужого прив'язаного результату в СУСІДНЬОМУ аркуші
        // того самого документа+періоду. Звузити до таблиць САМЕ цього аркуша
        // можна було б через `GetMethodologyIdsBoundToTableAsync` по кожній
        // таблиці — свідомо не роблю цього зараз (scope цієї підзадачі), і
        // точність порту — за оркестратором/наступною ітерацією.
        //
        // ⚠ Результат методології НЕ входить у зріз подання (`D-69`,
        // `SnapshotPayloadAsync` нижче копіює лише клітинки), тобто застаріле
        // число лишалося б видимим на поданому й навіть ЗАТВЕРДЖЕНОМУ аркуші
        // назавжди (посилання живе, а не копія) — і це вирішальний аргумент
        // за блокуванням, а не попередженням: попередження на екрані «Подати»
        // ніхто не побачить УДРУГЕ на екрані «Погоджено».
        var freshness = await methodologies
            .GetCalculationFreshnessAsync(documentId, periodKey, ct)
            .ConfigureAwait(false);
        if (freshness.IsStale)
        {
            throw new BusinessRuleException(
                "ECR-SUB-4221",
                "Подання неможливе: результати методологій застаріли — "
                + "входи документа змінилися після прогону розрахунку.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-SUB-4221.staleMethodologyResults",
                    ["calculatedAt"] = freshness.CalculatedAt,
                    ["inputsChangedAt"] = freshness.InputsChangedAt,
                });
        }

        // ⛔ ПЕРЕРАХУНОК ФОРМУЛ АРКУША — до валідації й зрізу. Що було: правка
        // входу комітилась і ставила каскадний перерахунок у ЧЕРГУ; «Подати»
        // одразу після неї фіксувало в зрізі свіжий вхід і ЗАСТАРІЛЕ обчислене
        // число, а задача з черги після подання поданий аркуш уже пропускає
        // (ФВ-9.17) — тобто застаріле число лишалося назавжди, до Reopen.
        //
        // ⚠ Під ВИНЯТКОВИМ блокуванням цього аркуша: правки й інші перерахунки
        // цього аркуша стоять, тож рахується рівно те, що потім подається.
        // Спільного блокування цього аркуша прогін не бере (той самий власник).
        //
        // ⚠ Формула аркуша має право читати ІНШІ аркуші документа — вони
        // читаються в останньому зафіксованому стані БЕЗ блокувань, і цього
        // досить: зріз фіксує аркуш, порахований із того, що було правдою на
        // момент подання, а пізніші зміни сусіда поданого аркуша не змінюють
        // (ФВ-9.17). Блокувати сусідів, тримаючи виняткове, означало б дедлок
        // двох подань, що читають одне одного (X(A)+S(B) проти X(B)+S(A)).
        await recalculation
            .RecalculateSheetUnderSubmitLockAsync(documentId, sheetDefId, key, ct)
            .ConfigureAwait(false);

        var instances = await rowStore.GetTableInstancesAsync(documentId, key, ct).ConfigureAwait(false);

        // ⛔ ВАЛІДАЦІЯ АРКУША, який подають (`ФВ-5.4`, директива №09 `W8` п.5).
        // Це те, чого тут не було зовсім: подання перевіряло лише осиротілі
        // рядки, тобто аркуш із блокувальними помилками подавався кодом `204`
        // і йшов далі по маршруту погодження як придатний.
        //
        // ⚠ Прогін СВІЖИЙ, а не читання збереженого підсумку
        // (`IValidationResultStore.GetLatestAsync`). Підсумок відповідає на
        // питання «що показала перевірка тоді», а подання питає «що з даними
        // ЗАРАЗ»: між натисканням «Перевірити» і «Подати» дані міняються, і
        // саме ця пара кроків — найзвичніший спосіб обійти перевірку, не
        // маючи такого наміру.
        //
        // ⚠ Рахується лише АРКУШ, який подають, а не документ цілком:
        // гранулярність робочого процесу — `аркуш × період` (`D-38`), і
        // блокувати подання одного аркуша помилкою сусіднього означало б
        // зробити багатоаркушевий документ неподаваним по частинах.
        var templateVersionId = await TemplateVersionOfAsync(documentId, instances, ct).ConfigureAwait(false);
        var snapshot = await metadata.GetAsync(templateVersionId, ct).ConfigureAwait(false);

        var sheet = snapshot.Sheets.FirstOrDefault(s => s.Id == sheetDefId);
        var tables = sheet?.Tables.Where(t => !t.IsDeleted).ToDictionary(t => t.Id)
                     ?? new Dictionary<int, Domain.Entities.Configuration.TableDef>();

        // ⛔ Шапка документа читається РЕАЛЬНО — той самий дефект, що й у
        // ValidateDocumentHandler/PatchCellsHandler: подання зобов'язане
        // рахувати РІВНО те саме, що показує кнопка «Перевірити» (R-B3).
        var headerValues = await headers.GetExpressionValuesAsync(documentId, ct).ConfigureAwait(false);

        // ⛔ Ідентифікатор поля → код (ФВ-9.4). Лише зіставлення з версії шаблону
        // (вже прочитаної як snapshot), запиту не додає. Значення шапки читаються
        // ЗАНОВО всередині транзакції (SnapshotPayloadAsync) — той самий принцип
        // свіжості, що й у клітинок: зріз подання має нести те, що правдиве ЗАРАЗ,
        // а не те, що було правдиве на момент валідації вище.
        var headerFieldCodes = snapshot.HeaderFields
            .Where(f => !f.IsDeleted)
            .ToDictionary(f => f.Id, f => f.Code);

        var blocking = new List<Validation.ValidationMessage>();

        foreach (var instance in instances)
        {
            if (!tables.TryGetValue(instance.TableDefId, out var table))
            {
                continue;
            }

            // ⛔ Обов'язкова колонка (`ColumnDef.IsRequired`), якої НІКОЛИ не
            // торкались редагуванням, не лишає запису в `doc.CellValue`
            // (ФВ-3.8) — і тому не проходить через жодну перевірку на шляху
            // запису: `PatchCellsHandler` перевіряє `IsRequired` лише в
            // момент явного `PATCH` цієї самої клітинки. Рядок із порожнім
            // обов'язковим полем, якого ніхто не торкався, спокійно проходив
            // подання. Перевірка тут читає САМІ РЯДКИ екземпляра
            // (`IRowStore.GetRowIdsAsync`), а не клітинки, — інакше рядок без
            // жодного запису в зрізі був би для неї «не існує взагалі».
            var requiredColumns = table.Columns.Where(c => !c.IsDeleted && c.IsRequired).ToList();
            if (table.ValidationRules.Count == 0 && requiredColumns.Count == 0)
            {
                continue;
            }

            var cells = await cellStore.ReadSliceAsync(instance.TableInstanceId, ct).ConfigureAwait(false);
            var rowIds = await rowStore.GetRowIdsAsync(instance.TableInstanceId, key, ct).ConfigureAwait(false);

            if (table.ValidationRules.Count > 0)
            {
                blocking.AddRange(Validation.TableValidation
                    .Run(validation, table, cells, rowIds, headerValues, currentUser.Language)
                    .Where(m => m.Severity == ValidationSeverity.Error));
            }

            blocking.AddRange(MissingRequiredColumnMessages(table, requiredColumns, cells, rowIds));
        }

        if (blocking.Count > 0)
        {
            throw new BusinessRuleException(
                "ECR-SUB-4221",
                $"Подання неможливе: блокувальних помилок валідації — {blocking.Count}.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-SUB-4221.validationBlocked",
                    ["messageCount"] = blocking.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["messages"] = blocking
                        .Select(m => new { m.RuleCode, m.Message, m.RowKey, m.ColumnCode })
                        .ToList(),
                });
        }

        // ⛔ `DAT-06`. Зріз, стан аркуша й проведення в звітність — ОДНИМ
        // комітом. Доти їх було три: `WorkflowStore.SaveSnapshotAsync` кличе
        // `SaveChangesAsync` сам (йому потрібен `IDENTITY` зрізу), далі
        // `ReportSnapshotSync.MarkSubmittedAsync` пише своє, і аж наприкінці
        // йшов `uow.SaveChangesAsync`. Збій між ними лишав у базі рівно той
        // стан, якого не має бути ніколи: зріз подання є, а аркуш не поданий —
        // або навпаки. Обидві половини — «доказ того, що пішло регуляторові»
        // (`ФВ-9.17`), і нарізно вони не доказ, а розбіжність.
        //
        // ⚠ `SaveSnapshotAsync` усередині транзакції лишається як був: його
        // `SaveChangesAsync` тепер лише матеріалізує `IDENTITY`, не комітячи.
        // `ExecuteInTransactionAsync` приєднується до зовнішньої транзакції
        // (`UnitOfWork.cs:174-178`), тож це обгортка, а не переробка.
        await uow.ExecuteInTransactionAsync(
            innerCt => SubmitCoreAsync(
                documentId, sheetDefId, periodKey, key, userId, templateVersionId, instances, headerFieldCodes, innerCt),
            ct).ConfigureAwait(false);
    }

    /// <summary>Зріз, стан аркуша й проведення в звітність — усе під транзакцією.</summary>
    private async Task SubmitCoreAsync(
        long documentId,
        int sheetDefId,
        int periodKey,
        PeriodKey key,
        int userId,
        int templateVersionId,
        IReadOnlyList<TableInstanceRef> instances,
        IReadOnlyDictionary<int, string> headerFieldCodes,
        CancellationToken ct)
    {
        var state = await workflow.GetOrCreateAsync(documentId, sheetDefId, key, ct).ConfigureAwait(false);

        // ⚠ Іммутабельний зріз створюється ДО зміни стану: якщо зріз не
        // збережеться, аркуш не має стати поданим. Поданий аркуш без зрізу —
        // звіт, який неможливо ні звірити, ні перерахувати «як тоді».
        var payload = await SnapshotPayloadAsync(documentId, instances, key, headerFieldCodes, ct).ConfigureAwait(false);
        var now = clock.UtcNow;

        // ⛔ `TemplateVersionId` — СПРАВЖНІЙ (директива №09 `W8` п.5).
        // Тут стояв нуль: зріз, створений заради того, щоб через рік можна
        // було сказати «за якою структурою це подавали», не ніс структури
        // взагалі. Нуль при цьому не помітний нізвідки — він виглядає як
        // значення.
        //
        // ⚠ `MethodologyVersionsJson`, `NumericMode` і `CalendarMode`
        // лишаються незаповненими СВІДОМО (`Q-155`, RESOLVED): чинні версії
        // методологій і чинні режими цього документа сюди не проведені —
        // `SubmitSheetHandler` не має порту, який би це дав. Раніше тут
        // писалися підставні `NumericMode.Legacy`/`CalendarMode.Actual`
        // «щоб не порожньо» — саме та помилка, яку зробив `TemplateVersionId:
        // 0` у `W8` до того, як її помітили: підставне значення виглядає як
        // зафіксований вибір, а не як «невідомо». `null` тут видно й
        // перевіряється, тому обидва поля тепер `byte?` аж до домену й
        // стовпця (`SubmissionSnapshot.NumericMode`/`CalendarMode`,
        // `calc.SubmissionSnapshot`).
        await workflow.SaveSnapshotAsync(
            new SubmissionSnapshotRecord(
                documentId, sheetDefId, periodKey,
                templateVersionId,
                MethodologyVersionsJson: null,
                NumericMode: null,
                CalendarMode: null,
                PayloadJson: payload,
                ContentHash: Hash(payload),
                SubmittedAt: now,
                SubmittedByUserId: userId),
            ct).ConfigureAwait(false);

        // ⛔ Подання СТАВИТЬ аркуш на перший крок маршруту (`ФВ-5.17`).
        // Без цього багатоетапність існувала б лише в таблиці: маршрут
        // завели б, а документ ішов би повз нього.
        //
        // ⚠ Маршруту немає — крок `null`, і поведінка та сама, що була.
        var step = await access
            .CurrentApprovalStepAsync(documentId, sheetDefId, key, ct)
            .ConfigureAwait(false);

        var fromStatus = state.Status;
        state.Submit(userId, now, step?.StepId);

        // `BE-11`: перехід лягає в журнал тим самим комітом, що й стан.
        await workflow.AddEventAsync(
            ApprovalEvent.For(state, fromStatus, ApprovalAction.Submit, userId, now), ct).ConfigureAwait(false);

        // ⛔ Подання СПОВІЩЕННЯ НЕ ПОРОДЖУЄ (`D-119`). Тут раніше стояла
        // постановка події в чергу — прибрано за рішенням замовника: лист про
        // кожне подання це шум, а шум вимикають разом із корисними листами.
        //
        // ⚠ Події черги — лише ЗБОЇ: збір, архівація, стани періодів,
        // перевищення бюджету перерахунку. Їх зводить `NotificationJob`.

        // ⛔ Подання МОРОЗИТЬ зріз звітності, якщо в періоді не лишилося
        // неподаних аркушів (`H-23b`, ФВ-9.17). На цьому тримається `ER-C-11`:
        // «подане не перераховується». Доти позначку `Submitted` не ставив
        // ніхто, і гарантія існувала лише на письмі — зріз, за яким звіт уже
        // пішов регуляторові, спокійно перебудовувався з іншими числами.
        await reports
            .MarkSubmittedAsync(documentId, key, userId, ct)
            .ConfigureAwait(false);

        await uow.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Обов'язкова колонка (<c>ColumnDef.IsRequired</c>) без заповненого
    /// значення для кожного існуючого рядка екземпляра.
    /// </summary>
    /// <remarks>
    /// ⚠ «Заповнене» перевіряється як <c>!Value.IsEmpty</c>, а не як «є запис
    /// у зрізі»: явна порожнеча (R-B4) теж матеріалізується, і рядок, у якому
    /// обов'язкову клітинку колись занулили, має блокувати подання так само,
    /// як рядок, де її взагалі не було. `PatchCellsHandler`/`ColumnDef.ValidateValue`
    /// вже забороняють ЗАПИСАТИ такий стан явно (`ECR-CELL-0422`) — ця
    /// перевірка ловить рядок, що прийшов до цього стану БЕЗ жодного запису
    /// (найчастіше — просто ніхто не торкався клітинки).
    /// </remarks>
    private static List<Validation.ValidationMessage> MissingRequiredColumnMessages(
        Domain.Entities.Configuration.TableDef table,
        List<Domain.Entities.Configuration.ColumnDef> requiredColumns,
        IReadOnlyList<CellRecord> cells,
        IReadOnlyDictionary<string, long> rowIds)
    {
        if (requiredColumns.Count == 0 || rowIds.Count == 0)
        {
            return [];
        }

        var filled = cells
            .Where(c => !c.Value.IsEmpty)
            .Select(c => (c.Address.TableRowId, c.Address.ColumnDefId))
            .ToHashSet();

        var messages = new List<Validation.ValidationMessage>();
        foreach (var (rowKey, rowId) in rowIds)
        {
            foreach (var column in requiredColumns)
            {
                if (filled.Contains((rowId, column.Id)))
                {
                    continue;
                }

                messages.Add(new Validation.ValidationMessage(
                    ValidationSeverity.Error,
                    "ECR-CELL-0422",
                    $"Колонка «{column.Code}» обов'язкова.",
                    table.Id,
                    rowKey,
                    column.Code,
                    BlocksSave: true));
            }
        }

        return messages;
    }

    /// <summary>Версія шаблону, за якою живе документ.</summary>
    /// <remarks>
    /// ⚠ Спершу з уже прочитаних екземплярів таблиць — там вона є
    /// (<see cref="TableInstanceRef.TemplateVersionId"/>), і зайвий запит
    /// нічого не додасть. Запит іде лише тоді, коли екземплярів немає взагалі:
    /// документ за цей період ще не відкривали, а зріз подання однаково має
    /// назвати структуру.
    /// </remarks>
    private async Task<int> TemplateVersionOfAsync(
        long documentId, IReadOnlyList<TableInstanceRef> instances, CancellationToken ct)
        => instances.Count > 0
            ? instances[0].TemplateVersionId
            : await documents.GetTemplateVersionIdAsync(documentId, ct).ConfigureAwait(false);

    /// <summary>Зліпок значень аркуша (і шапки документа) у стабільному порядку.</summary>
    /// <remarks>
    /// Порядок фіксований навмисно: контрольна сума має залежати від ДАНИХ, а
    /// не від того, як їх повернула база цього разу.
    ///
    /// ⛔ Читаються ЕКЗЕМПЛЯРИ ТАБЛИЦЬ, а не «зріз документа». Тут стояло
    /// <c>cellStore.ReadSliceAsync(documentId)</c> — при тому, що перший
    /// аргумент цього порту зветься <c>tableInstanceId</c>. Обидва
    /// <c>long</c>, тож компілятор мовчав, а зріз подання набирався з
    /// екземпляра таблиці, чий ідентифікатор ВИПАДКОВО збігся з номером
    /// документа: майже завжди — з порожнечі, іноді — з чужих даних. Той
    /// самий клас помилки, що вже коштував перевірки осиротілих рядків
    /// (<c>IRowStore.GetOrphanedRowIdsAsync</c>).
    ///
    /// ⛔ Значення шапки (ФВ-9.4) копіюються в payload, а не лишаються
    /// посиланням: за аналізом старої системи-джерела (Excel/VBA, вкладка
    /// "Contract") подання ЗАВЖДИ вбудовує поточну шапку в збережений запис —
    /// знімок на момент подання, потрібний для історичної точності (якщо
    /// шапку пізніше змінять, уже подані звіти мають зберігати те, що було
    /// правдою тоді). Поле без запису в <c>IDocumentHeaderStore.GetValuesAsync</c>
    /// (шапки ніхто не торкався) у payload не потрапляє — той самий підхід,
    /// що вже застосований до клітинок (R-B4: відсутність запису ≠ явна
    /// порожнеча, але сюди різниця не проведена, бо шапка й так необов'язкова
    /// в переважній більшості шаблонів).
    /// </remarks>
    private async Task<string> SnapshotPayloadAsync(
        long documentId,
        IReadOnlyList<TableInstanceRef> instances,
        PeriodKey periodKey,
        IReadOnlyDictionary<int, string> headerFieldCodes,
        CancellationToken ct)
    {
        var cells = new List<CellRecord>();

        foreach (var instance in instances)
        {
            cells.AddRange(await cellStore
                .ReadSliceAsync(instance.TableInstanceId, ct).ConfigureAwait(false));
        }

        var headerRaw = await headers.GetValuesAsync(documentId, ct).ConfigureAwait(false);
        var headerByCode = headerRaw
            .Where(kv => headerFieldCodes.ContainsKey(kv.Key))
            .ToDictionary(kv => headerFieldCodes[kv.Key], kv => kv.Value, StringComparer.Ordinal);

        return SubmissionPayload.Write(
            cells.Where(c => c.Address.PeriodKey.Value == periodKey.Value),
            headerByCode);
    }

    private static string Hash(string payload)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
}
