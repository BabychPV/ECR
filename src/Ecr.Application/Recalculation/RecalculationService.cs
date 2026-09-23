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
    IAuditWriter audit,
    IClock clock,
    IUnitOfWork uow)
{
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

    /// <summary>
    /// Перераховує залежне піддерево і записує результати.
    /// </summary>
    /// <param name="tableInstanceId">Екземпляр таблиці, у якому сталася правка.</param>
    /// <param name="dirty">Змінені комірки.</param>
    /// <param name="ct">Токен скасування.</param>
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
    public async Task<int> RecalculateAsync(long tableInstanceId, DirtySet dirty, CancellationToken ct)
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

        return await RunAsync(instance, dirty, ct).ConfigureAwait(false);
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
    public async Task<int> RecalculateAllAsync(
        long documentId, PeriodKey periodKey, CancellationToken ct, int? sheetDefId = null)
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
        foreach (var group in instances.GroupBy(i => i.TemplateVersionId).OrderBy(g => g.Key))
        {
            written += await RunAsync(group.First(), dirty: null, ct, sheetDefId).ConfigureAwait(false);
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
    private async Task<int> RunAsync(
        TableInstanceRef instance, DirtySet? dirty, CancellationToken ct, int? sheetDefId = null)
    {
        var periodKey = new PeriodKey(instance.PeriodKey);

        var snapshot = await metadata.GetAsync(instance.TemplateVersionId, ct).ConfigureAwait(false);
        var dependencies = await versions
            .ListFormulaDependenciesAsync(instance.TemplateVersionId, ct)
            .ConfigureAwait(false);

        // ⛔ Читаються ВСІ таблиці документа за період, а не лише та, де
        // сталася правка. Формула сусідньої таблиці, яка читає цю, живе в
        // СВОЄМУ екземплярі: з одним переліком рядків вона не отримала б ані
        // ребра графа, ані місця для запису — і мовчки не перераховувалася б.
        var instances = await rowStore
            .GetTableInstancesAsync(instance.DocumentId, periodKey, ct)
            .ConfigureAwait(false);

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
        var tables = snapshot.Sheets.SelectMany(s => s.Tables).ToDictionary(t => t.Id);

        var formulas = tables.Values
            .SelectMany(t => t.Formulas.Where(f => !f.IsDeleted).Select(f => (Table: t, Formula: f)))
            .ToDictionary(pair => pair.Formula.Id);

        List<int> targets;

        if (dirty is null)
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
                             && (sheetDefId is null
                                 || (formulas.TryGetValue(id, out var owned)
                                     && owned.Table.SheetDefId == sheetDefId)))];
        }
        else
        {
            var affected = Plan(dirty, plan);

            // ⛔ Крос-аркушні rollup рахуються ТУТ САМО, а не «колись».
            // Відкладення має сенс на інтерактивному шляху, де людина чекає на
            // кожному Tab; цей прогін уже фоновий, і відкласти означало б не
            // порахувати ніколи — саме те, чим був увесь `A7-63`.
            var rollups = TakeDeferredRollups(plan);

            targets = [.. affected, .. rollups];
        }

        if (targets.Count == 0)
        {
            return 0;
        }

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

        var (values, stored) = await LoadValuesAsync(instance, scope, ct).ConfigureAwait(false);

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
        var registryFields = await LoadRegistryFieldsAsync(
            dependencies, targets, tables, rowIdsByTable, values, ct).ConfigureAwait(false);

        var context = new SliceEvaluationContext(
            snapshot, values, EmptyHeaders,
            await PeriodOf(instance.DocumentId, periodKey, ct).ConfigureAwait(false),
            catalogue, rowIdsByTable, registryFields);

        // Результати групуються за екземпляром: кожна таблиця пишеться
        // своїм набором змін, бо `CellChangeSet` адресує один екземпляр.
        var byInstance = new Dictionary<long, List<CellRecord>>();

        foreach (var formulaId in targets)
        {
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
            var rowFilter = dirtyRows is not null
                            && rowLocal.Contains(formulaId)
                            && !tablesWithNonLocalTarget.Contains(owner.Table.Id)
                ? dirtyRows
                : null;

            Evaluate(
                owner.Table, owner.Formula, expression, rows, rowFilter,
                periodKey, context, values, stored, sink);
        }

        var written = byInstance.Values.Sum(list => list.Count);
        if (written == 0)
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
        var isLateEdit = periodState == Domain.Enums.PeriodState.Grace;

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
            foreach (var (target, records) in byInstance.Where(pair => pair.Value.Count > 0))
            {
                // ⚠ Старі значення читаються ДО запису — після `ApplyAsync` їх уже
                // немає ніде, а саме вони й становлять половину запису аудиту
                // (той самий порядок, що в `PatchCellsHandler.ReadPreviousValuesAsync`).
                var previous = await cellStore
                    .ReadCellsAsync([.. records.Select(r => r.Address)], token)
                    .ConfigureAwait(false);

                await cellStore.ApplyAsync(
                    new CellChangeSet(
                        target,
                        records,
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
                        isLateEdit),
                    token).ConfigureAwait(false);

                var rowKeyByRowId = rowKeyByRowIdByInstance.TryGetValue(target, out var found)
                    ? found
                    : new Dictionary<long, string>();

                // ⛔ `DAT-02` п. 2: аудит пишеться рівно по `records`, тобто по
                // тому, що ПІШЛО в `upserts`. Фільтр незміненого стоїть в
                // `Evaluate` — до того, як комірка потрапить у цей список, —
                // саме тому, щоб журнал і сховище не могли розійтися: другого
                // переліку, який довелося б тримати в тому самому стані, тут
                // немає.
                await audit.WriteCellChangesAsync(
                    [.. records.Select(r => new CellChangeRecord(
                        now, r.Address, instance.DocumentId,
                        RowKey: rowKeyByRowId.GetValueOrDefault(r.Address.TableRowId, string.Empty),
                        OldValue: Was(previous, r.Address),
                        NewValue: Describe(r.Value),
                        SystemUserId,
                        Origin: "Recalculation",
                        isLateEdit,
                        CorrelationId: null))],
                    token).ConfigureAwait(false);
            }

            await uow.SaveChangesAsync(token).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        return written;
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
        IReadOnlyDictionary<CellKey, CellValueData> stored,
        List<CellRecord> upserts)
    {
        foreach (var (rowKey, columnDefId) in Targets(table, formula, rowIds, rowFilter))
        {
            if (!rowIds.TryGetValue(rowKey, out var rowId))
            {
                continue;
            }

            context.CurrentTableDefId = table.Id;
            context.CurrentRowKey = rowKey;
            context.CurrentColumnDefId = columnDefId;

            var result = formulaEngine.Evaluate(parsed, context);

            // ⛔ Помилка обчислення НЕ записується як значення. `#REF` у
            // комірці — це не число, і покласти його в `ValueNumeric`
            // означало б або нуль, або текст у числовій колонці.
            if (result.Value.IsError || result.Value.IsNull)
            {
                continue;
            }

            var data = ToCellValue(result.Value);
            if (data is null)
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
            values[key] = result.Value;

            // ⛔ Директива №14 частина 3, `DAT-02` п. 1. Незмінене не
            // пишеться. Колонкова формула віддає ціль у КОЖНОМУ рядку
            // (`Targets` нижче), тож без цієї перевірки правка однієї комірки
            // давала до 500 рядків `MERGE`, стільки ж рядків аудиту, де
            // `старе = нове`, і — до п. 3 — стільки ж піднятих `RowVersion`.
            // Порівняння винесене в `CellValueComparison.AreEqual` і
            // перевірене окремо: саме воно вирішує, що таке «те саме».
            if (CellValueComparison.AreEqual(stored.GetValueOrDefault(key), data))
            {
                continue;
            }

            upserts.Add(new CellRecord(
                new CellAddress(periodKey, rowId, columnDefId), table.Id, data));
        }
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
    /// <param name="ct">Токен скасування.</param>
    private async Task<(
        Dictionary<CellKey, Ecr.Expressions.Evaluation.ExpressionValue> Values,
        Dictionary<CellKey, CellValueData> Stored)> LoadValuesAsync(
        TableInstanceRef instance, RecalculationReadScope scope, CancellationToken ct)
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
                values, stored, instance.DocumentId, periodKey, offset: 0, scope.TableDefIds, ct)
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
        if (scope.ReadsOtherPeriod && periodKey.Sequence > 1)
        {
            var previous = new PeriodKey(periodKey.Value - 1);
            await LoadPeriodAsync(
                    values, stored: null, instance.DocumentId, previous, offset: -1, scope.TableDefIds, ct)
                .ConfigureAwait(false);
        }

        return (values, stored);
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
    private async Task LoadPeriodAsync(
        Dictionary<CellKey, Ecr.Expressions.Evaluation.ExpressionValue> values,
        Dictionary<CellKey, CellValueData>? stored,
        long documentId,
        PeriodKey periodKey,
        int offset,
        IReadOnlySet<int>? tableDefIds,
        CancellationToken ct)
    {
        var all = await rowStore.GetTableInstancesAsync(documentId, periodKey, ct).ConfigureAwait(false);

        // ⛔ `CAL-02`. Фільтр стоїть ДО обох пакетних запитів, а не після них:
        // сенс рядка саме в тому, скільки комірок віддає база, а не скільки з
        // відданих потім знадобилось.
        IReadOnlyList<TableInstanceRef> instances = tableDefIds is null
            ? all
            : [.. all.Where(t => tableDefIds.Contains(t.TableDefId))];

        if (instances.Count == 0)
        {
            return;
        }

        var instanceIds = instances.Select(t => t.TableInstanceId).ToList();

        // ⛔ Q-166 (аудит фази 2, продуктивність): той самий випадок, що й у
        // `RunAsync` вище — ОДИН пакетний запит на рядки і на комірки ВСІХ
        // таблиць документа за період замість запиту на кожну; викликається
        // на кожне редагування комірки (`LoadValuesAsync` читає ДВА періоди).
        var rowIdsBatch = await rowStore.GetRowIdsBatchAsync(instanceIds, periodKey, ct).ConfigureAwait(false);
        var cellsBatch = await cellStore.ReadSlicesAsync(instanceIds, ct).ConfigureAwait(false);

        foreach (var table in instances)
        {
            var keys = rowIdsBatch.TryGetValue(table.TableInstanceId, out var foundKeys)
                ? foundKeys
                : new Dictionary<string, long>(StringComparer.Ordinal);
            var byId = keys.ToDictionary(pair => pair.Value, pair => pair.Key);

            var cells = cellsBatch.TryGetValue(table.TableInstanceId, out var foundCells)
                ? foundCells
                : [];

            foreach (var cell in cells)
            {
                if (!byId.TryGetValue(cell.Address.TableRowId, out var rowKey))
                {
                    continue;
                }

                var key = new CellKey(offset, table.TableDefId, rowKey, cell.Address.ColumnDefId);
                values[key] = FromCellValue(cell.Value);

                if (stored is not null)
                {
                    stored[key] = cell.Value;
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
                + "календарний контекст обчислити нема з чого.");

        return new Ecr.Expressions.PeriodContext(
            bounds.PeriodStart,
            bounds.PeriodEnd,
            Domain.Enums.CalendarMode.Actual,
            periodKey.Year,
            (byte)periodKey.Sequence);
    }

    private static readonly Dictionary<string, Ecr.Expressions.Evaluation.ExpressionValue> EmptyHeaders =
        new(StringComparer.Ordinal);

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
            CancellationToken ct)
    {
        var targetIds = new HashSet<int>(targets);

        // 1. Які (таблиця, рядок?, колонка, код поля) читає REGFIELD серед
        //    формул-ЦІЛЕЙ. `RowKey == null` — колонкова формула: залежність
        //    стосується КОЖНОГО рядка таблиці (той самий випадок, що
        //    `RecalculationPlanBuilder.AddCellEdge` уже обробляє для Cell).
        var needed = new List<(int TableDefId, string? RowKey, int ColumnDefId, string FieldCode)>();

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

            needed.Add((tableDefId, dependency.RowKey, columnDefId, fieldCode));
        }

        if (needed.Count == 0)
        {
            return null;
        }

        // 2. entryId ← уже завантажені `values` (Lookup-комірка читається тим
        //    самим шляхом, що й будь-яка інша), + який довідник (з колонки).
        var neededFieldsByEntry = new Dictionary<long, HashSet<string>>();
        var registryDefByEntry = new Dictionary<long, int>();

        foreach (var (tableDefId, rowKey, columnDefId, fieldCode) in needed)
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
                : rowIdsByTable.TryGetValue(tableDefId, out var rows) ? rows.Keys : [];

            foreach (var key in rowKeys)
            {
                if (!values.TryGetValue(new CellKey(0, tableDefId, key, columnDefId), out var cell)
                    || cell.AsNumber() is not { } entryIdRaw)
                {
                    continue;
                }

                var entryId = (long)entryIdRaw;
                registryDefByEntry[entryId] = registryDefId;

                if (!neededFieldsByEntry.TryGetValue(entryId, out var fields))
                {
                    neededFieldsByEntry[entryId] = fields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                }

                fields.Add(fieldCode);
            }
        }

        if (neededFieldsByEntry.Count == 0)
        {
            return null;
        }

        // 3. Визначення полів — по одному запиту на УНІКАЛЬНИЙ довідник.
        var fieldDefsByRegistry =
            new Dictionary<int, IReadOnlyDictionary<string, Domain.Entities.Configuration.RegistryFieldDef>>();

        foreach (var registryDefId in registryDefByEntry.Values.Distinct())
        {
            var definition = await registryStore.FindDefinitionByIdAsync(registryDefId, ct).ConfigureAwait(false);
            fieldDefsByRegistry[registryDefId] = definition?.Fields
                .ToDictionary(f => f.Code, StringComparer.OrdinalIgnoreCase)
                ?? new Dictionary<string, Domain.Entities.Configuration.RegistryFieldDef>(StringComparer.OrdinalIgnoreCase);
        }

        // 4. Значення — по одному запиту на УНІКАЛЬНИЙ запис.
        var snapshot = new Dictionary<long, IReadOnlyDictionary<string, Ecr.Expressions.Evaluation.ExpressionValue>>();

        foreach (var (entryId, fieldCodes) in neededFieldsByEntry)
        {
            if (!fieldDefsByRegistry.TryGetValue(registryDefByEntry[entryId], out var fieldDefs))
            {
                continue;
            }

            var registryValues = await registryStore.ListValuesAsync(entryId, ct).ConfigureAwait(false);
            var byFieldDefId = registryValues.ToDictionary(v => v.RegistryFieldDefId);

            var perEntry = new Dictionary<string, Ecr.Expressions.Evaluation.ExpressionValue>(
                StringComparer.OrdinalIgnoreCase);

            foreach (var fieldCode in fieldCodes)
            {
                if (!fieldDefs.TryGetValue(fieldCode, out var fieldDef)
                    || !byFieldDefId.TryGetValue(fieldDef.Id, out var registryValue))
                {
                    // Немає такого поля або запис ще не заповнив його —
                    // `GetRegistryField` віддасть #REF на відсутній ключ; тут
                    // просто нема що покласти в знімок.
                    continue;
                }

                if (ToExpressionValue(registryValue, fieldDef.DataType) is { } mapped)
                {
                    perEntry[fieldCode] = mapped;
                }
            }

            if (perEntry.Count > 0)
            {
                snapshot[entryId] = perEntry;
            }
        }

        return snapshot;
    }

    /// <summary>Значення поля довідника як значення виразу; типізовано за <c>RegistryFieldDef.DataType</c>.</summary>
    /// <remarks>
    /// ⚠ <c>Lookup</c>/<c>Unit</c>/<c>Formula</c>/<c>Calculated</c> тут
    /// НЕМАЄ: перші два REGFIELD сьогодні не читає (задача — decimal/text/
    /// bool/date), а останні два в довіднику взагалі не існують
    /// (<c>RegistryValue.Set</c> їх забороняє при записі).
    /// </remarks>
    private static Ecr.Expressions.Evaluation.ExpressionValue? ToExpressionValue(
        Domain.Entities.Dictionaries.RegistryValue value, Domain.Enums.CellDataType dataType)
        => dataType switch
        {
            Domain.Enums.CellDataType.Decimal or Domain.Enums.CellDataType.Int => value.ValueNumeric is { } n
                ? Ecr.Expressions.Evaluation.ExpressionValue.Number(n)
                : null,
            Domain.Enums.CellDataType.String => value.ValueString is { } s
                ? Ecr.Expressions.Evaluation.ExpressionValue.Text(s)
                : null,
            Domain.Enums.CellDataType.Bool => value.ValueBool is { } b
                ? Ecr.Expressions.Evaluation.ExpressionValue.Boolean(b)
                : null,
            Domain.Enums.CellDataType.Date => value.ValueDate is { } d
                ? Ecr.Expressions.Evaluation.ExpressionValue.Date(d)
                : null,
            _ => null,
        };

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

    /// <summary>Формула залежить від результату іншої формули.</summary>
    public void DependsOnFormula(int formulaDefId, int dependsOnFormulaDefId)
    {
        if (!_byFormula.TryGetValue(dependsOnFormulaDefId, out var list))
        {
            _byFormula[dependsOnFormulaDefId] = list = [];
        }

        list.Add(formulaDefId);
    }

    /// <summary>Формули, залежні від комірки.</summary>
    public IReadOnlyList<int> DependentsOf(CellAddress address)
        => _byCell.TryGetValue(address, out var list) ? list : [];

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
