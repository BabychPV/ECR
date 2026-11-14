using Ecr.Domain.Abstractions;
using Ecr.Application.Ports;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Recalculation;

/// <summary>
/// Інкрементний перерахунок формул шаблону.
/// </summary>
/// <remarks>
/// Результати **формул шаблону** матеріалізуються в <c>doc.CellValue</c> з
/// <c>IsCalculated = 1</c>. Результати **методологій** сюди не потрапляють —
/// вони живуть у <c>calc.CalculationResult</c> (D-69). Плутати ці два шляхи
/// не можна.
/// </remarks>
public sealed class RecalculationService(
    ICellStore cellStore,
    IRowStore rowStore,
    IPeriodStore periods,
    IMetadataCache metadata,
    ITemplateVersionStore versions,
    IFormulaEngine formulaEngine,
    IUnitCatalog unitCatalog,
    IRegistryStore registryStore,
    IDocumentHeaderStore headers,
    IAuditWriter audit,
    IClock clock,
    IUnitOfWork uow,

    // ⛔ Спільне блокування кожного аркуша, у який пише прогін, першою дією
    // транзакції запису, і стан аркуша, прочитаний ПІД ним
    // (`SubmitRecalculationRaceTests`) — той самий механізм, що в
    // `PatchCellsHandler`. Без нього перерахунок, що перетинався з поданням,
    // переписував обчислені числа вже поданого аркуша повз зріз подання.
    ISheetEditGate sheetGate,

    // D-230: хук зв'язків Rollup. Необов'язковий: прямі `new RecalculationService(...)` у тестах його не
    // передають, DI підставляє `RelationRecalculator`.
    Calculations.IRelationRecalculator? relationRecalculator = null,

    // ФВ-0.2: куди писати попередження про діапазон, розкритий за поточним порядком попри збережений
    // граф (`SliceEvaluationContext`). Необов'язковий: без нього лишається лічильник
    // `ecr.recalc.range_fallback`; DI підставляє `ILogger<RecalculationService>`.
    Microsoft.Extensions.Logging.ILogger<RecalculationService>? logger = null,

    // LP-40: знімок довідників для форм REG* у формулах шаблону (REGFIND/REGONE/агрегати). Необов'язковий:
    // прямі `new RecalculationService(...)` у тестах його не передають (REG* тоді #REF, як і раніше);
    // DI підставляє `RegistrySnapshotLoader`.
    IRegistrySnapshotLoader? registrySnapshots = null,

    // Q-17: власність введених комірок таблиць «місяці в колонках». Необов'язковий: прямі `new RecalculationService(...)`
    // у тестах його не передають; DI підставляє роутер, а вимкнений прапор (за замовчуванням) означає «не застосовується».
    IMonthColumnRouter? monthRouter = null,

    // ✎ D-461: позначки «ручне значення замість формули». Необов'язковий: прямі `new RecalculationService(...)` у тестах
    // його не передають (ручних значень тоді немає); DI підставляє сховище.
    IFormulaOverrideStore? formulaOverrides = null,

    // RPT-STABLE Part B: збережена мапа перейменування колонок (псевдонім на межі версій). Необов'язковий: без нього
    // колонка зі зміненим кодом на межі версій не знаходиться (NULL), як і колонка, якої немає.
    IColumnRenameMap? columnRenames = null,

    // SHEETNAME(): назви груп і копії аркушів документа — читаються лише коли формула версії згадує SHEETNAME
    // (`SheetDisplayNames.MayUse`). Необов'язковий: без нього SHEETNAME() дає назву аркуша шаблону.
    IDocumentSheetCopyStore? sheetCopies = null,

    // ✎ C-1 (D-475): стан помилок формул. Необов'язковий: прямі `new RecalculationService(...)` у тестах його не
    // передають (тоді стан не читається й не пишеться, але залежні від помилки однаково не перераховуються).
    IFormulaErrorStore? formulaErrors = null) : ISubmitRecalculation
{
    /// <summary>
    /// LP-40: джерела знімків довідників цього екземпляра сервісу (скоуп = один прогін задачі чи запит):
    /// читання БД — раз на набір довідників, знімок на дату — у пам'яті раз на (набір, бізнес-дата).
    /// </summary>
    private readonly TemplateRegistrySnapshots _templateSnapshots = new();

    /// <summary>✎ C-5: дефолти колонок версії як значення незаповнених комірок (02b §6.3).</summary>
    private readonly ColumnDefaultValues _columnDefaults = new(metadata, registryStore);

    /// <summary>Автор обчислених значень: їх ставить система, а не людина.</summary>
    /// <remarks>
    /// ⚠ Нуль тут не «невідомо хто», а «не людина». Підставити сюди того, хто
    /// правив комірку, означало б записати в аудит, що він власноруч ввів
    /// число, якого не вводив.
    /// </remarks>
    private const int SystemUserId = 0;

    /// <summary>Скільки чекати перед крос-аркушним rollup.</summary>
    /// <remarks>
    /// ⚠ Rollup через аркуш НЕ рахується синхронно: зміна однієї комірки
    /// тягне ланцюг по всьому документу, і користувач чекав би на кожному
    /// натисканні Tab. Відкладення на ~300 мс склеює серію правок в один
    /// перерахунок — саме так, як їх і робить людина.
    /// </remarks>
    public TimeSpan RollupDebounce { get; init; } = TimeSpan.FromMilliseconds(300);

    /// <summary>Формули, які треба перерахувати відкладено.</summary>
    /// <remarks>Наповнюється під час прогону; спорожняється планувальником.</remarks>
    public IReadOnlyCollection<int> DeferredRollups => _deferred;

    private readonly HashSet<int> _deferred = [];

    /// <summary>
    /// Забирає відкладені rollup-формули **у порядку обчислення** і спорожняє
    /// набір.
    /// </summary>
    /// <param name="plan">План перерахунку, зафіксований при публікації.</param>
    /// <remarks>
    /// ⛔ Сортування тут — не косметика (аудит 2026-09-16, §1.2). До цього
    /// відкладені rollup брались прямо з <c>HashSet&lt;int&gt;</c> і
    /// конкатенувались до вже відсортованого <c>affected</c> БЕЗ сортування.
    /// <c>Evaluate()</c> пише результат кожної формули у СПІЛЬНИЙ словник
    /// <c>values</c>, який читають наступні формули, тож порядок задає, які
    /// числа вони прочитають. Дві взаємозалежні крос-аркушні rollup-формули в
    /// одному проході (аркуш підсумків, що згортає два проміжні rollup-аркуші):
    /// якщо порядок перебору hash-set поставить «нижню» першою, вона прочитає
    /// ЗАСТАРІЛЕ значення «верхньої» — неправильне число, яке саме не
    /// виправиться до наступної незв'язаної правки.
    /// <para>
    /// Порядок береться з ПУБЛІКАЦІЇ (<c>plan.EvaluationOrder</c>), той самий,
    /// що й у <see cref="Plan"/> (крок 4): будувати топологічний порядок на
    /// кожен запит бюджет не передбачає (ФВ-9.4).
    /// </para>
    /// </remarks>
    public IReadOnlyList<int> TakeDeferredRollups(RecalculationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var rollups = _deferred.ToList();
        rollups.Sort((a, b) => plan.EvaluationOrder(a).CompareTo(plan.EvaluationOrder(b)));
        _deferred.Clear();

        return rollups;
    }

    /// <summary>Визначає, ЩО і в якому порядку перераховувати.</summary>
    /// <param name="dirty">Змінені комірки.</param>
    /// <param name="plan">План перерахунку, зафіксований при публікації.</param>
    /// <returns>Формули в порядку обчислення — ті, що рахуються зараз.</returns>
    public IReadOnlyList<int> Plan(DirtySet dirty, RecalculationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(dirty);
        ArgumentNullException.ThrowIfNull(plan);

        if (dirty.IsEmpty)
        {
            return [];
        }

        // 1. Зворотний індекс: які формули залежать від змінених комірок.
        var affected = new HashSet<int>();
        foreach (var seed in dirty.Seeds)
        {
            foreach (var formulaId in plan.DependentsOf(seed))
            {
                affected.Add(formulaId);
            }
        }

        // 2. Транзитивне розкриття, поки набір не перестане рости: формула,
        //    яка читає результат іншої формули, теж стає брудною.
        var growing = true;
        while (growing)
        {
            growing = false;
            foreach (var formulaId in affected.ToList())
            {
                foreach (var dependent in plan.DependentsOfFormula(formulaId))
                {
                    if (affected.Add(dependent))
                    {
                        growing = true;
                    }
                }
            }
        }

        // 3. Крос-аркушні rollup відкладаються, решта рахується зараз.
        //    Формули IsSnapshot не перераховуються каскадом НІКОЛИ: знімок на
        //    те й знімок, що зафіксував стан на момент подання.
        var now = new List<int>();
        foreach (var formulaId in affected)
        {
            if (plan.IsSnapshot(formulaId))
            {
                continue;
            }

            if (plan.IsCrossSheet(formulaId))
            {
                _deferred.Add(formulaId);
                continue;
            }

            now.Add(formulaId);
        }

        // 4. Порядок БЕРЕТЬСЯ з публікації, а не будується щоразу: сортувати
        //    граф на кожен запит — витрата, якої бюджет не передбачає (ФВ-9.4).
        now.Sort((a, b) => plan.EvaluationOrder(a).CompareTo(plan.EvaluationOrder(b)));
        return now;
    }

    /// <summary>L2: нове сховище спільних читань для одного проходу перерахунку документа.</summary>
    public RecalculationSharedLoad NewSharedLoad() => new(rowStore, versions, cellStore as IOwnerReadSession);

    /// <summary>
    /// Перераховує залежне піддерево і записує результати.
    /// </summary>
    /// <param name="tableInstanceId">Екземпляр таблиці, у якому сталася правка.</param>
    /// <param name="dirty">Змінені комірки.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <param name="changedColumns">
    /// WW-03: куди скласти колонки (таблиця, колонка), змінені цим прогоном — насіння й похідні, —
    /// щоб поставити перерахунок у періоди-читачі; <c>null</c> — не збирати.
    /// </param>
    /// <returns>Скільки комірок перераховано.</returns>
    /// <remarks>
    /// ⛔ Метод був заглушкою — <c>return Task.CompletedTask</c>. Це і є
    /// <c>A7-63</c> у своїй найтихішій формі: правка комірки не міняла
    /// жодного похідного числа, і жодна помилка про це не повідомляла.
    ///
    /// ⚠ Формула, результат якої НЕ можна обчислити чесно, не рахується
    /// зовсім: краще старе число з відомою причиною, ніж нове й неправильне.
    /// Таких випадків два, і обидва названі: знімок (<c>IsSnapshot</c>) і
    /// крос-аркушний rollup (<c>IsCrossSheet</c>, відкладається, а не
    /// пропускається). Предикат динамічного діапазону був третім до директиви
    /// №11 (T12, `#26`) — <see cref="SliceEvaluationContext.Read"/> тепер уміє
    /// його обчислити, і виключення нижче зняте.
    /// </remarks>
    /// <param name="approverEditSheets">
    /// ✎ D-442: подані аркуші, у яких насіння прогону — правка погоджувача поточного кроку (перевірена під
    /// блокуванням у <c>PatchCellsHandler</c>). Лише ці аркуші прогін пише попри <c>Submitted</c> (див.
    /// <see cref="EnterSheetsAsync"/>); <c>null</c>/порожньо — подані пропускаються всі, як і доти.
    /// </param>
    /// <param name="inputColumns">
    /// Q-17: куди скласти колонки НАСІННЯ (введені комірки, не похідні) — з них постановник визначає, чи є серед них
    /// стовпці з чужим власником, чиї читачі в інших екземплярах року треба перерахувати; <c>null</c> — не збирати.
    /// </param>
    /// <param name="shared">L2: спільні читання проходу (екземпляри, залежності); <c>null</c> — читати напряму.</param>
    public async Task<int> RecalculateAsync(
        long tableInstanceId, DirtySet dirty, CancellationToken ct,
        HashSet<(int TableDefId, int ColumnDefId)>? changedColumns = null,
        IReadOnlySet<int>? approverEditSheets = null,
        HashSet<(int TableDefId, int ColumnDefId)>? inputColumns = null,
        RecalculationSharedLoad? shared = null)
    {
        ArgumentNullException.ThrowIfNull(dirty);

        // ⛔ Порожнє насіння — це «нема чого рахувати», а НЕ «перерахувати
        // все». На шляху правки комірки зворотне прочитання перетворило б
        // кожне натискання Tab на прогін по всьому документу. Повний
        // перерахунок має власний вхід — <see cref="RecalculateAllAsync"/>.
        if (dirty.IsEmpty)
        {
            return 0;
        }

        var instance = await rowStore.ResolveTableInstanceAsync(tableInstanceId, ct).ConfigureAwait(false);

        // RS: правка комірки копії — «вид» цієї копії (instance.SheetCopyId), правка оригіналу — оригінал.
        return await RunAsync(
                instance, dirty, ct, changedColumns: changedColumns, approverEditSheets: approverEditSheets,
                inputColumns: inputColumns, shared: shared)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// WW-03: перераховує формули періоду, які читають ІНШИЙ період документа
    /// (<c>[Period:-N]</c>, <c>[Period:#k]</c>), після того як там змінилися наведені колонки.
    /// </summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="periodKey">Період-читач, який перераховується.</param>
    /// <param name="sources">Що змінилося: період-джерело (номер у році), таблиця, колонка.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <param name="changedColumns">Куди скласти колонки, змінені цим прогоном (для наступного fan-out).</param>
    /// <param name="shared">L2: спільні читання проходу; <c>null</c> — читати напряму.</param>
    /// <param name="annualPeriods">
    /// ✎ LAND-YEAR B58: період-читач — ЗАКРИТИЙ якір річної розкладки у відкритому році (вирішує
    /// <c>CrossPeriodFanOut.ClosedAnchorYearAsync</c>): прогін пише лише не-місячні цілі під агрегатом року, як прогін B24
    /// (<see cref="RecalculateAllAsync"/>). Значення — ключі місяців року; <c>null</c> — звичайний читач.
    /// </param>
    /// <returns>Скільки комірок перераховано.</returns>
    /// <remarks>
    /// ⛔ Це G2 із дизайну WW-03: залежність із ненульовим зсувом не входить у зворотний індекс
    /// плану (правка періоду k не «брудна» для формул періоду m у ТІЄЇ Ж таблиці комірок). Міст —
    /// цей вхід: цілі = формули з міжперіодним ребром на змінену колонку плюс їхні залежні в
    /// межах періоду. Закриті періоди тут НЕ перевіряються — це робить постановник (fan-out) і
    /// задача перед стартом; поданий/затверджений аркуш не пишеться так само, як у всіх входах
    /// (<see cref="EnterSheetsAsync"/>).
    /// </remarks>
    public async Task<int> RecalculateFromOtherPeriodsAsync(
        long documentId, PeriodKey periodKey, IReadOnlyCollection<CrossPeriodSource> sources, CancellationToken ct,
        HashSet<(int TableDefId, int ColumnDefId)>? changedColumns = null,
        RecalculationSharedLoad? shared = null,
        IReadOnlyList<int>? annualPeriods = null)
    {
        ArgumentNullException.ThrowIfNull(sources);

        if (sources.Count == 0)
        {
            return 0;
        }

        var instances = shared is null
            ? await rowStore.GetTableInstancesAsync(documentId, periodKey, ct).ConfigureAwait(false)
            : await shared.InstancesAsync(documentId, periodKey, ct).ConfigureAwait(false);

        var written = 0;
        foreach (var (representative, _) in ScopeRepresentatives(instances, ScopeFilter.All))
        {
            written += await RunAsync(
                    representative, dirty: null, ct, sheetDefId: null, heldSheet: null, instances,
                    changedColumns, sources, shared: shared, annualPeriods: annualPeriods)
                .ConfigureAwait(false);
        }

        return written;
    }

    /// <summary>
    /// RS: по одному представнику на (версія шаблону × «вид»): оригінал і кожна копія документа рахуються окремо,
    /// бо кожна має власні екземпляри таблиць свого аркуша.
    /// </summary>
    private static IEnumerable<(TableInstanceRef Representative, long? CopyId)> ScopeRepresentatives(
        IReadOnlyList<TableInstanceRef> instances, ScopeFilter filter)
    {
        foreach (var group in instances.GroupBy(i => i.TemplateVersionId).OrderBy(g => g.Key))
        {
            foreach (var byCopy in group.GroupBy(i => i.SheetCopyId).OrderBy(g => g.Key ?? 0))
            {
                if (filter.Includes(byCopy.Key))
                {
                    yield return (byCopy.First(), byCopy.Key);
                }
            }
        }
    }

    /// <summary>
    /// Перераховує ВСІ формули шаблону документа за період, а не лише
    /// залежні від щойно змінених комірок.
    /// </summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <param name="sheetDefId">
    /// Аркуш; <c>null</c> — увесь документ. Звужує лише ЦІЛІ запису (формули,
    /// чия таблиця належить цьому аркушу) — читання лишається на весь документ,
    /// бо формула аркуша має право читати сусідній (Q-331, `RecalculationJob`).
    /// </param>
    /// <param name="changedColumns">WW-03: куди скласти змінені колонки; <c>null</c> — не збирати.</param>
    /// <param name="writeSubmitted">✎ UAT-recalc-ux: писати й ПОДАНІ аркуші (ручний перерахунок, <c>RecalculationJob.WritesSubmitted</c>); затверджені — ніколи.</param>
    /// <returns>Скільки комірок перераховано.</returns>
    /// <remarks>
    /// ⛔ Окремий метод, а НЕ прапорець на <see cref="RecalculateAsync"/>.
    /// Прапорець довелося б поєднати з порожнім <see cref="DirtySet"/>, тобто
    /// зробити «порожньо» значущим значенням — рівно та двозначність
    /// «немає / порожньо», яку в цьому дереві вже виправляли одного разу
    /// (<c>D2-332</c>, `SaveMethodologyFormulaRequest.ArgumentsCsv`). Тут вона
    /// коштувала б дорожче: помилка означала б повний перерахунок документа
    /// на кожну правку комірки.
    ///
    /// ⛔ Існує, бо формула шаблону перераховується ЛИШЕ від запису в комірку
    /// (<c>PatchCellsHandler</c>). Формула, додана або змінена в шаблоні ПІСЛЯ
    /// того, як дані вже введені, не перераховувалася б ніколи — і методологія
    /// порахувала б свій результат зі застарілих входів
    /// (<c>CalculationInputBuilder</c> читає той самий <c>doc.CellValue</c>),
    /// видавши новий прогін із новою контрольною сумою від старих чисел.
    ///
    /// ⚠ Знімки (<c>IsSnapshot</c>) не перераховуються і тут: знімок на те й
    /// знімок, що зафіксував стан на момент подання. Крос-аркушні rollup,
    /// навпаки, рахуються ОДРАЗУ — цей прогін уже фоновий, і відкласти
    /// означало б не порахувати ніколи.
    /// </remarks>
    /// <param name="annualPeriods">
    /// ✎ LAND-YEAR B24 (D-NEW-3): прогін ЯКОРЯ року річної розкладки, чий місяць закритий, а рік — ні
    /// (<see cref="Security.AnnualPeriodRule.AnchorWritesAnnualTargets"/>; вирішує <c>RecalculationJob</c>). Значення — ключі
    /// всіх місяців календаря року: прогін пише лише НЕ-МІСЯЧНІ колонки маршрутизованих таблиць (власник — якір), а стан
    /// аркуша для запису — агрегат за цими місяцями (<see cref="Security.AnnualPeriodRule.AggregateStatus"/>). Місячні
    /// цілі (<c>C_M01</c>), таблиці поза маршрутизацією, копії аркушів і Rollup/Check закритого місяця не пишуться.
    /// <c>null</c> — звичайний прогін періоду.
    /// </param>
    public Task<int> RecalculateAllAsync(
        long documentId, PeriodKey periodKey, CancellationToken ct, int? sheetDefId = null,
        HashSet<(int TableDefId, int ColumnDefId)>? changedColumns = null, bool writeSubmitted = false,
        IReadOnlyList<int>? annualPeriods = null)
        => RecalculateDocumentAsync(
            documentId, periodKey, sheetDefId, heldSheet: null, ScopeFilter.All, ct, changedColumns, writeSubmitted,
            annualPeriods: annualPeriods);

    /// <inheritdoc />
    /// <remarks>
    /// ⚠ Той самий повний прогін, звужений до аркуша (<c>Q-331</c>), з однією
    /// різницею: спільного блокування ЦЬОГО аркуша прогін не бере — викликач
    /// (<c>SubmitSheetHandler</c>) уже тримає виняткове в тій самій транзакції
    /// (<see cref="EnterSheetsAsync"/>). Стан аркуша теж не перевіряється:
    /// подання саме переводить аркуш у <c>Submitted</c> і відмовить, якщо стан
    /// не той (<c>ApprovalState.Submit</c>), — тоді відкотиться й цей запис.
    /// </remarks>
    public Task<int> RecalculateSheetUnderSubmitLockAsync(
        long documentId, int sheetDefId, PeriodKey periodKey, CancellationToken ct, long? sheetCopyId = null)
        => RecalculateDocumentAsync(
            documentId, periodKey, sheetDefId, heldSheet: (sheetDefId, sheetCopyId), ScopeFilter.Of(sheetCopyId), ct);

    /// <inheritdoc />
    public async Task<IReadOnlyList<FormulaErrorCell>> RecalculateSheetForSubmitAsync(
        long documentId, int sheetDefId, PeriodKey periodKey, CancellationToken ct, long? sheetCopyId = null)
    {
        var errors = new List<FormulaErrorCell>();
        await RecalculateDocumentAsync(
                documentId, periodKey, sheetDefId, heldSheet: (sheetDefId, sheetCopyId), ScopeFilter.Of(sheetCopyId), ct,
                errorSink: errors)
            .ConfigureAwait(false);
        return errors;
    }

    /// <summary>Спільне тіло повного прогону документа (або одного аркуша).</summary>
    private async Task<int> RecalculateDocumentAsync(
        long documentId, PeriodKey periodKey, int? sheetDefId, (int SheetDefId, long? CopyId)? heldSheet,
        ScopeFilter scopes, CancellationToken ct,
        HashSet<(int TableDefId, int ColumnDefId)>? changedColumns = null, bool writeSubmitted = false,
        List<FormulaErrorCell>? errorSink = null,
        IReadOnlyList<int>? annualPeriods = null)
    {
        var instances = await rowStore
            .GetTableInstancesAsync(documentId, periodKey, ct)
            .ConfigureAwait(false);

        if (instances.Count == 0)
        {
            return 0;
        }

        // ⚠ Один прогін на ВЕРСІЮ ШАБЛОНУ, а не на екземпляр таблиці: тіло
        // нижче однаково читає всі таблиці документа за період і будує план
        // на весь знімок структури. Прогін на кожен екземпляр перерахував би
        // ті самі формули стільки разів, скільки в документі таблиць.
        var written = 0;
        foreach (var (representative, _) in ScopeRepresentatives(instances, scopes))
        {
            written += await RunAsync(
                    representative, dirty: null, ct, sheetDefId, heldSheet, instances, changedColumns, writeSubmitted: writeSubmitted,
                    errorSink: errorSink,
                    annualPeriods: annualPeriods)
                .ConfigureAwait(false);
        }

        return written;
    }

    /// <summary>Спільне тіло обох входів: <c>dirty is null</c> — повний прогін.</summary>
    /// <param name="instance">Один із екземплярів таблиць документа за період — джерело його версії шаблону.</param>
    /// <param name="dirty">Змінені комірки; <c>null</c> — повний прогін.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <param name="sheetDefId">
    /// Q-331: звужує повний прогін (<paramref name="dirty"/> = <c>null</c>) до
    /// формул, чия таблиця належить цьому аркушу; <c>null</c> — увесь документ.
    /// Інкрементний прогін (<paramref name="dirty"/> не <c>null</c>) його НЕ
    /// приймає — той шлях завжди йде від правки конкретної комірки, і звужувати
    /// каскад за аркушем означало б не порахувати залежну формулу сусіднього
    /// аркуша, на яку саме каскад і розрахований.
    /// </param>
    /// <param name="heldSheet">
    /// Аркуш (і його копія; <c>null</c> — оригінал), виняткове блокування якого ВЖЕ тримає викликач у цій самій
    /// транзакції (подання); <c>null</c> — таких немає.
    /// </param>
    /// <param name="knownInstances">
    /// Екземпляри документа за період, уже прочитані викликачем (O2: повний
    /// прогін читав той самий перелік тричі — тут, у викликачі й у
    /// <see cref="LoadPeriodAsync"/>); <c>null</c> — прочитати.
    /// </param>
    /// <param name="changedColumns">WW-03: куди скласти змінені колонки; <c>null</c> — не збирати.</param>
    /// <param name="crossPeriodSources">
    /// WW-03: прогін по зміні ІНШОГО періоду — цілі лише формули з міжперіодним ребром на ці колонки
    /// (<see cref="CrossPeriodReaders.Targets"/>); <c>null</c> — звичайний вхід.
    /// </param>
    /// <param name="approverEditSheets">D-442: подані аркуші правки погоджувача (лише інкрементний вхід).</param>
    /// <param name="inputColumns">Q-17: куди скласти колонки насіння (введені, не похідні); <c>null</c> — не збирати.</param>
    /// <param name="shared">L2: спільні читання проходу (екземпляри, залежності); <c>null</c> — читати напряму.</param>
    /// <param name="writeSubmitted">✎ UAT-recalc-ux: писати й ПОДАНІ аркуші (ручний перерахунок, <c>RecalculationJob.WritesSubmitted</c>); затверджені — ніколи.</param>
    /// <param name="errorSink">✎ C-1: куди скласти комірки аркуша (<paramref name="sheetDefId"/>) з помилкою формули; <c>null</c> — не збирати.</param>
    /// <param name="annualPeriods">✎ LAND-YEAR B24: прогін закритого якоря під агрегатом року (див. <see cref="RecalculateAllAsync"/>).</param>
    private async Task<int> RunAsync(
        TableInstanceRef instance, DirtySet? dirty, CancellationToken ct, int? sheetDefId = null,
        (int SheetDefId, long? CopyId)? heldSheet = null, IReadOnlyList<TableInstanceRef>? knownInstances = null,
        HashSet<(int TableDefId, int ColumnDefId)>? changedColumns = null,
        IReadOnlyCollection<CrossPeriodSource>? crossPeriodSources = null,
        IReadOnlySet<int>? approverEditSheets = null,
        HashSet<(int TableDefId, int ColumnDefId)>? inputColumns = null,
        RecalculationSharedLoad? shared = null, bool writeSubmitted = false,
        List<FormulaErrorCell>? errorSink = null,
        IReadOnlyList<int>? annualPeriods = null)
    {
        // ✎ LAND-YEAR B24: копії аркушів Q-17 не маршрутизуються — у закритому місяці їм писати нічого.
        if (annualPeriods is not null && instance.SheetCopyId is not null)
        {
            return 0;
        }

        var periodKey = new PeriodKey(instance.PeriodKey);

        // Q-17.4: власники введених комірок читаються один раз на ЦЮ задачу (≤ 13 партицій), а не на кожен період
        // читання (поточний + зсуви). Перерахунок введених комірок власників не пише — інвалідація не потрібна.
        // Прапор вимкнено — сховище звідси нічого не кешує (роутер не ходить до бази).
        using var ownerSession = (cellStore as IOwnerReadSession)?.BeginOwnerReadSession();

        var snapshot = await metadata.GetAsync(instance.TemplateVersionId, ct).ConfigureAwait(false);
        var dependencies = shared is null
            ? await versions.ListFormulaDependenciesAsync(instance.TemplateVersionId, ct).ConfigureAwait(false)
            : await shared.DependenciesAsync(instance.TemplateVersionId, ct).ConfigureAwait(false);

        // ⛔ Читаються ВСІ таблиці документа за період, а не лише та, де
        // сталася правка. Формула сусідньої таблиці, яка читає цю, живе в
        // СВОЄМУ екземплярі: з одним переліком рядків вона не отримала б ані
        // ребра графа, ані місця для запису — і мовчки не перераховувалася б.
        var allInstances = knownInstances
            ?? (shared is null
                ? await rowStore.GetTableInstancesAsync(instance.DocumentId, periodKey, ct).ConfigureAwait(false)
                : await shared.InstancesAsync(instance.DocumentId, periodKey, ct).ConfigureAwait(false));

        // ⛔ RS: «вид» прогону. Оригінал бачить лише оригінали; копія — свої таблиці зі своїх екземплярів, решту з
        // оригіналів. Далі за текстом `instances` — уже вид, тож ключування за TableDefId однозначне.
        var snapshotIndex = RecalculationPlanBuilder.SnapshotIndex.For(snapshot);
        var tables = snapshotIndex.Tables;
        var formulas = snapshotIndex.Formulas;

        CopyScope? copyScope = null;
        if (instance.SheetCopyId is { } copyId && tables.TryGetValue(instance.TableDefId, out var copyTable))
        {
            copyScope = new CopyScope(
                copyId, copyTable.SheetDefId,
                tables.Values.Where(t => t.SheetDefId == copyTable.SheetDefId).Select(t => t.Id).ToHashSet());

            // Повний прогін, звужений до іншого аркуша, копію цього аркуша не чіпає.
            if (sheetDefId is { } narrowed && narrowed != copyScope.SheetDefId)
            {
                return 0;
            }
        }

        var instances = CopyScope.Apply(allInstances, copyScope);

        // Формула копії пише лише в таблиці СВОГО аркуша: решта виду — оригінали, їх рахує прогін оригіналу.
        bool InScope(int formulaId)
            => copyScope is null
               || (formulas.TryGetValue(formulaId, out var scoped) && scoped.Table.SheetDefId == copyScope.SheetDefId);

        // ⛔ Q-166 (аудит фази 2, продуктивність): ОДИН пакетний запит на
        // рядки ВСІХ таблиць документа замість запиту на кожну — цей прогін
        // виконується на КОЖНЕ редагування комірки (через
        // `FormulaRecalculationJob`), а не лише на повний перерахунок, тож
        // N+1 тут коштує найдорожче серед усіх знахідок цього виміру.
        var rowIdsBatch = await rowStore
            .GetRowIdsBatchAsync([.. instances.Select(t => t.TableInstanceId)], periodKey, ct)
            .ConfigureAwait(false);

        var rowIdsByTable = new Dictionary<int, IReadOnlyDictionary<string, long>>();
        var instanceByTable = new Dictionary<int, long>();

        foreach (var table in instances)
        {
            instanceByTable[table.TableDefId] = table.TableInstanceId;
            rowIdsByTable[table.TableDefId] = rowIdsBatch.TryGetValue(table.TableInstanceId, out var found)
                ? found
                : new Dictionary<string, long>(StringComparer.Ordinal);
        }

        // WW-03: насіння каскаду теж «змінене» для читачів інших періодів — навіть якщо жодна похідна
        // комірка цього періоду не змінилась (читач у сусідньому періоді може читати саме правлену).
        if ((changedColumns is not null || inputColumns is not null) && dirty is not null)
        {
            var tableOfRow = new Dictionary<long, int>();
            foreach (var (tableDefId, byKey) in rowIdsByTable)
            {
                foreach (var rowId in byKey.Values)
                {
                    tableOfRow[rowId] = tableDefId;
                }
            }

            foreach (var seed in dirty.Seeds)
            {
                if (tableOfRow.TryGetValue(seed.TableRowId, out var seedTable))
                {
                    changedColumns?.Add((seedTable, seed.ColumnDefId));
                    inputColumns?.Add((seedTable, seed.ColumnDefId));
                }            }
        }

        // ⛔ Q-2xx (аудит фази 3, звітність): аудит записується за КЛЮЧЕМ
        // рядка (`RowKey`), а не лише за його ідентифікатором — той самий
        // намір, що вже закрив `PatchCellsHandler` (директива №09 `W8` п.4).
        // Зворотна мапа будується ТУТ, з уже прочитаних `rowIdsByTable`: другий
        // похід у базу заради того самого рядка нічого не додав би.
        var rowKeyByRowIdByInstance = instances.ToDictionary(
            t => t.TableInstanceId,
            t => rowIdsByTable.TryGetValue(t.TableDefId, out var byKey)
                ? byKey.ToDictionary(pair => pair.Value, pair => pair.Key)
                : new Dictionary<long, string>());

        var plan = RecalculationPlanBuilder.Build(snapshot, dependencies, rowIdsByTable, periodKey);

        // ⚠ Q-331: перенесено ВИЩЕ вибору цілей (`targets` нижче) — фільтр
        // повного прогону за аркушем потребує знати, якій таблиці належить
        // кожна формула, ДО того, як список цілей уже сформований.
        // L2b: словники таблиць і живих формул — з індексу знімка (будується раз на знімок, не на кожен прогін).
        // (індекс знімка `tables`/`formulas` побудовано вище — разом із видом копії)

        // ⛔ Q-17.3: колонки з ЧУЖИМ власником для екземплярів цього прогону. Введену комірку такої колонки пише лише
        // власник (прогін його екземпляра рахує її формули сам); цей прогін її не перезаписує — див. `Evaluate`.
        // Прапор вимкнено / проєкт не Monthly / таблиця поза маршрутизацією — порожньо, поведінка попередня.
        // RS: копії Q-17 не підтримують (їхні введені значення лежать у власних екземплярах, а не в «власника» року).
        // ✎ LAND-YEAR L1: `ownedElsewhere` — ОБЧИСЛЮВАНІ колонки з чужим власником (лише версія YearWithMonthColumns,
        // `IMonthColumnRouter.RoutesComputedVersion`): прогін періоду m пише лише те, чим володіє (C_Mm — період m,
        // не-місячне — якір); решту рахує й пише прогін власника, а цей читає її значення з власника (зріз сховища).
        var (foreignByTable, ownedElsewhere) = copyScope is not null
            ? (null, null)
            : await ForeignColumnsByTableAsync(instance.DocumentId, instances, tables, periodKey, ct)
                .ConfigureAwait(false);

        // ✎ LAND-YEAR B24: закритий якір пише лише не-місячні колонки маршрутизованих таблиць — решта (місячні C_M01,
        // таблиці поза маршрутизацією) «чужі» для цього прогону тим самим механізмом, що й колонки інших власників (L1).
        if (annualPeriods is not null)
        {
            ownedElsewhere = AnnualOnlyBlocked(ownedElsewhere, instances, tables);
        }

        // ✎ LAND-YEAR B60: прогін ВІДКРИТОГО якоря річної розкладки — не-місячні цілі пишуться під АГРЕГАТНИМ станом
        // аркуша за місяцями року (як PATCH не-місячної колонки), а місячні C_M01 — під станом аркуша самого січня.
        // Роутер і календар уже в кеші scope (ForeignColumnsByTableAsync вище) — нових запитів тут немає.
        var openAnchorYear = annualPeriods is null && copyScope is null && ownedElsewhere is not null
            ? await OpenAnchorYearAsync(instance, periodKey, ct).ConfigureAwait(false)
            : null;

        List<int> targets;

        if (crossPeriodSources is not null)
        {
            // ⛔ WW-03: прогін ПО ЗМІНІ ІНШОГО періоду. Цілі — формули цього періоду з міжперіодним
            // ребром на змінену колонку, плюс усе, що в межах періоду читає їхній результат.
            // Знімки (`IsSnapshot`) не чіпаються, як і в `Plan`; повний прогін документа тут не
            // потрібен — це і є ціль coalescing/fan-out (шторм перерахунків).
            targets = [.. CrossPeriodReaders.Targets(
                dependencies, crossPeriodSources, periodKey.Sequence, plan, formulas.Keys).Where(InScope)];
        }
        else if (dirty is null)
        {
            // ⛔ Повний прогін бере формули з ПЛАНУ, а не з насіння: формула,
            // додана в шаблон після введення даних, не має жодної брудної
            // комірки — і саме тому в інкрементний набір не потрапляє ніколи.
            //
            // ⚠ Q-331: коли `sheetDefId` заданий, ЦІЛІ звужуються до формул,
            // чия таблиця належить цьому аркушу — компроміс, а не половинчастий
            // фікс (`RecalculationJob` docs): методологія й формула шаблону
            // ПИШУТЬ у таблицю свого аркуша, а ЧИТАЄ контекст (`values` нижче)
            // усе одно ввесь документ, тож формула сусіднього аркуша, яка читає
            // цю таблицю, отримає вже перераховане значення так само, як і
            // раніше.
            targets = [.. plan.AllFormulas()
                .Where(id => !plan.IsSnapshot(id)
                             && InScope(id)
                             && (sheetDefId is null
                                 || (formulas.TryGetValue(id, out var owned)
                                     && owned.Table.SheetDefId == sheetDefId)))];
        }
        else
        {
            var affected = WithSeedWriters(Plan(dirty, plan), dirty, plan, snapshot, formulas, rowIdsByTable);

            // ⛔ Крос-аркушні rollup рахуються ТУТ САМО, а не «колись».
            // Відкладення має сенс на інтерактивному шляху, де людина чекає на
            // кожному Tab; цей прогін уже фоновий, і відкласти означало б не
            // порахувати ніколи — саме те, чим був увесь `A7-63`.
            var rollups = TakeDeferredRollups(plan);

            // RS: каскад від копії лишається всередині копії (формули інших аркушів читали б її як «оригінал»).
            targets = [.. affected.Where(InScope), .. rollups.Where(InScope)];
        }

        // ✎ LAND-YEAR L1: формули колонки/комірки, чия колонка має ЧУЖОГО власника, тут не цілі — інакше 11 періодів-читачів
        // кожної правки читали б увесь зріз заради формул, які нічого не запишуть. Рядкові формули (кілька колонок)
        // лишаються: їхні чужі комірки пропускає `Evaluate`. Поза річною розкладкою `ownedElsewhere` = null.
        if (ownedElsewhere is not null)
        {
            targets = [.. targets.Where(id => !formulas.TryGetValue(id, out var target)
                                              || target.Formula.Scope == Domain.Enums.FormulaScope.Row
                                              || target.Formula.ColumnDefId is not { } targetColumn
                                              || !ownedElsewhere.TryGetValue(target.Table.Id, out var elsewhere)
                                              || !elsewhere.Contains(targetColumn))];
        }

        // D-230: версія з активним Rollup/Check не виходить тут, навіть коли формул немає: джерело Rollup
        // могло змінитися без жодної залежної формули. Прапор — з наявного знімка (0 нових запитів).
        // ✎ LAND-YEAR B24: Rollup/Check закритого якоря не рахуються (їхні цілі — не лише не-місячні колонки).
        var relationsActive = relationRecalculator is not null && snapshot.HasActiveRollupOrCheck && annualPeriods is null;

        if (targets.Count == 0 && !relationsActive)
        {
            return 0;
        }

        // ✎ D-461: комірки з ручним значенням замість формули — ОДИН запит на прогін (індекс за періодом × екземпляром),
        // далі O(1) на комірку в `Evaluate`. Прогін їх не перезаписує НІКОЛИ (і після вимкнення прапорця формули), а
        // залежні формули читають ручне значення (воно вже в `values` зі сховища).
        // ⚠ Продуктивність (храповик `FormulaJobFixedCostTests`): версія, де жодна формула не дозволяє ручне значення
        // і позначок немає, не платить НІЧОГО — ні читання позначок, ні блокування перед записом. «Позначки є» —
        // похідне від версії (один EXISTS на ревізію знімка, `GetDerivedAsync`): позначка з'являється лише за
        // увімкненого прапорця, а перемикач піднімає ревізію, тож після вимкнення EXISTS перечитується.
        var overridesActive = targets.Count > 0
            && await OverridesActiveAsync(snapshot, formulas.Values.Select(f => f.Formula), ct).ConfigureAwait(false);
        IReadOnlySet<CellAddress>? overridden = !overridesActive
            ? null
            : await formulaOverrides!
                .ReadKeysAsync([.. instances.Select(i => i.TableInstanceId)], periodKey, ct)
                .ConfigureAwait(false);

        // ⛔ Директива №14 частина 3, `CAL-03`. Розбір винесено СЮДИ з
        // `Evaluate` не заради економії: класифікатор рядкової локальності
        // працює з AST, і робити другий `Parse` заради одного прапорця означало
        // б розбирати кожну формулу двічі на кожен прогін.
        var parsed = new Dictionary<int, Ecr.Expressions.Parsing.ParsedExpression>();
        var rowLocal = new HashSet<int>();
        var tablesWithNonLocalTarget = new HashSet<int>();

        // ⚠ `CAL-02`: цілі, які читають комірки, але про які граф залежностей
        // мовчить. Для них звужувати читання нема за чим — див.
        // `RecalculationReadScope.Compute`.
        var knownReads = RecalculationReadScope.FormulasWithStoredCellEdges(dependencies);
        var unknownReads = new List<int>();

        foreach (var formulaId in targets)
        {
            if (!formulas.TryGetValue(formulaId, out var owner))
            {
                continue;
            }

            var result = formulaEngine.Parse(owner.Formula.Expression, owner.Formula.Dialect);
            if (result.Expression is null)
            {
                // Непридатний вираз не проходить публікацію; якщо він тут — це
                // розбіжність між збереженим і чинним, і мовчки писати нуль було
                // б гірше, ніж не писати нічого.
                continue;
            }

            parsed[formulaId] = result.Expression;

            if (!knownReads.Contains(formulaId)
                && RecalculationReadScope.MentionsCells(result.Expression.Root))
            {
                unknownReads.Add(formulaId);
            }

            if (RowLocalFormulaClassifier.IsRowLocal(owner.Formula, result.Expression.Root))
            {
                rowLocal.Add(formulaId);
            }
            else
            {
                tablesWithNonLocalTarget.Add(owner.Table.Id);
            }
        }

        // ⛔ `CAL-02`: читається ЗАМИКАННЯ, а не документ. Скоуп рахується після
        // вибору цілей — саме цілі й визначають, що потрібно прочитати.
        var scope = RecalculationReadScope.Compute(
            dependencies,
            targets,
            formulas.ToDictionary(pair => pair.Key, pair => pair.Value.Table.Id),
            unknownReads);

        // ⛔ `CAL-03`: брудні рядки інкрементного прогону. Повний прогін
        // (`dirty is null`) не звужується — там цілі беруться з плану, а не з
        // насіння, і «брудних рядків» не існує за побудовою.
        var dirtyRows = dirty is null
            ? null
            : new HashSet<long>(dirty.Seeds.Select(seed => seed.TableRowId));

        // WW-03: які інші періоди читають вирази цілей (відносні зсуви й `[Period:#k]`).
        var periodReads = RecalculationReadScope.PeriodReferences(parsed.Values.Select(p => p.Root));

        // ✎ C-1 (D-475): стан помилок формул. Прапорець — із запиту екземплярів (`TableInstanceRef.HasFormulaErrors`,
        // EXISTS у тій самій команді); без помилок — нуль звернень (храповик FormulaJobFixedCostTests).
        var errors = new FormulaErrorTracker();
        var flagged = instances.Where(i => i.HasFormulaErrors).Select(i => i.TableInstanceId).ToList();
        IReadOnlyList<FormulaErrorRow> persisted = formulaErrors is not null && flagged.Count > 0
            ? await formulaErrors.ReadInstancesAsync(flagged, periodKey, ct).ConfigureAwait(false)
            : [];
        errors.Seed(persisted, offset: 0);
        var loadExtras = new PeriodLoadExtras
        {
            // ✎ C-5 (рішення PO 11.11): дефолти колонок версії — для комірок, яких немає (02b §6.3).
            Defaults = await _columnDefaults.ForVersionAsync(instance.TemplateVersionId, ct).ConfigureAwait(false),
        };

        var (values, stored) = await LoadValuesAsync(
            instance, scope, (instances, rowIdsBatch), periodReads, ct, copyScope, loadExtras).ConfigureAwait(false);
        await SeedNeighbourErrorsAsync(errors, loadExtras, periodKey, ct).ConfigureAwait(false);

        // ⛔ Знімок довідника одиниць передається В КОНТЕКСТ, а не читається
        // ним самим: `IEvaluationContext.Convert` — синхронний метод діалекту
        // виразів, а `IUnitCatalog.GetAsync` — ні. До цього тут не було ЖОДНОГО
        // джерела одиниць, і `CONVERT` у формулі шаблону відмовляв БЕЗУМОВНО,
        // незалежно від того, чи існує сама конверсія (директива №09 §6.5, `S-22`).
        var catalogue = await unitCatalog.GetAsync(ct).ConfigureAwait(false);

        // ⚠ Той самий принцип для REGFIELD: знімок полів довідника читається
        // ТУТ, звужений до Registry-залежностей ЦІЛЕЙ цього прогону (`CAL-02`
        // для довідника). Джерело — саме `dependencies` (`cfg.FormulaDependency`,
        // `DependsOnKind = KindRegistry`): якщо видобувач цю залежність не
        // заповнив, знімок лишиться порожнім, і REGFIELD віддасть `#REF`
        // навіть коли Lookup-комірка заповнена, — це і є той міст, який
        // з'єднує `DependencyExtractor` з фактичним перерахунком.
        // ✎ P1 06.11 (HSE401, 6428 рядків): поля довідника — лише для рядків, які прогін справді рахує. Колонкова
        // REGFIELD у рядково-локальній цілі першого проходу рахується лише в брудних рядках (`rowFilter` нижче, CAL-03),
        // а знімок брався для КОЖНОГО рядка таблиці: задача правки одного рядка тривала 12–26 с проти 1.9 с числової.
        // Умова звуження — та сама, що в `EvaluateTargets(narrowRows: true)`.
        bool NarrowsRows(int formulaId)
            => dirtyRows is not null
               && rowLocal.Contains(formulaId)
               && formulas.TryGetValue(formulaId, out var narrowOwner)
               && !tablesWithNonLocalTarget.Contains(narrowOwner.Table.Id);

        var registryFields = await LoadRegistryFieldsAsync(
            dependencies, targets, tables, rowIdsByTable, values, catalogue, ct, NarrowsRows, dirtyRows).ConfigureAwait(false);
        var registryFieldsNarrowed = dirtyRows is not null && targets.Any(NarrowsRows);

        // ⛔ Шапка документа читається РЕАЛЬНО (раніше — EmptyHeaders,
        // статичний порожній словник, і HDR.X завжди давав Null незалежно
        // від того, що записано в doc.DocumentHeaderValue). Один запит на
        // документ, а не на таблицю: шапка спільна для всіх таблиць
        // документа, і повторювати запит на кожен виклик RunAsync було б
        // зайвим походом у базу там, де достатньо одного.
        var headerValues = await headers.GetExpressionValuesAsync(instance.DocumentId, ct).ConfigureAwait(false);

        var periodContext = await PeriodOf(instance.DocumentId, periodKey, ct).ConfigureAwait(false);

        // ⛔ LP-40: знімок довідників для REG*-форм формул-ЦІЛЕЙ. Коди — з літералів розібраних цілей
        // (як `GenericCalculationModule.RegistryCodes`); нуль REG*-форм — нуль звернень до БД.
        var templateRegistries = await _templateSnapshots
            .GetAsync(
                parsed.Values.Select(p => p.Root), periodContext.End,
                registrySnapshots, registryStore, ct)
            .ConfigureAwait(false);

        // SHEETNAME(): назви аркушів «виду» прогону («<Назва> N» для копії). Один запит — лише коли хоч одна формула версії
        // згадує SHEETNAME (за текстом, без розбору; разом із каскадними цілями нижче); інакше — нуль звернень.
        var sheetNames = sheetCopies is not null
                         && SheetDisplayNames.MayUse(formulas.Values.Select(f => f.Formula.Expression))
            ? SheetDisplayNames.For(
                snapshot,
                await sheetCopies.ListGroupsAsync(instance.DocumentId, ct).ConfigureAwait(false),
                copyScope?.CopyId)
            : null;

        SliceEvaluationContext NewContext(
            IReadOnlyDictionary<long, IReadOnlyDictionary<string, Ecr.Expressions.Evaluation.ExpressionValue>>? fields)
            => new(
                snapshot, values, headerValues,
                periodContext,
                catalogue, rowIdsByTable, fields,
                formulaDependencies: dependencies,
                logger: logger,
                registries: templateRegistries,
                sheetNames: sheetNames)
            {
                // ✎ C-1: недовірені комірки прогону — читання такої комірки не дає цілі перерахуватися.
                Blocked = errors.Blocked,

                // ✎ C-11: рядки сусідніх періодів для предиката зі зсувом.
                OtherPeriodRows = loadExtras.OtherPeriodRows,
            };

        var context = NewContext(registryFields);

        // Результати групуються за екземпляром: кожна таблиця пишеться
        // своїм набором змін, бо `CellChangeSet` адресує один екземпляр.
        //
        // ⛔ V-03: усередині екземпляра — СЛОВНИК за адресою, а не список.
        // Рядкова й колонкова формули можуть цілити в одну комірку (публікація
        // це тепер відхиляє, але вже опубліковані версії лишаються), і список
        // давав два записи з однією адресою: `NormalizedCellStore.ApplyAsync`
        // падав на `PRIMARY KEY … dbo.@cells`, подання — 500, фонова задача
        // ретраїла хвилинами. Правило пріоритету детерміноване: пише ОСТАННЯ
        // в порядку обчислення — саме її значення вже лежить у контексті й
        // його читають залежні формули, тож база й каскад не розходяться.
        var byInstance = new Dictionary<long, Dictionary<CellAddress, CellRecord>>();

        void EvaluateTargets(IEnumerable<int> ids, bool narrowRows)
        {
            foreach (var formulaId in ids)
            {
                // ⛔ Аудит L2-03 (AN-38): процесорна фаза — між формулами — перевіряє
                // токен. Без цього межа `MaxDuration` і запит скасування лише
                // «просили» зупинитися: задача рахувала далі, тримала оренду й писала
                // результат скасованого перерахунку.
                ct.ThrowIfCancellationRequested();

                if (!formulas.TryGetValue(formulaId, out var owner))
                {
                    continue;
                }

                if (!instanceByTable.TryGetValue(owner.Table.Id, out var target)
                    || !rowIdsByTable.TryGetValue(owner.Table.Id, out var rows))
                {
                    // Таблиці формули немає в цьому документі за цей період —
                    // рахувати нема куди. Це нормально: шаблон описує таблиці,
                    // яких конкретний документ може не містити (`ФВ-3.2`).
                    continue;
                }

                if (!parsed.TryGetValue(formulaId, out var expression))
                {
                    continue;
                }

                if (!byInstance.TryGetValue(target, out var sink))
                {
                    byInstance[target] = sink = [];
                }

                // ⛔ `CAL-03`, і саме тут проходить межа безпеки. Рядкове звуження
                // застосовується лише тоді, коли в цю таблицю в ЦЬОМУ прогоні
                // не пише жодна НЕлокальна ціль. Тоді й тільки тоді твердження
                // «усе, що може змінитися в таблиці, лежить у брудних рядках»
                // доводиться індукцією: базу дає насіння (брудні комірки), крок —
                // рядково-локальна формула, яка читає лише свій рядок і пише лише
                // в нього. Варіант із накопиченням «рядків, куди вже записали»
                // виглядав би розумнішим, але спирався б на порядок обчислення
                // цілей, а він тут не єдиний: відкладені rollup дописуються в
                // кінець списку ОКРЕМО відсортованою пачкою (`targets` вище).
                var rowFilter = narrowRows
                                && dirtyRows is not null
                                && rowLocal.Contains(formulaId)
                                && !tablesWithNonLocalTarget.Contains(owner.Table.Id)
                    ? dirtyRows
                    : null;

                Evaluate(
                    owner.Table, owner.Formula, expression, rows, rowFilter,
                    periodKey, context, values, stored, sink, errors, tables,
                    foreignByTable?.GetValueOrDefault(owner.Table.Id), overridden,
                    ownedElsewhere?.GetValueOrDefault(owner.Table.Id));
            }
        }

        EvaluateTargets(targets, narrowRows: true);

        // D-230: Rollup — ПІСЛЯ формул документа (значення джерела вже перераховані) і тим самим шляхом
        // запису: записи йдуть у `byInstance`, далі той самий `WriteBatchAsync`/аудит/гейти аркушів.
        // Уся логіка — в `IRelationRecalculator`; без активного Rollup/Check тут нічого не читається.
        if (relationsActive)
        {
            var rollupSeeds = new DirtySet();
            var pending = byInstance.Values
                .SelectMany(cells => cells.Values)
                .ToDictionary(record => record.Address);

            var relationWrites = await relationRecalculator!
                .ComputeForDocumentAsync(
                    new Calculations.RelationRunInput(
                        snapshot, instance.TemplateVersionId, periodKey, instances, rowIdsBatch, pending),
                    ct)
                .ConfigureAwait(false);

            foreach (var relationWrite in relationWrites)
            {
                // RS: прогін копії пише лише в екземпляри цієї копії (rollup/check в оригінали рахує прогін оригіналу).
                if (copyScope is not null
                    && !instances.Any(i => i.TableInstanceId == relationWrite.TargetInstanceId
                                           && i.SheetCopyId == copyScope.CopyId))
                {
                    continue;
                }

                if (!byInstance.TryGetValue(relationWrite.TargetInstanceId, out var relationSink))
                {
                    byInstance[relationWrite.TargetInstanceId] = relationSink = [];
                }

                if (relationWrite.Unchanged)
                {
                    relationSink.Remove(relationWrite.Record.Address);
                }
                else
                {
                    relationSink[relationWrite.Record.Address] = relationWrite.Record;
                }

                // P2-B / D-5: ціль Rollup — нове ДЖЕРЕЛО для формул, що її читають. Значення Rollup лягає в
                // контекст (формули нижче читають його, а не збережене у базі), а комірка — в насіння каскаду.
                // R15b-1: і для Unchanged теж. Власний вираз цілі (валідатор вимагає Formula-колонку) у цьому
                // самому прогоні міг покласти в контекст своє значення (`[X] * 0` → 0), яке Rollup (= збереженому)
                // не записує, але формули над ціллю вже порахували від нього; без каскаду вони лишались нульовими.
                if (rowKeyByRowIdByInstance.TryGetValue(relationWrite.TargetInstanceId, out var rowKeys)
                    && rowKeys.TryGetValue(relationWrite.Record.Address.TableRowId, out var targetRowKey))
                {
                    values[new CellKey(0, relationWrite.Record.TableDefId, targetRowKey, relationWrite.Record.Address.ColumnDefId)]
                        = Ecr.Expressions.Evaluation.CellValueMapping.ToExpressionValue(
                            relationWrite.Record.Value, Ecr.Expressions.Evaluation.ExpressionValue.Null);
                }

                rollupSeeds.Add(relationWrite.Record.Address);
            }

            // Каскад: формули над ціллю (і ті, що читають їхні результати) рахуються ПІСЛЯ запису Rollup. Без
            // цього вони читали б стару/порожню ціль: Rollup іде після циклу формул, а ціль не є насінням
            // правки. Ланцюжки Rollup → формула → Rollup, як і раніше, не підтримуються (один прохід).
            if (!rollupSeeds.IsEmpty)
            {
                var cascade = Plan(rollupSeeds, plan);
                var deferredCascade = TakeDeferredRollups(plan);
                var cascadeIds = cascade.Concat(deferredCascade).Where(InScope).Distinct().ToList();
                cascadeIds.Sort((a, b) => plan.EvaluationOrder(a).CompareTo(plan.EvaluationOrder(b)));

                var cascadeUnknown = new List<int>();
                var cascadeTargets = new List<int>();
                foreach (var formulaId in cascadeIds)
                {
                    if (!formulas.TryGetValue(formulaId, out var owner))
                    {
                        continue;
                    }

                    if (!parsed.TryGetValue(formulaId, out var expression))
                    {
                        var result = formulaEngine.Parse(owner.Formula.Expression, owner.Formula.Dialect);
                        if (result.Expression is null)
                        {
                            continue;
                        }

                        parsed[formulaId] = expression = result.Expression;
                    }

                    if (!knownReads.Contains(formulaId) && RecalculationReadScope.MentionsCells(expression.Root))
                    {
                        cascadeUnknown.Add(formulaId);
                    }

                    cascadeTargets.Add(formulaId);
                }

                if (cascadeTargets.Count > 0)
                {
                    // Входи каскадних формул могли не потрапити в замикання читання першого проходу (воно
                    // будувалось від цілей, а не від цілі Rollup): докладаємо відсутнє, не чіпаючи вже порахованого.
                    var extraScope = RecalculationReadScope.Compute(
                        dependencies,
                        cascadeTargets,
                        formulas.ToDictionary(pair => pair.Key, pair => pair.Value.Table.Id),
                        cascadeUnknown);
                    var (extraValues, extraStored) = await LoadValuesAsync(
                        instance, extraScope, (instances, rowIdsBatch),
                        RecalculationReadScope.PeriodReferences(
                            cascadeTargets.Where(parsed.ContainsKey).Select(id => parsed[id].Root)),
                        ct, copyScope, loadExtras).ConfigureAwait(false);
                    await SeedNeighbourErrorsAsync(errors, loadExtras, periodKey, ct).ConfigureAwait(false);
                    foreach (var (key, value) in extraValues)
                    {
                        values.TryAdd(key, value);
                    }

                    foreach (var (key, value) in extraStored)
                    {
                        stored.TryAdd(key, value);
                    }

                    // ✎ P1 06.11: каскад рахує ВСІ рядки — знімок полів довідника, звужений для першого проходу,
                    // тут перечитується повним (для цілей обох проходів), інакше REGFIELD поза брудними рядками дав би #REF.
                    if (registryFieldsNarrowed)
                    {
                        var full = await LoadRegistryFieldsAsync(
                                dependencies, [.. targets, .. cascadeTargets], tables, rowIdsByTable, values, catalogue, ct)
                            .ConfigureAwait(false);
                        context = NewContext(full);
                        registryFieldsNarrowed = false;
                    }

                    // Рядкове звуження вимкнено: брудні рядки першого проходу не описують рядки цілі Rollup.
                    EvaluateTargets(cascadeTargets, narrowRows: false);
                }
            }
        }

        var written = byInstance.Values.Sum(cells => cells.Count);

        // ✎ C-1: комірки аркуша з помилкою — викликачу подання (RecalculateSheetForSubmitAsync), навіть коли нічого
        // не пишеться (та сама помилка, що вже збережена).
        errorSink?.AddRange(errors.Outcomes.Values
            .Where(o => o.Kind is not null
                        && (sheetDefId is null || (tables.TryGetValue(o.TableDefId, out var errorTable) && errorTable.SheetDefId == sheetDefId)))
            .Select(o => new FormulaErrorCell(
                o.TableDefId, instances.FirstOrDefault(i => i.TableDefId == o.TableDefId)?.SheetCopyId, o.RowKey,
                o.Address.ColumnDefId, o.Kind!, o.ErrorCode!, o.Source?.PeriodKey, o.Source?.TableDefId,
                o.Source?.RowKey, o.Source?.ColumnDefId)));

        var errorDelta = formulaErrors is null
            ? FormulaErrorDelta.Empty
            : FormulaErrorDelta.Build(
                errors, persisted, instance.DocumentId, instanceByTable, instances,
                fullRun: dirty is null && crossPeriodSources is null,
                targetFormulas: targets.ToHashSet(), liveFormulas: formulas.Keys.ToHashSet(), now: clock.UtcNow);

        // ✎ LAND-YEAR B24 × C-1b(2): прогін закритого якоря не відвідує заблоковані комірки (місячні C_M01, таблиці поза
        // маршрутизацією, колонки інших власників) — їхні збережені помилки формул не «застарілі», а належать закритому
        // місяцю (чи іншому власнику), тож повний прогін їх не знімає.
        if (annualPeriods is not null && ownedElsewhere is not null && formulaErrors is not null)
        {
            bool Skipped(FormulaErrorRow row)
                => ownedElsewhere.TryGetValue(row.TableDefId, out var skipped) && skipped.Contains(row.ColumnDefId);

            errorDelta.Deletes.RemoveAll(Skipped);
            errorDelta.Events.RemoveAll(e => e.Resolved && Skipped(e.Row));
        }

        if (written == 0 && errorDelta.IsEmpty)
        {
            return 0;
        }

        // ⛔ `IsLateEdit` ОБЧИСЛЮЄТЬСЯ і тут (`D-70`, директива №09 `W8` п.6).
        // Похідне число, пораховане в `Grace`, — така сама пізня зміна, як і
        // введене руками: у звіт воно піде тим самим шляхом, і відрізняти їх
        // за походженням означало б залишити половину пізніх значень
        // непоміченими. «Хто змінив» тут і далі система (`SystemUserId`) —
        // це різні питання: КОЛИ і ХТО.
        var periodState = await periods
            .FindPeriodStateAsync(instance.DocumentId, periodKey.Value, ct)
            .ConfigureAwait(false);
        // WW-03 (рішення людини 13.10, п. 6): похідна зміна від правки ІНШОГО місяця — не «пізня».
        // ✎ LAND-YEAR B60: прогін закритого якоря пише лише не-місячне — «пізня» за річним правилом (агрегат місяців,
        // AnnualPeriodRule.IsLate, як PATCH не-місячної колонки), а не за станом самого якоря (Closed → ніколи).
        var isLateEdit = crossPeriodSources is null
                         && (annualPeriods is { Count: > 0 }
                             ? Security.AnnualPeriodRule.IsLate(
                                 await YearStatesAsync(instance.DocumentId, annualPeriods, ct).ConfigureAwait(false))
                             : periodState == Domain.Enums.PeriodState.Grace);

        // ⛔ Q-2xx (аудит фази 3, звітність). До цього рядка формули шаблону
        // писалися в `doc.CellValue` ЧЕРЕЗ `cellStore.ApplyAsync` без жодного
        // запису в `aud.CellChange` — `RecalculationService` не мав
        // `IAuditWriter` серед залежностей узагалі. Похідне число, змінене
        // перерахунком (правка шаблону, пізній перерахунок після виправлення
        // формули, каскад від ручної правки), не лишало жодного сліду в
        // журналі, хоча коментар до `SystemUserId` — двома абзацами вище —
        // уже описував саме такий запис («означало б записати в аудит, що він
        // власноруч ввів число, якого не вводив»): аудит малося на увазі
        // писати, лише ніхто цього не зробив. `Origin = "Recalculation"` уже
        // описаний у контракті `CellChangeRecord` (`UserEdit | Import |
        // Recalculation | Migration`) — досі жоден код його не використовував.
        var now = clock.UtcNow;

        // Аркуш кожного екземпляра, у який цей прогін пише: ключ блокування
        // «документ × аркуш × період» (`ISheetEditGate`).
        var sheetOfInstance = instances
            .Where(t => (byInstance.ContainsKey(t.TableInstanceId) || errorDelta.Instances.Contains(t.TableInstanceId))
                        && tables.ContainsKey(t.TableDefId))
            .ToDictionary(t => t.TableInstanceId, t => (tables[t.TableDefId].SheetDefId, t.SheetCopyId));

        // ⚠ Рахується ВСЕРЕДИНІ транзакції: аркуш, поданий, поки прогін рахував,
        // не пишеться, і повернути «записано N» за нього означало б збрехати.
        var applied = 0;

        // ⚠ Один запис на екземпляр: перерахунок торкається десятків комірок,
        // і окрема транзакція на кожну перетворила б фонову задачу на джерело
        // блокувань саме тоді, коли документ активно правлять.
        //
        // ⛔ Директива №14 частина 3, `DAT-02` п. 4 (він же `S-04`). До цього
        // тіло циклу давало ТРИ незалежні коміти на кожен екземпляр:
        // `ApplyAsync` комітив власною короткою транзакцією (ambient не було —
        // `NormalizedCellStore.ApplyAsync:257`), `WriteCellChangesAsync` писав
        // аудит поза нею, а `SaveChanges` закривав усе наприкінці. Збій між
        // ними лишав змінені числа БЕЗ рядка аудиту — тобто похідне значення,
        // яке ніхто не пояснить, і це рівно той стан, через який аудит сюди
        // взагалі заводили. Тепер `ApplyAsync` бачить ambient-транзакцію і
        // приєднується до неї (`NormalizedCellStore.ApplyAsync:243-252`):
        // значення й аудит лягають ОДНИМ комітом або не лягають зовсім.
        await uow.ExecuteInTransactionAsync(async token =>
        {
            // ⚠ Лічильник обнуляється на КОЖНУ спробу: стратегія повторів EF
            // може виконати це замикання вдруге.
            applied = 0;

            // ⛔ L6-02: перерахунок — теж писар комірок. Структура документа —
            // першою (подання, що кличе перерахунок, уже тримає її тим самим
            // власником): формули, пораховані за старою версією, після переносу
            // не пишуться під старими `ColumnDefId`.
            Documents.DocumentStructure.EnsureUnchanged(
                await sheetGate.EnterStructureForPeriodAsync(instance.DocumentId, exclusive: false, instance.PeriodKey, token).ConfigureAwait(false),
                instance.TemplateVersionId,
                instance.DocumentId);

            // ✎ LAND-YEAR B60: аркуші відкритого якоря, у які прогін пише не-місячне, — ще й агрегатний стан (окремий набір).
            // ✎ B103: і аркуші, де змінюється лише стан помилки формули НЕ-місячної комірки (значення не пишеться).
            var annualSheets = openAnchorYear is null
                ? null
                : AnnualTargetSheets(byInstance, sheetOfInstance, instances, tables, errorDelta.Cells());
            var annualWritable = annualSheets is { Count: > 0 } ? new HashSet<(int SheetDefId, long? CopyId)>() : null;

            var writable = await EnterSheetsAsync(
                    instance.DocumentId, periodKey, sheetOfInstance.Values, heldSheet, token, approverEditSheets, writeSubmitted,
                    annualPeriods,
                    annualWritable is null ? null : (openAnchorYear!, annualSheets!, annualWritable))
                .ConfigureAwait(false);

            // ⛔ O2 (ФВ-9.8, замір I2). Доти тіло нижче виконувалося НА КОЖЕН
            // екземпляр: `ReadCellsAsync` → `MERGE doc.CellValue` →
            // `INSERT aud.CellChange`, тобто 3 × N_таблиць звернень на
            // документо-період (90 таблиць × 12 періодів = 1 080 `MERGE` на
            // документ-рік, ~11 мс ЦП SQL кожен — 66 % ЦП SQL річного
            // перерахунку). Тепер — по одному на документо-період: той самий
            // `MERGE` з табличним параметром (`ApplyBatchAsync`, P8), лише з
            // усіма екземплярами разом. Порядок і вміст записів ті самі:
            // екземпляри в порядку `byInstance`, комірки — в порядку `cells`.
            var batch = new List<(long Target, IReadOnlyList<CellRecord> Records)>();
            foreach (var (target, cells) in byInstance.Where(pair => pair.Value.Count > 0))
            {
                // ⛔ Аркуш поданий або затверджений — його обчислені числа вже в
                // зрізі подання й не змінюються (ФВ-9.17, `RecalculationWritePolicy`):
                // шлях змінити подане — Reopen. Екземпляр без відомого аркуша не
                // пишеться з тієї самої причини — перевірити його нема чим.
                if (!sheetOfInstance.TryGetValue(target, out var sheetKey))
                {
                    continue;
                }

                // ✎ LAND-YEAR B60: аркуш відкритого якоря з не-місячними цілями — кожна комірка під своїм станом:
                // не-місячна маршрутизованої таблиці — агрегат року, решта (C_M01, таблиці поза маршрутизацією) — січень.
                if (annualWritable is not null && annualSheets!.Contains(sheetKey))
                {
                    var annualColumns = NonMonthRoutedColumns(target, instances, tables);
                    var allowed = cells.Values
                        .Where(c => annualColumns.Contains(c.Address.ColumnDefId)
                            ? annualWritable.Contains(sheetKey)
                            : writable.Contains(sheetKey))
                        .ToList();
                    if (allowed.Count > 0)
                    {
                        batch.Add((target, allowed));
                    }

                    continue;
                }

                if (!writable.Contains(sheetKey))
                {
                    continue;
                }

                batch.Add((target, [.. cells.Values]));
            }

            applied = batch.Sum(item => item.Records.Count);

            if (changedColumns is not null)
            {
                var tableOfInstance = instances.ToDictionary(t => t.TableInstanceId, t => t.TableDefId);
                foreach (var (target, records) in batch)
                {
                    if (!tableOfInstance.TryGetValue(target, out var changedTable))
                    {
                        continue;
                    }

                    foreach (var record in records)
                    {
                        changedColumns.Add((changedTable, record.Address.ColumnDefId));
                    }
                }
            }

            if (batch.Count > 0)
            {
                await WriteBatchAsync(
                    instance.DocumentId, batch, rowKeyByRowIdByInstance, isLateEdit, now, token, overridesActive)
                    .ConfigureAwait(false);
            }

            // ✎ C-1 (D-475): стан помилок і події журналу — лише для аркушів, у які прогін пише (поданий/затверджений
            // аркуш не чіпається, як і його значення), тим самим комітом, що й значення.
            // ✎ B103: стан помилки — за тим самим вибором стану аркуша на комірку, що й значення (B60(а)): не-місячна
            // колонка маршрутизованої таблиці на аркуші відкритого якоря — агрегат року, решта — період прогону.
            if (!errorDelta.IsEmpty)
            {
                bool ErrorWritable(FormulaErrorRow row)
                {
                    if (!sheetOfInstance.TryGetValue(row.TableInstanceId, out var sheetKey))
                    {
                        return false;
                    }

                    return annualWritable is not null
                           && annualSheets!.Contains(sheetKey)
                           && NonMonthRoutedColumns(row.TableInstanceId, instances, tables).Contains(row.ColumnDefId)
                        ? annualWritable.Contains(sheetKey)
                        : writable.Contains(sheetKey);
                }

                await ApplyFormulaErrorsAsync(errorDelta, instance.DocumentId, stored, ErrorWritable, now, token)
                    .ConfigureAwait(false);
            }

            await uow.SaveChangesAsync(token).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        return applied;
    }

    /// <summary>✎ C-1 (далі C-5, C-11): що завантажувач періодів бере й віддає понад значення комірок.</summary>
    private sealed class PeriodLoadExtras
    {
        /// <summary>C-1: екземпляри ІНШИХ періодів (зсув ≠ 0) з тієї самої версії, що мають стан помилок формул.</summary>
        public List<(int Offset, long TableInstanceId)> ErrorInstances { get; } = [];

        /// <summary>✎ C-5: дефолти колонок версії прогону — значення комірок, яких немає (поточний і сусідні періоди).</summary>
        public ColumnDefaultSet Defaults { get; init; } = ColumnDefaultSet.Empty;

        /// <summary>
        /// ✎ C-11: рядки сусідніх періодів (зсув, таблиця версії прогону) — джерело рядків предиката зі зсувом.
        /// Посилання на вже прочитані словники, без копій; каскад Rollup доповнює той самий об'єкт.
        /// </summary>
        public Dictionary<(int PeriodOffset, int TableDefId), IReadOnlyDictionary<string, long>> OtherPeriodRows { get; } = [];
    }

    /// <summary>
    /// ✎ C-1: засіває стан помилок сусідніх періодів, які прочитав завантажувач (їхні комірки з помилкою — «недовірені»
    /// для читачів цього прогону). Один запит на зсув і лише коли такі екземпляри є.
    /// </summary>
    private async Task SeedNeighbourErrorsAsync(
        FormulaErrorTracker errors, PeriodLoadExtras extras, PeriodKey periodKey, CancellationToken ct)
    {
        if (formulaErrors is not null)
        {
            foreach (var group in extras.ErrorInstances.GroupBy(e => e.Offset))
            {
                var neighbour = await formulaErrors.ReadInstancesAsync(
                        [.. group.Select(e => e.TableInstanceId).Distinct()], new PeriodKey(periodKey.Value + group.Key), ct)
                    .ConfigureAwait(false);
                errors.Seed(neighbour, group.Key);
            }
        }

        extras.ErrorInstances.Clear();
    }

    /// <summary>
    /// ✎ C-1 (D-475): пише різницю стану помилок формул і події журналу <c>CalcError</c>/<c>CalcErrorResolved</c> —
    /// лише для екземплярів аркушів, у які прогін пише. Значення комірки подія не змінює (<c>OldValue = NewValue</c>).
    /// </summary>
    private async Task ApplyFormulaErrorsAsync(
        FormulaErrorDelta delta, long documentId, Dictionary<CellKey, CellValueData> stored,
        Func<FormulaErrorRow, bool> writable, DateTime now, CancellationToken ct)
    {
        await formulaErrors!.ApplyAsync(
                [.. delta.Upserts.Where(writable)],
                [.. delta.Deletes.Where(writable).Select(d => d.Address)],
                ct)
            .ConfigureAwait(false);

        var events = delta.Events.Where(e => writable(e.Row)).ToList();
        if (events.Count == 0)
        {
            return;
        }

        await audit.WriteCellChangesAsync(
                [.. events.Select(e =>
                {
                    var current = stored.TryGetValue(new CellKey(0, e.Row.TableDefId, e.Row.RowKey, e.Row.ColumnDefId), out var cell)
                        ? Describe(cell)
                        : null;
                    return new CellChangeRecord(
                        now, e.Row.Address, documentId, e.Row.RowKey,
                        OldValue: current, NewValue: current, SystemUserId,
                        Origin: e.Resolved ? Documents.CellChangeOrigins.CalcErrorResolved : Documents.CellChangeOrigins.CalcError,
                        IsLateEdit: false, CorrelationId: null,
                        Reason: FormulaErrorAuditReason.Of(e.Row, e.Resolved));
                })],
                ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Пише обчислені комірки ВСІХ екземплярів документо-періоду одним пакетом:
    /// одне читання старих значень, один <c>MERGE</c>, один запис аудиту.
    /// </summary>
    /// <param name="documentId">Документ прогону.</param>
    /// <param name="batch">Екземпляри, у які писати можна, з їхніми комірками — у порядку запису.</param>
    /// <param name="rowKeyByRowIdByInstance">Ключ рядка за його ідентифікатором — для аудиту.</param>
    /// <param name="isLateEdit">Зміна в <c>Grace</c> (<c>D-70</c>).</param>
    /// <param name="now">Мить зміни для аудиту.</param>
    /// <param name="lockOverrides">✎ D-461: блокувати й перечитати позначки ручних значень перед записом (лише коли вони можливі).</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// ⚠ Виконується ВСЕРЕДИНІ транзакції запису, під спільними блокуваннями
    /// аркушів (<see cref="EnterSheetsAsync"/>): сховище й аудит приєднуються
    /// до ambient-транзакції (Q-243), тож значення й аудит, як і доти, лягають
    /// одним комітом або не лягають зовсім.
    ///
    /// ⚠ Пакет рівносильний поштучному запису за побудовою: адреси різних
    /// екземплярів не перетинаються (рядок належить рівно одному екземпляру),
    /// тож запис одного не змінює «старих» значень іншого, і прочитати їх усі
    /// до запису — те саме, що читати перед кожним
    /// (<c>RecalculationBatchWriteTests</c>, побайтне порівняння з поштучним).
    /// </remarks>
    private async Task WriteBatchAsync(
        long documentId,
        IReadOnlyList<(long Target, IReadOnlyList<CellRecord> Records)> batch,
        Dictionary<long, Dictionary<long, string>> rowKeyByRowIdByInstance,
        bool isLateEdit,
        DateTime now,
        CancellationToken ct,
        bool lockOverrides = false)
    {
        // ⚠ Старі значення читаються ДО запису — після `ApplyBatchAsync` їх уже
        // немає ніде, а саме вони й становлять половину запису аудиту
        // (той самий порядок, що в `PatchCellsHandler.ReadPreviousValuesAsync`).
        // ✎ D-461: ручне значення, поставлене ПАРАЛЕЛЬНО з цим прогоном (після читання позначок на старті), не
        // перезаписується: позначки адрес запису блокуються до коміту й перечитуються — та, що вже є, знімає адресу з
        // пакета; правка, що ставить нову, чекає на цей коміт і пише ручне значення після. Одна команда на пакет.
        if (lockOverrides && formulaOverrides is not null)
        {
            var locked = await formulaOverrides
                .LockKeysAsync([.. batch.SelectMany(item => item.Records.Select(r => r.Address))], ct)
                .ConfigureAwait(false);
            if (locked.Count > 0)
            {
                var unlocked = batch
                    .Select(item => (item.Target, Records: (IReadOnlyList<CellRecord>)[.. item.Records.Where(r => !locked.Contains(r.Address))]))
                    .Where(item => item.Records.Count > 0)
                    .ToArray();
                if (unlocked.Length == 0)
                {
                    return;
                }

                batch = unlocked;
            }
        }

        var previous = await cellStore
            .ReadCellsAsync([.. batch.SelectMany(item => item.Records.Select(r => r.Address))], ct)
            .ConfigureAwait(false);

        await cellStore.ApplyBatchAsync(
            [.. batch.Select(item => new CellChangeSet(
                        item.Target,
                        item.Records,
                        Deletes: [],

                        // ⛔ `D14-07` (директива №14 частина 3, `DAT-02` п. 3).
                        // Доти сюди йшли ВСІ рядки батчу, і `TouchRowsAsync`
                        // піднімав `RowVersion` кожному — включно з рядками,
                        // де перерахунок нічого не змінив. Наслідок бачив не
                        // перерахунок, а людина: її сітка тримала версії,
                        // видані попереднім `PATCH`, фонова задача підміняла
                        // їх усі, і наступне автозбереження діставало
                        // `ECR-CELL-0409` на порожньому місці.
                        //
                        // ⚠ `RowVersion` стереже ВВЕДЕНЕ: два оператори за
                        // одну комірку. Обчислена колонка має тип `Formula`,
                        // писати в неї руками сервер відмовляє
                        // (`ECR-CELL-4221`), тож конкурувати за неї нема кому,
                        // і версія рядка про неї нічого не каже. Перевірено
                        // перед зміною: `RowVersion` не входить у жоден
                        // `ETag` (у зрізу таблиці `ETag` немає взагалі), а
                        // єдиний його споживач — `baseVersion` оптимістичного
                        // блокування (`PatchCellsHandler`, `useCellPatch.ts`).
                        // Гонку двох перерахунків одного документа закриває
                        // `MI-02` (черга з виключністю на ціль), а не версія
                        // рядка: вона її й не закривала — обидва прогони
                        // однаково пишуть без `ExpectedRowVersions`.
                        TouchedRowIds: [],
                        ChangedByUserId: SystemUserId,
                        isLateEdit))],
            ct).ConfigureAwait(false);

        // ⛔ `DAT-02` п. 2: аудит пишеться рівно по `Records`, тобто по тому,
        // що ПІШЛО в `upserts`. Фільтр незміненого стоїть в `Evaluate` — до
        // того, як комірка потрапить у цей список, — саме тому, щоб журнал і
        // сховище не могли розійтися: другого переліку, який довелося б
        // тримати в тому самому стані, тут немає.
        await audit.WriteCellChangesAsync(
            [.. batch.SelectMany(item =>
            {
                var rowKeyByRowId = rowKeyByRowIdByInstance.TryGetValue(item.Target, out var found)
                    ? found
                    : new Dictionary<long, string>();

                return item.Records.Select(r => new CellChangeRecord(
                    now, r.Address, documentId,
                    RowKey: rowKeyByRowId.GetValueOrDefault(r.Address.TableRowId, string.Empty),
                    OldValue: Was(previous, r.Address),
                    NewValue: Describe(r.Value),
                    SystemUserId,
                    Origin: "Recalculation",
                    isLateEdit,
                    CorrelationId: null));
            })],
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Спільні блокування аркушів, у які пише прогін, — у СТАБІЛЬНОМУ порядку;
    /// повертає аркуші, у які писати можна.
    /// </summary>
    /// <param name="documentId">Документ прогону.</param>
    /// <param name="periodKey">Період прогону.</param>
    /// <param name="sheets">Аркуші екземплярів (з копією; <c>null</c> — оригінал), у які прогін пише (з повторами).</param>
    /// <param name="heldSheet">
    /// Аркуш (з копією), виняткове блокування якого вже тримає викликач (подання): не
    /// блокується вдруге і вважається придатним до запису.
    /// </param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// ⛔ Що було. Перерахунок читав дані й писав <c>doc.CellValue</c> без
    /// жодного блокування і без перевірки стану аркуша. Подання, що
    /// перетиналося з ним, фіксувало у <c>calc.SubmissionSnapshot</c> старе
    /// обчислене число, а перерахунок одразу після того писав нове в живу комірку
    /// вже ПОДАНОГО аркуша — подана форма й дані розходились мовчки
    /// (<c>SubmitRecalculationRaceTests</c>, обидва порядки). Той самий дефект і
    /// без гонки: каскадна задача після правки (<c>FormulaRecalculationJob</c>)
    /// стану аркуша не перевіряла зовсім, тож правка → «Подати» → задача з черги
    /// переписувала подане число.
    ///
    /// ⚠ Порядок блокувань — за ключем «документ × аркуш × період», тобто
    /// всередині одного прогону (документ і період сталі) — за
    /// <c>SheetDefId</c> за зростанням. Прогін бере кілька СПІЛЬНИХ блокувань в
    /// одній транзакції; спільні між собою сумісні, але черга SQL Server FIFO:
    /// спільний запит стає за винятковим (подання), що вже чекає. Два
    /// багатоаркушеві власники спільних блокувань у різному порядку плюс два
    /// подання в черзі дають цикл; однаковий порядок його виключає. Подання
    /// тримає ОДИН ключ і на перерахунок не чекає ні на що інше, правка — теж
    /// один ключ.
    ///
    /// ⚠ Не взято вчасно — <c>ECR-DOC-4091</c> з порту: задача падає з причиною,
    /// а не пише повз блокування.
    ///
    /// ⛔ X6-02: чекати (до 30 с) дозволено лише ПЕРШИЙ аркуш, поки прогін ще не
    /// тримає жодного. Наступні — без черги (<see cref="ISheetEditGate.EnterEditNoWaitAsync"/>):
    /// черга FIFO, і прогін, що чекав аркуш N, тримаючи S на аркушах 1…N-1, ставив
    /// за собою подання тих аркушів, а за ними — автозбереження всіх, хто їх правив
    /// (аж до 409 «аркуш подається»). Відмова відкочує транзакцію запису й звільняє
    /// все взяте; фонова задача повторює прогін пізніше (<c>FormulaRecalculationJob</c>
    /// відкладає себе, <c>RecalculationJob</c> — теж відкладає себе, Y1-02).
    /// </remarks>
    /// <param name="approverEditSheets">
    /// ✎ D-442 (борг AN-129): аркуші, у яких правку зробив погоджувач поточного кроку. Такий аркуш пишеться,
    /// якщо під спільним блокуванням він і досі <c>Submitted</c>: формули оновлюються одразу, як у чернетці,
    /// а не лише на затвердженні. Хто погоджувач, перевірено під блокуванням у момент правки
    /// (<c>PatchCellsHandler.EnsureSheetStillEditableAsync</c>); прогін лише виводить обчислені числа з уже
    /// записаних входів. <c>Approved</c> не пишеться ніколи (затвердження бере ВИНЯТКОВЕ блокування й
    /// перераховує аркуш саме, <c>ApprovalSnapshotRefresh</c>); інші подані аркуші — так само пропускаються.
    /// </param>
    /// <param name="writeSubmitted">✎ UAT-recalc-ux: писати й ПОДАНІ аркуші (ручний перерахунок, <c>RecalculationJob.WritesSubmitted</c>); затверджені — ніколи.</param>
    /// <param name="annualPeriods">
    /// ✎ LAND-YEAR B24: місяці року — прогін закритого якоря пише не-місячне, тож стан аркуша — АГРЕГАТ за місяцями
    /// (<see cref="Security.AnnualPeriodRule.AggregateStatus"/>, те саме правило, що в <c>PatchCellsHandler</c> для
    /// не-місячних колонок), під спільними блокуваннями ВСІХ місяців аркуша за зростанням ключа (як у PATCH);
    /// <c>null</c> — стан аркуша періоду прогону, як доти.
    /// </param>
    /// <param name="openAnchor">
    /// ✎ LAND-YEAR B60: прогін ВІДКРИТОГО якоря річної розкладки — місяці року, аркуші з не-місячними цілями і куди
    /// скласти ті з них, що пишуться за АГРЕГАТОМ (<see cref="Security.AnnualPeriodRule.AggregateStatus"/>, як PATCH);
    /// повернений набір для цих аркушів — за станом самого періоду прогону (місячні цілі). <c>null</c> — як доти.
    /// </param>
    private async Task<IReadOnlySet<(int SheetDefId, long? CopyId)>> EnterSheetsAsync(
        long documentId, PeriodKey periodKey, IEnumerable<(int SheetDefId, long? CopyId)> sheets,
        (int SheetDefId, long? CopyId)? heldSheet, CancellationToken ct,
        IReadOnlySet<int>? approverEditSheets = null, bool writeSubmitted = false,
        IReadOnlyList<int>? annualPeriods = null,
        (IReadOnlyList<int> Months, IReadOnlySet<(int SheetDefId, long? CopyId)> Sheets,
            HashSet<(int SheetDefId, long? CopyId)> Writable)? openAnchor = null)
    {
        var writable = new HashSet<(int SheetDefId, long? CopyId)>();
        var holdsAny = false;

        bool Writable(Domain.Enums.DocumentStatus status, int sheetDefId)
            => status is not (Domain.Enums.DocumentStatus.Submitted or Domain.Enums.DocumentStatus.Approved)
               || (status == Domain.Enums.DocumentStatus.Submitted
                   && (writeSubmitted || approverEditSheets?.Contains(sheetDefId) == true));

        // RS: ключ блокування — (аркуш, копія); копії йдуть після оригіналу свого аркуша за зростанням Id.
        foreach (var sheet in sheets.Distinct().OrderBy(s => s.SheetDefId).ThenBy(s => s.CopyId ?? 0))
        {
            var (sheetDefId, copyId) = sheet;

            // ⛔ Аркуш, чиє ВИНЯТКОВЕ блокування вже тримає викликач (подання),
            // повторно не блокується: це той самий власник, і стан аркуша під
            // цим блокуванням змінює сам викликач.
            if (heldSheet is { } held && held == sheet)
            {
                writable.Add(sheet);
                continue;
            }

            Domain.Enums.DocumentStatus status;
            if (annualPeriods is { Count: > 0 } && copyId is null)
            {
                // ⛔ X6-02: чекати — лише перше блокування прогону; далі без черги (відмова відкочує запис).
                var statuses = new List<Domain.Enums.DocumentStatus>(annualPeriods.Count);
                foreach (var month in annualPeriods.Distinct().Order())
                {
                    statuses.Add(holdsAny
                        ? await sheetGate.EnterEditNoWaitAsync(documentId, sheetDefId, new PeriodKey(month), ct).ConfigureAwait(false)
                        : await sheetGate.EnterEditAsync(documentId, sheetDefId, new PeriodKey(month), ct).ConfigureAwait(false));
                    holdsAny = true;
                }

                status = Security.AnnualPeriodRule.AggregateStatus(statuses);
            }
            else if (openAnchor is { } anchorYear && copyId is null && anchorYear.Sheets.Contains(sheet))
            {
                // ✎ LAND-YEAR B60: відкритий якір — блокування всіх місяців аркуша за зростанням (як PATCH не-місячної
                // колонки); стан для місячних цілей — сам період прогону, для не-місячних — агрегат.
                var statuses = new List<Domain.Enums.DocumentStatus>(anchorYear.Months.Count);
                status = Domain.Enums.DocumentStatus.Draft;
                foreach (var month in anchorYear.Months.Append(periodKey.Value).Distinct().Order())
                {
                    var monthStatus = holdsAny
                        ? await sheetGate.EnterEditNoWaitAsync(documentId, sheetDefId, new PeriodKey(month), ct).ConfigureAwait(false)
                        : await sheetGate.EnterEditAsync(documentId, sheetDefId, new PeriodKey(month), ct).ConfigureAwait(false);
                    holdsAny = true;
                    statuses.Add(monthStatus);
                    if (month == periodKey.Value)
                    {
                        status = monthStatus;
                    }
                }

                if (Writable(Security.AnnualPeriodRule.AggregateStatus(statuses), sheetDefId))
                {
                    anchorYear.Writable.Add(sheet);
                }
            }
            else
            {
                status = (holdsAny, copyId) switch
                {
                    (true, null) => await sheetGate.EnterEditNoWaitAsync(documentId, sheetDefId, periodKey, ct).ConfigureAwait(false),
                    (false, null) => await sheetGate.EnterEditAsync(documentId, sheetDefId, periodKey, ct).ConfigureAwait(false),
                    (true, { } c) => await sheetGate.EnterCopyEditNoWaitAsync(documentId, sheetDefId, periodKey, c, ct).ConfigureAwait(false),
                    (false, { } c) => await sheetGate.EnterCopyEditAsync(documentId, sheetDefId, periodKey, c, ct).ConfigureAwait(false),
                };
            }

            holdsAny = true;

            // ✎ UAT-recalc-ux (слово людини 07.11: «тільки погоджені не мають перераховуватися»): ЯВНИЙ повний
            // перерахунок (`RecalculationJob`, `writeSubmitted`) пише й поданий аркуш; затверджений — ніколи.
            if (Writable(status, sheetDefId))
            {
                writable.Add(sheet);
            }
        }

        return writable;
    }

    /// <summary>Обчислює одну формулу в усіх її цільових комірках.</summary>
    /// <remarks>
    /// ⚠ Результат ОДРАЗУ лягає у значення контексту: наступна формула в
    /// порядку обчислення читає вже нове число. Без цього каскад дав би
    /// правильний перший рівень і застарілий другий — те саме, що й
    /// відсутність каскаду, але з виглядом працездатності.
    /// </remarks>
    private void Evaluate(
        Domain.Entities.Configuration.TableDef table,
        Domain.Entities.Configuration.FormulaDef formula,
        Ecr.Expressions.Parsing.ParsedExpression parsed,
        IReadOnlyDictionary<string, long> rowIds,
        IReadOnlySet<long>? rowFilter,
        PeriodKey periodKey,
        SliceEvaluationContext context,
        Dictionary<CellKey, Ecr.Expressions.Evaluation.ExpressionValue> values,
        Dictionary<CellKey, CellValueData> stored,
        Dictionary<CellAddress, CellRecord> upserts,
        FormulaErrorTracker errors,
        IReadOnlyDictionary<int, Domain.Entities.Configuration.TableDef> tablesForInputs,
        HashSet<int>? foreignColumns = null,
        IReadOnlySet<CellAddress>? overridden = null,
        HashSet<int>? ownedElsewhere = null)
    {
        // ФВ-0.2: діапазони цієї формули — за ключами, зафіксованими при публікації.
        context.UseFormula(formula, parsed.Root);

        foreach (var (rowKey, columnDefId) in Targets(table, formula, rowIds, rowFilter))
        {
            // ✎ LAND-YEAR L1: обчислювана колонка з чужим власником (лише YearWithMonthColumns) — її пише прогін
            // власника; тут ні обчислення, ні запису (контекст тримає значення власника зі зрізу).
            if (ownedElsewhere is not null && ownedElsewhere.Contains(columnDefId))
            {
                continue;
            }

            if (!rowIds.TryGetValue(rowKey, out var rowId))
            {
                continue;
            }

            // ⛔ Q-17.3: у колонці з чужим власником ВВЕДЕНУ комірку власника (зріз перегляду віддає її як
            // `IsCalculated = false`) перерахунок не перезаписує — інакше два екземпляри писали б одне й те саме
            // значення двома шляхами. Обчислена комірка тієї ж колонки (формула рівня комірки в рядку підсумків)
            // лишається справою екземпляра перегляду: власник бачить лише ВВЕДЕНІ комірки, тож пропуск лишив би
            // дірку у формулах, що читають весь рядок.
            // ✎ D-461: ОДНЕ правило «введене людиною в ціль формули не перезаписується» на два випадки — Q-17.3
            // (введена комірка власника в колонці з чужим власником) і ручне значення замість формули
            // (`doc.CellFormulaOverride`). Перевірка — O(1) за вже прочитаними наборами, без запитів на комірку.
            var targetKey = new CellKey(0, table.Id, rowKey, columnDefId);
            var targetAddress = new CellAddress(periodKey, rowId, columnDefId);

            if (IsManualTarget(table.Id, rowKey, rowId, columnDefId, periodKey, stored, foreignColumns, overridden))
            {
                // ✎ C-1: ручне значення — не результат формули; стан помилки цієї комірки (якщо був) знімається.
                errors.Blocked.Remove(targetKey);
                errors.Ok(targetKey, targetAddress, table.Id, rowKey, formula.Id);
                continue;
            }

            context.CurrentTableDefId = table.Id;
            context.CurrentRowKey = rowKey;
            context.CurrentColumnDefId = columnDefId;

            // ✎ C-1: ціль рахується заново — її попередній стан (помилка чи «не оновлено») більше не діє. Порядок
            // обчислення топологічний, тож джерела цієї цілі вже переоцінені в цьому прогоні.
            errors.Blocked.Remove(targetKey);
            context.BeginCell();

            var result = formulaEngine.Evaluate(parsed, context);

            // ✎ C-1 (рішення PO 11.11, D-475): формула прочитала комірку з помилкою (чи «не оновлену через помилку») —
            // ціль НЕ перераховується від неї: лишається останнє збережене значення, як після транзакції, що не відбулася.
            if (context.BlockedRead is { } source)
            {
                upserts.Remove(targetAddress);
                errors.Block(targetKey, targetAddress, table.Id, rowKey, formula.Id, source);
                continue;
            }

            // ⛔ Помилка обчислення НЕ записується як значення. `#REF` у
            // комірці — це не число, і покласти його в `ValueNumeric`
            // означало б або нуль, або текст у числовій колонці.
            // ✎ C-1: і старе число НЕ стирається — «нічого не оновлено»; помилка йде в стан прогону, залежні формули
            // бачать її через `Blocked`. Входи — повторним обчисленням зі збором читань (лише на помилці).
            if (result.Value.IsError)
            {
                upserts.Remove(targetAddress);
                context.BeginTrace();
                _ = formulaEngine.Evaluate(parsed, context);
                errors.Fail(
                    targetKey, targetAddress, table.Id, rowKey, formula.Id,
                    result.Value.ErrorCode ?? Ecr.Expressions.ExpressionErrors.BadValue,
                    FormulaErrorInputs.Format(context.EndTrace(), tablesForInputs));
                continue;
            }

            // ✎ C-1: формула порахувалася — стан помилки комірки (якщо був) знімається, хоч би що далі (Null, тип).
            errors.Ok(targetKey, targetAddress, table.Id, rowKey, formula.Id);

            // ⛔ ПРИЙМАЛЬНА RC14 P2-D: `Null` (вхід стерто: `NULL * x = Null`) раніше
            // теж пропускався — і обчислене значення, що лежало в комірці,
            // лишалося жити (EMIS 75 після очищення FUEL). Тепер раніше
            // ОБЧИСЛЕНА (`IsCalculated`) комірка очищається явною порожнечею;
            // уведене людиною (`IsCalculated = false`) не чіпається, а вже
            // порожня обчислена не пишеться повторно (DAT-02).
            if (result.Value.IsNull)
            {
                var nullKey = new CellKey(0, table.Id, rowKey, columnDefId);
                if (stored.GetValueOrDefault(nullKey) is { IsCalculated: true, IsEmpty: false })
                {
                    values[nullKey] = result.Value;
                    upserts[new CellAddress(periodKey, rowId, columnDefId)] = new CellRecord(
                        new CellAddress(periodKey, rowId, columnDefId),
                        table.Id,
                        new CellValueData { IsEmpty = true, IsCalculated = true });
                }

                continue;
            }

            // ⛔ C1-02 (аудит 2026-10-09c). Число формули — до масштабу СХОВИЩА
            // (`decimal(34,16)`) ДО порівняння, запису й контексту. Ділення дає
            // 28 знаків, а збережене читається з 16: `x / 3` інакше «змінювалось»
            // на кожному прогоні (MERGE, аудит `старе = нове`, піднятий
            // `RowVersion`), а залежна формула цього прогону бачила б інше число,
            // ніж побачить наступного разу з бази.
            var value = AsStored(result.Value);
            var data = ToCellValue(value);
            if (data is null)
            {
                continue;
            }

            // ⛔ V-04: формула рядка без явної колонки розкривається на ВСІ
            // колонки рядка — і до цього писала число в String/Date/Bool/
            // Lookup/Unit (`ValueNumeric = 15` у колонці дати). Колонка, тип
            // якої результату не приймає, пропускається ДО контексту: інакше
            // залежна формула прочитала б із неї число, якого в базі немає.
            if (formula.Scope == Domain.Enums.FormulaScope.Row
                && formula.ColumnDefId is null
                && !FormulaTargetTypes.Accepts(
                    table.Columns.FirstOrDefault(c => c.Id == columnDefId)?.DataType, result.Value.Type))
            {
                continue;
            }

            var key = new CellKey(0, table.Id, rowKey, columnDefId);

            // ⚠ Значення контексту оновлюється ЗАВЖДИ, навіть коли запису не
            // буде: наступна формула читає його зі спільного словника, і
            // «нічого не змінилось» для сховища не означає «нічого не класти»
            // для каскаду. Різниця тут нульова за побудовою (значення те
            // саме), але залежність порядку — ні, і покласти цей рядок після
            // `continue` означало б завести її наново.
            values[key] = value;

            // ⛔ Директива №14 частина 3, `DAT-02` п. 1. Незмінене не
            // пишеться. Колонкова формула віддає ціль у КОЖНОМУ рядку
            // (`Targets` нижче), тож без цієї перевірки правка однієї комірки
            // давала до 500 рядків `MERGE`, стільки ж рядків аудиту, де
            // `старе = нове`, і — до п. 3 — стільки ж піднятих `RowVersion`.
            // Порівняння винесене в `CellValueComparison.AreEqual` і
            // перевірене окремо: саме воно вирішує, що таке «те саме».
            var address = new CellAddress(periodKey, rowId, columnDefId);

            // ⚠ V-03: «те саме, що в базі» від ПІЗНІШОЇ формули знімає й запис
            // ранішої в ту саму комірку — інакше в базу пішло б значення, яке
            // в контексті вже перекрите.
            if (CellValueComparison.AreEqual(stored.GetValueOrDefault(key), data))
            {
                upserts.Remove(address);
                continue;
            }

            upserts[address] = new CellRecord(address, table.Id, data);
        }
    }

    /// <summary>Похідне від версії: «у документах версії є позначки ручних значень».</summary>
    private sealed record OverrideMarksPresence(bool Any);

    /// <summary>
    /// ✎ D-461: чи доводиться прогону дивитися на ручні значення: формула версії дозволяє ручне значення (зі знімка,
    /// без запиту) АБО в документах версії є позначки (EXISTS раз на ревізію знімка — кеш похідних версії).
    /// </summary>
    private async Task<bool> OverridesActiveAsync(
        Domain.Entities.Configuration.TemplateVersionSnapshot snapshot,
        IEnumerable<Domain.Entities.Configuration.FormulaDef> formulas,
        CancellationToken ct)
    {
        if (formulaOverrides is null)
        {
            return false;
        }

        if (formulas.Any(f => f.AllowManualOverride))
        {
            return true;
        }

        var presence = await metadata
            .GetDerivedAsync(
                snapshot.TemplateVersionId,
                "d461-override-marks",
                async token => new OverrideMarksPresence(
                    await formulaOverrides.AnyForVersionAsync(snapshot.TemplateVersionId, token).ConfigureAwait(false)),
                ct,

                // ✎ Аудит 31.10 (F3): «позначок немає» — не довше за вікно ревізії й на опублікованій версії: перша
                // позначка ревізії не піднімає (гонка з вимкненням прапорця), і кешована на години відповідь «немає»
                // ховала б її від перерахунку. Застаріле «є» коштує лише зайвого читання позначок.
                shortLived: p => !p.Any)
            .ConfigureAwait(false);

        return presence.Any;
    }

    /// <summary>
    /// ✎ D-461: ціль формули, яку прогін НЕ перезаписує: ручне значення замість формули (позначка) або (Q-17.3)
    /// введена комірка власника в колонці з чужим власником.
    /// </summary>
    private static bool IsManualTarget(
        int tableDefId,
        string rowKey,
        long rowId,
        int columnDefId,
        PeriodKey periodKey,
        Dictionary<CellKey, CellValueData> stored,
        HashSet<int>? foreignColumns,
        IReadOnlySet<CellAddress>? overridden)
        => (overridden is { Count: > 0 } && overridden.Contains(new CellAddress(periodKey, rowId, columnDefId)))
           || (foreignColumns is not null
               && foreignColumns.Contains(columnDefId)
               && stored.TryGetValue(new CellKey(0, tableDefId, rowKey, columnDefId), out var ownerInput)
               && !ownerInput.IsCalculated);

    /// <summary>
    /// ✎ D-461: до формул, що ЧИТАЮТЬ насіння, додає формули, що його ПИШУТЬ (колонки — на обчислюваній колонці,
    /// комірки), і все, що читає їхній результат. Інакше повернення формули (видалення ручного значення) лишило б
    /// комірку порожньою до наступної правки входу.
    /// </summary>
    /// <remarks>
    /// ⚠ Звичайна правка вводу формулу-«автора» не має (у комірку формули без ручного значення писати не можна), тож
    /// для неї набір не змінюється. Для ручного значення автор рахується, але сама ручна комірка пропускається в
    /// <see cref="Evaluate"/> — дешево й однаково. Рядкова локальність (`CAL-03`) звужує автора до рядків насіння.
    /// Колонка → таблиця — за знімком (O(1)); ключ рядка — лише для комірочних формул тієї колонки.
    /// </remarks>
    private static IReadOnlyList<int> WithSeedWriters(
        IReadOnlyList<int> affected,
        DirtySet dirty,
        RecalculationPlan plan,
        Domain.Entities.Configuration.TemplateVersionSnapshot snapshot,
        Dictionary<int, (Domain.Entities.Configuration.TableDef Table, Domain.Entities.Configuration.FormulaDef Formula)> formulas,
        Dictionary<int, IReadOnlyDictionary<string, long>> rowIdsByTable)
    {
        HashSet<int>? writers = null;
        Dictionary<int, List<(int Id, Domain.Entities.Configuration.FormulaDef Formula, Domain.Entities.Configuration.TableDef Table)>>? byColumn = null;
        Dictionary<int, Dictionary<long, string>>? keyByRowId = null;

        foreach (var seed in dirty.Seeds)
        {
            if (!snapshot.ColumnsById.TryGetValue(seed.ColumnDefId, out var column))
            {
                continue;
            }

            byColumn ??= formulas
                .Where(p => p.Value.Formula.ColumnDefId is not null
                            && p.Value.Formula.Scope is Domain.Enums.FormulaScope.Column or Domain.Enums.FormulaScope.Cell)
                .GroupBy(p => p.Value.Formula.ColumnDefId!.Value)
                .ToDictionary(g => g.Key, g => g.Select(p => (p.Key, p.Value.Formula, p.Value.Table)).ToList());

            if (!byColumn.TryGetValue(column.Id, out var candidates))
            {
                continue;
            }

            foreach (var (id, formula, table) in candidates)
            {
                if (plan.IsSnapshot(id))
                {
                    continue;
                }

                if (formula.Scope == Domain.Enums.FormulaScope.Column)
                {
                    if (column.IsComputed)
                    {
                        (writers ??= []).Add(id);
                    }

                    continue;
                }

                // Комірочна: лише якщо рядок насіння — рядок формули.
                keyByRowId ??= [];
                if (!keyByRowId.TryGetValue(table.Id, out var keys))
                {
                    keys = rowIdsByTable.TryGetValue(table.Id, out var byKey)
                        ? byKey.ToDictionary(p => p.Value, p => p.Key)
                        : [];
                    keyByRowId[table.Id] = keys;
                }

                if (keys.TryGetValue(seed.TableRowId, out var seedKey)
                    && formula.RowDefId is { } rowDefId
                    && table.Rows.FirstOrDefault(r => r.Id == rowDefId)?.RowKeyValue == seedKey)
                {
                    (writers ??= []).Add(id);
                }
            }
        }

        if (writers is null)
        {
            return affected;
        }

        var all = new HashSet<int>(affected);
        var queue = new Queue<int>(writers.Where(all.Add));
        while (queue.Count > 0)
        {
            foreach (var dependent in plan.DependentsOfFormula(queue.Dequeue()))
            {
                if (!plan.IsSnapshot(dependent) && !plan.IsCrossSheet(dependent) && all.Add(dependent))
                {
                    queue.Enqueue(dependent);
                }
            }
        }

        var ordered = all.ToList();
        ordered.Sort((a, b) => plan.EvaluationOrder(a).CompareTo(plan.EvaluationOrder(b)));
        return ordered;
    }

    /// <summary>
    /// Q-17.3: колонки таблиць прогону, чиї введені комірки належать екземпляру ІНШОГО періоду. <c>null</c> — немає
    /// жодної (прапор вимкнено, проєкт не місячний, таблиці поза маршрутизацією, період поза календарем).
    /// </summary>
    /// <returns>
    /// Усі колонки з чужим власником і (✎ LAND-YEAR L1) їхня підмножина ОБЧИСЛЮВАНИХ — лише у версії YearWithMonthColumns;
    /// для решти версій друга мапа <c>null</c>.
    /// </returns>
    private async Task<(Dictionary<int, HashSet<int>>? Foreign, Dictionary<int, HashSet<int>>? OwnedElsewhere)>
        ForeignColumnsByTableAsync(
            long documentId,
            IReadOnlyList<TableInstanceRef> instances,
            Dictionary<int, Domain.Entities.Configuration.TableDef> tables,
            PeriodKey periodKey,
            CancellationToken ct)
    {
        if (monthRouter is not { Enabled: true }
            || await monthRouter.GetDocumentAsync(documentId, ct).ConfigureAwait(false) is not { } routed)
        {
            return (null, null);
        }

        Dictionary<int, HashSet<int>>? result = null;
        Dictionary<int, HashSet<int>>? computed = null;
        foreach (var instance in instances)
        {
            if (!tables.TryGetValue(instance.TableDefId, out var table) || !monthRouter.AppliesToTable(table))
            {
                continue;
            }

            var foreign = await monthRouter.ForeignColumnsAsync(routed, table, periodKey, ct).ConfigureAwait(false);
            if (foreign.Count > 0)
            {
                (result ??= [])[table.Id] = [.. foreign.Select(f => f.Column.Id)];
            }

            if (foreign.Any(f => f.Column.IsComputed))
            {
                (computed ??= [])[table.Id] = [.. foreign.Where(f => f.Column.IsComputed).Select(f => f.Column.Id)];
            }
        }

        return (result, computed);
    }

    /// <summary>
    /// ✎ LAND-YEAR B60: місяці року, якщо період прогону — ЯКІР року документа річної розкладки
    /// (<see cref="IMonthColumnRouter.RoutesComputedVersion"/>); інакше <c>null</c>.
    /// </summary>
    /// <param name="instance">Представник прогону (версія шаблону, документ).</param>
    /// <param name="periodKey">Період прогону.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>Документ і календар року — з кешу роутера (їх уже прочитав <see cref="ForeignColumnsByTableAsync"/>).</remarks>
    private async Task<IReadOnlyList<int>?> OpenAnchorYearAsync(
        TableInstanceRef instance, PeriodKey periodKey, CancellationToken ct)
    {
        if (monthRouter is not { Enabled: true }
            || !monthRouter.RoutesComputedVersion(instance.TemplateVersionId)
            || await monthRouter.GetDocumentAsync(instance.DocumentId, ct).ConfigureAwait(false) is not { } routed
            || await monthRouter.AnchorOfAsync(routed.ProjectId, periodKey.Year, ct).ConfigureAwait(false) != periodKey)
        {
            return null;
        }

        var months = await monthRouter.YearPeriodKeysAsync(routed.ProjectId, periodKey.Year, ct).ConfigureAwait(false);
        return months.Count > 1 ? months : null;
    }

    /// <summary>✎ LAND-YEAR B60: ефективні стани місяців року (з винятком документа) — вхід річного правила «пізньої» правки.</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="months">Ключі місяців року.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>Лише прогін закритого якоря (B24/B58): ≤ 12 запитів на прогін, не гарячий шлях правки.</remarks>
    private async Task<List<Domain.Enums.PeriodState>> YearStatesAsync(
        long documentId, IReadOnlyList<int> months, CancellationToken ct)
    {
        var states = new List<Domain.Enums.PeriodState>(months.Count);
        foreach (var month in months.Distinct().Order())
        {
            if (await periods.FindPeriodStateAsync(documentId, month, ct).ConfigureAwait(false) is { } state)
            {
                states.Add(state);
            }
        }

        return states;
    }

    /// <summary>
    /// ✎ LAND-YEAR B60: аркуші (оригінали) прогону відкритого якоря, у які він пише хоч одну НЕ-місячну колонку
    /// маршрутизованої таблиці — їм потрібен ще й агрегатний стан.
    /// </summary>
    /// <param name="byInstance">Що пише прогін: екземпляр → комірки.</param>
    /// <param name="sheetOfInstance">Аркуш кожного екземпляра запису.</param>
    /// <param name="instances">Екземпляри виду прогону.</param>
    /// <param name="tables">Таблиці знімка.</param>
    /// <param name="errorCells">✎ B103: комірки зі зміною стану помилки формули (<see cref="FormulaErrorDelta.Cells"/>).</param>
    private HashSet<(int SheetDefId, long? CopyId)> AnnualTargetSheets(
        Dictionary<long, Dictionary<CellAddress, CellRecord>> byInstance,
        Dictionary<long, (int SheetDefId, long? SheetCopyId)> sheetOfInstance,
        IReadOnlyList<TableInstanceRef> instances,
        Dictionary<int, Domain.Entities.Configuration.TableDef> tables,
        IEnumerable<(long TableInstanceId, int ColumnDefId)>? errorCells = null)
    {
        var result = new HashSet<(int SheetDefId, long? CopyId)>();
        var written = byInstance
            .Where(pair => pair.Value.Count > 0)
            .SelectMany(pair => pair.Value.Keys.Select(a => (pair.Key, a.ColumnDefId)));

        // ✎ B103: комірки, у яких змінюється лише стан помилки формули, — так само (значення там не пишеться).
        foreach (var group in written.Concat(errorCells ?? []).GroupBy(c => c.Item1))
        {
            if (!sheetOfInstance.TryGetValue(group.Key, out var sheet) || sheet.SheetCopyId is not null)
            {
                continue;
            }

            var annual = NonMonthRoutedColumns(group.Key, instances, tables);
            if (annual.Count > 0 && group.Any(c => annual.Contains(c.Item2)))
            {
                result.Add(sheet);
            }
        }

        return result;
    }

    /// <summary>✎ LAND-YEAR B60: не-місячні колонки таблиці екземпляра, якщо вона маршрутизована; інакше порожньо.</summary>
    /// <param name="tableInstanceId">Екземпляр.</param>
    /// <param name="instances">Екземпляри виду прогону.</param>
    /// <param name="tables">Таблиці знімка.</param>
    private HashSet<int> NonMonthRoutedColumns(
        long tableInstanceId,
        IReadOnlyList<TableInstanceRef> instances,
        Dictionary<int, Domain.Entities.Configuration.TableDef> tables)
    {
        var tableDefId = instances.FirstOrDefault(i => i.TableInstanceId == tableInstanceId)?.TableDefId;
        return tableDefId is { } id
               && tables.TryGetValue(id, out var table)
               && monthRouter is { Enabled: true }
               && monthRouter.AppliesToTable(table)
            ? [.. table.Columns.Where(c => !c.IsDeleted && !c.IsMonthColumn).Select(c => c.Id)]
            : [];
    }

    /// <summary>
    /// ✎ LAND-YEAR B24: колонки, яких НЕ пише прогін закритого якоря під агрегатом року: у маршрутизованій таблиці —
    /// місячні (їх власник — місяць, і для якоря це закритий січень) плюс колонки інших власників (L1); у таблиці поза
    /// маршрутизацією (власник — сам закритий місяць) — усі.
    /// </summary>
    private Dictionary<int, HashSet<int>> AnnualOnlyBlocked(
        Dictionary<int, HashSet<int>>? ownedElsewhere,
        IReadOnlyList<TableInstanceRef> instances,
        Dictionary<int, Domain.Entities.Configuration.TableDef> tables)
    {
        var blocked = new Dictionary<int, HashSet<int>>();
        foreach (var tableDefId in instances.Select(i => i.TableDefId).Distinct())
        {
            if (!tables.TryGetValue(tableDefId, out var table))
            {
                continue;
            }

            var routed = monthRouter is { Enabled: true } && monthRouter.AppliesToTable(table);
            var set = table.Columns.Where(c => !routed || c.IsMonthColumn).Select(c => c.Id).ToHashSet();
            if (ownedElsewhere is not null && ownedElsewhere.TryGetValue(tableDefId, out var elsewhere))
            {
                set.UnionWith(elsewhere);
            }

            blocked[tableDefId] = set;
        }

        return blocked;
    }

    /// <summary>Комірки, які обчислює формула.</summary>
    /// <remarks>
    /// ⚠ Область формули визначає ціль однозначно (<c>CK_Formula_Scope</c>):
    /// колонкова рахує свою колонку в кожному рядку, рядкова — свій рядок у
    /// кожній колонці, комірочна — рівно одну комірку.
    ///
    /// ⛔ <paramref name="rowFilter"/> — директива №14 частина 3, <c>CAL-03</c>.
    /// Звужується ЛИШЕ колонкова область: саме вона віддавала кожен
    /// <c>rowIds.Keys</c>, тобто рівно стільки обчислень, скільки рядків у
    /// таблиці. Рядкова й комірочна області й так дають по одному рядку, і
    /// фільтрувати їх означало б додати шлях, яким формула може НЕ порахуватися,
    /// нічого не вигравши.
    /// </remarks>
    /// <param name="table">Таблиця формули.</param>
    /// <param name="formula">Формула.</param>
    /// <param name="rowIds">Ключ рядка → ідентифікатор рядка в екземплярі.</param>
    /// <param name="rowFilter">
    /// Рядки, які треба порахувати; <c>null</c> — усі (повний прогін або
    /// формула, яку класифікатор не визнав рядково-локальною).
    /// </param>
    private static IEnumerable<(string RowKey, int ColumnDefId)> Targets(
        Domain.Entities.Configuration.TableDef table,
        Domain.Entities.Configuration.FormulaDef formula,
        IReadOnlyDictionary<string, long> rowIds,
        IReadOnlySet<long>? rowFilter)
    {
        var rowKey = FormulaOutputs.RowKeyOf(table, formula);

        switch (formula.Scope)
        {
            case Domain.Enums.FormulaScope.Column when formula.ColumnDefId is { } columnId:
                foreach (var (key, rowId) in rowIds)
                {
                    if (rowFilter is not null && !rowFilter.Contains(rowId))
                    {
                        continue;
                    }

                    yield return (key, columnId);
                }

                break;

            case Domain.Enums.FormulaScope.Row when rowKey is not null:
                if (formula.ColumnDefId is { } single)
                {
                    yield return (rowKey, single);
                    break;
                }

                foreach (var column in table.Columns.Where(c => !c.IsDeleted))
                {
                    yield return (rowKey, column.Id);
                }

                break;

            case Domain.Enums.FormulaScope.Cell
                when rowKey is not null && formula.ColumnDefId is { } cellColumn:
                yield return (rowKey, cellColumn);
                break;

            default:
                break;
        }
    }

    /// <summary>Значення таблиць із замикання читання за поточний і суміжні періоди.</summary>
    /// <remarks>
    /// ⛔ Директива №14 частина 3, <c>CAL-02</c>. Доти читалися ВСІ таблиці
    /// документа (у великому шаблоні ~90) × усі рядки × усі комірки, і
    /// попередній період — завжди, коли <c>Sequence &gt; 1</c>. Формула цього
    /// аркуша справді має право читати сусідню таблицю — але ті таблиці, які
    /// вона читає, перелічені в <c>cfg.FormulaDependency</c> поіменно, і
    /// читати решту документа «щоб напевно» означало платити за весь документ
    /// на кожне автозбереження.
    /// </remarks>
    /// <param name="instance">Екземпляр таблиці — джерело документа й періоду.</param>
    /// <param name="scope">Замикання читання: що саме потрібно цьому прогону.</param>
    /// <param name="periodReads">
    /// WW-03: які ІНШІ періоди читають вирази цілей (відносні зсуви й абсолютні <c>[Period:#k]</c>);
    /// <c>null</c> — невідомо, лишається старе читання попереднього періоду.
    /// </param>
    /// <param name="current">
    /// Екземпляри й рядки ПОТОЧНОГО періоду, уже прочитані <see cref="RunAsync"/>
    /// (O2): читати їх тут удруге означало б два зайві звернення на кожен
    /// документо-період.
    /// </param>
    /// <param name="ct">Токен скасування.</param>
    /// <param name="copyScope">RS: вид копії для ІНШИХ періодів (та сама копія); <c>null</c> — оригінали.</param>
    /// <param name="extras">✎ C-1: куди скласти екземпляри сусідніх періодів зі станом помилок; <c>null</c> — не збирати.</param>
    private async Task<(
        Dictionary<CellKey, Ecr.Expressions.Evaluation.ExpressionValue> Values,
        Dictionary<CellKey, CellValueData> Stored)> LoadValuesAsync(
        TableInstanceRef instance,
        RecalculationReadScope scope,
        (IReadOnlyList<TableInstanceRef> Instances,
            IReadOnlyDictionary<long, IReadOnlyDictionary<string, long>> RowIds) current,
        (HashSet<int> Offsets, HashSet<int> Absolutes)? periodReads,
        CancellationToken ct,
        CopyScope? copyScope = null,
        PeriodLoadExtras? extras = null)
    {
        var values = new Dictionary<CellKey, Ecr.Expressions.Evaluation.ExpressionValue>();

        // ⛔ Директива №14 частина 3, `DAT-02` п. 1: щоб не писати незмінене,
        // потрібне саме ЗБЕРЕЖЕНЕ значення комірки, а не його подання як
        // значення виразу. `FromCellValue` втрачає рівно те, за чим тут
        // доведеться відрізняти: явну порожнечу (`IsEmpty`) від відсутньої
        // комірки і обчислене число від уведеного людиною (`IsCalculated`).
        // Другого походу в базу це не коштує — обидві мапи наповнює той самий
        // прохід по вже прочитаному зрізу.
        //
        // ⚠ Лише ПОТОЧНИЙ період (`offset: 0`): писати перерахунок може тільки
        // в нього, і порівнювати з чимось із минулого місяця не було б із чим.
        var stored = new Dictionary<CellKey, CellValueData>();
        var periodKey = new PeriodKey(instance.PeriodKey);

        await LoadPeriodAsync(
                values, stored, instance.DocumentId, periodKey, offset: 0, scope.TableDefIds, ct, current,
                copyScope: copyScope, extras: extras)
            .ConfigureAwait(false);

        // ⛔ Попередній період завантажується, коли він існує. `[Period:-1]` у
        // січні — це `null` за визначенням (`02b` §3.2), а не помилка: січень
        // не має попереднього місяця. Але в лютому це реальні числа, і
        // прочитати їх як порожнечу означало б тихо занизити результат.
        //
        // ⛔ `CAL-02`: і лише тоді, коли серед цілей є формула з посиланням на
        // інший період. Ознака не вигадана — вона вже в моделі залежності:
        // `DependencyExtractor` пише крос-періодному посиланню
        // `DependsOnKind = 3` і ненульовий `PeriodOffset`
        // (`DependencyExtractor.cs:124-125`). Доти другий прохід читання
        // виконувався БЕЗУМОВНО з лютого й далі — тобто одинадцять місяців
        // на рік документ читався двічі заради значень, яких жодна формула не
        // запитувала.
        // ⛔ WW-03 (G1): раніше читався ЛИШЕ зсув −1; `[Period:-2]`, `[Period:+1]` мовчки давали
        // `null`. Тепер читається рівно та множина періодів, яку названо в виразах цілей:
        // відносні зсуви й абсолютні `[Period:#k]` (k − номер поточного = зсув). Вихід за межі
        // року (номер < 1 чи > 99) — `null` за визначенням (02b §3.2), період без даних — теж.
        var wanted = new SortedSet<int>();
        if (periodReads is not null)
        {
            foreach (var offset in periodReads.Value.Offsets)
            {
                wanted.Add(offset);
            }

            foreach (var absolute in periodReads.Value.Absolutes)
            {
                wanted.Add(absolute - periodKey.Sequence);
            }
        }

        // Запасний шлях старої поведінки: граф каже «читає інший період», а вираз цілі не розібрано.
        if (wanted.Count == 0 && scope.ReadsOtherPeriod)
        {
            wanted.Add(-1);
        }

        foreach (var offset in wanted)
        {
            var sequence = periodKey.Sequence + offset;
            if (offset == 0 || sequence is < 1 or > 99)
            {
                continue;
            }

            await LoadPeriodAsync(
                    values, stored: null, instance.DocumentId, new PeriodKey(periodKey.Value + offset), offset,
                    scope.TableDefIds, ct, known: null, currentVersionId: instance.TemplateVersionId,
                    copyScope: copyScope, extras: extras)
                .ConfigureAwait(false);
        }

        return (values, stored);
    }

    /// <summary>
    /// Таблиці/колонки екземплярів ІНШОЇ версії → відповідники у версії <paramref name="currentVersion"/> (за кодами аркуша,
    /// таблиці й колонки; код, що змінився, - через збережену мапу перейменувань). Екземпляр без таблиці-відповідника
    /// у результат не потрапляє.
    /// </summary>
    private async Task<Dictionary<long, (int TableDefId, Dictionary<int, int> Columns)>> TranslateForeignInstancesAsync(
        IReadOnlyList<TableInstanceRef> all, int currentVersion, CancellationToken ct)
    {
        var result = new Dictionary<long, (int, Dictionary<int, int>)>();
        var current = await metadata.GetAsync(currentVersion, ct).ConfigureAwait(false);
        var (forward, backward) = columnRenames is null
            ? ((IReadOnlyDictionary<int, int>)new Dictionary<int, int>(), (IReadOnlyDictionary<int, int>)new Dictionary<int, int>())
            : await columnRenames.GetAsync(ct).ConfigureAwait(false);

        foreach (var foreign in all.Where(t => t.TemplateVersionId != currentVersion))
        {
            var other = await metadata.GetAsync(foreign.TemplateVersionId, ct).ConfigureAwait(false);
            var source = other.Sheets
                .SelectMany(s => s.Tables.Select(t => (Sheet: s, Table: t)))
                .FirstOrDefault(x => x.Table.Id == foreign.TableDefId);
            if (source.Table is null)
            {
                continue;
            }

            var target = current.Sheets
                .Where(s => !s.IsDeleted && string.Equals(s.Code, source.Sheet.Code, StringComparison.Ordinal))
                .SelectMany(s => s.Tables)
                .FirstOrDefault(t => !t.IsDeleted && string.Equals(t.Code, source.Table.Code, StringComparison.Ordinal));
            if (target is null)
            {
                continue;
            }

            var targetByCode = target.Columns.Where(c => !c.IsDeleted).ToDictionary(c => c.Code, c => c.Id, StringComparer.Ordinal);
            var map = new Dictionary<int, int>();
            foreach (var column in source.Table.Columns)
            {
                if (targetByCode.TryGetValue(column.Code, out var same))
                {
                    map[column.Id] = same;
                }
                else if ((forward.TryGetValue(column.Id, out var renamed)
                          && current.ColumnsById.TryGetValue(renamed, out var asNew) && asNew.TableDefId == target.Id)
                         || (backward.TryGetValue(column.Id, out renamed)
                             && current.ColumnsById.TryGetValue(renamed, out asNew) && asNew.TableDefId == target.Id))
                {
                    map[column.Id] = renamed;
                }
            }

            result[foreign.TableInstanceId] = (target.Id, map);
        }

        return result;
    }

    /// <summary>Значення всіх таблиць документа за один період.</summary>
    /// <param name="values">Значення як входи виразів.</param>
    /// <param name="stored">
    /// Ті самі комірки в тому вигляді, у якому вони лежать у базі; <c>null</c>
    /// — не збирати (суміжний період, у який перерахунок не пише).
    /// </param>
    /// <param name="documentId">Документ.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="offset">Зсув періоду відносно поточного.</param>
    /// <param name="tableDefIds">
    /// Замикання читання (<c>CAL-02</c>): екземпляри решти таблиць документа в
    /// пакетні запити не потрапляють. <c>null</c> — читати весь документ.
    /// </param>
    /// <param name="ct">Токен скасування.</param>
    /// <param name="known">
    /// Екземпляри й рядки ВСІХ таблиць цього періоду, уже прочитані викликачем;
    /// <c>null</c> — прочитати (суміжний період). Рядки екземпляра від
    /// переліку інших не залежать, тож підмножина надмножини — те саме, що
    /// окремий запит на підмножину.
    /// </param>
    /// <param name="currentVersionId">
    /// Версія періоду, що рахується (RPT-STABLE Part B): екземпляри суміжного періоду на іншій версії розвʼязуються
    /// за кодами в її таблиці/колонки; <c>null</c> - без розвʼязання (поточний період).
    /// </param>
    /// <param name="copyScope">RS: вид копії (її таблиці — зі своїх екземплярів); <c>null</c> — лише оригінали.</param>
    /// <param name="extras">✎ C-1: куди скласти екземпляри сусідніх періодів зі станом помилок; <c>null</c> — не збирати.</param>
    private async Task LoadPeriodAsync(
        Dictionary<CellKey, Ecr.Expressions.Evaluation.ExpressionValue> values,
        Dictionary<CellKey, CellValueData>? stored,
        long documentId,
        PeriodKey periodKey,
        int offset,
        IReadOnlySet<int>? tableDefIds,
        CancellationToken ct,
        (IReadOnlyList<TableInstanceRef> Instances,
            IReadOnlyDictionary<long, IReadOnlyDictionary<string, long>> RowIds)? known = null,
        int? currentVersionId = null,
        CopyScope? copyScope = null,
        PeriodLoadExtras? extras = null)
    {
        var all = known?.Instances
                  ?? await rowStore.GetTableInstancesAsync(documentId, periodKey, ct).ConfigureAwait(false);

        // ✎ RPT-STABLE Part B: суміжний період на ІНШІЙ версії шаблону (шкала версій проєкту) - таблиця й колонки
        // розвʼязуються за КОДОМ у версію періоду, що рахується (+ збережена мапа перейменувань як псевдонім); колонка,
        // якої в тій версії немає, дає NULL (не 0 і не помилку) - ключа в `values` просто немає.
        Dictionary<long, (int TableDefId, Dictionary<int, int> Columns)>? translations = null;
        if (currentVersionId is { } currentVersion && all.Any(t => t.TemplateVersionId != currentVersion))
        {
            translations = await TranslateForeignInstancesAsync(all, currentVersion, ct).ConfigureAwait(false);
            all =
            [
                .. all.Where(t => t.TemplateVersionId == currentVersion || translations.ContainsKey(t.TableInstanceId))
                    .Select(t => translations.TryGetValue(t.TableInstanceId, out var tr)
                        ? t with { TableDefId = tr.TableDefId, TemplateVersionId = currentVersion }
                        : t),
            ];
        }

        // ⛔ RS: вид копії для ІНШОГО періоду — та сама копія (її Id наскрізний для документа), решта — оригінали.
        // Після перекладу версій: TableDefId уже у версії прогону, на якій побудовано `copyScope.TableDefIds`.
        all = CopyScope.Apply(all, copyScope);

        // ⛔ `CAL-02`. Фільтр стоїть ДО обох пакетних запитів, а не після них:
        // сенс рядка саме в тому, скільки комірок віддає база, а не скільки з
        // відданих потім знадобилось.
        IReadOnlyList<TableInstanceRef> instances = tableDefIds is null
            ? all
            : [.. all.Where(t => tableDefIds.Contains(t.TableDefId))];

        // ✎ C-1: сусідній період — його комірки з помилкою теж «недовірені» для читачів цього прогону. Екземпляр ІНШОЇ
        // версії (перекладений RPT-STABLE) не засівається: коди його колонок інші (рідкісний випадок, «Ризики» C-1).
        if (offset != 0 && extras is not null)
        {
            extras.ErrorInstances.AddRange(instances
                .Where(t => t.HasFormulaErrors && (translations is null || !translations.ContainsKey(t.TableInstanceId)))
                .Select(t => (offset, t.TableInstanceId)));
        }

        if (instances.Count == 0)
        {
            return;
        }

        var instanceIds = instances.Select(t => t.TableInstanceId).ToList();

        // ⛔ Q-166 (аудит фази 2, продуктивність): той самий випадок, що й у
        // `RunAsync` вище — ОДИН пакетний запит на рядки і на комірки ВСІХ
        // таблиць документа за період замість запиту на кожну; викликається
        // на кожне редагування комірки (`LoadValuesAsync` читає ДВА періоди).
        var rowIdsBatch = known?.RowIds
                          ?? await rowStore.GetRowIdsBatchAsync(instanceIds, periodKey, ct).ConfigureAwait(false);
        // ⚠ O3b: усі екземпляри — цього періоду, тож ключ партиції відомий
        // (EcrPerfI2, 90 екземплярів: 1 984 → 181 читання doc.TableInstance).
        var cellsBatch = await cellStore.ReadSlicesAsync(instanceIds, periodKey, ct).ConfigureAwait(false);

        foreach (var table in instances)
        {
            var keys = rowIdsBatch.TryGetValue(table.TableInstanceId, out var foundKeys)
                ? foundKeys
                : new Dictionary<string, long>(StringComparer.Ordinal);
            var byId = keys.ToDictionary(pair => pair.Value, pair => pair.Key);

            // ✎ C-11 (рішення PO 11.11): рядки ЗСУНУТОГО періоду — джерело рядків предиката `[Period:-n].[T].[WHERE …]`
            // (умова й значення — на рядках того періоду). Нуль нових звернень: ключі вже прочитано вище для значень.
            if (offset != 0 && extras is not null)
            {
                extras.OtherPeriodRows[(offset, table.TableDefId)] = keys;
            }

            var cells = cellsBatch.TryGetValue(table.TableInstanceId, out var foundCells)
                ? foundCells
                : [];

            foreach (var cell in cells)
            {
                if (!byId.TryGetValue(cell.Address.TableRowId, out var rowKey))
                {
                    continue;
                }

                var columnId = cell.Address.ColumnDefId;
                if (translations is not null
                    && translations.TryGetValue(table.TableInstanceId, out var translated)
                    && !translated.Columns.TryGetValue(columnId, out columnId))
                {
                    continue;   // колонки немає у версії періоду, що рахується
                }

                var key = new CellKey(offset, table.TableDefId, rowKey, columnId);
                values[key] = FromCellValue(cell.Value);

                if (stored is not null)
                {
                    stored[key] = cell.Value;
                }
            }

            // ✎ C-5 (рішення PO 11.11, 02b §6.3): рядок є, запису комірки немає → дефолт колонки (версії, що рахується:
            // `table.TableDefId` уже перекладено на неї для сусіднього періоду іншої версії). Наявна комірка, зокрема
            // явна порожнеча `IsEmpty = 1`, уже лежить у `values` як `Null` і дефолту не отримує. У `stored` не йде:
            // дефолт не пишеться (D-283), а дефолтні колонки — входи, перерахунок їх не пише.
            if (extras is not null)
            {
                foreach (var (columnId, value) in extras.Defaults.Of(table.TableDefId))
                {
                    var asValue = FromCellValue(value);
                    foreach (var rowKey in keys.Keys)
                    {
                        values.TryAdd(new CellKey(offset, table.TableDefId, rowKey, columnId), asValue);
                    }
                }
            }
        }
    }

    /// <summary>Календарний контекст періоду.</summary>
    /// <remarks>
    /// ⚠ Межі беруться з реальних меж періоду документа
    /// (<see cref="IPeriodStore.FindPeriodBoundsAsync"/>), а НЕ виводяться
    /// арифметикою з <see cref="PeriodKey"/>: попередня версія цього методу
    /// читала <c>Sequence</c> як номер МІСЯЦЯ (`Math.Clamp(..., 1, 12)`) для
    /// будь-якого <see cref="Domain.Enums.PeriodKind"/> — для квартального чи
    /// річного проєкту це підставляло чужі межі (28 днів замість 91/365) у
    /// <c>[Period].Days/.Hours/.Seconds</c>, якими шаблонні формули діляться
    /// напряму (D-78, D-112 — той самий клас дефекту, якого
    /// <c>Ecr.Calculations.GenericCalculationModule.PeriodAsync</c> у
    /// сусідньому проєкті явно уникає тим самим способом).
    ///
    /// «Сьогодні» тут так само не підставляється: формули шаблону, які
    /// читають межі періоду, належать до крос-періодних і в каскад не
    /// потрапляють, і результат не повинен залежати від моменту перерахунку
    /// (<c>ФВ-1.12</c>).
    /// </remarks>
    private async Task<Ecr.Expressions.PeriodContext> PeriodOf(
        long documentId, PeriodKey periodKey, CancellationToken ct)
    {
        var bounds = await periods
            .FindPeriodBoundsAsync(documentId, periodKey.Value, ct)
            .ConfigureAwait(false)
            ?? throw new Domain.Abstractions.DomainException(
                "ECR-PRD-0404",
                $"Періоду {periodKey.Value} для документа {documentId} не існує: "
                + "календарний контекст обчислити нема з чого.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-PRD-0404.periodForDocument",
                    ["periodKey"] = periodKey.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["documentId"] = documentId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                });

        return new Ecr.Expressions.PeriodContext(
            bounds.PeriodStart,
            bounds.PeriodEnd,
            Domain.Enums.CalendarMode.Actual,
            periodKey.Year,
            (byte)periodKey.Sequence);
    }

    /// <summary>Значення комірки як значення виразу.</summary>
    private static Ecr.Expressions.Evaluation.ExpressionValue FromCellValue(CellValueData value)
    {
        if (value.ValueNumeric is { } number)
        {
            return Ecr.Expressions.Evaluation.ExpressionValue.Number(number);
        }

        if (value.ValueBool is { } flag)
        {
            return Ecr.Expressions.Evaluation.ExpressionValue.Boolean(flag);
        }

        if (value.ValueDate is { } date)
        {
            return Ecr.Expressions.Evaluation.ExpressionValue.Date(date);
        }

        if (value.ValueString is { } text)
        {
            return Ecr.Expressions.Evaluation.ExpressionValue.Text(text);
        }

        // ⚠ Той самий вибір, що й у `CellValueMapping.ToExpressionValue`
        // (`Ecr.Expressions`): id запису довідника — ЧИСЛО, а не окремий тип
        // значення. Це навмисно те саме число, яке приймає перший аргумент
        // REGFIELD, — «той самий механізм отримання значення комірки», яким
        // комірки взагалі передаються у формулах.
        if (value.ValueRegistryEntryId is { } entryId)
        {
            return Ecr.Expressions.Evaluation.ExpressionValue.Number(entryId);
        }

        return Ecr.Expressions.Evaluation.ExpressionValue.Null;
    }

    /// <summary>
    /// Знімок полів довідника для <c>REGFIELD</c>: id запису → (код поля →
    /// значення), звужений до Registry-залежностей ФОРМУЛ-ЦІЛЕЙ цього прогону.
    /// </summary>
    /// <remarks>
    /// ⛔ Це і є міст між <c>DependencyExtractor</c> і фактичним перерахунком
    /// (`CAL-02` для довідника, за тим самим принципом, що
    /// <see cref="RecalculationReadScope"/> звужує читання таблиць документа).
    /// Без Registry-запису в <c>dependencies</c> — байдуже, зламаний видобувач
    /// чи формула щойно додана, — цикл нижче просто не знайде, що завантажити,
    /// і <c>REGFIELD</c> поверне <c>#REF</c>, хоча Lookup-комірка заповнена.
    ///
    /// ⚠ Без пакетної оптимізації по всіх записях одразу (на відміну від
    /// `Q-166` для комірок): запит на РЕЄСТР (раз на унікальний
    /// <c>RegistryDefId</c>) і запит на ЗАПИС (раз на унікальний
    /// <c>EntryId</c>). Формул із REGFIELD у корпусі одиниці, а не сотні —
    /// той самий бюджет тут не спрацьовує, і пакетувати нема що вимірювати.
    /// </remarks>
    private async Task<IReadOnlyDictionary<long, IReadOnlyDictionary<string, Ecr.Expressions.Evaluation.ExpressionValue>>?>
        LoadRegistryFieldsAsync(
            IReadOnlyList<Domain.Entities.Configuration.FormulaDependency> dependencies,
            IReadOnlyList<int> targets,
            Dictionary<int, Domain.Entities.Configuration.TableDef> tables,
            Dictionary<int, IReadOnlyDictionary<string, long>> rowIdsByTable,
            Dictionary<CellKey, Ecr.Expressions.Evaluation.ExpressionValue> values,
            UnitCatalogSnapshot units,
            CancellationToken ct,
            Func<int, bool>? narrowsRows = null,
            HashSet<long>? dirtyRows = null)
    {
        var targetIds = new HashSet<int>(targets);

        // 1. Які (таблиця, рядок?, колонка, код поля) читає REGFIELD серед
        //    формул-ЦІЛЕЙ. `RowKey == null` — колонкова формула: залежність
        //    стосується КОЖНОГО рядка таблиці (той самий випадок, що
        //    `RecalculationPlanBuilder.AddCellEdge` уже обробляє для Cell);
        //    ✎ P1 06.11: крім рядково-локальної цілі, що рахується лише в брудних рядках (`narrowsRows`).
        var needed = new List<(int TableDefId, string? RowKey, int ColumnDefId, string FieldCode, bool Narrow)>();

        foreach (var dependency in dependencies)
        {
            if (dependency.DependsOnKind != Ecr.Expressions.Binding.DependencyExtractor.KindRegistry
                || dependency.FormulaDefId is not { } formulaId
                || !targetIds.Contains(formulaId)
                || dependency.TableDefId is not { } tableDefId
                || dependency.ColumnDefId is not { } columnDefId
                || dependency.FilterJson is not { } fieldCode)
            {
                continue;
            }

            needed.Add((tableDefId, dependency.RowKey, columnDefId, fieldCode,
                dirtyRows is not null && narrowsRows is not null && narrowsRows(formulaId)));
        }

        if (needed.Count == 0)
        {
            return null;
        }

        // 2. entryId ← уже завантажені `values` (Lookup-комірка читається тим
        //    самим шляхом, що й будь-яка інша), + який довідник (з колонки).
        var requests = new List<Registries.RegistryFieldRequest>();

        foreach (var (tableDefId, rowKey, columnDefId, fieldCode, narrow) in needed)
        {
            if (!tables.TryGetValue(tableDefId, out var table))
            {
                continue;
            }

            var column = table.Columns.FirstOrDefault(c => c.Id == columnDefId && !c.IsDeleted);
            if (column?.LookupRegistryDefId is not { } registryDefId)
            {
                continue;
            }

            IEnumerable<string> rowKeys = rowKey is not null
                ? [rowKey]
                : !rowIdsByTable.TryGetValue(tableDefId, out var rows) ? []
                : narrow ? rows.Where(pair => dirtyRows!.Contains(pair.Value)).Select(pair => pair.Key)
                : rows.Keys;

            foreach (var key in rowKeys)
            {
                if (!values.TryGetValue(new CellKey(0, tableDefId, key, columnDefId), out var cell)
                    || cell.AsNumber() is not { } entryIdRaw)
                {
                    continue;
                }

                requests.Add(new Registries.RegistryFieldRequest((long)entryIdRaw, registryDefId, fieldCode));
            }
        }

        // 3–4. Визначення полів і значення — СПІЛЬНИЙ завантажувач (D16-04):
        //      той самий, яким знімок будують правила валідації.
        return await Registries.RegistryFieldSnapshotLoader
            .LoadAsync(registryStore, requests, ct, units).ConfigureAwait(false);
    }

    /// <summary>Число виразу, приведене до масштабу сховища комірки.</summary>
    /// <remarks>
    /// ⛔ C1-02. Правило — те, яким значення реально лягає в <c>doc.CellValue</c>:
    /// TVP <c>decimal(34,16)</c> округлює на шляху запису «від нуля»
    /// (<see cref="MidpointRounding.AwayFromZero"/>; виміряно на SQL Server:
    /// <c>0.00000000000000025 → 0.0000000000000003</c>,
    /// <c>0.99999999999999999999 → 1.0000000000000000</c>). Те саме правило
    /// вже тримають <c>RegistryValue</c> і <c>CellValueReader</c>. Інші типи
    /// (і <c>double</c> діалекту <c>Legacy</c>) не чіпаються.
    /// </remarks>
    private static Ecr.Expressions.Evaluation.ExpressionValue AsStored(Ecr.Expressions.Evaluation.ExpressionValue value)
        => value.Type == Ecr.Expressions.Ast.ExpressionValueType.Number && value.Value is decimal number
            ? Ecr.Expressions.Evaluation.ExpressionValue.Number(
                decimal.Round(number, Documents.CellValueReader.StorageScale, MidpointRounding.AwayFromZero))
            : value;

    /// <summary>Число виразу до масштабу сховища - для <see cref="RowLocalPreview"/> (те саме правило, що в прогоні).</summary>
    internal static Ecr.Expressions.Evaluation.ExpressionValue AsStoredScale(Ecr.Expressions.Evaluation.ExpressionValue value)
        => AsStored(value);

    /// <summary>Значення виразу як значення комірки - для <see cref="RowLocalPreview"/>.</summary>
    internal static CellValueData? ToStoredCell(Ecr.Expressions.Evaluation.ExpressionValue value) => ToCellValue(value);

    /// <summary>Значення виразу як значення комірки; <c>null</c> — записувати нічого.</summary>
    private static CellValueData? ToCellValue(Ecr.Expressions.Evaluation.ExpressionValue value)
        => value.Type switch
        {
            Ecr.Expressions.Ast.ExpressionValueType.Number =>
                new CellValueData { ValueNumeric = (decimal)value.Value!, IsCalculated = true },
            Ecr.Expressions.Ast.ExpressionValueType.Boolean =>
                new CellValueData { ValueBool = (bool)value.Value!, IsCalculated = true },
            Ecr.Expressions.Ast.ExpressionValueType.Date =>
                new CellValueData { ValueDate = (DateTime)value.Value!, IsCalculated = true },
            Ecr.Expressions.Ast.ExpressionValueType.Text =>
                new CellValueData { ValueString = (string)value.Value!, IsCalculated = true },
            _ => null,
        };

    /// <summary>Значення комірки ДО перерахунку; <c>null</c> — комірки не було.</summary>
    /// <remarks>
    /// Той самий контракт, що <c>PatchCellsHandler.Was</c>: «комірки не було»
    /// і «комірка була порожня» — різні стани (R-B4), і перший лишається
    /// <c>null</c>, а не вигаданим порожнім рядком.
    /// </remarks>
    private static string? Was(
        IReadOnlyDictionary<CellAddress, CellValueData> previous, CellAddress address)
        => previous.TryGetValue(address, out var value) ? Describe(value) : null;

    /// <summary>Текстове подання значення комірки для журналу аудиту.</summary>
    private static string? Describe(CellValueData v)
        => v.IsEmpty ? string.Empty
         : v.ValueNumeric?.ToString(System.Globalization.CultureInfo.InvariantCulture)
           ?? v.ValueString
           ?? v.ValueBool?.ToString()
           ?? v.ValueDate?.ToString("O", System.Globalization.CultureInfo.InvariantCulture)
           ?? v.ValueRegistryEntryId?.ToString(System.Globalization.CultureInfo.InvariantCulture)
           ?? v.ValueUnitId?.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// План перерахунку, зафіксований при публікації.
/// </summary>
/// <remarks>
/// Проєкція <c>cfg.FormulaDependency</c> і <c>cfg.FormulaDef.EvaluationOrder</c>.
/// Окремий тип, а не запити до бази: план читається на кожну правку комірки, і
/// кожне звернення до БД тут — це затримка в інтерактивному сценарії.
/// </remarks>
public sealed class RecalculationPlan
{
    private readonly Dictionary<CellAddress, List<int>> _byCell = [];
    private readonly Dictionary<int, List<int>> _byColumn = [];
    private readonly Dictionary<int, List<int>> _byFormula = [];
    private readonly Dictionary<int, int> _order = [];
    private readonly HashSet<int> _crossSheet = [];
    private readonly HashSet<int> _snapshot = [];

    /// <summary>Оголошує формулу з її порядком обчислення.</summary>
    /// <param name="formulaDefId">Формула.</param>
    /// <param name="evaluationOrder">Порядок, обчислений при публікації.</param>
    /// <param name="isCrossSheet">Чи читає вона інший аркуш.</param>
    /// <param name="isSnapshot">Чи є вона знімком.</param>
    public void Declare(int formulaDefId, int evaluationOrder, bool isCrossSheet = false, bool isSnapshot = false)
    {
        _order[formulaDefId] = evaluationOrder;
        if (isCrossSheet)
        {
            _crossSheet.Add(formulaDefId);
        }

        if (isSnapshot)
        {
            _snapshot.Add(formulaDefId);
        }
    }

    /// <summary>Формула залежить від комірки.</summary>
    public void DependsOnCell(int formulaDefId, CellAddress address)
    {
        if (!_byCell.TryGetValue(address, out var list))
        {
            _byCell[address] = list = [];
        }

        list.Add(formulaDefId);
    }

    /// <summary>Кількість ребер «комірка → формула» (діагностика розміру плану; тест P-2).</summary>
    internal int CellEdgeCount => _byCell.Values.Sum(list => list.Count);

    /// <summary>Формула залежить від результату іншої формули.</summary>
    public void DependsOnFormula(int formulaDefId, int dependsOnFormulaDefId)
    {
        if (!_byFormula.TryGetValue(dependsOnFormulaDefId, out var list))
        {
            _byFormula[dependsOnFormulaDefId] = list = [];
        }

        list.Add(formulaDefId);
    }

    /// <summary>Формула залежить від УСІХ рядків колонки (предикат динамічної таблиці), а не від окремих комірок.</summary>
    /// <remarks>
    /// ⛔ D3: ребра <see cref="DependsOnCell"/> такої залежності адресують лише ЖИВІ рядки на момент побудови
    /// плану. Видалений (чи ще не існуючий) рядок адреси в плані не має, і видалення в ньому не знаходило б
    /// жодної залежної формули — підсумок лишався б із видаленим рядком. Колонковий індекс відповідає на
    /// «що залежить від колонки X будь-якого рядка» і для таких адрес.
    /// </remarks>
    public void DependsOnColumn(int formulaDefId, int columnDefId)
    {
        if (!_byColumn.TryGetValue(columnDefId, out var list))
        {
            _byColumn[columnDefId] = list = [];
        }

        if (!list.Contains(formulaDefId))
        {
            list.Add(formulaDefId);
        }
    }

    /// <summary>Формули, залежні від комірки (і від усіх рядків її колонки, див. <see cref="DependsOnColumn"/>).</summary>
    public IReadOnlyList<int> DependentsOf(CellAddress address)
    {
        var exact = _byCell.TryGetValue(address, out var list) ? list : null;
        if (!_byColumn.TryGetValue(address.ColumnDefId, out var wholeColumn))
        {
            return exact ?? [];
        }

        return exact is null ? wholeColumn : [.. exact.Union(wholeColumn)];
    }

    /// <summary>Формули, залежні від результату формули.</summary>
    public IReadOnlyList<int> DependentsOfFormula(int formulaDefId)
        => _byFormula.TryGetValue(formulaDefId, out var list) ? list : [];

    /// <summary>Порядок обчислення формули.</summary>
    public int EvaluationOrder(int formulaDefId) => _order.GetValueOrDefault(formulaDefId, int.MaxValue);

    /// <summary>Усі оголошені формули плану в порядку обчислення.</summary>
    /// <remarks>
    /// ⚠ Єдиний шлях дістати формулу, від якої НІЩО не брудне.
    /// <see cref="DependentsOf"/> і <see cref="DependentsOfFormula"/> обидва
    /// починаються з насіння, тож формула, додана в шаблон після введення
    /// даних, не з'явилася б у жодному наборі — і не перерахувалася б ніколи.
    ///
    /// ⚠ Другий ключ сортування — сам ідентифікатор: порядок обчислення не
    /// унікальний, а обхід словника не має гарантованого порядку. Без нього
    /// два прогони на тих самих даних могли б дати різну послідовність запису
    /// (<c>ФВ-9.5</c>).
    /// </remarks>
    public IReadOnlyList<int> AllFormulas()
        => [.. _order.OrderBy(pair => pair.Value).ThenBy(pair => pair.Key).Select(pair => pair.Key)];

    /// <summary>Чи читає формула інший аркуш.</summary>
    public bool IsCrossSheet(int formulaDefId) => _crossSheet.Contains(formulaDefId);

    /// <summary>Чи є формула знімком.</summary>
    public bool IsSnapshot(int formulaDefId) => _snapshot.Contains(formulaDefId);
}
