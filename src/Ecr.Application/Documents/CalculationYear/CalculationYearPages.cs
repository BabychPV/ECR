// src/Ecr.Application/Documents/CalculationYear/CalculationYearPages.cs
using System.Globalization;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Documents.CalculationYear;

/// <summary>
/// Зведення огляду року (<c>GET …/calculation-results/year/summary</c>): місяці, таблиці з лічильниками, варіанти
/// фільтрів — без жодного числа. Числа таблиці — окремими сторінками (<see cref="CalculationYearRowsDto"/>).
/// </summary>
/// <remarks>
/// ⚠ Огляд документа Land цілком (21 923 виходи) важив 9.17 МБ і читався 4.5–6.4 с тепло: екран тепер бере зведення
/// (кілобайти), а рядки — лише розгорнутих таблиць, сторінками.
/// </remarks>
/// <param name="DocumentId">Документ.</param>
/// <param name="Year">Рік.</param>
/// <param name="Months">Рівно 12 місяців, січень першим.</param>
/// <param name="Tables">Таблиці в порядку показу (аркуш → копія → таблиця); без виходів — немає.</param>
/// <param name="Methodologies">Методології для фільтра (код, назва).</param>
/// <param name="Substances">Речовини для фільтра (код, назва).</param>
/// <param name="HasFormulas">Чи є виходи формул шаблону (варіант «Template formula» у фільтрі методологій).</param>
/// <param name="TotalRows">Усього виходів документа (без фільтрів).</param>
/// <param name="MatchedRows">Виходів, що проходять фільтри.</param>
/// <param name="ProblemRows">Із них — «проблемних» (<see cref="CalculationYearFilter.IsProblem"/>).</param>
/// <param name="FreshnessHidden">Див. <see cref="CalculationYearViewDto.FreshnessHidden"/>.</param>
public sealed record CalculationYearSummaryDto(
    long DocumentId,
    int Year,
    IReadOnlyList<CalculationYearMonthDto> Months,
    IReadOnlyList<CalculationYearTableSummaryDto> Tables,
    IReadOnlyList<CalculationYearOptionDto> Methodologies,
    IReadOnlyList<CalculationYearOptionDto> Substances,
    bool HasFormulas,
    int TotalRows,
    int MatchedRows,
    int ProblemRows,
    bool FreshnessHidden);

/// <summary>Таблиця у зведенні.</summary>
/// <param name="Key">Ключ таблиці для запиту рядків: <c>TableDefId</c>, <c>TableDefId~SheetCopyId</c>, <c>0</c> — числа без таблиці.</param>
/// <param name="SheetDefId">Аркуш.</param>
/// <param name="SheetCode">Код аркуша.</param>
/// <param name="SheetNameL10n">Назва аркуша.</param>
/// <param name="SheetCopyId">Копія аркуша; <c>null</c> — оригінал.</param>
/// <param name="TableDefId"><c>0</c> — числа до D-401 без встановленої таблиці.</param>
/// <param name="TableCode">Код таблиці.</param>
/// <param name="TableNameL10n">Назва таблиці.</param>
/// <param name="Rows">Виходів у таблиці (без фільтрів).</param>
/// <param name="Matched">Виходів, що проходять фільтри.</param>
/// <param name="Problems">«Проблемних» серед тих, що проходять фільтри.</param>
public sealed record CalculationYearTableSummaryDto(
    string Key,
    int SheetDefId,
    string SheetCode,
    LocalizedText SheetNameL10n,
    long? SheetCopyId,
    int TableDefId,
    string TableCode,
    LocalizedText TableNameL10n,
    int Rows,
    int Matched,
    int Problems);

/// <summary>Варіант фільтра.</summary>
public sealed record CalculationYearOptionDto(string Code, LocalizedText NameL10n);

