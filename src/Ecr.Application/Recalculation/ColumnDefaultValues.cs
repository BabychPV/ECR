// src/Ecr.Application/Recalculation/ColumnDefaultValues.cs
using System.Globalization;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Recalculation;

/// <summary>
/// ✎ C-5 (рішення PO 11.11, 02b §6.3): значення за замовчуванням колонок версії як значення комірки — коли рядок є,
/// а запису комірки немає. Явна порожнеча (<c>IsEmpty = 1</c>) дефолту не отримує. Не пишеться ніде (D-283 Q10=A):
/// підстановка лише при ЧИТАННІ — у формулах, перегляді рядка, валідації й методологіях.
/// </summary>
/// <remarks>
/// Версія без дефолтів не платить нічого (перебір колонок кешованого знімка). Дефолти Lookup — КОД запису довідника
/// (як <c>NOTPASSEDOVERHAUL</c> у Land), а комірка тримає id: одне читання на довідник при побудові, далі — похідне
/// версії з кешу метаданих.
/// </remarks>
public sealed class ColumnDefaultValues(IMetadataCache metadata, IRegistryStore registries)
{
    /// <summary>Вид похідних даних версії (<see cref="IMetadataCache.GetDerivedAsync{T}"/>).</summary>
    public const string DerivedKind = "c5-column-defaults";

    /// <summary>Дефолти колонок версії.</summary>
    public async Task<ColumnDefaultSet> ForVersionAsync(int templateVersionId, CancellationToken ct)
    {
        var snapshot = await metadata.GetAsync(templateVersionId, ct).ConfigureAwait(false);
        if (snapshot is null || !Candidates(snapshot).Any())
        {
            return ColumnDefaultSet.Empty;
        }

        // Лише значення з Lookup потребують походу в базу — їх і кешуємо похідним версії. Підміна кешу без
        // реалізації похідних (null) — будуємо напряму.
        if (!Candidates(snapshot).Any(c => c.Column.DataType == CellDataType.Lookup))
        {
            return await BuildAsync(snapshot, ct).ConfigureAwait(false);
        }

        var cached = await metadata.GetDerivedAsync(
                templateVersionId, DerivedKind, token => BuildAsync(snapshot, token), ct,
                // Код, якого (ще) немає в довіднику, — не довше за вікно ревізії (той самий прийом, що D-461 F3).
                shortLived: set => set.HasUnresolvedLookup)
            .ConfigureAwait(false);
        return cached ?? await BuildAsync(snapshot, ct).ConfigureAwait(false);
    }

    /// <summary>Вхідні колонки з дефолтом разом із таблицею, якій вони належать у знімку.</summary>
    private static IEnumerable<(int TableDefId, ColumnDef Column)> Candidates(TemplateVersionSnapshot snapshot)
        => snapshot.Sheets.Where(s => !s.IsDeleted).SelectMany(s => s.Tables).Where(t => !t.IsDeleted)
            .SelectMany(t => t.Columns
                .Where(c => !c.IsDeleted && !c.IsComputed && !string.IsNullOrWhiteSpace(c.DefaultValue))
                .Select(c => (t.Id, c)));

    private async Task<ColumnDefaultSet> BuildAsync(TemplateVersionSnapshot snapshot, CancellationToken ct)
    {
        var values = new Dictionary<int, (int TableDefId, CellValueData Value)>();
        var lookups = new List<(int TableDefId, ColumnDef Column)>();
        foreach (var (tableDefId, column) in Candidates(snapshot))
        {
            if (column.DataType == CellDataType.Lookup)
            {
                lookups.Add((tableDefId, column));
            }
            else if (Parse(column) is { } value)
            {
                values[column.Id] = (tableDefId, value);
            }
        }

        var unresolved = false;
        foreach (var group in lookups
                     .Where(c => c.Column.LookupRegistryDefId is not null)
                     .GroupBy(c => c.Column.LookupRegistryDefId!.Value))
        {
            var codes = group.Select(c => c.Column.DefaultValue!.Trim()).Distinct(StringComparer.Ordinal).ToList();
            var entries = await registries.FindEntriesByCodesAsync(group.Key, codes, ct).ConfigureAwait(false) ?? [];
            foreach (var (tableDefId, column) in group)
            {
                var text = column.DefaultValue!.Trim();
                var entry = entries.FirstOrDefault(e => string.Equals(e.Code.ToString(), text, StringComparison.Ordinal));
                if (entry is not null)
                {
                    values[column.Id] = (tableDefId, new CellValueData { ValueRegistryEntryId = entry.Id });
                }
                else
                {
                    unresolved = true;
                }
            }
        }

        return new ColumnDefaultSet(values, unresolved);
    }

