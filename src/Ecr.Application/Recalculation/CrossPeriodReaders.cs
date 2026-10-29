using Ecr.Domain.Entities.Configuration;

namespace Ecr.Application.Recalculation;

/// <summary>
/// Що змінилося в іншому періоді документа: номер періоду-джерела в році, таблиця й колонка.
/// </summary>
/// <param name="SourceSequence">Номер періоду-джерела в році (<c>PeriodKey.Sequence</c>).</param>
/// <param name="TableDefId">Таблиця, у якій змінилася колонка.</param>
/// <param name="ColumnDefId">Змінена колонка.</param>
/// <param name="OwnerInput">
/// Q-17: змінено ВВЕДЕНИЙ стовпець таблиці «місяці в колонках», чиї значення читають усі екземпляри року через
/// роутер власності: читачі — формули ІНШИХ періодів із залежністю-коміркою з зсувом 0 на цю колонку.
/// </param>
public readonly record struct CrossPeriodSource(
    int SourceSequence, int TableDefId, int ColumnDefId, bool OwnerInput = false);

/// <summary>
/// Зворотний індекс МІЖПЕРІОДНИХ залежностей формул: хто читає змінену колонку з іншого періоду
/// (WW-03, закриття G2 дизайну).
/// </summary>
/// <remarks>
/// ⛔ <see cref="RecalculationPlanBuilder"/> навмисно не кладе міжперіодне ребро в зворотний індекс
/// плану: план адресує комірки ОДНОГО періоду. Без цього класу правка січня не перераховувала б
/// формулу лютого з <c>[Period:-1]</c> (і 7.0 періоду m, що читає <c>[Period:#k]</c>) — число
/// лишалося б старим без жодної помилки. Тут рахується лише «хто» і «який період»; саме
/// перерахунок — <see cref="RecalculationService.RecalculateFromOtherPeriodsAsync"/>.
/// <para>
/// ⚠ Точність — до колонки таблиці, а не до рядка: одна правка не коштує розбору рядків інших
/// періодів, а зайвий перерахунок формули дешевший за пропущений.
/// </para>
/// </remarks>
public static class CrossPeriodReaders
{
    private const byte CrossPeriodKind = 3;
    private const byte AbsoluteKind = 5;
    private const byte CellKind = 0;

    /// <summary>
    /// Номери періодів року, формули яких читають змінені колонки з періоду <paramref name="sourceSequence"/>.
    /// </summary>
    /// <param name="dependencies">Граф залежностей версії шаблону.</param>
    /// <param name="changed">Змінені колонки періоду-джерела.</param>
    /// <param name="sourceSequence">Період, у якому сталася зміна.</param>
    /// <param name="existingSequences">Періоди року, які існують у документі.</param>
    /// <returns>Номери періодів-читачів; без самого джерела.</returns>
    public static IReadOnlySet<int> ReaderSequences(
        IReadOnlyList<FormulaDependency> dependencies,
        IReadOnlyCollection<(int TableDefId, int ColumnDefId)> changed,
        int sourceSequence,
        IReadOnlyCollection<int> existingSequences)
        => ReaderSequences(dependencies, changed, [], sourceSequence, existingSequences);