/// <summary>
/// Сторінка виходів однієї таблиці (<c>GET …/calculation-results/year/rows</c>): довідники — лише ті, на які
/// посилаються рядки сторінки.
/// </summary>
/// <param name="Table">Ключ таблиці (<see cref="CalculationYearTableSummaryDto.Key"/>).</param>
/// <param name="Total">Виходів таблиці, що проходять фільтри (усі сторінки).</param>
/// <param name="NextCursor">
/// Курсор наступної сторінки (<see cref="CalculationYearCursor"/>); <c>null</c> — сторінка остання. Курсор, виданий до
/// перерахунку чи зміни фільтра, сервер відхиляє 422 — клієнт читає таблицю з першої сторінки.
/// </param>
/// <param name="Equipment">Обладнання сторінки (<c>Table</c> = 0: таблиця одна).</param>
/// <param name="Methodologies">Методології сторінки.</param>
/// <param name="Substances">Речовини сторінки.</param>
/// <param name="Units">Одиниці сторінки.</param>
/// <param name="Columns">Колонки формул сторінки.</param>
/// <param name="Rows">Виходи сторінки в порядку показу; індекси — у переліки цієї сторінки.</param>
public sealed record CalculationYearRowsDto(
    string Table,
    int Total,
    string? NextCursor,
    IReadOnlyList<CalculationYearEquipmentDto> Equipment,
    IReadOnlyList<CalculationYearMethodologyDto> Methodologies,
    IReadOnlyList<CalculationYearSubstanceDto> Substances,
    IReadOnlyList<CalculationYearUnitDto> Units,
    IReadOnlyList<CalculationYearColumnDto> Columns,
    IReadOnlyList<CalculationYearRowDto> Rows);

/// <summary>Фільтри огляду (екран, зведення, сторінки й експорт «як відфільтровано» — одне правило).</summary>
/// <param name="Query">Пошук: назва обладнання (будь-якою мовою), IDCode, ключ рядка; без урахування регістру.</param>
/// <param name="Sheet">Аркуш (<c>SheetDefId</c>) або <c>null</c>.</param>
/// <param name="Methodology">Код методології; <see cref="FormulaMethodology"/> — формули шаблону; <c>null</c> — усі.</param>
/// <param name="Substance">Код речовини або <c>null</c>.</param>
/// <param name="ProblemsOnly">Лише «проблемні» виходи.</param>
public sealed record CalculationYearFilter(
    string? Query = null, int? Sheet = null, string? Methodology = null, string? Substance = null, bool ProblemsOnly = false)
{
    /// <summary>Код «методології» формул шаблону у фільтрі.</summary>
    public const string FormulaMethodology = "formula";

    /// <summary>Без фільтрів.</summary>
    public static CalculationYearFilter None { get; } = new();

    /// <summary>Чи потрібні підписи рядків (пошук іде по них).</summary>
    public bool NeedsLabels => !string.IsNullOrWhiteSpace(Query);

    /// <summary>
    /// «Проблема» виходу: значення із застарілого місяця або пропуск усередині року (між першим і останнім місяцем зі
    /// значенням є місяць із прогоном, але без числа). Окремих станів «бракує входу» / «помилка» сервер не зберігає.
    /// </summary>
    public static bool IsProblem(IReadOnlyList<CalculationYearMonthDto> months, CalculationYearRowDto row)
    {
        ArgumentNullException.ThrowIfNull(months);
        ArgumentNullException.ThrowIfNull(row);
        for (var i = 0; i < row.States.Length && i < months.Count; i++)
        {
            if (row.States[i] == 'v' && months[i].RunState == "outOfDate")
            {
                return true;
            }
        }

        var first = row.States.IndexOf('v', StringComparison.Ordinal);
        var last = row.States.LastIndexOf('v');
        return first >= 0 && row.States.AsSpan(first, last - first + 1).Contains('a');
    }

    /// <summary>Чи проходить вихід фільтри.</summary>
    public bool Matches(CalculationYearViewDto view, CalculationYearRowDto row)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(row);
        var equipment = view.Equipment[row.Equipment];
        var table = view.Tables[equipment.Table];
        if (Sheet is { } sheet && (table.TableDefId == 0 || table.SheetDefId != sheet))
        {
            return false;
        }

        if (!string.IsNullOrEmpty(Methodology))
        {
            var code = row.Methodology is { } m ? view.Methodologies[m].Code : FormulaMethodology;
            if (!string.Equals(code, Methodology, StringComparison.Ordinal))
            {
                return false;
            }
        }

        if (!string.IsNullOrEmpty(Substance)
            && !(row.Substance is { } s && string.Equals(view.Substances[s].Code, Substance, StringComparison.Ordinal)))
        {
            return false;
        }

        if (NeedsLabels && !Found(equipment, Query!.Trim()))
        {
            return false;
        }

        return !ProblemsOnly || IsProblem(view.Months, row);
    }

    private static bool Found(CalculationYearEquipmentDto equipment, string query)
    {
        bool Has(string? text) => text is not null && text.Contains(query, StringComparison.OrdinalIgnoreCase);
        return Has(equipment.Name) || Has(equipment.IdCode) || Has(equipment.RowKey)
               || (equipment.LabelL10n?.Values.Values.Any(Has) ?? false);
    }
}

