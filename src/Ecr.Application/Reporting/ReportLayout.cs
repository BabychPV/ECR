// src/Ecr.Application/Reporting/ReportLayout.cs
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Errors;

namespace Ecr.Application.Reporting;

/// <summary>Підсумок над колонкою зрізу.</summary>
/// <param name="Column">Код ОПИСАНОЇ колонки версії.</param>
/// <param name="Fn">Функція: <c>sum</c>, <c>count</c>, <c>avg</c>, <c>min</c>, <c>max</c>.</param>
public sealed record ReportTotalCommand(string Column, string Fn);

/// <summary>Макет зрізу (<c>RulesJson</c>, схема 2, секція <c>layout</c>).</summary>
/// <param name="GroupBy">
/// Код колонки групування; <c>null</c> — плаский перелік, і тоді підсумки, якщо
/// вони задані, дають один підсумковий рядок.
/// </param>
/// <param name="Totals">Підсумки в порядку опису.</param>
/// <param name="ShowGroupHeader">Чи малювати рядок заголовка групи.</param>
public sealed record ReportLayoutCommand(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? GroupBy = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<ReportTotalCommand>? Totals = null,
    bool ShowGroupHeader = false);

/// <summary>Зріз, розкладений за макетом: рядки в порядку групи, групи й підсумки.</summary>
/// <param name="Rows">Рядки в порядку показу: групи одна за одною, усередині — за <c>RowNo</c>.</param>
/// <param name="Groups">Групи в тому самому порядку; порожньо — групування немає.</param>
/// <param name="Totals">Підсумки по ВСІХ рядках; порожньо — підсумків не оголошено.</param>
public sealed record ReportLayoutView(
    IReadOnlyList<SnapshotRow> Rows,
    IReadOnlyList<SnapshotRowGroup> Groups,
    IReadOnlyList<SnapshotTotal> Totals);

/// <summary>
/// Макет зрізу (<c>R8</c>, <c>D-52a</c>): ОДНА група й підсумки — перевірка при
/// створенні версії і застосування на видачі ОДНИМ кодом.
/// </summary>
/// <remarks>
/// ⛔ <b>У <c>rpt.ReportRow</c> макет не потрапляє</b>, і <c>ContentHash</c>
/// його не бачить: сума доводить, що не змінилися ДАНІ, а заголовок групи в ній
/// означав би, що зміна способу ПОКАЗУ читається як підміна звіту. Тому макет
/// застосовується там, де зріз читають, — у <c>GET …/rows</c> і в книзі, яка
/// бере готові групи й підсумки з тієї самої сторінки.
///
/// ⚠ Підсумки рахуються ТУТ, а не в SQL і не в писарі книги: друга реалізація
/// розійшлася б із першою мовчки — на тому самому <c>null</c>, на якому
/// <c>AVG</c> у SQL і «сума / кількість рядків» дають різні числа.
/// </remarks>
public sealed class ReportLayout
{
    /// <summary>Функція підсумку: сума.</summary>
    public const string Sum = "sum";

    /// <summary>Функція підсумку: кількість НЕПОРОЖНІХ значень.</summary>
    public const string Count = "count";

    /// <summary>Функція підсумку: середнє.</summary>
    public const string Avg = "avg";

    /// <summary>Функція підсумку: найменше значення.</summary>
    public const string Min = "min";

    /// <summary>Функція підсумку: найбільше значення.</summary>
    public const string Max = "max";

    /// <summary>Закритий перелік функцій підсумку.</summary>
    public static readonly string[] Functions = [Sum, Count, Avg, Min, Max];

    /// <summary>Стеля кількості підсумків версії.</summary>
    /// <remarks>
    /// Межа не про швидкість: підсумок по кожній колонці широкого опису означає,
    /// що зріз намагаються зробити зведеною таблицею, а це вже SSRS (<c>D-52a</c>).
    /// </remarks>
    public const int MaxTotals = 20;

    private const string MessageKey = "err.ECR-RPT-0422.layout";

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>Макет, якого немає: видача йде тим самим шляхом, що й до <c>R8</c>.</summary>
    private static readonly ReportLayout None = new(null, [], false);

    private readonly IReadOnlyList<ReportTotalCommand> _totals;

    private ReportLayout(string? groupBy, IReadOnlyList<ReportTotalCommand> totals, bool showGroupHeader)
    {
        GroupBy = groupBy;
        _totals = totals;
        ShowGroupHeader = showGroupHeader;
    }

    /// <summary>Код колонки групування; <c>null</c> — плаский перелік.</summary>
    public string? GroupBy { get; }

