using Ecr.Domain.Enums;
using Ecr.Expressions;
using Ecr.Expressions.Ast;
using Ecr.Expressions.Evaluation;
using Ecr.Expressions.Functions;

namespace Ecr.TestKit;

/// <summary>
/// Контекст обчислення для тестів: таблиця значень замість бази.
/// </summary>
/// <remarks>
/// ⚠ Розрізняє ТРИ стани комірки (02b §6.3), і це головне, заради чого він
/// існує окремо від заглушки:
/// <list type="bullet">
/// <item>ключа немає в <see cref="Cells"/> — комірки немає, береться
/// <see cref="Defaults"/>, а якщо його теж немає — <c>null</c>;</item>
/// <item>ключ є зі значенням <see cref="ExpressionValue.Null"/> — ЯВНА
/// порожнеча: <see cref="Defaults"/> НЕ застосовується;</item>
/// <item>ключ є зі значенням — саме воно.</item>
/// </list>
/// Різниця істотна: «не заповнювали» може мати дефолт, «свідомо лишили
/// порожнім» — ні.
/// </remarks>
public sealed class TestEvaluationContext : IEvaluationContext
{
    private readonly Evaluator _evaluator;

    /// <summary>Створює контекст із порожньою таблицею значень.</summary>
    public TestEvaluationContext()
    {
        _evaluator = new Evaluator(new FunctionRegistry());
        Period = new PeriodContext(
            new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31), CalendarMode.Actual, 2026, 1);
    }

    /// <summary>Значення комірок: <c>sheet|table|row|column</c> → значення.</summary>
    public Dictionary<string, ExpressionValue> Cells { get; } = new(StringComparer.Ordinal);

    /// <summary>Значення за замовчуванням: <c>sheet|table|column</c> → значення.</summary>
    public Dictionary<string, ExpressionValue> Defaults { get; } = new(StringComparer.Ordinal);

    /// <summary>Рядки таблиці в порядку <c>Ordinal</c>: <c>sheet|table</c> → ключі.</summary>
    public Dictionary<string, List<string>> Rows { get; } = new(StringComparer.Ordinal);

    /// <summary>Аргументи методології (<c>@Name</c>).</summary>
    public Dictionary<string, ExpressionValue> Arguments { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Константи методології (<c>CST.Name</c>).</summary>
    public Dictionary<string, ExpressionValue> Constants { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Результати інших формул (<c>!Name</c>).</summary>
    public Dictionary<string, ExpressionValue> FormulaResults { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Поля шапки (<c>HDR.Name</c>).</summary>
    public Dictionary<string, ExpressionValue> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Множники конверсії: <c>from|to</c> → коефіцієнт.</summary>
    public Dictionary<string, decimal> Conversions { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Зсуви періодів, доступні в межах проєкту.</summary>
    /// <remarks>
    /// Порожня множина плюс нуль означає перший період: <c>[Period:-1]</c> тоді
    /// дає <c>null</c>, а не помилку — січень не має попереднього місяця, і це
    /// нормальна ситуація (02b §3.2).
    /// </remarks>
    public HashSet<int> AvailablePeriodOffsets { get; } = [0];

    /// <summary>Аркуш формули, що обчислюється.</summary>
    public string CurrentSheet { get; set; } = "S";

    /// <summary>Таблиця формули.</summary>
    public string CurrentTable { get; set; } = "T";

    /// <summary>Рядок формули; порожній для формул рівня колонки.</summary>
    public string CurrentRow { get; set; } = string.Empty;

    /// <summary>Колонка, яку підставляє плейсхолдер <c>{Month}</c>.</summary>
    public string CurrentMonthColumn { get; set; } = string.Empty;

    /// <inheritdoc />
    public PeriodContext Period { get; set; }

    /// <summary>Ключ комірки.</summary>
    public static string Key(string sheet, string table, string row, string column)
        => $"{sheet}|{table}|{row}|{column}";

    /// <summary>Записує значення комірки.</summary>
    public void SetCell(string sheet, string table, string row, string column, ExpressionValue value)
    {
        Cells[Key(sheet, table, row, column)] = value;
        var rows = Rows.TryGetValue($"{sheet}|{table}", out var list) ? list : Rows[$"{sheet}|{table}"] = [];
        if (!rows.Contains(row, StringComparer.Ordinal))
        {
            rows.Add(row);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<ExpressionValue> Read(CellReferenceNode reference)
    {
        ArgumentNullException.ThrowIfNull(reference);

        if (!AvailablePeriodOffsets.Contains(reference.PeriodOffset))
        {
            // Вихід за межі проєкту — це null, а НЕ помилка.
            return [ExpressionValue.Null];
        }

        var sheet = reference.SheetCode ?? CurrentSheet;
        var table = reference.TableCode ?? CurrentTable;
        var column = reference.ColumnSelector is "{Month}" or "{Period}"
            ? CurrentMonthColumn
            : reference.ColumnSelector;

        var keys = ResolveRows(reference.Row, sheet, table);
        if (keys is null)
        {
            return [ExpressionValue.Error(ExpressionErrors.BadReference)];
        }

        var values = new List<ExpressionValue>(keys.Count);
        foreach (var rowKey in keys)
        {
            values.Add(ReadOne(sheet, table, rowKey, column, reference.PeriodOffset));
        }

        return values;
    }

    private List<string>? ResolveRows(RowSelector selector, string sheet, string table)
    {
        var all = Rows.TryGetValue($"{sheet}|{table}", out var list) ? list : [];

        switch (selector)
        {
            case RowSelector.Current:
                return [CurrentRow];

            case RowSelector.Single single:
                return all.Contains(single.RowKey, StringComparer.Ordinal) || all.Count == 0
                    ? [single.RowKey]
                    : null;

            case RowSelector.Range range:
            {
                var from = all.IndexOf(range.FromRowKey);
                var to = all.IndexOf(range.ToRowKey);
                return from < 0 || to < 0 || from > to ? null : all[from..(to + 1)];
            }

            case RowSelector.Predicate predicate:
            {
                // Предикат обчислюється В РАНТАЙМІ над фактичними рядками:
                // на момент публікації рядків динамічної таблиці ще немає.
                var matched = new List<string>();
                var saved = CurrentRow;
                var savedTable = CurrentTable;
                var savedSheet = CurrentSheet;
                try
                {
                    CurrentSheet = sheet;
                    CurrentTable = table;
                    foreach (var rowKey in all)
                    {
                        CurrentRow = rowKey;
                        var verdict = _evaluator.Evaluate(predicate.Condition, this);
                        if (verdict.Type == ExpressionValueType.Boolean && (bool)verdict.Value!)
                        {
                            matched.Add(rowKey);
                        }
                    }
                }
                finally
                {
                    CurrentRow = saved;
                    CurrentTable = savedTable;
                    CurrentSheet = savedSheet;
                }

                return matched;
            }

            default:
                return null;
        }
    }

    private ExpressionValue ReadOne(string sheet, string table, string row, string column, int periodOffset)
    {
        var key = periodOffset == 0
            ? Key(sheet, table, row, column)
            : $"{Key(sheet, table, row, column)}@{periodOffset}";

        if (Cells.TryGetValue(key, out var value))
        {
            return value;
        }

        return Defaults.TryGetValue($"{sheet}|{table}|{column}", out var fallback)
            ? fallback
            : ExpressionValue.Null;
    }

    /// <inheritdoc />
    public ExpressionValue GetCell(int tableDefId, string rowKey, int columnDefId, int periodOffset)
        => ReadOne(CurrentSheet, CurrentTable, rowKey, columnDefId.ToString(System.Globalization.CultureInfo.InvariantCulture), periodOffset);

    /// <inheritdoc />
    public IReadOnlyList<ExpressionValue> GetCellsByPredicate(int tableDefId, string filterJson, int columnDefId)
        => [];

    /// <inheritdoc />
    public ExpressionValue GetArgument(string name)
        => Arguments.GetValueOrDefault(name, ExpressionValue.Null);

    /// <inheritdoc />
    public ExpressionValue GetConstant(string name)
        => Constants.GetValueOrDefault(name, ExpressionValue.Null);

    /// <inheritdoc />
    public ExpressionValue GetFormulaResult(string name)
        => FormulaResults.GetValueOrDefault(name, ExpressionValue.Null);

    /// <inheritdoc />
    public ExpressionValue GetHeader(string name)
        => Headers.GetValueOrDefault(name, ExpressionValue.Null);

    /// <inheritdoc />
    public ExpressionValue Convert(ExpressionValue value, string fromUnitCode, string toUnitCode)
    {
        if (value.IsError || value.IsNull)
        {
            return value;
        }

        if (string.Equals(fromUnitCode, toUnitCode, StringComparison.OrdinalIgnoreCase))
        {
            return value;
        }

        if (value.AsNumber() is not { } number)
        {
            return ExpressionValue.Error(ExpressionErrors.BadValue);
        }

        return Conversions.TryGetValue($"{fromUnitCode}|{toUnitCode}", out var factor)
            ? ExpressionValue.Number(number * factor)
            : ExpressionValue.Error(ExpressionErrors.BadUnit);
    }
}
