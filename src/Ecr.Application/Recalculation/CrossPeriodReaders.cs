using Ecr.Domain.Entities.Configuration;

namespace Ecr.Application.Recalculation;

/// <summary>
/// Що змінилося в іншому періоді документа: номер періоду-джерела в році, таблиця й колонка.
/// </summary>
/// <param name="SourceSequence">Номер періоду-джерела в році (<c>PeriodKey.Sequence</c>).</param>
/// <param name="TableDefId">Таблиця, у якій змінилася колонка.</param>
/// <param name="ColumnDefId">Змінена колонка.</param>
public readonly record struct CrossPeriodSource(int SourceSequence, int TableDefId, int ColumnDefId);

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
    {
        ArgumentNullException.ThrowIfNull(dependencies);
        ArgumentNullException.ThrowIfNull(changed);
        ArgumentNullException.ThrowIfNull(existingSequences);

        var result = new SortedSet<int>();
        if (changed.Count == 0)
        {
            return result;
        }

        var existing = existingSequences as ISet<int> ?? new HashSet<int>(existingSequences);

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
        var affected = new HashSet<int>();

        foreach (var dependency in dependencies)
        {
            if (dependency.FormulaDefId is not { } formulaId
                || !live.Contains(formulaId)
                || dependency.PeriodOffset is not { } value
                || value == 0)
            {
                continue;
            }

            foreach (var source in sources)
            {
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