    /// <summary>Чи показувати рядок заголовка групи.</summary>
    public bool ShowGroupHeader { get; }

    /// <summary>Чи макет нічого не змінює: ні групи, ні підсумків.</summary>
    public bool IsEmpty => GroupBy is null && _totals.Count == 0;

    /// <summary>Перевіряє макет версії й готує його до застосування.</summary>
    /// <param name="schema">Схема правил версії: макет несе лише схема 2.</param>
    /// <param name="layout">Секція <c>layout</c>; <c>null</c> — макету немає.</param>
    /// <param name="columns">Колонки версії: групувати й підсумовувати можна лише ОПИСАНУ колонку.</param>
    /// <exception cref="BusinessRuleException">
    /// Колонки в описі немає, функція невідома або <c>sum</c>/<c>avg</c> стоять
    /// над нечисловою колонкою — <c>ECR-RPT-0422</c>.
    /// </exception>
    public static ReportLayout Compile(
        int schema, ReportLayoutCommand? layout, IReadOnlyList<ReportColumnCommand>? columns)
    {
        if (layout is null)
        {
            return None;
        }

        if (schema != ReportRowRules.Schema)
        {
            // ⛔ Та сама причина, що й у правил: схема 1 секції `layout` не
            // читає, тож прийняти її означало б мовчки проігнорувати макет.
            throw Invalid("schema", $"макет несе лише схема {ReportRowRules.Schema}, а не {schema}");
        }

        var described = columns ?? [];
        var totals = layout.Totals ?? [];

        if (totals.Count > MaxTotals)
        {
            throw Invalid("totals", $"підсумків {totals.Count}, а стеля — {MaxTotals}");
        }

        var groupBy = string.IsNullOrWhiteSpace(layout.GroupBy) ? null : layout.GroupBy;

        if (groupBy is not null && Column(described, groupBy) is null)
        {
            throw Invalid("groupBy", $"колонки «{groupBy}» в описі версії немає");
        }

        var compiled = new List<ReportTotalCommand>(totals.Count);

        foreach (var total in totals)
        {
            var column = Column(described, total?.Column)
                         ?? throw Invalid("column", $"колонки «{total?.Column}» в описі версії немає");

            var fn = Array.Find(Functions, f => string.Equals(f, total!.Fn, StringComparison.OrdinalIgnoreCase))
                     ?? throw Invalid(
                         "fn", $"функції «{total!.Fn}» немає: є {string.Join(", ", Functions)}");

            // ⛔ Сума й середнє над текстом дали б нуль і виглядали б як
            // порахований підсумок — тобто брехали б числом, а не відмовляли.
            if (fn is Sum or Avg && !string.Equals(column.Kind, ReportSourceColumns.Number, StringComparison.Ordinal))
            {
                throw Invalid(
                    "fn", $"«{fn}» рахується лише над числовою колонкою, а «{column.Code}» — «{column.Kind}»");
            }

            compiled.Add(new ReportTotalCommand(column.Code, fn));
        }

        return new ReportLayout(groupBy, compiled, layout.ShowGroupHeader);
    }

    /// <summary>Макет ЗБЕРЕЖЕНОЇ версії; схема 1 — макету немає.</summary>
    /// <param name="rulesJson">Вміст <c>rpt.ReportVersion.RulesJson</c>.</param>
    /// <param name="columns">Колонки зрізу в порядку опису.</param>
    public static ReportLayout Of(string? rulesJson, IReadOnlyList<ReportColumnCommand> columns)
    {
        if (ReportRules.Parse(rulesJson).Schema != ReportRowRules.Schema)
        {
            return None;
        }

        ReportRulesCommand? stored = null;

        try
        {
            stored = JsonSerializer.Deserialize<ReportRulesCommand>(rulesJson!, Options);
        }
        catch (JsonException)
        {
            // Зламані правила = правила за замовчуванням, як їх читає `ReportRules.Parse`.
        }

        return Compile(ReportRowRules.Schema, stored?.Layout, columns);
    }