    /// <summary>
    /// Текст дефолту → значення за типом колонки (інваріантна культура; запасний — десяткова кома, як у Land-шаблонах).
    /// Непридатний дефолт (текст у числовій колонці) — як відсутній: сітка покаже текст як є.
    /// </summary>
    internal static CellValueData? Parse(ColumnDef column)
    {
        // null — машинний запис (Invariant, крапка), як PATCH інтеграції; далі — культура з комою.
        foreach (var culture in new CultureInfo?[] { null, Localization.NumberCulture.ForLanguage("ru") })
        {
            try
            {
                if (Documents.CellValueReader.Read(column.DefaultValue!.Trim(), column, culture) is { IsEmpty: false } value)
                {
                    return value;
                }

                return null;
            }
            catch (BusinessRuleException)
            {
                // наступна культура
            }
        }

        return null;
    }
}

/// <summary>✎ C-5: дефолти колонок версії за таблицею.</summary>
public sealed class ColumnDefaultSet
{
    /// <summary>Немає жодного дефолту.</summary>
    public static ColumnDefaultSet Empty { get; } = new(new Dictionary<int, (int, CellValueData)>(), false);

    private readonly Dictionary<int, List<(int ColumnDefId, CellValueData Value)>> _byTable;

    internal ColumnDefaultSet(IReadOnlyDictionary<int, (int TableDefId, CellValueData Value)> byColumn, bool hasUnresolvedLookup)
    {
        _byTable = byColumn.GroupBy(p => p.Value.TableDefId)
            .ToDictionary(g => g.Key, g => g.Select(p => (p.Key, p.Value.Value)).ToList());
        HasUnresolvedLookup = hasUnresolvedLookup;
    }

    /// <summary>Є Lookup-дефолт, коду якого немає в довіднику.</summary>
    public bool HasUnresolvedLookup { get; }

    /// <summary>Жодної таблиці з дефолтами.</summary>
    public bool IsEmpty => _byTable.Count == 0;

    /// <summary>Дефолти колонок таблиці (порожньо — немає).</summary>
    public IReadOnlyList<(int ColumnDefId, CellValueData Value)> Of(int tableDefId)
        => _byTable.TryGetValue(tableDefId, out var list) ? list : [];

    /// <summary>
    /// Зріз таблиці + віртуальні комірки дефолтів для пар (рядок, колонка), яких у зрізі НЕМАЄ. Наявна комірка
    /// (зокрема явна порожнеча <c>IsEmpty = 1</c>) не замінюється (02b §6.3).
    /// </summary>
    public IReadOnlyList<CellRecord> Complete(
        IReadOnlyList<CellRecord> cells, int tableDefId, IReadOnlyDictionary<string, long> rowIds, PeriodKey period)
    {
        ArgumentNullException.ThrowIfNull(cells);
        ArgumentNullException.ThrowIfNull(rowIds);
        var defaults = Of(tableDefId);
        if (defaults.Count == 0 || rowIds.Count == 0)
        {
            return cells;
        }

        var present = cells.Select(c => (c.Address.TableRowId, c.Address.ColumnDefId)).ToHashSet();
        var result = new List<CellRecord>(cells);
        foreach (var rowId in rowIds.Values)
        {
            foreach (var (columnDefId, value) in defaults)
            {
                if (!present.Contains((rowId, columnDefId)))
                {
                    result.Add(new CellRecord(new CellAddress(period, rowId, columnDefId), tableDefId, value));
                }
            }
        }

        return result;
    }
}
