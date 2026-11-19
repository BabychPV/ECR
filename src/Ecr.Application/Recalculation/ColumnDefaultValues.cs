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
///
/// ✎ B175: дефолти РЯДКА (<see cref="RowDef.CellDefaults"/>) — той самий механізм на рівні фіксованого рядка.
/// Пріоритет на пару (рядок, колонка): записана комірка (зокрема явна порожнеча) &gt; дефолт рядка &gt; дефолт
/// колонки; дефолт рядка <c>""</c> прибирає дефолт колонки в цьому рядку.
/// </remarks>
public sealed class ColumnDefaultValues(IMetadataCache metadata, IRegistryStore registries)
{
    /// <summary>Вид похідних даних версії (<see cref="IMetadataCache.GetDerivedAsync{T}"/>).</summary>
    public const string DerivedKind = "c5-column-defaults";

    /// <summary>Дефолти колонок версії.</summary>
    public async Task<ColumnDefaultSet> ForVersionAsync(int templateVersionId, CancellationToken ct)
    {
        var snapshot = await metadata.GetAsync(templateVersionId, ct).ConfigureAwait(false);
        if (snapshot is null || (!Candidates(snapshot).Any() && !RowCandidates(snapshot).Any()))
        {
            return ColumnDefaultSet.Empty;
        }

        // Лише значення з Lookup потребують походу в базу — їх і кешуємо похідним версії. Підміна кешу без
        // реалізації похідних (null) — будуємо напряму.
        if (!Candidates(snapshot).Any(c => c.Column.DataType == CellDataType.Lookup)
            && !RowCandidates(snapshot).Any(c => c.Column.DataType == CellDataType.Lookup && c.Text.Length > 0))
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

    /// <summary>
    /// ✎ B124/B131: вхідні колонки таблиці з ПРИДАТНИМ дефолтом — насіння перерахунку нового рядка (D-491: дефолт
    /// входить в агрегати, тож новий рядок їх змінює). Умова — та сама, що в <see cref="BuildAsync"/>: непридатний
    /// дефолт (<see cref="Parse(ColumnDef)"/> → <c>null</c>) як відсутній; Lookup розв'язується довідником лише при
    /// читанні, тож береться завжди (зайвий перерахунок дешевший за пропущений). Таблиця без дефолтів — порожньо.
    /// ✎ B175: плюс колонки, на які посилається дефолт будь-якого живого рядка (зокрема <c>""</c>, що прибирає
    /// дефолт колонки, — він теж змінює значення нового рядка).
    /// </summary>
    /// <param name="table">Таблиця версії.</param>
    public static IReadOnlyList<int> SeedColumns(TableDef table)
    {
        ArgumentNullException.ThrowIfNull(table);

        var rowCodes = table.Rows
            .Where(r => !r.IsDeleted)
            .SelectMany(r => r.CellDefaults.Keys)
            .ToHashSet(StringComparer.Ordinal);

        return
        [
            .. table.Columns
                .Where(c => !c.IsDeleted && !c.IsComputed
                            && ((!string.IsNullOrWhiteSpace(c.DefaultValue)
                                 && (c.DataType == CellDataType.Lookup || Parse(c) is not null))
                                || rowCodes.Contains(c.Code)))
                .Select(c => c.Id),
        ];
    }

    /// <summary>Вхідні колонки з дефолтом разом із таблицею, якій вони належать у знімку.</summary>
    private static IEnumerable<(int TableDefId, ColumnDef Column)> Candidates(TemplateVersionSnapshot snapshot)
        => snapshot.Sheets.Where(s => !s.IsDeleted).SelectMany(s => s.Tables).Where(t => !t.IsDeleted)
            .SelectMany(t => t.Columns
                .Where(c => !c.IsDeleted && !c.IsComputed && !string.IsNullOrWhiteSpace(c.DefaultValue))
                .Select(c => (t.Id, c)));

    /// <summary>
    /// ✎ B175: дефолти живих рядків на живі вхідні колонки (код колонки, якої немає, — пропускається: публікація
    /// таке відхиляє, а в чернетці це не привід ламати читання). <c>Text</c> обрізаний; порожній — «прибрати».
    /// </summary>
    private static IEnumerable<(int TableDefId, string RowKey, ColumnDef Column, string Text)> RowCandidates(
        TemplateVersionSnapshot snapshot)
        => snapshot.Sheets.Where(s => !s.IsDeleted).SelectMany(s => s.Tables).Where(t => !t.IsDeleted)
            .SelectMany(t => t.Rows
                .Where(r => !r.IsDeleted && r.CellDefaults.Count > 0)
                .SelectMany(r => r.CellDefaults
                    .Select(d => (Code: d.Key, Text: (d.Value ?? string.Empty).Trim(),
                        Column: t.Columns.FirstOrDefault(c => !c.IsDeleted && string.Equals(c.Code, d.Key, StringComparison.Ordinal))))
                    .Where(d => d.Column is { IsComputed: false })
                    .Select(d => (t.Id, r.RowKeyValue, d.Column!, d.Text))));

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

        // ✎ B175: дефолти рядків. null — «прибрати дефолт колонки» (порожній текст).
        var rowValues = new List<RowDefault>();
        var rowLookups = new List<(int TableDefId, string RowKey, ColumnDef Column, string Text)>();
        foreach (var candidate in RowCandidates(snapshot))
        {
            if (candidate.Text.Length == 0)
            {
                rowValues.Add(new RowDefault(candidate.TableDefId, candidate.RowKey, candidate.Column.Id, null));
            }
            else if (candidate.Column.DataType == CellDataType.Lookup)
            {
                rowLookups.Add(candidate);
            }
            else if (Parse(candidate.Column, candidate.Text) is { } value)
            {
                rowValues.Add(new RowDefault(candidate.TableDefId, candidate.RowKey, candidate.Column.Id, value));
            }
        }

        var unresolved = false;

        // Один запит на довідник для дефолтів і колонок, і рядків — той самий шлях, що й до B175.
        var requests = lookups
            .Where(c => c.Column.LookupRegistryDefId is not null)
            .Select(c => (Registry: c.Column.LookupRegistryDefId!.Value, Text: c.Column.DefaultValue!.Trim()))
            .Concat(rowLookups
                .Where(c => c.Column.LookupRegistryDefId is not null)
                .Select(c => (Registry: c.Column.LookupRegistryDefId!.Value, c.Text)))
            .GroupBy(r => r.Registry);
        var entriesByRegistry = new Dictionary<int, IReadOnlyList<Domain.Entities.Dictionaries.RegistryEntry>>();
        foreach (var group in requests)
        {
            var codes = group.Select(r => r.Text).Distinct(StringComparer.Ordinal).ToList();
            entriesByRegistry[group.Key] =
                await registries.FindEntriesByCodesAsync(group.Key, codes, ct).ConfigureAwait(false) ?? [];
        }

        long? Resolve(ColumnDef column, string text)
        {
            if (column.LookupRegistryDefId is not { } registry
                || !entriesByRegistry.TryGetValue(registry, out var entries))
            {
                return null;
            }

            return entries.FirstOrDefault(e => string.Equals(e.Code.ToString(), text, StringComparison.Ordinal))?.Id;
        }

        foreach (var (tableDefId, column) in lookups.Where(c => c.Column.LookupRegistryDefId is not null))
        {
            if (Resolve(column, column.DefaultValue!.Trim()) is { } id)
            {
                values[column.Id] = (tableDefId, new CellValueData { ValueRegistryEntryId = id });
            }
            else
            {
                unresolved = true;
            }
        }

        foreach (var (tableDefId, rowKey, column, text) in rowLookups.Where(c => c.Column.LookupRegistryDefId is not null))
        {
            if (Resolve(column, text) is { } id)
            {
                rowValues.Add(new RowDefault(tableDefId, rowKey, column.Id, new CellValueData { ValueRegistryEntryId = id }));
            }
            else
            {
                // Невідомий код рядка — як відсутній дефолт рядка (діє дефолт колонки); кеш — короткий.
                unresolved = true;
            }
        }

        return new ColumnDefaultSet(values, unresolved, rowValues);
    }

