using Ecr.Domain.Entities.Configuration;

namespace Ecr.Application.Recalculation;

/// <summary>
/// ✎ B79: які введені стовпці версії шаблону читає хоч одна формула — індекс «стовпець → є залежні розрахунки».
/// </summary>
/// <remarks>
/// <para>
/// Правило те саме, яким каскад визначає читачів (<see cref="CrossPeriodReaders"/>), і з тією самою точністю — до
/// стовпця таблиці, не до рядка: залежність-комірка зі зсувом 0 (<c>ReadsRoutedInput</c>) і міжперіодні ребра
/// (<c>[Period:-1]</c>, <c>[Period:#k]</c>; предикатне посилання будить будь-який стовпець своєї таблиці).
/// </para>
/// <para>
/// ⚠ Лише ФОРМУЛИ шаблону (<c>cfg.FormulaDependency</c>, <c>SourceKind = 0</c>). Входи методологій (аргументи
/// <c>@Код</c>, <c>MethodologyRequiredInput</c>, <c>MatchJson</c> прив'язки) у графі версії не зберігаються
/// (<see cref="FormulaDependency.ForBinding"/> не пише ніхто), а версії методологій змінюються незалежно від ревізії
/// шаблону — їхній індекс потребує окремої проєкції зі своїм скиданням кешу.
/// </para>
/// </remarks>
public sealed class GeneralInputDependents
{
    /// <summary>Вид похідних даних версії (<c>IMetadataCache.GetDerivedAsync</c>).</summary>
    public const string DerivedKind = "b79-general-input-dependents";

    private const byte CellKind = 0;
    private const byte CrossPeriodKind = 3;
    private const byte AbsoluteKind = 5;

    /// <summary>Жоден стовпець залежних не має.</summary>
    public static GeneralInputDependents None { get; } = new([], []);

    private readonly HashSet<(int TableDefId, int ColumnDefId)> _columns;
    private readonly HashSet<int> _tables;

    private GeneralInputDependents(HashSet<(int, int)> columns, HashSet<int> tables)
    {
        _columns = columns;
        _tables = tables;
    }

    /// <summary>Чи є хоч один стовпець із залежними (порожній індекс — правило B79 мовчить).</summary>
    public bool IsEmpty => _columns.Count == 0 && _tables.Count == 0;

    /// <summary>Будує індекс із графа залежностей формул версії.</summary>
    /// <param name="dependencies">Залежності живих формул версії.</param>
    public static GeneralInputDependents From(IEnumerable<FormulaDependency> dependencies)
    {
        ArgumentNullException.ThrowIfNull(dependencies);

        var columns = new HashSet<(int, int)>();
        var tables = new HashSet<int>();

        foreach (var dependency in dependencies)
        {
            if (dependency.FormulaDefId is null || dependency.TableDefId is not { } tableDefId)
            {
                continue;
            }

            bool wholeTable;
            switch (dependency.DependsOnKind)
            {
                case CellKind when dependency.PeriodOffset is null or 0:
                    wholeTable = dependency.ColumnDefId is null;
                    break;

                case CrossPeriodKind or AbsoluteKind when dependency.PeriodOffset is not null and not 0:
                    wholeTable = dependency.ColumnDefId is null
                                 || (dependency.RowKey is null && dependency.FilterJson is not null);
                    break;

                default:
                    continue;
            }

            if (wholeTable)
            {
                tables.Add(tableDefId);
            }
            else
            {
                columns.Add((tableDefId, dependency.ColumnDefId!.Value));
            }
        }

        return columns.Count == 0 && tables.Count == 0 ? None : new GeneralInputDependents(columns, tables);
    }

    /// <summary>Чи читає хоч одна формула стовпець <paramref name="columnDefId"/> таблиці <paramref name="tableDefId"/>.</summary>
    /// <param name="tableDefId">Таблиця стовпця.</param>
    /// <param name="columnDefId">Стовпець.</param>
    public bool Reads(int tableDefId, int columnDefId)
        => _tables.Contains(tableDefId) || _columns.Contains((tableDefId, columnDefId));
}