    /// <summary>
    /// Q-17: номери періодів-читачів із урахуванням змінених ВВЕДЕНИХ стовпців таблиць «місяці в колонках»,
    /// чиє значення живе в одному екземплярі (власник) і читається з усіх інших через роутер власності.
    /// </summary>
    /// <param name="dependencies">Граф залежностей версії шаблону.</param>
    /// <param name="changed">Змінені колонки періоду-джерела (міжперіодні ребра зі зсувом ≠ 0).</param>
    /// <param name="routedInputs">
    /// Змінені введені стовпці таблиць із маршрутизацією: таблиця, колонка, номер місяця колонки
    /// (<c>ColumnDef.MonthNumber</c>; <c>null</c> — не-місячний стовпець, власник якого — якір року).
    /// </param>
    /// <param name="sourceSequence">Період, у якому відбувся каскад (там формули вже перераховано локально).</param>
    /// <param name="existingSequences">Періоди року, які існують у документі.</param>
    /// <returns>Номери періодів-читачів; без самого джерела.</returns>
    /// <remarks>
    /// ⛔ Нове правило: залежність-комірка (<c>DependsOnKind = 0</c>) зі зсувом <c>null</c>/0 на змінену введену колонку
    /// робить читачем КОЖЕН наявний період року, окрім джерела. Раніше такий читач був би «сам собі»: комірку
    /// власника читали лише з власного екземпляра, а тепер значення M{m} видно у зрізі кожного екземпляра року.
    /// Виключається лише період каскаду: коли правку зроблено з перегляду іншого періоду, власник місячного
    /// стовпця (<c>MonthNumber</c>) теж серед читачів — його власний прогін не залежить від того, хто поставив каскад.
    /// <c>MonthNumber</c> тут довідковий: правило не звужується ним, бо «зайвий перерахунок дешевший за пропущений».
    /// </remarks>
    public static IReadOnlySet<int> ReaderSequences(
        IReadOnlyList<FormulaDependency> dependencies,
        IReadOnlyCollection<(int TableDefId, int ColumnDefId)> changed,
        IReadOnlyCollection<(int TableDefId, int ColumnDefId, int? MonthNumber)> routedInputs,
        int sourceSequence,
        IReadOnlyCollection<int> existingSequences)
    {
        ArgumentNullException.ThrowIfNull(dependencies);
        ArgumentNullException.ThrowIfNull(changed);
        ArgumentNullException.ThrowIfNull(routedInputs);
        ArgumentNullException.ThrowIfNull(existingSequences);

        var result = new SortedSet<int>();
        var existing = existingSequences as ISet<int> ?? new HashSet<int>(existingSequences);

        if (ReadsRoutedInput(dependencies, routedInputs).Count > 0)
        {
            foreach (var sequence in existing)
            {
                if (sequence != sourceSequence)
                {
                    result.Add(sequence);
                }
            }
        }

        if (changed.Count == 0)
        {
            return result;
        }

        foreach (var dependency in dependencies)
        {
            if (dependency.FormulaDefId is null
                || dependency.PeriodOffset is not { } value
                || value == 0
                || !Matches(dependency, changed))
            {
                continue;
            }

            switch (dependency.DependsOnKind)
            {
                case CrossPeriodKind:
                    // Читач у періоді r читає r+value; джерело s ⇒ r = s − value.
                    var reader = sourceSequence - value;
                    if (reader != sourceSequence && existing.Contains(reader))
                    {
                        result.Add(reader);
                    }

                    break;

                case AbsoluteKind when value == sourceSequence:
                    // Формула шаблону — одна на всі періоди: читач — будь-який інший період року.
                    foreach (var sequence in existing)
                    {
                        if (sequence != sourceSequence)
                        {
                            result.Add(sequence);
                        }
                    }

                    break;

                default:
                    break;
            }
        }

        return result;
    }

    /// <summary>
    /// Формули періоду-читача <paramref name="readerSequence"/>, що читають наведені зміни, плюс усі
    /// їхні залежні в межах періоду; без знімків.
    /// </summary>
    /// <param name="dependencies">Граф залежностей версії шаблону.</param>
    /// <param name="sources">Що змінилося в інших періодах.</param>
    /// <param name="readerSequence">Період, що перераховується.</param>
    /// <param name="plan">План цього періоду (залежні формули, порядок, знімки).</param>
    /// <param name="liveFormulas">Формули версії, які існують (не видалені).</param>
    /// <returns>Формули у порядку обчислення.</returns>
    public static List<int> Targets(
        IReadOnlyList<FormulaDependency> dependencies,
        IReadOnlyCollection<CrossPeriodSource> sources,
        int readerSequence,
        RecalculationPlan plan,
        IEnumerable<int> liveFormulas)
    {
        ArgumentNullException.ThrowIfNull(dependencies);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(liveFormulas);

        var live = liveFormulas as ISet<int> ?? new HashSet<int>(liveFormulas);
        var affected = DirectlyAffected(dependencies, sources, readerSequence, live);

        // Транзитивне розкриття — те саме, що в `RecalculationService.Plan`: формула, яка читає
        // результат брудної формули, теж брудна.
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

        var ordered = affected.Where(id => !plan.IsSnapshot(id)).ToList();
        ordered.Sort((a, b) => plan.EvaluationOrder(a).CompareTo(plan.EvaluationOrder(b)));

        return ordered;
    }

