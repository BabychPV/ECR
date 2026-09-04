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
    IMetadataCache metadata,
    IFormulaEngine formulaEngine)
{
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

    /// <summary>Перераховує залежне піддерево.</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="dirty">Змінені комірки.</param>
    /// <param name="ct">Токен скасування.</param>
    public Task RecalculateAsync(long documentId, PeriodKey periodKey, DirtySet dirty, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(dirty);

        // ⚠ Запис результатів у doc.CellValue разом із побудовою контексту
        // обчислення — Етап 5 (`ICellStore` пакетно, ФВ-12.1). Тут завершена
        // частина, яка визначає ЩО і В ЯКОМУ ПОРЯДКУ рахувати: саме вона
        // відрізняє інкрементний перерахунок від повного.
        return Task.CompletedTask;
    }
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
