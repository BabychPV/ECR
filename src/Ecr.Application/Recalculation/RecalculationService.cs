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
    IMetadataCache metadata,
    ITemplateVersionStore versions,
    IFormulaEngine formulaEngine,
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
    /// Таких випадків три, і всі названі: знімок (<c>IsSnapshot</c>),
    /// крос-аркушний rollup (<c>IsCrossSheet</c>, відкладається) і предикат
    /// динамічного діапазону, для якого потрібні значення інших колонок.
    /// </remarks>
    public async Task<int> RecalculateAsync(long tableInstanceId, DirtySet dirty, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(dirty);

        if (dirty.IsEmpty)
        {
            return 0;
        }

        var instance = await rowStore.ResolveTableInstanceAsync(tableInstanceId, ct).ConfigureAwait(false);
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

        var rowIdsByTable = new Dictionary<int, IReadOnlyDictionary<string, long>>();
        var instanceByTable = new Dictionary<int, long>();

        foreach (var table in instances)
        {
            instanceByTable[table.TableDefId] = table.TableInstanceId;
            rowIdsByTable[table.TableDefId] = await rowStore
                .GetRowIdsAsync(table.TableInstanceId, periodKey, ct)
                .ConfigureAwait(false);
        }

        var plan = RecalculationPlanBuilder.Build(snapshot, dependencies, rowIdsByTable, periodKey);
        var affected = Plan(dirty, plan);

        // ⛔ Крос-аркушні rollup рахуються ТУТ САМО, а не «колись». Відкладення
        // має сенс на інтерактивному шляху, де людина чекає на кожному Tab;
        // цей прогін уже фоновий, і відкласти означало б не порахувати ніколи
        // — саме те, чим був увесь `A7-63`.
        var rollups = DeferredRollups.ToList();
        _deferred.Clear();

        if (affected.Count == 0 && rollups.Count == 0)
        {
            return 0;
        }

        var tables = snapshot.Sheets.SelectMany(s => s.Tables).ToDictionary(t => t.Id);

        var formulas = tables.Values
            .SelectMany(t => t.Formulas.Where(f => !f.IsDeleted).Select(f => (Table: t, Formula: f)))
            .ToDictionary(pair => pair.Formula.Id);

        // ⛔ Формули з предикатом динамічного діапазону виключаються ЯВНО.
        // Обчислити предикат тут нічим: він читає інші колонки кожного рядка,
        // а не ту, на яку посилається. Мовчазне «порожній набір» дало б суму
        // без доданків — тобто неправильне число замість старого.
        var predicated = dependencies
            .Where(d => d is { FormulaDefId: not null, RowKey: null, FilterJson: not null })
            .Select(d => d.FormulaDefId!.Value)
            .ToHashSet();

        var values = await LoadValuesAsync(instance, ct).ConfigureAwait(false);
        var context = new SliceEvaluationContext(snapshot, values, EmptyHeaders, PeriodOf(periodKey));

        // Результати групуються за екземпляром: кожна таблиця пишеться
        // своїм набором змін, бо `CellChangeSet` адресує один екземпляр.
        var byInstance = new Dictionary<long, List<CellRecord>>();

        foreach (var formulaId in affected.Concat(rollups))
        {
            if (predicated.Contains(formulaId) || !formulas.TryGetValue(formulaId, out var owner))
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

            if (!byInstance.TryGetValue(target, out var sink))
            {
                byInstance[target] = sink = [];
            }

            Evaluate(owner.Table, owner.Formula, rows, periodKey, context, values, sink);
        }

        var written = byInstance.Values.Sum(list => list.Count);
        if (written == 0)
        {
            return 0;
        }

        // ⚠ Один запис на екземпляр: перерахунок торкається десятків комірок,
        // і окрема транзакція на кожну перетворила б фонову задачу на джерело
        // блокувань саме тоді, коли документ активно правлять.
        foreach (var (target, records) in byInstance.Where(pair => pair.Value.Count > 0))
        {
            await cellStore.ApplyAsync(
                new CellChangeSet(
                    target,
                    records,
                    Deletes: [],
                    TouchedRowIds: [.. records.Select(u => u.Address.TableRowId).Distinct()],
                    ChangedByUserId: SystemUserId,
                    IsLateEdit: false),
                ct).ConfigureAwait(false);
        }

        await uow.SaveChangesAsync(ct).ConfigureAwait(false);

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
        IReadOnlyDictionary<string, long> rowIds,
        PeriodKey periodKey,
        SliceEvaluationContext context,
        Dictionary<CellKey, Ecr.Expressions.Evaluation.ExpressionValue> values,
        List<CellRecord> upserts)
    {
        var parsed = formulaEngine.Parse(formula.Expression, formula.Dialect);
        if (parsed.Expression is null)
        {
            // Непридатний вираз не проходить публікацію; якщо він тут — це
            // розбіжність між збереженим і чинним, і мовчки писати нуль було б
            // гірше, ніж не писати нічого.
            return;
        }

        foreach (var (rowKey, columnDefId) in Targets(table, formula, rowIds))
        {
            if (!rowIds.TryGetValue(rowKey, out var rowId))
            {
                continue;
            }

            context.CurrentTableDefId = table.Id;
            context.CurrentRowKey = rowKey;
            context.CurrentColumnDefId = columnDefId;

            var result = formulaEngine.Evaluate(parsed.Expression, context);

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

            values[new CellKey(0, table.Id, rowKey, columnDefId)] = result.Value;

            upserts.Add(new CellRecord(
                new CellAddress(periodKey, rowId, columnDefId), table.Id, data));
        }
    }

    /// <summary>Комірки, які обчислює формула.</summary>
    /// <remarks>
    /// ⚠ Область формули визначає ціль однозначно (<c>CK_Formula_Scope</c>):
    /// колонкова рахує свою колонку в кожному рядку, рядкова — свій рядок у
    /// кожній колонці, комірочна — рівно одну комірку.
    /// </remarks>
    private static IEnumerable<(string RowKey, int ColumnDefId)> Targets(
        Domain.Entities.Configuration.TableDef table,
        Domain.Entities.Configuration.FormulaDef formula,
        IReadOnlyDictionary<string, long> rowIds)
    {
        var rowKey = FormulaOutputs.RowKeyOf(table, formula);

        switch (formula.Scope)
        {
            case Domain.Enums.FormulaScope.Column when formula.ColumnDefId is { } columnId:
                foreach (var key in rowIds.Keys)
                {
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

    /// <summary>Значення всіх таблиць документа за поточний і суміжні періоди.</summary>
    /// <remarks>
    /// ⚠ Читаються ВСІ таблиці документа за період, а не лише та, у якій
    /// сталася правка: формула цього аркуша має право читати сусідню таблицю,
    /// і без її значень результат був би тихо іншим.
    /// </remarks>
    private async Task<Dictionary<CellKey, Ecr.Expressions.Evaluation.ExpressionValue>> LoadValuesAsync(
        TableInstanceRef instance, CancellationToken ct)
    {
        var values = new Dictionary<CellKey, Ecr.Expressions.Evaluation.ExpressionValue>();
        var periodKey = new PeriodKey(instance.PeriodKey);

        await LoadPeriodAsync(values, instance.DocumentId, periodKey, offset: 0, ct).ConfigureAwait(false);

        // ⛔ Попередній період завантажується, коли він існує. `[Period:-1]` у
        // січні — це `null` за визначенням (`02b` §3.2), а не помилка: січень
        // не має попереднього місяця. Але в лютому це реальні числа, і
        // прочитати їх як порожнечу означало б тихо занизити результат.
        if (periodKey.Sequence > 1)
        {
            var previous = new PeriodKey(periodKey.Value - 1);
            await LoadPeriodAsync(values, instance.DocumentId, previous, offset: -1, ct).ConfigureAwait(false);
        }

        return values;
    }

    /// <summary>Значення всіх таблиць документа за один період.</summary>
    private async Task LoadPeriodAsync(
        Dictionary<CellKey, Ecr.Expressions.Evaluation.ExpressionValue> values,
        long documentId,
        PeriodKey periodKey,
        int offset,
        CancellationToken ct)
    {
        var instances = await rowStore.GetTableInstancesAsync(documentId, periodKey, ct).ConfigureAwait(false);

        foreach (var table in instances)
        {
            var keys = await rowStore.GetRowIdsAsync(table.TableInstanceId, periodKey, ct).ConfigureAwait(false);
            var byId = keys.ToDictionary(pair => pair.Value, pair => pair.Key);

            var cells = await cellStore.ReadSliceAsync(table.TableInstanceId, ct).ConfigureAwait(false);

            foreach (var cell in cells)
            {
                if (!byId.TryGetValue(cell.Address.TableRowId, out var rowKey))
                {
                    continue;
                }

                values[new CellKey(offset, table.TableDefId, rowKey, cell.Address.ColumnDefId)] =
                    FromCellValue(cell.Value);
            }
        }
    }

    /// <summary>Календарний контекст періоду.</summary>
    /// <remarks>
    /// ⚠ Межі періоду тут не резолвляться: формули шаблону, які їх читають,
    /// належать до крос-періодних і в каскад не потрапляють. Підставити
    /// «сьогодні» означало б зробити результат залежним від моменту
    /// перерахунку (<c>ФВ-1.12</c>).
    /// </remarks>
    private static Ecr.Expressions.PeriodContext PeriodOf(PeriodKey periodKey)
        => new(
            new DateOnly(periodKey.Year, Math.Clamp(periodKey.Sequence, 1, 12), 1),
            new DateOnly(periodKey.Year, Math.Clamp(periodKey.Sequence, 1, 12),
                DateTime.DaysInMonth(periodKey.Year, Math.Clamp(periodKey.Sequence, 1, 12))),
            Domain.Enums.CalendarMode.Actual,
            periodKey.Year,
            (byte)periodKey.Sequence);

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

        return value.ValueString is { } text
            ? Ecr.Expressions.Evaluation.ExpressionValue.Text(text)
            : Ecr.Expressions.Evaluation.ExpressionValue.Null;
    }

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

    /// <summary>Чи читає формула інший аркуш.</summary>
    public bool IsCrossSheet(int formulaDefId) => _crossSheet.Contains(formulaDefId);

    /// <summary>Чи є формула знімком.</summary>
    public bool IsSnapshot(int formulaDefId) => _snapshot.Contains(formulaDefId);
}