    /// <summary>
    /// L1 (затримка перерахунку): чи матиме період-читач <paramref name="readerSequence"/> хоч одну ціль —
    /// тобто чи варто ставити задачу. Це ТОЙ САМИЙ відбір, що й <see cref="Targets"/> (спільний
    /// <see cref="DirectlyAffected"/>), без плану: транзитивні залежні лише ДОДАЮТЬ цілі до прямих, тож порожні
    /// прямі цілі ⇒ порожній <see cref="Targets"/> ⇒ задача повернулася б з <c>written = 0</c>.
    /// </summary>
    /// <param name="dependencies">Граф залежностей версії шаблону ПЕРІОДУ-ЧИТАЧА.</param>
    /// <param name="sources">Що змінилося в інших періодах.</param>
    /// <param name="readerSequence">Період-читач.</param>
    /// <param name="liveFormulaTables">Живі (не видалені) формули версії → таблиця формули.</param>
    /// <param name="presentTables">Таблиці, що мають екземпляр у періоді-читачі.</param>
    /// <returns><c>true</c>, якщо хоч одна пряма ціль живе в таблиці, яка є в періоді-читачі.</returns>
    /// <remarks>
    /// ⛔ Таблиця формули без екземпляра в періоді: <c>RecalculationService</c> такої цілі не пише
    /// («рахувати нема куди»), тому вона не робить період читачем. Безпечно лише ДО того, як
    /// відкинути період; версії з Rollup/Check (<c>HasActiveRollupOrCheck</c>) викликач не відсікає взагалі.
    /// </remarks>
    public static bool HasTargets(
        IReadOnlyList<FormulaDependency> dependencies,
        IReadOnlyCollection<CrossPeriodSource> sources,
        int readerSequence,
        IReadOnlyDictionary<int, int> liveFormulaTables,
        IReadOnlySet<int> presentTables)
    {
        ArgumentNullException.ThrowIfNull(dependencies);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(liveFormulaTables);
        ArgumentNullException.ThrowIfNull(presentTables);

        var live = new HashSet<int>(liveFormulaTables.Keys);

        return DirectlyAffected(dependencies, sources, readerSequence, live)
            .Any(id => presentTables.Contains(liveFormulaTables[id]));
    }

    /// <summary>Формули, що напряму читають зміни інших періодів (до транзитивного розкриття).</summary>
    private static HashSet<int> DirectlyAffected(
        IReadOnlyList<FormulaDependency> dependencies,
        IReadOnlyCollection<CrossPeriodSource> sources,
        int readerSequence,
        ISet<int> live)
    {
        var affected = new HashSet<int>();

        foreach (var dependency in dependencies)
        {
            if (dependency.FormulaDefId is not { } formulaId || !live.Contains(formulaId))
            {
                continue;
            }

            // Q-17: введений стовпець власника (зсув 0) — читач у кожному іншому періоді року.
            if (dependency.DependsOnKind == CellKind
                && dependency.PeriodOffset is null or 0
                && sources.Any(s => s.OwnerInput
                                    && s.SourceSequence != readerSequence
                                    && dependency.TableDefId == s.TableDefId
                                    && (dependency.ColumnDefId is null || dependency.ColumnDefId == s.ColumnDefId)))
            {
                affected.Add(formulaId);
                continue;
            }

            if (dependency.PeriodOffset is not { } value || value == 0)
            {
                continue;
            }

            foreach (var source in sources)
            {
                if (source.OwnerInput)
                {
                    continue;
                }

                var readsSource = dependency.DependsOnKind switch
                {
                    CrossPeriodKind => readerSequence + value == source.SourceSequence,
                    AbsoluteKind => value == source.SourceSequence && readerSequence != source.SourceSequence,
                    _ => false,
                };

                if (readsSource
                    && dependency.TableDefId == source.TableDefId
                    && (dependency.ColumnDefId is null || dependency.ColumnDefId == source.ColumnDefId))
                {
                    affected.Add(formulaId);
                    break;
                }
            }
        }

        return affected;
    }