    /// <summary>
    /// Текст дефолту → значення за типом колонки (інваріантна культура; запасний — десяткова кома, як у Land-шаблонах).
    /// Непридатний дефолт (текст у числовій колонці) — як відсутній: сітка покаже текст як є.
    /// </summary>
    internal static CellValueData? Parse(ColumnDef column) => Parse(column, column.DefaultValue!);

    /// <summary>✎ B175: те саме для довільного тексту (дефолт рядка).</summary>
    internal static CellValueData? Parse(ColumnDef column, string text)
    {
        // null — машинний запис (Invariant, крапка), як PATCH інтеграції; далі — культура з комою.
        foreach (var culture in new CultureInfo?[] { null, Localization.NumberCulture.ForLanguage("ru") })
        {
            try
            {
                if (Documents.CellValueReader.Read(text.Trim(), column, culture) is { IsEmpty: false } value)
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

/// <summary>✎ B175: дефолт рядка; <c>Value = null</c> — прибрати дефолт колонки в цьому рядку.</summary>
internal sealed record RowDefault(int TableDefId, string RowKey, int ColumnDefId, CellValueData? Value);

/// <summary>✎ C-5: дефолти колонок версії за таблицею (✎ B175: і рядків).</summary>
public sealed class ColumnDefaultSet
{
    /// <summary>Немає жодного дефолту.</summary>
    public static ColumnDefaultSet Empty { get; } = new(new Dictionary<int, (int, CellValueData)>(), false);

    private readonly Dictionary<int, List<(int ColumnDefId, CellValueData Value)>> _byTable;

    /// <summary>✎ B175: таблиця → ключ рядка → ефективні дефолти рядка (колонки з урахуванням перекриття).</summary>
    private readonly Dictionary<int, Dictionary<string, List<(int ColumnDefId, CellValueData Value)>>> _byRow;

    internal ColumnDefaultSet(
        IReadOnlyDictionary<int, (int TableDefId, CellValueData Value)> byColumn,
        bool hasUnresolvedLookup,
        IReadOnlyCollection<RowDefault>? rowDefaults = null)
    {
        _byTable = byColumn.GroupBy(p => p.Value.TableDefId)
            .ToDictionary(g => g.Key, g => g.Select(p => (p.Key, p.Value.Value)).ToList());
        HasUnresolvedLookup = hasUnresolvedLookup;

        _byRow = [];
        foreach (var table in (rowDefaults ?? []).GroupBy(d => d.TableDefId))
        {
            var columns = Of(table.Key);
            var rows = new Dictionary<string, List<(int ColumnDefId, CellValueData Value)>>(StringComparer.Ordinal);
            foreach (var row in table.GroupBy(d => d.RowKey, StringComparer.Ordinal))
            {
                // Перекриття: дефолт рядка замінює дефолт колонки; null — прибирає його.
                var own = row.GroupBy(d => d.ColumnDefId).ToDictionary(g => g.Key, g => g.Last().Value);
                var effective = new List<(int ColumnDefId, CellValueData Value)>();
                foreach (var (columnDefId, value) in columns)
                {
                    if (!own.ContainsKey(columnDefId))
                    {
                        effective.Add((columnDefId, value));
                    }
                }

                foreach (var (columnDefId, value) in own)
                {
                    if (value is not null)
                    {
                        effective.Add((columnDefId, value));
                    }
                }

                rows[row.Key] = effective;
            }

            _byRow[table.Key] = rows;
        }
    }

    /// <summary>Є Lookup-дефолт, коду якого немає в довіднику.</summary>
    public bool HasUnresolvedLookup { get; }

    /// <summary>Жодної таблиці з дефолтами.</summary>
    public bool IsEmpty => _byTable.Count == 0 && _byRow.Count == 0;

    /// <summary>Дефолти колонок таблиці (порожньо — немає). ⚠ Без дефолтів рядків — для рядка див. <see cref="OfRow"/>.</summary>
    public IReadOnlyList<(int ColumnDefId, CellValueData Value)> Of(int tableDefId)
        => _byTable.TryGetValue(tableDefId, out var list) ? list : [];

    /// <summary>
    /// ✎ B175: ефективні дефолти рядка — дефолти колонок, перекриті дефолтами рядка (<c>""</c> рядка прибирає дефолт
    /// колонки). Рядок без власних дефолтів — те саме, що <see cref="Of"/>.
    /// </summary>
    public IReadOnlyList<(int ColumnDefId, CellValueData Value)> OfRow(int tableDefId, string rowKey)
        => _byRow.TryGetValue(tableDefId, out var rows) && rows.TryGetValue(rowKey, out var own)
            ? own
            : Of(tableDefId);

    /// <summary>
    /// Зріз таблиці + віртуальні комірки дефолтів для пар (рядок, колонка), яких у зрізі НЕМАЄ. Наявна комірка
    /// (зокрема явна порожнеча <c>IsEmpty = 1</c>) не замінюється (02b §6.3) — ні дефолтом колонки, ні рядка (B175).
    /// </summary>
    /// <param name="cells">Збережені комірки зрізу.</param>
    /// <param name="tableDefId">Таблиця версії.</param>
    /// <param name="rowIds">Ключ рядка → <c>TableRow.Id</c>.</param>
    /// <param name="period">Період зрізу.</param>
    public IReadOnlyList<CellRecord> Complete(
        IReadOnlyList<CellRecord> cells, int tableDefId, IReadOnlyDictionary<string, long> rowIds, PeriodKey period)
    {
        ArgumentNullException.ThrowIfNull(cells);
        ArgumentNullException.ThrowIfNull(rowIds);
        if ((Of(tableDefId).Count == 0 && !_byRow.ContainsKey(tableDefId)) || rowIds.Count == 0)
        {
            return cells;
        }

        var present = cells.Select(c => (c.Address.TableRowId, c.Address.ColumnDefId)).ToHashSet();
        var result = new List<CellRecord>(cells);
        foreach (var (rowKey, rowId) in rowIds)
        {
            foreach (var (columnDefId, value) in OfRow(tableDefId, rowKey))
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