/// <summary>Зведення й сторінки з огляду, побудованого <see cref="CalculationYearViewBuilder"/> (чисті функції).</summary>
public static class CalculationYearPages
{
    /// <summary>Найбільша сторінка рядків.</summary>
    public const int MaxLimit = 1000;

    /// <summary>Сторінка за замовчуванням.</summary>
    public const int DefaultLimit = 200;

    /// <summary>Ключ таблиці: <c>TableDefId</c>, <c>TableDefId~SheetCopyId</c>; <c>0</c> — числа без таблиці.</summary>
    public static string KeyOf(CalculationYearTableDto table)
    {
        ArgumentNullException.ThrowIfNull(table);
        return KeyOf(new CalculationYearSlice(table.TableDefId, table.SheetCopyId));
    }

    /// <summary>Ключ таблиці за визначенням і копією.</summary>
    public static string KeyOf(CalculationYearSlice slice)
    {
        ArgumentNullException.ThrowIfNull(slice);
        var def = slice.TableDefId.ToString(CultureInfo.InvariantCulture);
        return slice.TableDefId == 0 || slice.SheetCopyId is not { } copy
            ? def
            : def + "~" + copy.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Розбирає ключ таблиці; <c>false</c> — ключ не такої форми.</summary>
    public static bool TryParseKey(string? key, out CalculationYearSlice slice)
    {
        slice = new CalculationYearSlice(0, null);
        if (string.IsNullOrEmpty(key) || key.Length > 40)
        {
            return false;
        }

        var parts = key.Split('~');
        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var def))
        {
            return false;
        }

        long? copy = null;
        if (parts.Length == 2)
        {
            if (def == 0 || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var c))
            {
                return false;
            }