    /// <summary>Розкладає рядки зрізу за макетом.</summary>
    /// <param name="rows">УСІ рядки зрізу за зростанням <c>RowNo</c>.</param>
    /// <remarks>
    /// ⚠ Порядок груп — за значенням колонки групування; порожнє значення йде
    /// ОСТАННІМ («не заповнено» — не менше за найменше). Усередині групи рядки
    /// лишаються в порядку <c>RowNo</c>, тобто в порядку побудови.
    /// </remarks>
    public ReportLayoutView Apply(IReadOnlyList<SnapshotRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        if (IsEmpty)
        {
            return new ReportLayoutView(rows, [], []);
        }

        if (GroupBy is not { } groupBy)
        {
            return new ReportLayoutView(rows, [], TotalsOf(rows));
        }

        var groups = rows
            .GroupBy(r => Cell(r, groupBy))
            .OrderBy(g => g.Key, GroupOrder.Instance)
            .Select(g => (Value: g.Key, Rows: (IReadOnlyList<SnapshotRow>)[.. g]))
            .ToList();

        return new ReportLayoutView(
            [.. groups.SelectMany(g => g.Rows)],
            [.. groups.Select(g => new SnapshotRowGroup(groupBy, g.Value, g.Rows.Count, TotalsOf(g.Rows)))],
            TotalsOf(rows));
    }

    /// <summary>Підсумки по переліку рядків.</summary>
    private IReadOnlyList<SnapshotTotal> TotalsOf(IReadOnlyList<SnapshotRow> rows)
        => [.. _totals.Select(t => new SnapshotTotal(
            t.Column, t.Fn, Fold(t.Fn, [.. rows.Select(r => Cell(r, t.Column))])))];

    /// <summary>Згортає значення колонки однією функцією.</summary>
    /// <remarks>
    /// ⛔ <c>null</c> — «не вимірювали», а не нуль: <c>sum</c> і <c>count</c>
    /// його не бачать, а <c>avg</c> не бере його В ЗНАМЕННИК. Середнє з нулями
    /// замість порожніх колонок менше за справжнє рівно настільки, наскільки
    /// звіт неповний, — і виглядає як нормальне число.
    /// </remarks>
    /// <param name="fn">Функція підсумку.</param>
    /// <param name="cells">Значення колонки на КОЖНОМУ рядку, разом із порожніми.</param>
    private static object? Fold(string fn, IReadOnlyList<object?> cells)
    {
        var numbers = cells.Select(AsNumber).Where(n => n.HasValue).Select(n => n!.Value).ToList();

        return fn switch
        {
            Count => (decimal)cells.Count(c => c is not null),
            Sum => numbers.Count == 0 ? null : numbers.Sum(),
            Avg => numbers.Count == 0 ? null : numbers.Sum() / numbers.Count,
            Min => Edge(cells, first: true),
            Max => Edge(cells, first: false),
            _ => null,
        };
    }

    /// <summary>Найменше або найбільше НЕПОРОЖНЄ значення; <c>null</c> — таких немає.</summary>
    private static object? Edge(IReadOnlyList<object?> cells, bool first)
    {
        object? edge = null;
        var seen = false;

        foreach (var cell in cells.Where(c => c is not null))
        {
            if (!seen || (GroupOrder.Instance.Compare(cell, edge) < 0) == first)
            {
                edge = cell;
                seen = true;
            }
        }

        return edge;
    }

    private static object? Cell(SnapshotRow row, string column)
        => row.Cells.TryGetValue(column, out var value) ? value : null;

    private static ReportColumnCommand? Column(IReadOnlyList<ReportColumnCommand> columns, string? code)
        => code is null ? null : columns.FirstOrDefault(c => string.Equals(c.Code, code, StringComparison.Ordinal));

    /// <summary>Число зі значення комірки; <c>null</c> — числом не читається.</summary>
    /// <remarks>
    /// ⚠ Колонка під <c>sum</c>/<c>avg</c> перевірена як числова при створенні
    /// версії, тож <c>decimal</c> очікуваний; решта арм — на зріз, побудований
    /// до <c>D-52a</c> (значення приїхало рядком).
    /// </remarks>
    private static decimal? AsNumber(object? value)
        => value switch
        {
            decimal number => number,
            long number => number,
            int number => number,
            double number when double.IsFinite(number) => (decimal)number,
            string text when decimal.TryParse(
                text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null,
        };

    private static BusinessRuleException Invalid(string part, string reason)
        => new(
            ErrorCodes.ReportInvalid,
            $"Макет звіту ({part}): {reason}.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = MessageKey,
                ["part"] = part,
                ["reason"] = reason,
            });

    /// <summary>Порядок значень групи: числа — числом, решта — текстом, порожнє — останнє.</summary>
    private sealed class GroupOrder : IComparer<object?>
    {
        public static readonly GroupOrder Instance = new();

        public int Compare(object? x, object? y)
        {
            if (x is null || y is null)
            {
                return x is null ? (y is null ? 0 : 1) : -1;
            }

            return AsNumber(x) is { } left && AsNumber(y) is { } right
                ? left.CompareTo(right)
                : string.CompareOrdinal(Text(x), Text(y));
        }

        private static string Text(object value)
            => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
    }
}
