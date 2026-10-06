using System.Security.Cryptography;
using System.Text;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;
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
    //
    // ✎ Той самий порт тепер дає й `GetMethodologyIdsBoundToTablesAsync` —
    // звуження перевірки `IsStale` до аркушів, що мають хоч одну методологічну
    // прив'язку (див. коментар над перевіркою нижче).
    IMethodologyStore methodologies,

    // ⛔ Граф залежностей формул версії (`cfg.FormulaDependency`) — той самий,
    // яким живе каскадний перерахунок (`RecalculationService`,
    // `RecalculationReadScope`). Потрібен, щоб перевірка застарілості
    // методологій бачила таблиці ІНШИХ аркушів, які формули цього аркуша
    // читають (див. `FreshnessTablesAsync`).
    ITemplateVersionStore versions,

    // ⛔ D16-04: знімок полів довідника для `REGFIELD` у правилах — той самий,
    // що будує «Перевірити» (`TableValidation.RunAsync`).
    IRegistryStore registries,

    // ФВ-5.19: факт підтвердження попереджень — результатна подія аудиту в тій
    // самій транзакції, що й зріз (`IAuditWriter`, C4).
    IAuditWriter audit)
{
    /// <summary>
    /// Ключ тексту відмови подання з причиною <c>InsufficientGrantLevel</c> (D-285):
    /// називає й право <c>Document.Submit</c>, на відміну від загального <c>deny.InsufficientGrantLevel</c>.
    /// </summary>
    public const string SubmitInsufficientLevelReasonKey = "deny.InsufficientGrantLevel.Submit";

    /// <summary>Тип події аудиту: подавач підтвердив попередження валідації (ФВ-5.19).</summary>
    public const string WarningsAcknowledgedEventType = "SheetSubmitWarningsAcknowledged";

    /// <summary>Ключ каталогу відмови «попередження потребують підтвердження» (ФВ-5.19).</summary>
    public const string WarningsNeedConfirmationMessageKey = "err.ECR-SUB-4221.warningsNeedConfirmation";

    /// <summary>Подає аркуш на погодження без підтвердження попереджень.</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="sheetDefId">Аркуш.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task HandleAsync(long documentId, int sheetDefId, int periodKey, CancellationToken ct)
        => HandleAsync(documentId, sheetDefId, periodKey, acknowledgeWarnings: false, ct);

    /// <summary>Подає аркуш на погодження.</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="sheetDefId">Аркуш.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="acknowledgeWarnings">
    /// Подавач підтвердив попередження (<c>Warning</c>) валідації (ФВ-5.19): без
    /// підтвердження їхня наявність відхиляє подання з переліком.
    /// </param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="NotFoundException">Аркуша немає в складі документа.</exception>
    /// <exception cref="AccessDeniedException">Немає рівня <c>Submit</c>.</exception>
    /// <exception cref="BusinessRuleException">
    /// Валідація, осиротілі рядки, застарілі результати методологій або
    /// непідтверджені попередження.
    /// </exception>
    public async Task HandleAsync(
        long documentId, int sheetDefId, int periodKey, bool acknowledgeWarnings, CancellationToken ct)
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

        // ⛔ S2 / B-08: видимість документа — ПЕРШОЮ, до перевірки складу. Інакше
        // невидимий документ відповідав би `403` (рішення про подання) або
        // `404 sheetNotInDocument` — і те, і те каже, що документ існує.
        var profile = await access.BuildProfileAsync(userId, ct).ConfigureAwait(false);
        await Documents.DocumentVisibility.RequireVisibleAsync(access, profile, documentId, ct).ConfigureAwait(false);

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

        // ⛔ Схований від читача аркуш — та сама відмова, що й аркуш поза складом, ДО перевірки
        // стану/гранта (інакше `reason` розповідав би про стан аркуша, якого читач не бачить).
        await Documents.DocumentVisibility
            .RequireSheetVisibleAsync(documents, access, profile, documentId, sheetDefId, key, ct).ConfigureAwait(false);

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
                // ⛔ L6-02: структура документа — спільно й першою. Подання не йде
                // паралельно з переносом версії: перенос або вже зафіксований (і
                // версію нижче читаємо нову), або чекає на подання.
                var structure = await sheetGate.EnterStructureAsync(documentId, exclusive: false, innerCt)
                    .ConfigureAwait(false);

                // ⛔ AN-36b (рев'ю AN-36, P2-1): склад документа вище перевірено ДО
                // транзакції. Перенос версії, що зафіксувався між тим і цим блокуванням,
                // перенумерував аркуші — старого `sheetDefId` у новій версії немає, і
                // подання будувало б валідацію без жодної таблиці й стан погодження на
                // неіснуючий аркуш. Аркуш звіряється з версією, прочитаною ПІД
                // блокуванням (знімок метаданих — з кешу, без звернення до бази).
                if (structure is { } version)
                {
                    var current = await metadata.GetAsync(version, innerCt).ConfigureAwait(false);
                    Documents.DocumentStructure.EnsureSheetPresent(
                        current.Sheets.Any(x => x.Id == sheetDefId), documentId, sheetDefId);
                }

                // ⛔ L6-06: шапка — спільно, після структури й до аркуша. Подання
                // валідує шапку й кладе її у зріз; правка шапки (виняткове) або
                // вже зафіксована й подання бачить нову, або чекає на подання й
                // бачить поданий аркуш. Доти у зріз ішла шапка, яку вже правили.
                await sheetGate.EnterHeaderAsync(documentId, exclusive: false, innerCt).ConfigureAwait(false);
                await sheetGate.EnterSubmitAsync(documentId, sheetDefId, key, innerCt).ConfigureAwait(false);
                await SubmitUnderLockAsync(
                        documentId, sheetDefId, periodKey, key, userId, profile, acknowledgeWarnings, innerCt)
                    .ConfigureAwait(false);
            },
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Права, осиротілі рядки, валідація і зріз — під блокуванням, яке взяв
    /// <see cref="HandleAsync(long, int, int, bool, CancellationToken)"/>.
    /// </summary>
    private async Task SubmitUnderLockAsync(
        long documentId, int sheetDefId, int periodKey, PeriodKey key, int userId, AccessProfile profile,
        bool acknowledgeWarnings, CancellationToken ct)
    {
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
                    // ✎ D-285: «рівень замалий» для ПОДАННЯ має власний текст — він
                    // називає право Document.Submit; загальний `deny.InsufficientGrantLevel`
                    // бачать і при вставці в комірку, і при погодженні.
                    ["reasonKey"] = decision.Reason == EditDenyReason.InsufficientGrantLevel
                        ? SubmitInsufficientLevelReasonKey
                        : $"deny.{decision.Reason}",
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
            // ⛔ S6 (ФВ-6.6): блокують УСІ сироти документа, але тіло відмови
            // називає лише рядки таблиць, які подавач бачить. Рядки прихованих
            // таблиць не входять ні в `rowIds`, ні в число; коли видимих немає —
            // одне знеособлене зауваження без числа й адреси (як у валідації).
            // Межі читання й відповідність «рядок → таблиця» — лише на шляху
            // відмови: успішне подання не платить за них жодним запитом.
            var visibleOrphans = await VisibleOrphansAsync(profile, documentId, key, orphaned, ct)
                .ConfigureAwait(false);
            if (visibleOrphans.Count == 0)
            {
                throw new BusinessRuleException(
                    ErrorCodes.SubmitBlocked,
                    "Подання неможливе: є зауваження поза вашою видимістю.",
                    new Dictionary<string, object?>
                    {
                        ["messageKey"] = Validation.HiddenValidationIssues.MessageKey,
                    });
            }

            throw new BusinessRuleException(
                ErrorCodes.SubmitBlocked,
                $"Подання неможливе: рядків із втраченим посиланням на реєстр — {visibleOrphans.Count}.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-SUB-4221.orphanedRows",
                    ["rowCount"] = visibleOrphans.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["rowIds"] = visibleOrphans,
                });
        }

        // ⛔ Структура АРКУША, який подають, потрібна вже тут — до перевірки
        // застарілості методологій нижче, — щоб звузити ту перевірку до
        // таблиць САМЕ цього аркуша. Раніше цей блок (`templateVersionId` /
        // `snapshot` / `tables`) рахувався лише перед валідацією (див. нижче
        // за текстом); тепер рахується один раз тут і використовується в
        // обох місцях.
        var instances = await rowStore.GetTableInstancesAsync(documentId, key, ct).ConfigureAwait(false);
        var templateVersionId = await TemplateVersionOfAsync(documentId, instances, ct).ConfigureAwait(false);
        var snapshot = await metadata.GetAsync(templateVersionId, ct).ConfigureAwait(false);

        var sheet = snapshot.Sheets.FirstOrDefault(s => s.Id == sheetDefId);
        var tables = sheet?.Tables.Where(t => !t.IsDeleted).ToDictionary(t => t.Id)
                     ?? new Dictionary<int, Domain.Entities.Configuration.TableDef>();

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
        // ✎ ЗВУЖЕННЯ (мінімальне, наступний крок над початковим фіксом):
        // перевірку `IsStale` пропускаємо ЦІЛКОМ, якщо в АРКУШІ, що подають,
        // немає ЖОДНОЇ таблиці з методологічною прив'язкою
        // (`IMethodologyStore.GetMethodologyIdsBoundToTablesAsync` на всі
        // таблиці аркуша разом).
        // Аркуш, до жодної методології не причетний, більше не блокується
        // застарілістю ЧУЖОГО прив'язаного результату в сусідньому аркуші
        // того самого документа+періоду.
        //
        // ✎ ЗВУЖЕННЯ ДО АРКУША: для аркуша З прив'язкою свіжість питаємо лише
        // по таблицях ЦЬОГО аркуша (`tableDefIds`) — зміна входів у таблиці
        // іншого аркуша того самого документа+періоду його більше не блокує.
        // ⚠ Передаються ВСІ таблиці аркуша, а не лише прив'язані: зміна в
        // неприв'язаній таблиці того самого аркуша може бути входом
        // методології (консервативно — блокувати зайве, ніж подати застаріле).
        // Дисплей (`GetCalculationResultsHandler`) і далі питає по всьому
        // документу (`null`).
        // ⚠ До таблиць аркуша додається замикання міжтабличних (зокрема
        // крос-аркушевих) залежностей їхніх формул — див. `FreshnessTablesAsync`.
        //
        // ⚠ Результат методології НЕ входить у зріз подання (`D-69`,
        // `SnapshotPayloadAsync` нижче копіює лише клітинки), тобто застаріле
        // число лишалося б видимим на поданому й навіть ЗАТВЕРДЖЕНОМУ аркуші
        // назавжди (посилання живе, а не копія) — і це вирішальний аргумент
        // за блокуванням, а не попередженням: попередження на екрані «Подати»
        // ніхто не побачить УДРУГЕ на екрані «Погоджено».
        //
        // ✎ P3: прив'язки — ОДНИМ зверненням на всі таблиці аркуша
        // (`GetMethodologyIdsBoundToTablesAsync`), не циклом по таблицях: цикл
        // робив N походів у базу під винятковим блокуванням подання
        // (`SubmitSheetQueryCountTests`). Семантика та сама — «бодай одна
        // таблиця аркуша прив'язана».
        var sheetHasMethodologyBinding = false;
        if (tables.Count > 0)
        {
            var boundMethodologyIds = await methodologies
                .GetMethodologyIdsBoundToTablesAsync(tables.Keys, ct)
                .ConfigureAwait(false);
            sheetHasMethodologyBinding = boundMethodologyIds is { Count: > 0 };
        }

        if (sheetHasMethodologyBinding)
        {
            // ⛔ Не лише таблиці аркуша, а їхнє ЗАМИКАННЯ за графом формул.
            // Формула таблиці цього аркуша може читати таблицю ІНШОГО аркуша
            // (`FormulaDef.IsCrossSheet`); похідна клітинка тоді — вхід
            // методології, але її перезапис каскадом має `Origin =
            // Recalculation`, який стор навмисно не рахує, а сама правка лежить
            // у чужій таблиці. Без замикання подання проходило із застарілим
            // числом методології.
            var freshnessTables = await FreshnessTablesAsync(
                snapshot, templateVersionId, tables.Keys, ct).ConfigureAwait(false);
            var freshness = await methodologies
                .GetCalculationFreshnessAsync(documentId, periodKey, freshnessTables, ct)
                .ConfigureAwait(false);
            if (freshness.IsStale)
            {
                // ⛔ S6 (ФВ-6.6): `inputsChangedAt` — час правки, яка могла лежати
                // в прихованій таблиці замикання. Подавач, що не бачить бодай
                // однієї таблиці замикання, отримує те саме блокування без цього
                // часу (`null`). `calculatedAt` — час прогону, не даних таблиць.
                // Замикання `null` — перевірка по всьому документу, тож і
                // видимим мусить бути весь документ.
                var readable = await access.ReadScopeAsync(profile, documentId, ct).ConfigureAwait(false);
                var seesAllInputs = freshnessTables is null
                    ? readable.HiddenTableIds().Count == 0
                    : freshnessTables.All(readable.CanReadTable);

                throw new BusinessRuleException(
                    ErrorCodes.SubmitBlocked,
                    "Подання неможливе: результати методологій застаріли — "
                    + "входи документа змінилися після прогону розрахунку.",
                    new Dictionary<string, object?>
                    {
                        ["messageKey"] = "err.ECR-SUB-4221.staleMethodologyResults",
                        ["calculatedAt"] = freshness.CalculatedAt,
                        ["inputsChangedAt"] = seesAllInputs ? freshness.InputsChangedAt : null,
                    });
            }
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
        //
        // ⚠ `templateVersionId`/`snapshot`/`tables` уже прочитані вище (для
        // звуження перевірки застарілості) — тут вони лише використовуються.

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

        // ФВ-5.19: `Warning` не блокує, але потребує підтвердження подавача.
        var warnings = new List<Validation.ValidationMessage>();

        // ⛔ Обов'язкова колонка (`ColumnDef.IsRequired`), якої НІКОЛИ не
        // торкались редагуванням, не лишає запису в `doc.CellValue`
        // (ФВ-3.8) — і тому не проходить через жодну перевірку на шляху
        // запису: `PatchCellsHandler` перевіряє `IsRequired` лише в
        // момент явного `PATCH` цієї самої клітинки. Рядок із порожнім
        // обов'язковим полем, якого ніхто не торкався, спокійно проходив
        // подання. Перевірка тут читає САМІ РЯДКИ екземпляра
        // (`IRowStore.GetRowIdsBatchAsync`), а не клітинки, — інакше рядок без
        // жодного запису в зрізі був би для неї «не існує взагалі».
        var toValidate = new List<(TableInstanceRef Instance, Domain.Entities.Configuration.TableDef Table,
            List<Domain.Entities.Configuration.ColumnDef> RequiredColumns)>();
        foreach (var instance in instances)
        {
            if (!tables.TryGetValue(instance.TableDefId, out var table))
            {
                continue;
            }

            var requiredColumns = table.Columns.Where(c => !c.IsDeleted && c.IsRequired).ToList();
            if (table.ValidationRules.Count == 0 && requiredColumns.Count == 0)
            {
                continue;
            }

            toValidate.Add((instance, table, requiredColumns));
        }

        // ⛔ P3 (перф-аудит): зріз і рядки — ДВОМА пакетними запитами на всі
        // таблиці аркуша, а не парою запитів на КОЖНУ. Тут стояв цикл
        // `ReadSliceAsync` + `GetRowIdsAsync` по екземплярах — і все це під
        // винятковим блокуванням аркуша (`EnterSubmitAsync` вище), тобто
        // правки цього аркуша чекали на кожен похід у базу. Той самий прийом,
        // що вже закрив Q-165/Q-168 у `ValidateDocumentHandler`.
        // Храповик — `SubmitSheetQueryCountTests`.
        IReadOnlyDictionary<long, IReadOnlyList<CellRecord>> cellsByInstance =
            new Dictionary<long, IReadOnlyList<CellRecord>>();
        IReadOnlyDictionary<long, IReadOnlyDictionary<string, long>> rowIdsByInstance =
            new Dictionary<long, IReadOnlyDictionary<string, long>>();
        if (toValidate.Count > 0)
        {
            var validateIds = toValidate.ConvertAll(v => v.Instance.TableInstanceId);
            cellsByInstance = await cellStore.ReadSlicesAsync(validateIds, key, ct).ConfigureAwait(false);
            rowIdsByInstance = await rowStore.GetRowIdsBatchAsync(validateIds, key, ct).ConfigureAwait(false);
        }

        var rowIdsByTable = new Dictionary<int, IReadOnlyDictionary<string, long>>();

        foreach (var (instance, table, requiredColumns) in toValidate)
        {
            var cells = cellsByInstance.TryGetValue(instance.TableInstanceId, out var slice)
                ? slice
                : [];
            var rowIds = rowIdsByInstance.TryGetValue(instance.TableInstanceId, out var ids)
                ? ids
                : new Dictionary<string, long>(StringComparer.Ordinal);
            rowIdsByTable[table.Id] = rowIds;

            if (table.ValidationRules.Count > 0)
            {
                var tableMessages = await Validation.TableValidation
                    .RunAsync(validation, registries, snapshot, table, cells, rowIds, headerValues, currentUser.Language, ct)
                    .ConfigureAwait(false);
                blocking.AddRange(tableMessages.Where(m => m.Severity == ValidationSeverity.Error));
                warnings.AddRange(tableMessages.Where(m => m.Severity == ValidationSeverity.Warning));
            }

            blocking.AddRange(Validation.TableValidation.MissingRequiredColumnMessages(table, requiredColumns, cells, rowIds, currentUser.Language));
        }

        // ⛔ D-230: зв'язки Check аркуша. Block → Error блокує подання, Warn → Warning (з підтвердженням),
        // Info не впливає. Без активних Rollup/Check (`HasActiveRollupOrCheck`, з кешу метаданих) —
        // жодного додаткового запиту. Свіжі комірки: Rollup уже перераховано вище під тим самим блокуванням.
        if (snapshot.HasActiveRollupOrCheck)
        {
            var sheetInstances = instances.Where(i => tables.ContainsKey(i.TableDefId)).ToList();
            var relationMessages = await Validation.RelationCheckRunner
                .RunAsync(versions, cellStore, rowStore, metadata, sheetInstances, key, currentUser.Language, ct)
                .ConfigureAwait(false);
            blocking.AddRange(relationMessages.Where(m => m.Severity == ValidationSeverity.Error));
            warnings.AddRange(relationMessages.Where(m => m.Severity == ValidationSeverity.Warning));
        }

        if (blocking.Count > 0)
        {
            // ⛔ Порядок у тілі 422 — ЯВНИЙ, як на екрані: таблиця аркуша →
            // рядок → колонка. Доти він ішов за порядком рядків із БД без
            // `ORDER BY` (словник рядків) і за `TableInstance.Id` (порядок
            // створення екземплярів, а не таблиць на аркуші) — тобто міг
            // змінитися від плану запиту на тих самих даних.
            blocking = OrderAsOnScreen(blocking, tables, rowIdsByTable);

            // ⛔ S6 (ФВ-6.6): подання рахує аркуш ЦІЛКОМ, але тіло відмови читає
            // той, хто подає, — і таблиці й колонки під його забороною в ньому
            // не називаються. Приховані помилки — одним знеособленим
            // зауваженням без числа й адреси (`HiddenValidationIssues`).
            var anyPeriod = await access.ReadScopeAsync(profile, documentId, ct).ConfigureAwait(false);
            DocumentReadScope? readable = null;
            var shown = Validation.HiddenValidationIssues.ForViewer(
                blocking, m => Validation.HiddenValidationIssues.CanSee(readable ??= anyPeriod.InPeriod(key), m));
            var onlyHidden = shown.Count == 1 && Validation.HiddenValidationIssues.IsPlaceholder(shown[0]);

            // ⚠ Число — лише разом із видимими: `messageCount` рахує рядки
            // переліку (знеособлене — один рядок), а коли видимих немає, число
            // не несе нічого, крім натяку на обсяг прихованого, — його немає.
            var details = new Dictionary<string, object?>
            {
                ["messageKey"] = onlyHidden
                    ? Validation.HiddenValidationIssues.MessageKey
                    : "err.ECR-SUB-4221.validationBlocked",
                ["messages"] = MessageDetails(shown),
            };
            if (!onlyHidden)
            {
                details["messageCount"] = shown.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            throw new BusinessRuleException(
                ErrorCodes.SubmitBlocked,
                onlyHidden
                    ? "Подання неможливе: є зауваження поза вашою видимістю."
                    : $"Подання неможливе: блокувальних помилок валідації — {shown.Count}.",
                details);
        }

        // ⛔ ФВ-5.19: «Warning — з підтвердженням». Блокувальних помилок немає, але
        // є попередження, яких подавач ще не підтвердив, — відмова з їхнім
        // переліком (той самий код і форма тіла, що й `validationBlocked`;
        // клієнт розрізняє за `messageKey` і показує діалог підтвердження).
        //
        // ⚠ Лише ВИДИМІ подавачеві: приховане попередження (таблиця/колонка під
        // забороною читання) підтверджувати нема чого — він його не бачить, а
        // нічого не блокуючи, воно не має й тримати подання (`HiddenValidationIssues`).
        var shownWarnings = new List<Validation.ValidationMessage>();
        if (warnings.Count > 0)
        {
            var warnScope = await access.ReadScopeAsync(profile, documentId, ct).ConfigureAwait(false);
            DocumentReadScope? warnReadable = null;
            shownWarnings = OrderAsOnScreen(
                Validation.HiddenValidationIssues.ForViewer(
                    warnings, m => Validation.HiddenValidationIssues.CanSee(warnReadable ??= warnScope.InPeriod(key), m)),
                tables, rowIdsByTable);
        }

        if (shownWarnings.Count > 0 && !acknowledgeWarnings)
        {
            throw new BusinessRuleException(
                ErrorCodes.SubmitBlocked,
                $"Подання потребує підтвердження: попереджень валідації — {shownWarnings.Count}.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = WarningsNeedConfirmationMessageKey,
                    ["messages"] = MessageDetails(shownWarnings),
                    ["messageCount"] = shownWarnings.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
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
                documentId, sheetDefId, periodKey, key, userId, templateVersionId, instances, headerFieldCodes,
                shownWarnings, innerCt),
            ct).ConfigureAwait(false);
    }

    /// <summary>Перелік повідомлень для тіла відмови.</summary>
    private static List<object> MessageDetails(IEnumerable<Validation.ValidationMessage> messages)
        => [.. messages.Select(m => (object)new
        {
            m.RuleCode,
            m.Message,
            m.RowKey,
            m.ColumnCode,
            MessageKey = Validation.HiddenValidationIssues.IsPlaceholder(m)
                ? Validation.HiddenValidationIssues.MessageKey
                : null,
        })];

    /// <summary>Осиротілі рядки, які подавач має право бачити (S6).</summary>
    /// <remarks>
    /// ⚠ Документ без жодної прихованої таблиці — список як є, без запиту
    /// «рядок → таблиця». Інакше рядок невідомої таблиці — невидимий (закрито
    /// за замовчуванням, як у <see cref="DocumentReadScope"/>).
    /// </remarks>
    private async Task<IReadOnlyList<long>> VisibleOrphansAsync(
        AccessProfile profile, long documentId, PeriodKey key, IReadOnlyList<long> orphaned, CancellationToken ct)
    {
        var readable = await access.ReadScopeAsync(profile, documentId, ct).ConfigureAwait(false);
        if (readable.HiddenTableIds().Count == 0)
        {
            return orphaned;
        }

        var tableOfRow = await rowStore.GetTableDefIdsOfRowsAsync(orphaned, key, ct).ConfigureAwait(false);
        return
        [
            .. orphaned.Where(rowId => tableOfRow.TryGetValue(rowId, out var tableDefId)
                                       && readable.CanReadTable(tableDefId)),
        ];
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
        List<Validation.ValidationMessage> acknowledgedWarnings,
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

        // ФВ-5.19: подавач підтвердив попередження — слід у журналі, тією самою
        // транзакцією, що й зріз (результатна подія, C4): відкат подання не
        // лишає підтвердження, якого не було.
        if (acknowledgedWarnings.Count > 0)
        {
            await audit.WriteSecurityEventAsync(
                new SecurityEventRecord(
                    now,
                    WarningsAcknowledgedEventType,
                    TargetUserId: null,
                    TargetRoleId: null,
                    System.Text.Json.JsonSerializer.Serialize(new
                    {
                        documentId,
                        sheetDefId,
                        periodKey,
                        warningCount = acknowledgedWarnings.Count,
                        warnings = acknowledgedWarnings
                            .Select(m => new { m.RuleCode, m.RowKey, m.ColumnCode })
                            .ToList(),
                    }),
                    userId,
                    currentUser.CorrelationId),
                ct).ConfigureAwait(false);
        }

        await uow.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Блокувальні повідомлення в детермінованому порядку: таблиця аркуша
    /// (<c>TableDef.Ordinal</c>, далі <c>Id</c>) → рядок → колонка
    /// (<c>ColumnDef.Ordinal</c>) → код правила.
    /// </summary>
    /// <remarks>
    /// ⚠ Повідомлення без рядка (правила рівня таблиці й документа) — після
    /// рядкових своєї таблиці; без колонки — після колонкових свого рядка.
    /// Сортування стабільне (<c>OrderBy</c>), тож рівні ключі зберігають
    /// порядок, у якому їх видав двигун правил.
    /// </remarks>
    private static List<Validation.ValidationMessage> OrderAsOnScreen(
        List<Validation.ValidationMessage> messages,
        Dictionary<int, Domain.Entities.Configuration.TableDef> tables,
        Dictionary<int, IReadOnlyDictionary<string, long>> rowIdsByTable)
    {
        int TablePosition(Validation.ValidationMessage m)
            => tables.TryGetValue(m.TableDefId, out var table) ? table.Ordinal : int.MaxValue;

        // ⚠ НАБЛИЖЕННЯ: ключ рядка — `TableRow.Id`, тобто порядок СТВОРЕННЯ
        // рядків. Він розходиться з порядком на екрані (`TableRow.Ordinal`)
        // після вставки рядка посередині чи перестановки; пакетного `Ordinal`
        // у `IRowStore` немає ([debt]).
        long RowPosition(Validation.ValidationMessage m)
            => m.RowKey is { } rowKey
               && rowIdsByTable.TryGetValue(m.TableDefId, out var rowIds)
               && rowIds.TryGetValue(rowKey, out var rowId)
                ? rowId
                : long.MaxValue;

        int ColumnPosition(Validation.ValidationMessage m)
            => m.ColumnCode is { } code
               && tables.TryGetValue(m.TableDefId, out var table)
               && table.Columns.FirstOrDefault(c => string.Equals(c.Code, code, StringComparison.Ordinal)) is { } column
                ? column.Ordinal
                : int.MaxValue;

        return
        [
            .. messages
                .OrderBy(TablePosition)
                .ThenBy(m => m.TableDefId)
                .ThenBy(RowPosition)
                .ThenBy(m => m.RowKey is null ? 1 : 0)
                .ThenBy(m => m.RowKey, StringComparer.Ordinal)
                .ThenBy(ColumnPosition)
                .ThenBy(m => m.RuleCode, StringComparer.Ordinal),
        ];
    }

    /// <summary>
    /// Таблиці, зміна входів у яких робить результат методології аркуша
    /// застарілим: таблиці аркуша плюс транзитивне замикання того, що читають
    /// їхні формули (<c>cfg.FormulaDependency</c>).
    /// </summary>
    /// <remarks>
    /// ⚠ Замикання будується тим самим <see cref="Recalculation.RecalculationReadScope.Compute"/>,
    /// що звужує читання каскадного перерахунку, — другого графа тут немає.
    /// Один крок <c>Compute</c> дає прямі читання формул; повторюємо, доки
    /// набір таблиць росте (A читає B, B читає C → правка в C каскадом
    /// переписує B, потім A, і всі перезаписи — <c>Recalculation</c>).
    ///
    /// ⚠ Граф версії порожній, а формули в замиканні Є — версія, для якої граф
    /// не наповнювали (до <c>A7-63</c>): що формули читають, невідомо, тож
    /// перевірка йде по всьому документу (<c>null</c>), як до звуження.
    /// </remarks>
    private async Task<IReadOnlyCollection<int>?> FreshnessTablesAsync(
        Domain.Entities.Configuration.TemplateVersionSnapshot snapshot,
        int templateVersionId,
        IEnumerable<int> sheetTableIds,
        CancellationToken ct)
    {
        var scope = new HashSet<int>(sheetTableIds);

        var tableByFormula = snapshot.Sheets
            .SelectMany(s => s.Tables)
            .Where(t => !t.IsDeleted)
            .SelectMany(t => t.Formulas.Where(f => !f.IsDeleted).Select(f => (FormulaId: f.Id, TableId: t.Id)))
            .ToDictionary(p => p.FormulaId, p => p.TableId);

        IReadOnlyList<Domain.Entities.Configuration.FormulaDependency>? dependencies = null;

        while (true)
        {
            var targets = tableByFormula
                .Where(p => scope.Contains(p.Value))
                .Select(p => p.Key)
                .ToList();
            if (targets.Count == 0)
            {
                break;
            }

            // Граф читається лише тоді, коли в замиканні взагалі є формули.
            dependencies ??= await versions
                .ListFormulaDependenciesAsync(templateVersionId, ct)
                .ConfigureAwait(false) ?? [];
            if (dependencies.Count == 0)
            {
                return null;
            }

            var reads = Recalculation.RecalculationReadScope.Compute(
                dependencies, targets, tableByFormula, targetsWithUnknownReads: []);
            if (reads.TableDefIds is null)
            {
                return null;
            }

            var before = scope.Count;
            scope.UnionWith(reads.TableDefIds);
            if (scope.Count == before)
            {
                break;
            }
        }

        return [.. scope.Order()];
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
        // ⛔ P3 (перф-аудит): ОДИН пакетний запит на всі екземпляри замість
        // запиту на кожен — під винятковим блокуванням аркуша це ~90
        // послідовних походів у базу на типовому документі. Порядок, у якому
        // пакет повертає комірки, на payload не впливає: `SubmissionPayload.Write`
        // сам сортує за (рядок, колонка), тож `ContentHash` для тих самих даних
        // той самий байт-у-байт. Храповик — `SubmitSheetQueryCountTests`.
        var cells = new List<CellRecord>();

        if (instances.Count > 0)
        {
            var slices = await cellStore
                .ReadSlicesAsync([.. instances.Select(i => i.TableInstanceId)], periodKey, ct)
                .ConfigureAwait(false);
            foreach (var instance in instances)
            {
                if (slices.TryGetValue(instance.TableInstanceId, out var slice))
                {
                    cells.AddRange(slice);
                }
            }
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