            copy = c;
        }
        else if (parts.Length != 1)
        {
            return false;
        }

        slice = new CalculationYearSlice(def, copy);
        return true;
    }

    /// <summary>Зведення: таблиці з лічильниками і варіанти фільтрів.</summary>
    public static CalculationYearSummaryDto Summary(CalculationYearViewDto view, CalculationYearFilter filter)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(filter);
        var counts = new (int Rows, int Matched, int Problems)[view.Tables.Count];
        int matched = 0, problems = 0;
        foreach (var row in view.Rows)
        {
            var table = view.Equipment[row.Equipment].Table;
            counts[table].Rows++;
            if (!filter.Matches(view, row))
            {
                continue;
            }

            counts[table].Matched++;
            matched++;
            if (CalculationYearFilter.IsProblem(view.Months, row))
            {
                counts[table].Problems++;
                problems++;
            }
        }

        // Порядок таблиць — порядок їхніх перших виходів (огляд уже впорядкований аркуш → копія → таблиця).
        var order = new List<int>();
        var seen = new HashSet<int>();
        foreach (var row in view.Rows)
        {
            var table = view.Equipment[row.Equipment].Table;
            if (seen.Add(table))
            {
                order.Add(table);
            }
        }

        var tables = order.Select(i =>
        {
            var t = view.Tables[i];
            return new CalculationYearTableSummaryDto(
                KeyOf(t), t.SheetDefId, t.SheetCode, t.SheetNameL10n, t.SheetCopyId, t.TableDefId, t.TableCode,
                t.TableNameL10n, counts[i].Rows, counts[i].Matched, counts[i].Problems);
        }).ToList();

        return new CalculationYearSummaryDto(
            view.DocumentId,
            view.Year,
            view.Months,
            tables,
            [.. view.Methodologies.Select(m => new CalculationYearOptionDto(m.Code, m.NameL10n))],
            [.. view.Substances.Select(s => new CalculationYearOptionDto(s.Code, s.NameL10n))],
            view.Rows.Any(r => r.Column is not null),
            view.Rows.Count,
            matched,
            problems,
            view.FreshnessHidden);
    }

    /// <summary>
    /// Сторінка виходів таблиці <paramref name="tableKey"/> з довідниками лише цієї сторінки; <see langword="null"/> —
    /// курсор <paramref name="after"/> видано з іншого набору рядків (перерахунок, інший фільтр чи таблиця).
    /// </summary>
    /// <param name="view">Огляд.</param>
    /// <param name="tableKey">Ключ таблиці.</param>
    /// <param name="filter">Фільтри.</param>
    /// <param name="after">Курсор попередньої сторінки; <see langword="null"/> — перша сторінка.</param>
    /// <param name="limit">Розмір сторінки.</param>
    public static CalculationYearRowsDto? Page(
        CalculationYearViewDto view, string tableKey, CalculationYearFilter filter, CalculationYearCursor? after, int limit)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(filter);
        var matched = view.Rows
            .Where(r => KeyOf(view.Tables[view.Equipment[r.Equipment].Table]) == tableKey && filter.Matches(view, r))
            .ToList();
        if (after is not null && !after.Fits(matched))
        {
            return null;
        }

        var start = after?.Position ?? 0;
        var page = matched.Skip(start).Take(limit).ToList();
        var end = start + page.Count;
        var next = end < matched.Count ? CalculationYearCursor.Encode(matched, end) : null;

        var equipment = new Remap<CalculationYearEquipmentDto>(i => view.Equipment[i] with { Table = 0 });
        var methodologies = new Remap<CalculationYearMethodologyDto>(i => view.Methodologies[i]);
        var substances = new Remap<CalculationYearSubstanceDto>(i => view.Substances[i]);
        var units = new Remap<CalculationYearUnitDto>(i => view.Units[i]);
        var columns = new Remap<CalculationYearColumnDto>(i => view.Columns[i]);
        var rows = page.ConvertAll(r => r with
        {
            Equipment = equipment.Of(r.Equipment),
            Methodology = methodologies.Of(r.Methodology),
            Substance = substances.Of(r.Substance),
            Unit = units.Of(r.Unit),
            Column = columns.Of(r.Column),
        });

        return new CalculationYearRowsDto(
            tableKey, matched.Count, next, equipment.Items, methodologies.Items, substances.Items, units.Items, columns.Items, rows);
    }

    /// <summary>Переіндексація довідника під сторінку: лише ті елементи, на які посилаються її рядки.</summary>
    private sealed class Remap<T>(Func<int, T> source)
    {
        private readonly Dictionary<int, int> map = [];

        public List<T> Items { get; } = [];

        public int Of(int index)
        {
            if (!map.TryGetValue(index, out var mapped))
            {
                mapped = Items.Count;
                map.Add(index, mapped);
                Items.Add(source(index));
            }

            return mapped;
        }

        public int? Of(int? index) => index is { } i ? Of(i) : null;
    }
}

/// <summary>Обсяг читання огляду (<see cref="CalculationYearViewBuilder"/>): межі доступу від нього не залежать.</summary>
/// <param name="Table">Лише ця таблиця; <c>null</c> — увесь документ.</param>
/// <param name="Labels">Чи читати підписи рядків (назва, IDCode).</param>
/// <param name="Aggregate">Без значень, лише наявність числа в місяці (зведення).</param>
public sealed record CalculationYearScope(CalculationYearSlice? Table, bool Labels, bool Aggregate)
{
    /// <summary>Увесь документ зі значеннями й підписами (експорт).</summary>
    public static CalculationYearScope Full { get; } = new(null, Labels: true, Aggregate: false);

    /// <summary>Зведення: увесь документ без значень; підписи — лише для пошуку.</summary>
    public static CalculationYearScope Summary(CalculationYearFilter filter)
        => new(null, Labels: filter?.NeedsLabels ?? false, Aggregate: true);

    /// <summary>Сторінка однієї таблиці зі значеннями й підписами.</summary>
    public static CalculationYearScope Page(CalculationYearSlice table) => new(table, Labels: true, Aggregate: false);
}

/// <summary>Таблиця, рядки якої читаються (<see cref="CalculationYearViewBuilder"/>).</summary>
/// <param name="TableDefId"><c>0</c> — числа без встановленої таблиці (до D-401).</param>
/// <param name="SheetCopyId">Копія аркуша; <c>null</c> — оригінал.</param>
public sealed record CalculationYearSlice(int TableDefId, long? SheetCopyId);
