using System.Text.Json;
using ClosedXML.Excel;
using Ecr.Application.Common;
using Ecr.Application.Documents;
using Ecr.Application.Documents.Dto;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.ValueObjects;

namespace Ecr.Adapters.Excel;

/// <summary>
/// Імпорт із <c>.xlsx</c> — **завжди** через попередній перегляд diff (ФВ-4.3).
/// </summary>
/// <remarks>
/// Імпорт без перегляду — це спосіб непомітно перезаписати чужу роботу.
/// Тому застосування розділене на два кроки, і між ними користувач бачить,
/// що саме зміниться, що конфліктує і що буде відхилено.
/// </remarks>
public sealed class ExcelImporter(
    IMetadataCache metadata,
    IRegistryStore registries,
    IAccessDecisionService access,
    ICurrentUser currentUser,
    IImportPreviewStore previews,
    PatchCellsHandler patch,
    ImportDiffBuilder diffBuilder,
    ICellStore cellStore,
    IRowStore rowStore,
    IUnitOfWork uow,
    IBackgroundJobScheduler jobs,

    // ⛔ Спільні блокування аркушів книги беруться ЗАЗДАЛЕГІДЬ і в стабільному
    // порядку (`ExcelImportSheetLockOrderTests`) — див. `ApplyAsync`.
    ISheetEditGate sheetGate,

    // Порти оверлею необов'язкові лише заради тестів, що конструюють імпортер
    // вручну; у контейнері розв'язуються завжди.
    IMethodologyStore? methodologies = null,
    ICalculationResultStore? results = null) : IExcelImporter
{
    /// <summary>Порожній зріз — таблиця без жодного рядка чи непорожньої комірки.</summary>
    private static readonly IReadOnlyDictionary<string, long> EmptyRowIds =
        new Dictionary<string, long>(StringComparer.Ordinal);

    private static readonly IReadOnlyDictionary<string, string> EmptyVersions =
        new Dictionary<string, string>(StringComparer.Ordinal);

    private static readonly IReadOnlyList<CellRecord> EmptySlice = [];

    /// <summary>
    /// Скільки живе побудований diff.
    /// </summary>
    /// <remarks>
    /// ⚠ Пів години — це час на перегляд, а не на робочий день. Diff — знімок
    /// чужих даних на момент побудови; застосований назавтра, він перезаписав
    /// би все, що зробили після нього, і виглядало б це як «імпорт зіпсував
    /// документ».
    /// </remarks>
    public static TimeSpan PreviewLifetime => TimeSpan.FromMinutes(30);

    /// <summary>Налаштування збереження diff; спільні на всі виклики.</summary>
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <inheritdoc />
    public async Task<ImportPreview> PreviewAsync(long documentId, Stream file, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(file);

        // ⛔ S10: огляд zip-каталогу потребує позиціювання. Потік без нього
        // буферизується з тією ж стелею, що й тіло запиту, — інакше сам буфер
        // став би обходом запобіжника.
        var seekable = await XlsxSafetyGate.SeekableAsync(file, ct).ConfigureAwait(false)
                       ?? throw XlsxSafetyGate.Rejection("файл більший за стелю пакета");
        await using var buffered = ReferenceEquals(seekable, file) ? null : seekable;

        using var workbook = Open(seekable);
        var map = ReadMap(workbook);

        // ⛔ Книга з чужого документа не імпортується в цей. Однакова
        // структура шаблону робить таку книгу правдоподібною до останньої
        // комірки — і саме тому перевірка тут, а не в очах користувача.
        if (map.DocumentId != documentId)
        {
            throw new BusinessRuleException(
                "ECR-IMP-0422",
                $"Книгу вивантажено з документа {map.DocumentId}, а імпорт іде в {documentId}.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-IMP-0422.workbookOtherDocument",
                    ["expectedDocumentId"] = documentId,
                    ["actualDocumentId"] = map.DocumentId,
                });
        }

        var period = new PeriodKey(map.PeriodKey);

        // ⛔ Q-236 (аудит фази 3, Excel-обмін). ВЕРСІЯ ШАБЛОНУ РЕЗОЛВИТЬСЯ З
        // БД за documentId/period, а НЕ береться з `map.TemplateVersionId`.
        // Той самий принцип, що й у `IRowStore.ResolveTableInstanceAsync`
        // («клієнт не має диктувати, за якою версією тлумачити дані») —
        // книга .xlsx тут рівно такий самий «клієнт», як і тіло HTTP-запиту:
        // до `A7-29`/цього фікса код довіряв полю, записаному в файл на
        // момент ЕКСПОРТУ, і republish шаблону між експортом і імпортом
        // (звичайна подія за рік звітності) робив ВЕСЬ diff порівнянням проти
        // структури, якої вже нема — з мовчазним ризиком не лише хибного
        // типу комірки (`Read` нижче бере тип з цього знімка), а й того, що
        // `ApplyAsync` впаде на `PatchCellsHandler` (він завжди резолвить
        // ПОТОЧНУ версію) лише на ПІВ книги, лишивши решту незастосованою без
        // жодного натяку в перегляді.
        //
        // ⚠ Той самий виклик заразом дає список `TableInstanceId`, які СПРАВДІ
        // належать цьому документу за цей період — блок книги з чужим
        // (чи вигаданим) `TableInstanceId` інакше пішов би прямо в пакетні
        // читання рядків/комірок нижче без жодної перевірки належності.
        // ⛔ Екземпляри таблиць створюються при ПЕРШОМУ відкритті документа
        // (`GetDocumentTablesHandler`, `A7-30`). Документ, створений і ще не
        // відкритий, їх не має, і перегляд імпорту відмовляв «документа не існує або він
        // порожній» — хоча документ є і шаблон дає йому таблиці (UX-прохід
        // 2026-09-24, живий стенд). Виклик ідемпотентний.
        await rowStore.EnsureTableInstancesAsync(documentId, period, ct).ConfigureAwait(false);

        var instances = await rowStore.GetTableInstancesAsync(documentId, period, ct).ConfigureAwait(false);

        if (instances.Count == 0)
        {
            throw new NotFoundException(
                "ECR-DOC-0404",
                $"Документа {documentId} за період {map.PeriodKey} не існує або він порожній.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-DOC-0404.periodEmpty",
                    ["documentId"] = documentId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["periodKey"] = map.PeriodKey.ToString(System.Globalization.CultureInfo.InvariantCulture),
                });
        }

        var validInstances = instances.ToDictionary(i => i.TableInstanceId, i => i.TableDefId);
        var snapshot = await metadata.GetAsync(instances[0].TemplateVersionId, ct).ConfigureAwait(false);
        var tables = snapshot.Sheets.SelectMany(s => s.Tables).ToDictionary(t => t.Id);

        var userId = currentUser.UserId
                     ?? throw new AccessDeniedException(
                         "ECR-AUTH-0401", "Анонімний запит не може імпортувати дані.",
                         new Dictionary<string, object?> { ["messageKey"] = "err.ECR-AUTH-0401.signInRequired" });

        var profile = await access.BuildProfileAsync(userId, ct).ConfigureAwait(false);
        var lookups = await LookupsAsync(snapshot, ct).ConfigureAwait(false);

        // ⛔ S6 (ФВ-6.6): межі читання того, хто імпортує. Перегляд порівнює
        // книгу з ПОТОЧНИМИ значеннями, і будь-яка відповідь, що залежить від
        // значення прихованої комірки («нічого не зміниться» проти «відмова»),
        // — оракул: підставляючи числа в книгу, прочитати приховане можна було
        // без жодного права на читання. Приховане порівнянню не віддається
        // зовсім (див. нижче і `ImportDiffBuilder.Build`).
        var readable = (await access.ReadScopeAsync(profile, documentId, ct).ConfigureAwait(false)).InPeriod(period);

        var diffs = new List<TableDiff>(map.Tables.Count);
        var changes = new List<ImportChange>();
        var rejected = new List<ImportRejection>();
        var overwritable = new List<ImportChange>();

        // ⛔ Q-168 (аудит фази 2, продуктивність). Таблиці з файлу, яких немає
        // в чинній версії шаблону, відхиляються ТУТ, ДО пакетного читання —
        // інакше довелося б або читати рядки/комірки для екземпляра, який
        // діагностика все одно відкине, або гілкувати пакетний запит під
        // список, що звужується в циклі.
        var validBlocks = new List<(ExcelTableBlock Block, TableDef Table)>(map.Tables.Count);

        foreach (var block in map.Tables)
        {
            // ⛔ Q-236: екземпляр з файлу має належати ЦЬОМУ документу за
            // ЦЕЙ період, і його `TableDefId` — збігатися з тим, що зараз
            // справді стоїть у БД за цим `TableInstanceId`. Без цієї
            // перевірки книга з чужого документа (той самий шаблон, інший
            // проєкт) могла б підмінити `TableInstanceId` і змусити код нижче
            // прочитати рядки й комірки ЧУЖОГО екземпляра ще ДО будь-якого
            // рішення про доступ.
            //
            // ⛔ S6: таблиця під забороною читання — та сама відмова, що й
            // неіснуючий екземпляр, і ДО читання її рядків і комірок. Інакше
            // відмова несла б код і назву прихованої таблиці зі знімка, а
            // «зміна проти відмови» — чи збігається її значення з книжковим.
            if (!validInstances.TryGetValue(block.TableInstanceId, out var actualTableDefId)
                || actualTableDefId != block.TableDefId
                || !readable.CanReadTable(actualTableDefId))
            {
                rejected.Add(new ImportRejection(
                    "—", block.TableCode, "ECR-IMP-0422",
                    "Екземпляра таблиці з файлу немає в цьому документі за цей період: "
                    + "структуру, ймовірно, змінено після експорту.",
                    block.TableCode,
                    MessageKey: ImportMessageKeys.InstanceMissing));

                continue;
            }

            if (!tables.TryGetValue(block.TableDefId, out var table))
            {
                rejected.Add(new ImportRejection(
                    "—", block.TableCode, "ECR-IMP-0422",
                    "Таблиці з файлу немає в чинній версії шаблону.",
                    block.TableCode,
                    MessageKey: ImportMessageKeys.TableMissing));

                continue;
            }

            validBlocks.Add((block, table));
        }

        var tableInstanceIds = validBlocks.ConvertAll(v => v.Block.TableInstanceId);

        // ⛔ Три пакетні запити на ВСЮ книгу замість трьох на КОЖНУ таблицю
        // (~90 у типовому шаблоні): GetRowIdsBatchAsync і GetRowVersionsBatchAsync
        // поруч, ReadSlicesAsync — той самий прийом, що вже закрив Q-165 для
        // ValidateDocumentHandler.
        var rowIdsBatch = await rowStore.GetRowIdsBatchAsync(tableInstanceIds, period, ct).ConfigureAwait(false);
        var versionsBatch = await rowStore.GetRowVersionsBatchAsync(tableInstanceIds, period, ct).ConfigureAwait(false);
        var slicesBatch = await cellStore.ReadSlicesAsync(tableInstanceIds, period, ct).ConfigureAwait(false);

        // ⛔ P1 (живий прохід 2026-10-01): експорт пише в колонку `Calculated`
        // ЧИСЛО методології (`CalculatedCellOverlay`, F-02), а не формулу, тож
        // пропуск «обчислювана з формулою» не спрацьовує, а в `doc.CellValue`
        // значення такої колонки немає — незмінена книга давала ECR-CELL-4221.
        // Порівнюємо з тим самим оверлеєм, що пише експорт: рівне — мовчки
        // пропуск, змінене — відхилення як і раніше.
        if (methodologies is not null && results is not null)
        {
            var included = tableInstanceIds.ToHashSet();
            slicesBatch = await new Ecr.Application.Calculations.CalculatedCellOverlay(methodologies, results)
                .ApplyAsync(
                    documentId,
                    map.PeriodKey,
                    snapshot,
                    [.. instances.Where(i => included.Contains(i.TableInstanceId))],
                    rowIdsBatch,
                    slicesBatch,
                    ct)
                .ConfigureAwait(false);
        }

        // ⚠ Рішення про доступ — ПАКЕТНО на зріз. Поштучна перевірка
        // тисяч комірок імпорту не вкладається в жоден бюджет і саме тому
        // спокушає її пропустити.
        // ⛔ P8 (перф-аудит): і не на зріз по черзі, а одним викликом на ВСЮ
        // книгу. Доти `CanEditSliceAsync` на кожну таблицю коштував ~5–7
        // звернень, тобто ~91 × 6 на типовий шаблон; тепер — стала кількість,
        // незалежна від числа таблиць (`ImportPreviewAccessQueryCountTests`).
        var decisionsBatch = await access
            .CanEditSlicesAsync(profile, tableInstanceIds, ct)
            .ConfigureAwait(false);

        foreach (var (block, table) in validBlocks)
        {
            var worksheet = workbook.Worksheet(block.SheetName);

            // ⛔ Немає рішень на екземпляр — відмова, а не порожній словник:
            // `ImportDiffBuilder` читає відсутнє рішення як «заборони немає».
            if (!decisionsBatch.TryGetValue(block.TableInstanceId, out var decisions))
            {
                throw new InvalidOperationException(
                    $"Служба доступу не повернула рішень для екземпляра таблиці {block.TableInstanceId}.");
            }

            var rowIds = rowIdsBatch.GetValueOrDefault(
                block.TableInstanceId, (IReadOnlyDictionary<string, long>)EmptyRowIds);
            var versions = versionsBatch.GetValueOrDefault(
                block.TableInstanceId, (IReadOnlyDictionary<string, string>)EmptyVersions);
            var current = slicesBatch.GetValueOrDefault(
                block.TableInstanceId, (IReadOnlyList<CellRecord>)EmptySlice);

            var diff = diffBuilder.Build(
                worksheet, block, map.PeriodKey, table, decisions, lookups, rowIds, versions, current,
                readable.CanReadColumn,
                Ecr.Application.Localization.NumberCulture.ForLanguage(currentUser.Language));

            diffs.Add(diff);
            changes.AddRange(diff.Changes);
            rejected.AddRange(diff.Rejected);
            overwritable.AddRange(diff.Overwritable ?? []);
        }

        // ⛔ `V-10`: значення поза рядками таблиць (порожній документ, рядок під
        // таблицею) — відмова з поясненням, а не мовчазний пропуск, після якого
        // діалог каже «файл збігається з аркушем».
        foreach (var sheet in validBlocks.GroupBy(v => v.Block.SheetName, StringComparer.OrdinalIgnoreCase))
        {
            rejected.AddRange(StrayValueDetector.Find(workbook.Worksheet(sheet.Key), [.. sheet]));
        }

        var token = Guid.NewGuid().ToString("N");

        await previews
            .SaveAsync(
                token,
                // ⛔ L6-02 / N1-04: версія шаблону, за якою побудовано перегляд, їде в план — під
                // замком структури `ApplyAsync` звіряє її з версією проєкту.
                JsonSerializer.Serialize(
                    new ImportPlan(documentId, map.PeriodKey, diffs, userId, instances[0].TemplateVersionId), Options),
                PreviewLifetime,
                ct)
            .ConfigureAwait(false);

        // Конфліктів на етапі перегляду ще немає: вони з'являються, якщо між
        // переглядом і застосуванням хтось правив ті самі рядки. Показувати їх
        // наперед означало б вигадати їх.
        return new ImportPreview(
            token, changes, rejected, [], overwritable, OutdatedWorkbook(map) ? [ImportMessageKeys.OutdatedWorkbook] : null);
    }

    /// <summary>
    /// Чи вивантажено книгу до того, як карта почала нести версії рядків (D1-02)
    /// і відбитки введених комірок (AN-118).
    /// </summary>
    /// <remarks>
    /// ⛔ AN-118 (R1-02). Без версії рядок, змінений після експорту, не стає
    /// конфліктом, а без відбитка незмінена комірка конфліктного рядка не
    /// відрізняється від правки: застаріле значення з такої книги повертає
    /// новіше чуже і, бувши записане імпортом, стає «людським» (`HumanOriginsSql`)
    /// — інтеграція його більше не виправить. Відмовляти таку книгу не можна:
    /// у ній бувають тижні офлайн-правок (D-41), і відмова змусила б людину
    /// вводити їх наново. Тож — попередження в перегляді: людина бачить перелік
    /// змін і може вивантажити книгу заново.
    /// ⚠ Блок без введених колонок відбитка не має й не потребує; рядок без
    /// версії в новій книзі не буває (експорт бере версії тим самим пакетом, що й рядки).
    /// </remarks>
    public static bool OutdatedWorkbook(ExcelWorkbookMap map)
    {
        ArgumentNullException.ThrowIfNull(map);

        return map.Tables.Any(block =>
            block.Columns.Any(c => !c.IsCalculated)
            && block.Rows.Any(row => row.Version is null || row.Cells is null));
    }

    /// <summary>Скільки комірок змінить diff без перезапису конфліктних рядків (AN-103).</summary>
    public Task<int> CountPendingChangesAsync(long documentId, string previewToken, CancellationToken ct)
        => CountPendingChangesAsync(documentId, previewToken, null, ct);

    /// <inheritdoc />
    public async Task<int> CountPendingChangesAsync(
        long documentId, string previewToken, IReadOnlyList<ImportOverwriteRow>? overwriteRows, CancellationToken ct)
    {
        var plan = await LoadPlanAsync(previewToken, documentId, ct).ConfigureAwait(false);

        return Effective(plan, overwriteRows).Plan.Tables.Sum(t => t.Changes.Count);
    }

    /// <summary>Застосовує diff без перезапису конфліктних рядків (AN-103).</summary>
    public Task<PatchCellsResponse> ApplyAsync(long documentId, string previewToken, CancellationToken ct)
        => ApplyAsync(documentId, previewToken, null, ct);

    /// <inheritdoc />
    /// <remarks>
    /// ⛔ `DAT-05` («усе або нічого»). До цієї правки цикл нижче кликав
    /// <c>patch.HandleAsync</c> на КОЖНУ таблицю окремо — кожну зі своєю
    /// транзакцією і своєю задачею перерахунку. Конфлікт на третій із десяти
    /// означав: дві вже застосовано й закомічено, клієнт отримує помилку БЕЗ
    /// переліку застосованого, документ лишається в стані, якого ніхто не
    /// замовляв. Головний аргумент не в транзакціях: користувач щойно бачив
    /// diff ЦІЛОЇ КНИГИ і натиснув «Apply» на нього — часткове застосування не
    /// відповідає жодному екрану системи (`D14-04`). Альтернатива «частковий
    /// результат із переліком» відхилена свідомо: вона чесніша за колишню
    /// поведінку, але вимагає нового екрана «застосовано 2 з 10» і лишає той
    /// самий незамовлений стан.
    ///
    /// ⚠ Одна транзакція на всю книгу працює без змін у сховищах:
    /// <c>NormalizedCellStore.ApplyAsync</c> приєднується до ambient-транзакції
    /// (<c>db.Database.CurrentTransaction</c>) замість відкриття власної,
    /// <c>RowStore</c>, <c>DocumentStore</c> і <c>AuditWriter</c> ідуть через
    /// ТОЙ САМИЙ <c>EcrDbContext</c> (один DI-скоуп), а вкладений
    /// <c>ExecuteInTransactionAsync</c> усередині <c>PatchCellsHandler</c>
    /// бачить відкриту транзакцію і просто виконує тіло, лишаючи коміт нам.
    ///
    /// ⚠ Ціна — довша транзакція; межу задає розмір книги (`S-16`/`S-17`), а
    /// під RCSI читачів вона не блокує.
    ///
    /// ⚠ Перерахунок ставиться ОДИН на документ. Момент — за планувальником
    /// (MI-02 (в), <see cref="IBackgroundJobScheduler.EnlistsInCallerTransaction"/>):
    /// черга в базі — ОСТАННІМ оператором транзакції книги, після всього запису
    /// (відкат книги відкочує й задачу); Quartz тримає чергу в пам'яті — ПІСЛЯ
    /// коміту, бо воркер стартує раніше за коміт і прочитав би або старі дані,
    /// або — після відкату — дані, яких не було ніколи.
    ///
    /// ⛔ Саме останнім (умова «Аудиту» 2): постановка на ціль бере
    /// UPDLOCK+HOLDLOCK на слот у <c>UX_JobProgress_Target_Queued</c> до кінця
    /// транзакції, і на початку чи посередині книги він тримався б увесь її
    /// запис (<c>ExcelImportTransactionalEnqueueTests</c> — тест порядку).
    ///
    /// ⛔ P8 (застосування 3/3): книга пише ОДНИМ книжковим шляхом
    /// (<see cref="PatchCellsHandler.HandleWorkbookAsync"/>), а не циклом
    /// поштучних PATCH — ~18 звернень до бази НА КОЖНУ таблицю (219 на книгу з
    /// 12 таблиць) стали сталими на книгу (<c>ExcelImportApplyWorkbookTests</c>).
    /// Правила ті самі — той самий обробник, ті самі методи правил.
    /// </remarks>
    public async Task<PatchCellsResponse> ApplyAsync(
        long documentId, string previewToken, IReadOnlyList<ImportOverwriteRow>? overwriteRows, CancellationToken ct)
    {
        var loaded = await LoadPlanAsync(previewToken, documentId, ct).ConfigureAwait(false);

        // ✎ AN-114 (D-338): план із доданими змінами рядків, які людина явно
        // позначила «перезаписати», — далі весь шлях працює саме з ним.
        var (plan, overwritten) = Effective(loaded, overwriteRows);

        var applied = 0;
        var versions = new Dictionary<string, string>(StringComparer.Ordinal);
        var validation = new List<ValidationMessageDto>();
        var seeds = new List<RecalculationSeed>();
        var rowWindowChanges = new List<RowWindowChange>();
        long seedTableInstanceId = 0;
        var diffs = plan.Tables.Where(t => t.Changes.Count > 0).ToList();

        var period = new PeriodKey(plan.PeriodKey);
        var (sheets, templateVersions) = await SheetsInLockOrderAsync(documentId, period, plan, ct).ConfigureAwait(false);

        var enlist = jobs.EnlistsInCallerTransaction;

        // ⚠ Одна задача на ВСЮ книгу, а не одна на таблицю. Каскад і так
        // документний: `RecalculationService.RunAsync` резолвить документ із
        // переданого екземпляра таблиці й далі читає ВСІ таблиці документа за
        // період — формула сусіднього аркуша має право читати цю. Тому
        // `TableInstanceId` тут — лише точка входу в документ, а насіння
        // (`seeds`) зібране з усіх таблиць книги.
        async Task EnqueueRecalculationAsync(CancellationToken token)
        {
            if (seeds.Count > 0)
            {
                // O1: злиття за документо-періодом (системна постановка — ціль без автора).
                await jobs
                    .EnqueueCoalescedAsync<IFormulaRecalculationJob>(
                        FormulaRecalculationTarget.Of(documentId, plan.PeriodKey, createdByUserId: null),
                        new { DocumentId = documentId, TableInstanceId = seedTableInstanceId, plan.PeriodKey, Cells = seeds },
                        token)
                    .ConfigureAwait(false);
            }
        }

        await uow.ExecuteInTransactionAsync(
            async innerCt =>
            {
                // ⛔ Першою дією транзакції — спільні блокування ВСІХ аркушів
                // книги в порядку ключа «документ × аркуш × період» (у межах
                // книги — `SheetDefId` за зростанням), той самий порядок, що в
                // `RecalculationService`. Доти їх брав кожен
                // `PatchCellsHandler` сам — у порядку ТАБЛИЦЬ КНИГИ: черга
                // блокувань FIFO, спільний запит стає за винятковим (подання),
                // що вже чекає, і імпорт (B, потім A) проти перерахунку
                // (A, потім B) з двома поданнями в черзі давав цикл. Повторне
                // спільне блокування того самого аркуша в обробнику — той самий
                // власник, воно видається одразу.
                //
                // ⚠ Стан аркуша тут не перевіряється: це робить
                // `PatchCellsHandler` своєю відмовою (`ECR-ACCS-0403`), тож двох
                // правд про «подано» не з'являється. Прочитаний тут під
                // блокуванням стан він отримує готовим — без другого звернення.
                // ⛔ L6-02: ще раніше — структура документа (порядок `doc-structure` →
                // `sheet-edit`). Аркуші вище прочитано за версією ДО транзакції;
                // перенос, що зафіксувався відтоді, — відмова, а не блокування
                // аркушів старої структури.
                var locked = await sheetGate.EnterStructureAsync(documentId, exclusive: false, innerCt)
                    .ConfigureAwait(false);
                foreach (var version in templateVersions)
                {
                    DocumentStructure.EnsureUnchanged(locked, version, documentId);
                }

                // ⛔ L6-02 / N1-04. `templateVersions` вище — з екземплярів, прочитаних ПРИ застосуванні, тож
                // після переносу між переглядом і застосуванням вони вже нової версії й збігаються
                // з `locked`: розбіжність приховувалась. Перегляд (відхилення, коди довідників, типи комірок)
                // побудовано за версією з плану — вона і є міркою. Плани до цієї правки версії не мають.
                if (plan.TemplateVersionId is { } previewedVersion)
                {
                    DocumentStructure.EnsureUnchanged(locked, previewedVersion, documentId);
                }

                // ⛔ R7-Y2-02 (X6-02): чекати (до 30 с) дозволено лише ПЕРШИЙ аркуш, поки імпорт ще не
                // тримає жодного. Наступні — без черги (`EnterEditNoWaitAsync`): черга `sp_getapplock` FIFO, і
                // імпорт, що чекав аркуш N (його саме подають), тримаючи S на аркушах 1…N-1, ставив за собою
                // подання тих аркушів, а за ними — автозбереження всіх, хто їх правив. Відмова
                // (`409 ECR-DOC-4091 sheetBeingSubmitted`) відкочує транзакцію й звільняє все взяте:
                // синхронний імпорт клієнт повторює як минущий, фоновий (`ExcelImportJob`) відкладає себе.
                var statuses = new Dictionary<int, Ecr.Domain.Enums.DocumentStatus>();
                var holdsAny = false;
                foreach (var sheetDefId in sheets)
                {
                    statuses[sheetDefId] = holdsAny
                        ? await sheetGate.EnterEditNoWaitAsync(documentId, sheetDefId, period, innerCt).ConfigureAwait(false)
                        : await sheetGate.EnterEditAsync(documentId, sheetDefId, period, innerCt).ConfigureAwait(false);
                    holdsAny = true;
                }

                // ⚠ Накопичувачі скидаються НА ПОЧАТКУ замикання, а не поруч із
                // оголошенням: <c>ExecuteInTransactionAsync</c> віддає тіло
                // стратегії повторів EF (<c>EnableRetryOnFailure</c>), яка має
                // право виконати його ще раз після транзієнтного збою. Без
                // скидання другий прохід додав би свої числа до чисел першого.
                applied = 0;
                versions.Clear();
                validation.Clear();
                seeds.Clear();
                rowWindowChanges.Clear();
                seedTableInstanceId = 0;

                // ⚠ Версії рядків беруться з ПЕРЕГЛЯДУ і передаються як
                // baseVersion. Саме це змушує звичайний шлях запису відхилити
                // всю книгу, якщо між переглядом і застосуванням хтось правив ті
                // самі рядки (ECR-CELL-0409) — тобто конфлікт ловить одна
                // перевірка, а не дві, які вміють розійтися.
                //
                // ⚠ Той самий обробник, що й batch-PATCH, із Origin = "Import":
                // окремий шлях запису означав би, що аудит, валідація і
                // перерахунок для імпорту працюють інакше.
                var requests = diffs
                    .Select(diff => new PatchCellsRequest(
                        diff.TableInstanceId,
                        diff.PeriodKey,
                        CellChangeOrigins.Import,
                        [.. diff.Changes
                            .GroupBy(c => c.RowKey, StringComparer.Ordinal)
                            .Select(g => new PatchRow(
                                g.Key,
                                BaseRowVersion(diff.RowVersions, g.Key),
                                [.. g.Select(c => new PatchCell(c.ColumnCode, c.NewValue))]))]))
                    .ToList();

                IReadOnlyList<PatchCellsResponse> responses;
                try
                {
                    responses = await patch
                        .HandleWorkbookAsync(requests, seeds, rowWindowChanges, innerCt, statuses, overwritten)
                        .ConfigureAwait(false);
                }
                catch (EcrException error) when (Blame(error, diffs) is { } named)
                {
                    throw named;
                }

                foreach (var response in responses)
                {
                    applied += response.AppliedCells;
                    validation.AddRange(response.Validation);

                    foreach (var (key, version) in response.RowVersions)
                    {
                        versions[key] = version;
                    }
                }

                seedTableInstanceId = diffs.Count > 0 ? diffs[0].TableInstanceId : 0;

                // ⛔ MI-02 (в): останній оператор транзакції — див. remarks.
                if (enlist)
                {
                    await EnqueueRecalculationAsync(innerCt).ConfigureAwait(false);
                }
            },
            ct)
            .ConfigureAwait(false);

        if (!enlist)
        {
            await EnqueueRecalculationAsync(ct).ConfigureAwait(false);
        }

        // ⛔ L6-11: підтягування вікон рядків — лише ПІСЛЯ коміту книги, як у
        // поштучного PATCH: задача, поставлена всередині транзакції, читала б
        // дані до коміту, а після відкату лишалась би без даних узагалі.
        await patch.NotifyRowWindowsAsync(rowWindowChanges, ct).ConfigureAwait(false);

        // Прибирається ЛИШЕ після успіху: якщо застосування впало на конфлікті,
        // користувач має змогу подивитися перегляд ще раз, а не будувати його
        // наново з файлу, якого може вже не бути під рукою.
        await previews.RemoveAsync(previewToken, ct).ConfigureAwait(false);

        return new PatchCellsResponse(applied, versions, validation);
    }

    /// <summary>Версія рядка з перегляду для <c>baseVersion</c> застосування (L6-08).</summary>
    /// <param name="versions">Версії рядків таблиці на момент перегляду.</param>
    /// <param name="rowKey">Рядок книги.</param>
    /// <returns>Версія з перегляду; рядка тоді не було — нульова версія, якої не буває в живого рядка.</returns>
    /// <remarks>
    /// ⛔ Що було: рядок без версії йшов із <c>baseVersion = null</c>, а це для
    /// <c>PatchCellsHandler</c> намір СТВОРИТИ рядок (R-B2). Рядки книги — ті, що
    /// існували на експорті; рядок, якого не стало до перегляду, імпорт мовчки
    /// відтворював би, а в таблиці зі стелею створення бере виняткове блокування
    /// аркуша поверх уже взятого спільного — прихований дедлок двох імпортів.
    /// Нульова версія — звичайний конфлікт <c>ECR-CELL-0409</c> («рядок змінився»).
    /// </remarks>
    public static string BaseRowVersion(IReadOnlyDictionary<string, string> versions, string rowKey)
    {
        ArgumentNullException.ThrowIfNull(versions);
        return versions.GetValueOrDefault(rowKey) ?? MissingRowVersion;
    }

    /// <summary>Нульовий <c>rowversion</c> у base64: живий рядок такої версії не має.</summary>
    public static readonly string MissingRowVersion = Convert.ToBase64String(new byte[8]);

    /// <summary>
    /// План, який справді застосовується: збережений перегляд плюс зміни рядків,
    /// що людина явно позначила «перезаписати» (AN-114, D-338), і ці рядки по
    /// екземплярах — для журналу.
    /// </summary>
    /// <param name="plan">Збережений план перегляду.</param>
    /// <param name="overwriteRows">Позначені рядки; <c>null</c>/порожньо — план без змін (AN-103).</param>
    /// <returns>План для застосування і перезаписані рядки (<c>TableInstanceId</c> → <c>RowKey</c>).</returns>
    /// <remarks>
    /// ⛔ Перезаписати можна ЛИШЕ рядок, який перегляд показав конфліктом «змінено
    /// після експорту» (<see cref="TableDiff.Overwritable"/>). Будь-який інший —
    /// відмова всього застосування (<c>ECR-IMP-0422</c>, <c>overwriteNotConflict</c>),
    /// а не мовчазний пропуск: клієнт, що просить перезаписати не те, побачив
    /// не той перегляд. Відмови прав, типу й меж у <c>Overwritable</c> не
    /// потрапляють узагалі (<see cref="ImportDiffBuilder"/>), тож прапорець їх не обходить.
    /// <para>
    /// ⚠ Версія рядка для <c>baseVersion</c> — та сама, з ПЕРЕГЛЯДУ: перезапис
    /// погоджено на чуже значення, яке людина бачила; правка, що прийшла вже після
    /// перегляду, і далі відхиляє книгу конфліктом <c>ECR-CELL-0409</c>.
    /// </para>
    /// </remarks>
    /// <exception cref="BusinessRuleException"><c>ECR-IMP-0422</c> — рядок не був конфліктом.</exception>
    public static (ImportPlan Plan, IReadOnlyDictionary<long, IReadOnlySet<string>>? Overwritten) Effective(
        ImportPlan plan, IReadOnlyList<ImportOverwriteRow>? overwriteRows)
    {
        ArgumentNullException.ThrowIfNull(plan);

        if (overwriteRows is null || overwriteRows.Count == 0)
        {
            return (plan, null);
        }

        var wanted = new HashSet<(string TableCode, string RowKey)>();
        foreach (var row in overwriteRows)
        {
            if (row?.TableCode is null || row.RowKey is null)
            {
                throw OverwriteNotConflict(row?.TableCode, row?.RowKey);
            }

            wanted.Add((row.TableCode, row.RowKey));
        }

        var matched = new HashSet<(string TableCode, string RowKey)>();
        var overwritten = new Dictionary<long, IReadOnlySet<string>>();
        var tables = new List<TableDiff>(plan.Tables.Count);

        foreach (var diff in plan.Tables)
        {
            var taken = (diff.Overwritable ?? [])
                .Where(c => c.TableCode is not null && wanted.Contains((c.TableCode, c.RowKey)))
                .ToList();

            if (taken.Count == 0)
            {
                tables.Add(diff);
                continue;
            }

            foreach (var change in taken)
            {
                matched.Add((change.TableCode!, change.RowKey));
            }

            overwritten[diff.TableInstanceId] = taken.Select(c => c.RowKey).ToHashSet(StringComparer.Ordinal);
            tables.Add(diff with { Changes = [.. diff.Changes, .. taken] });
        }

        if (wanted.FirstOrDefault(w => !matched.Contains(w)) is { TableCode: not null } stray)
        {
            throw OverwriteNotConflict(stray.TableCode, stray.RowKey);
        }

        return (plan with { Tables = tables }, overwritten);
    }

    /// <summary>Відмова: просять перезаписати рядок, який не був конфліктом «змінено після експорту».</summary>
    private static BusinessRuleException OverwriteNotConflict(string? tableCode, string? rowKey)
        => new(
            "ECR-IMP-0422",
            "Only rows that were changed after the workbook was exported can be overwritten.",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["messageKey"] = "err.ECR-IMP-0422.overwriteNotConflict",
                ["tableCode"] = tableCode ?? string.Empty,
                ["rowKey"] = rowKey ?? string.Empty,
            });

    /// <summary>
    /// Аркуші таблиць книги, у які застосування пише, — у порядку, у якому
    /// береться їхнє блокування (<c>SheetDefId</c> за зростанням), і версії шаблону,
    /// за якими їх визначено (звіряються під блокуванням структури, L6-02).
    /// </summary>
    /// <remarks>
    /// ⚠ Читається ДО транзакції: це структура (екземпляр → таблиця → аркуш),
    /// яка між переглядом і застосуванням не змінюється. Екземпляр, якого вже
    /// немає в документі, сюди не потрапляє — його відхилить сам
    /// <c>PatchCellsHandler</c> своєю відмовою.
    /// </remarks>
    private async Task<(IReadOnlyList<int> Sheets, IReadOnlyList<int> TemplateVersions)> SheetsInLockOrderAsync(
        long documentId, PeriodKey period, ImportPlan plan, CancellationToken ct)
    {
        var changed = plan.Tables.Where(t => t.Changes.Count > 0).Select(t => t.TableInstanceId).ToHashSet();
        if (changed.Count == 0)
        {
            return ([], []);
        }

        var instances = await rowStore.GetTableInstancesAsync(documentId, period, ct).ConfigureAwait(false);
        var sheets = new SortedSet<int>();
        var templateVersions = new List<int>();

        foreach (var group in instances.Where(i => changed.Contains(i.TableInstanceId)).GroupBy(i => i.TemplateVersionId))
        {
            templateVersions.Add(group.Key);
            var snapshot = await metadata.GetAsync(group.Key, ct).ConfigureAwait(false);
            var sheetOfTable = snapshot.Sheets
                .SelectMany(s => s.Tables)
                .ToDictionary(t => t.Id, t => t.SheetDefId);

            foreach (var instance in group)
            {
                if (sheetOfTable.TryGetValue(instance.TableDefId, out var sheetDefId))
                {
                    sheets.Add(sheetDefId);
                }
            }
        }

        return ([.. sheets], templateVersions);
    }

    /// <summary>
    /// Конфлікт версії таблиці, на якій книга спинилася, — лише з комірками, що
    /// змінилися після перегляду; <c>null</c> — відмову лишити як є.
    /// </summary>
    /// <remarks>
    /// ⚠ Номер таблиці (<c>tableInstanceId</c>) відмова вже несе — його додає
    /// книжковий шлях (<see cref="PatchCellsHandler.HandleWorkbookAsync"/>) тим
    /// самим ключем, що додавав тут колишній поштучний цикл. Лишилося те, чого
    /// обробник знати не може: що показав перегляд.
    ///
    /// ⛔ F-24: конфлікт версії — РЯДКОВИЙ, і обробник запису перелічує КОЖНУ
    /// комірку батчу в розбіжному рядку. Для імпорту це означало список
    /// комірок, яких ніхто, крім самого імпорту, не чіпав: їхнє чинне
    /// значення рівно те, що перегляд показав як «було». Лишаються ті, що
    /// справді змінилися після перегляду, — саме їх людині треба звірити.
    /// </remarks>
    private static ConcurrencyConflictException? Blame(EcrException error, IReadOnlyList<TableDiff> diffs)
    {
        if (error is not ConcurrencyConflictException
            || error.Details is not { } source
            || source.GetValueOrDefault("tableInstanceId") is not string instance
            || source.GetValueOrDefault("conflicts") is not IEnumerable<CellConflictDto> conflicts
            || diffs.FirstOrDefault(d => string.Equals(
                   d.TableInstanceId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                   instance,
                   StringComparison.Ordinal)) is not { } diff)
        {
            return null;
        }

        var details = new Dictionary<string, object?>(source, StringComparer.Ordinal)
        {
            ["conflicts"] = ChangedSincePreview(conflicts, diff),
        };

        return new ConcurrencyConflictException(error.ErrorCode, error.Message, details);
    }

    /// <summary>
    /// Розбіжності, у яких чинне значення комірки вже НЕ те, що перегляд
    /// показав як «було» (F-24).
    /// </summary>
    /// <remarks>
    /// ⚠ Комірка без пари в плані лишається: невідомо, що бачив перегляд, —
    /// а прибрати справжню розбіжність гірше, ніж показати зайву.
    /// </remarks>
    private static List<CellConflictDto> ChangedSincePreview(
        IEnumerable<CellConflictDto> conflicts, TableDiff diff)
    {
        var previewed = new Dictionary<(string RowKey, string ColumnCode), object?>();

        foreach (var change in diff.Changes)
        {
            previewed[(change.RowKey, change.ColumnCode)] = change.OldValue;
        }

        return
        [
            .. conflicts.Where(conflict =>
                !previewed.TryGetValue((conflict.RowKey, conflict.ColumnCode), out var old)
                || !SameScalar(old, conflict.TheirValue)),
        ];
    }

    /// <summary>
    /// Чи те саме значення: з плану (після JSON — <see cref="JsonElement"/>) і
    /// з бази (скаляр CLR).
    /// </summary>
    private static bool SameScalar(object? previewed, object? current)
    {
        var left = CellValueReader.Normalize(previewed);
        var right = CellValueReader.Normalize(current);

        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        if (TryNumber(left, out var a) && TryNumber(right, out var b))
        {
            return a == b;
        }

        if (right is DateTime date && left is string text
            && DateTime.TryParse(
                text, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
        {
            return parsed == date;
        }

        return string.Equals(
            Convert.ToString(left, System.Globalization.CultureInfo.InvariantCulture),
            Convert.ToString(right, System.Globalization.CultureInfo.InvariantCulture),
            StringComparison.Ordinal);
    }

    private static bool TryNumber(object value, out decimal number)
    {
        switch (value)
        {
            case decimal d:
                number = d;
                return true;
            case int i:
                number = i;
                return true;
            case long l:
                number = l;
                return true;
            default:
                number = 0;
                return false;
        }
    }

    /// <summary>
    /// Читає й розбирає раніше збережений <see cref="ImportPlan"/>.
    /// </summary>
    /// <param name="previewToken">Токен перегляду.</param>
    /// <param name="documentId">
    /// Документ застосування; <c>null</c> — виклик лише РАХУЄ зміни
    /// (<c>CountPendingChangesAsync</c>) і документ ще невідомий обробнику.
    /// </param>
    /// <param name="ct">Скасування.</param>
    /// <remarks>
    /// ⚠ Спільна для <c>CountPendingChangesAsync</c> і
    /// <c>ApplyAsync</c> (директива №11, T10 #45): порогове рішення
    /// «синхронно чи в чергу» рахує зміни ТИМ САМИМ читанням, яким їх потім
    /// застосовують, — другий незалежний розбір <c>previewToken</c> міг би
    /// одного дня порахувати інакше, ніж застосує.
    /// </remarks>
    private async Task<ImportPlan> LoadPlanAsync(string previewToken, long? documentId, CancellationToken ct)
    {
        // ⛔ Порожній токен — той самий «перегляду немає», а не `ArgumentException`:
        // він приходить із тіла запиту (`{"previewToken": ""}`) і давав 500.
        var stored = (string.IsNullOrWhiteSpace(previewToken)
                         ? null
                         : await previews.FindAsync(previewToken, ct).ConfigureAwait(false))
                     ?? throw new BusinessRuleException(
                         "ECR-IMP-0422",
                         "Перегляд імпорту не знайдено або його строк вийшов: побудуйте його заново.",

                         // ⛔ F-24: без ключа деталь приїжджала українським
                         // реченням під англійським заголовком.
                         new Dictionary<string, object?> { ["messageKey"] = "err.ECR-IMP-0422.previewExpired" });

        var plan = JsonSerializer.Deserialize<ImportPlan>(stored, Options)
                   ?? throw new BusinessRuleException(
                       "ECR-IMP-0422", "Збережений перегляд імпорту не читається.",
                       new Dictionary<string, object?> { ["messageKey"] = "err.ECR-IMP-0422.previewUnreadable" });

        // ⛔ L1-20: токен перегляду належить користувачеві, що його збудував; чужий токен — той самий «перегляду
        // немає» (не розкриваємо, що він існує). Плани без власника (до цієї правки) лишаються чинними до спливу строку.
        if (plan.UserId is { } owner && owner != currentUser.UserId)
        {
            throw new BusinessRuleException(
                "ECR-IMP-0422",
                "Перегляд імпорту не знайдено або його строк вийшов: побудуйте його заново.",
                new Dictionary<string, object?> { ["messageKey"] = "err.ECR-IMP-0422.previewExpired" });
        }

        if (documentId is not null && plan.DocumentId != documentId)
        {
            throw new BusinessRuleException(
                "ECR-IMP-0422",
                $"Перегляд належить документу {plan.DocumentId}, а застосування йде в {documentId}.",
                new Dictionary<string, object?> { ["messageKey"] = "err.ECR-IMP-0422.previewOtherDocument" });
        }

        return plan;
    }

    /// <summary>Відкриває книгу або каже, що це не книга.</summary>
    /// <remarks>
    /// ⛔ S10: спершу — <see cref="XlsxSafetyGate"/> (межі розпакованого
    /// розміру, стиснення й кількості записів) ПОЗА <c>try</c> нижче: його
    /// відмова вже має код і ключ, а ClosedXML не бачить непридатного пакета
    /// взагалі.
    /// </remarks>
    private static XLWorkbook Open(Stream file)
    {
        XlsxSafetyGate.EnsureSafe(file);

        try
        {
            return new XLWorkbook(file);
        }
        // ⛔ F-07: файл, що не є книгою, ClosedXML відхиляє чим завгодно, крім
        // того, що тут перелічували: текст і PDF — `OpenXmlPackageException`,
        // zip без частини книги — навіть `NullReferenceException` зсередини
        // `XLWorkbook.LoadSpreadsheetDocument`. Кожне з них ставало 500. Тому
        // фільтр — не за типом, а за місцем: конструктор лише РОЗБИРАЄ файл, і
        // будь-яка його відмова означає «це не книга» (скасування — не
        // відмова розбору, воно йде далі як є).
#pragma warning disable CA1031 // Причина — у ⛔ вище: перелік типів відмов стороннього розбору не закривається.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            throw new BusinessRuleException(
                "ECR-IMP-0422", "Файл не читається як книга .xlsx.",
                new Dictionary<string, object?> { ["messageKey"] = "err.ECR-IMP-0422.notAWorkbook" });
        }
    }

    /// <summary>Карта книги з прихованого аркуша.</summary>
    /// <remarks>
    /// ⛔ Книга без карти не імпортується. Зіставляти аркуші й колонки за
    /// позиціями означало б, що вставлена користувачем колонка зсуває всі
    /// дані на одну вправо — і diff покаже це як зміну кожного значення,
    /// цілком правдоподібну на вигляд.
    /// </remarks>
    private static ExcelWorkbookMap ReadMap(XLWorkbook workbook)
    {
        if (!workbook.Worksheets.TryGetWorksheet(ExcelWorkbookMap.SheetName, out var sheet))
        {
            throw new BusinessRuleException(
                "ECR-IMP-0422",
                "У книзі немає службового аркуша з картою: імпортувати можна лише файл, "
                + "вивантажений цією системою.",
                new Dictionary<string, object?> { ["messageKey"] = "err.ECR-IMP-0422.noMapSheet" });
        }

        // ⚠ Карта складається з усіх непорожніх комірок стовпця A: експортер
        // ріже її на шматки по 30 000 символів, бо в одну комірку більше за
        // 32 767 Excel не приймає (`A7-29`). Книги, збережені до цього,
        // читаються тим самим кодом: у них шматок рівно один.
        var json = string.Concat(
            sheet.Column(1)
                .CellsUsed()
                .OrderBy(c => c.Address.RowNumber)
                .Select(c => c.GetString()));

        ExcelWorkbookMap? map;

        try
        {
            map = string.IsNullOrWhiteSpace(json)
                ? null
                : JsonSerializer.Deserialize<ExcelWorkbookMap>(json, Options);
        }
        catch (JsonException)
        {
            // ⚠ Зіпсована вручну карта — та сама відмова, що й порожня, а не 500.
            map = null;
        }

        return map
               ?? throw new BusinessRuleException(
                   "ECR-IMP-0422", "Карта книги порожня або пошкоджена.",
                   new Dictionary<string, object?> { ["messageKey"] = "err.ECR-IMP-0422.mapBroken" });
    }

    /// <summary>Коди записів довідників: <c>RegistryDefId</c> → код → <c>Id</c>.</summary>
    private async Task<IReadOnlyDictionary<int, IReadOnlyDictionary<string, long>>> LookupsAsync(
        TemplateVersionSnapshot snapshot, CancellationToken ct)
    {
        var registryIds = snapshot.ColumnsById.Values
            .Where(c => !c.IsDeleted && c.LookupRegistryDefId is not null)
            .Select(c => c.LookupRegistryDefId!.Value)
            .Distinct()
            .ToList();

        var result = new Dictionary<int, IReadOnlyDictionary<string, long>>();

        foreach (var registryId in registryIds)
        {
            var entries = await registries.ListEntriesAsync(registryId, ct).ConfigureAwait(false);

            result[registryId] = entries
                .GroupBy(e => e.Code, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.OrdinalIgnoreCase);
        }

        return result;
    }
}

/// <summary>Збережений між переглядом і застосуванням план імпорту.</summary>
/// <param name="DocumentId">Документ.</param>
/// <param name="PeriodKey">Період.</param>
/// <param name="Tables">Diff-и таблиць.</param>
/// <param name="UserId">Користувач, що збудував перегляд (L1-20); <c>null</c> — план до прив'язки.</param>
/// <param name="TemplateVersionId">
/// Версія шаблону, за якою збудовано перегляд (L6-02 / N1-04); <c>null</c> — план до цієї правки,
/// звірка версії тоді лише за екземплярами, прочитаними при застосуванні.
/// </param>
public sealed record ImportPlan(
    long DocumentId, int PeriodKey, IReadOnlyList<TableDiff> Tables, int? UserId = null, int? TemplateVersionId = null);
