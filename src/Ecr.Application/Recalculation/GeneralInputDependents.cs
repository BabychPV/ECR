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

    /// <summary>
    /// ✎ B79 (квіз 16.11): аркуш формули-читача невідомий (індекс зібрано без нього, <see cref="From(IEnumerable{FormulaDependency})"/>) —
    /// правило вважає читачем аркуш самого стовпця.
    /// </summary>
    public const int UnknownReaderSheet = 0;

    private static readonly IReadOnlySet<int> NoReaders = new HashSet<int>();

    private readonly Dictionary<(int TableDefId, int ColumnDefId), HashSet<int>> _columns;
    private readonly Dictionary<int, HashSet<int>> _tables;

    private GeneralInputDependents(
        Dictionary<(int, int), HashSet<int>> columns, Dictionary<int, HashSet<int>> tables)
    {
        _columns = columns;
        _tables = tables;
    }

    /// <summary>Чи є хоч один стовпець із залежними (порожній індекс — правило B79 мовчить).</summary>
    public bool IsEmpty => _columns.Count == 0 && _tables.Count == 0;

    /// <summary>Будує індекс із графа залежностей формул версії (аркуш читача невідомий).</summary>
    /// <param name="dependencies">Залежності живих формул версії.</param>
    public static GeneralInputDependents From(IEnumerable<FormulaDependency> dependencies)
    {
        ArgumentNullException.ThrowIfNull(dependencies);
        return From(dependencies.Select(d => (d, UnknownReaderSheet)));
    }

    /// <summary>
    /// ✎ B79 (квіз 16.11, «і користувач має побачити чому»): індекс з аркушем формули-читача — правило блокують
    /// затверджені місяці БУДЬ-ЯКОГО аркуша, чия формула читає стовпець, і відмова їх називає.
    /// </summary>
    /// <param name="dependencies">Залежності живих формул версії з аркушем формули.</param>
    public static GeneralInputDependents From(IEnumerable<(FormulaDependency Dependency, int ReaderSheetDefId)> dependencies)
    {
        ArgumentNullException.ThrowIfNull(dependencies);

        var columns = new Dictionary<(int, int), HashSet<int>>();
        var tables = new Dictionary<int, HashSet<int>>();

        foreach (var (dependency, readerSheet) in dependencies)
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

            HashSet<int>? readers;
            if (wholeTable)
            {
                if (!tables.TryGetValue(tableDefId, out readers))
                {
                    tables[tableDefId] = readers = [];
                }
            }
            else if (!columns.TryGetValue((tableDefId, dependency.ColumnDefId!.Value), out readers))
            {
                columns[(tableDefId, dependency.ColumnDefId!.Value)] = readers = [];
            }

            readers.Add(readerSheet);
        }

        return columns.Count == 0 && tables.Count == 0 ? None : new GeneralInputDependents(columns, tables);
    }

    /// <summary>Чи читає хоч одна формула стовпець <paramref name="columnDefId"/> таблиці <paramref name="tableDefId"/>.</summary>
    /// <param name="tableDefId">Таблиця стовпця.</param>
    /// <param name="columnDefId">Стовпець.</param>
    public bool Reads(int tableDefId, int columnDefId)
        => _tables.ContainsKey(tableDefId) || _columns.ContainsKey((tableDefId, columnDefId));

    /// <summary>
    /// ✎ B79 (квіз 16.11): аркуші формул, що читають стовпець (<see cref="UnknownReaderSheet"/> — аркуш невідомий);
    /// порожньо — читачів немає.
    /// </summary>
    /// <param name="tableDefId">Таблиця стовпця.</param>
    /// <param name="columnDefId">Стовпець.</param>
    public IReadOnlySet<int> ReaderSheets(int tableDefId, int columnDefId)
    {
        var byTable = _tables.GetValueOrDefault(tableDefId);
        var byColumn = _columns.GetValueOrDefault((tableDefId, columnDefId));
        if (byTable is null)
        {
            return byColumn ?? NoReaders;
        }

        if (byColumn is null)
        {
            return byTable;
        }

        var union = new HashSet<int>(byTable);
        union.UnionWith(byColumn);
        return union;
    }

    /// <summary>Усі аркуші-читачі індексу (для одного запиту станів), без <see cref="UnknownReaderSheet"/>.</summary>
    public IReadOnlySet<int> AllReaderSheets()
    {
        var all = new HashSet<int>();
        foreach (var readers in _tables.Values.Concat(_columns.Values))
        {
            all.UnionWith(readers);
        }

        all.Remove(UnknownReaderSheet);
        return all;
    }
}