    /// <summary>
    /// Змінені колонки, які хоч одна формула читає через міжперіодне ребро, — те, що варто нести
    /// в задачу періоду-читача (решта нічого там не змінить).
    /// </summary>
    /// <param name="dependencies">Граф залежностей версії шаблону.</param>
    /// <param name="changed">Змінені колонки періоду-джерела.</param>
    /// <returns>Підмножина <paramref name="changed"/>.</returns>
    public static List<(int TableDefId, int ColumnDefId)> ReadAcrossPeriods(
        IReadOnlyList<FormulaDependency> dependencies,
        IReadOnlyCollection<(int TableDefId, int ColumnDefId)> changed)
    {
        ArgumentNullException.ThrowIfNull(dependencies);
        ArgumentNullException.ThrowIfNull(changed);

        var result = new List<(int TableDefId, int ColumnDefId)>();
        foreach (var column in changed)
        {
            foreach (var dependency in dependencies)
            {
                if (dependency.FormulaDefId is not null
                    && dependency.DependsOnKind is CrossPeriodKind or AbsoluteKind
                    && dependency.PeriodOffset is not null and not 0
                    && dependency.TableDefId == column.TableDefId
                    && (dependency.ColumnDefId is null || dependency.ColumnDefId == column.ColumnDefId))
                {
                    result.Add(column);
                    break;
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Q-17: підмножина змінених введених стовпців, які хоч одна формула читає КОМІРКОЮ зі зсувом 0
    /// (<c>DependsOnKind = 0</c>) — те, що варто нести в задачу періоду-читача.
    /// </summary>
    /// <param name="dependencies">Граф залежностей версії шаблону.</param>
    /// <param name="routedInputs">Змінені введені стовпці таблиць «місяці в колонках».</param>
    /// <returns>Підмножина <paramref name="routedInputs"/>.</returns>
    public static List<(int TableDefId, int ColumnDefId, int? MonthNumber)> ReadsRoutedInput(
        IReadOnlyList<FormulaDependency> dependencies,
        IReadOnlyCollection<(int TableDefId, int ColumnDefId, int? MonthNumber)> routedInputs)
    {
        ArgumentNullException.ThrowIfNull(dependencies);
        ArgumentNullException.ThrowIfNull(routedInputs);

        var result = new List<(int TableDefId, int ColumnDefId, int? MonthNumber)>();
        foreach (var input in routedInputs)
        {
            foreach (var dependency in dependencies)
            {
                if (dependency.FormulaDefId is not null
                    && dependency.DependsOnKind == CellKind
                    && dependency.PeriodOffset is null or 0
                    && dependency.TableDefId == input.TableDefId
                    && (dependency.ColumnDefId is null || dependency.ColumnDefId == input.ColumnDefId))
                {
                    result.Add(input);
                    break;
                }
            }
        }

        return result;
    }

    private static bool Matches(
        FormulaDependency dependency, IReadOnlyCollection<(int TableDefId, int ColumnDefId)> changed)
    {
        foreach (var (tableDefId, columnDefId) in changed)
        {
            if (dependency.TableDefId == tableDefId
                && (dependency.ColumnDefId is null || dependency.ColumnDefId == columnDefId))
            {
                return true;
            }
        }

        return false;
    }
}
