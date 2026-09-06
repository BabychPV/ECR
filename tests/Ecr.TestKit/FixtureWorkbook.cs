using System.Globalization;
using System.Text.Json;
using Ecr.Domain.Enums;
using Ecr.Expressions.Evaluation;
using Ecr.Expressions.Functions;
using Ecr.TestKit;

namespace Ecr.TestKit;

/// <summary>
/// Фікстура <c>water-demo.json</c>, завантажена і порахована.
/// </summary>
/// <remarks>
/// ⚠ Очікування беруться З ФАЙЛУ, а не з коду тесту. Якщо код дає інше — правий
/// файл (`08-workflow.md` §4). Підганяти очікування під результат коду
/// заборонено: саме так «зелені» тести перестають щось означати.
///
/// Порядок обчислення знаходиться ітеративно, а не топологічним сортуванням:
/// фікстура мала, а мета цього класу — дати ЧИСЛА для звірки, а не повторити
/// рушій публікації. Топологію перевіряє <c>TopologicalSorterTests</c>.
/// </remarks>
public sealed class FixtureWorkbook
{
    private const int MaxPasses = 10;

    private readonly JsonElement _root;
    private readonly List<FormulaTarget> _targets = [];

    private FixtureWorkbook(JsonElement root)
    {
        _root = root;
        Context = new TestEvaluationContext();
    }

    /// <summary>Контекст із даними документа й порахованими формулами.</summary>
    public TestEvaluationContext Context { get; }

    /// <summary>Формули, які не належать діалекту шаблонів і тому не рахувалися.</summary>
    /// <remarks>
    /// ⚠ У фікстурі це <c>F6</c> і <c>F7</c>: вони вживають <c>CONVERT</c> і
    /// <c>CST.</c> у формулах ШАБЛОНУ, хоча <c>02b</c> §7 залишає шаблону
    /// одинадцять функцій без жодної з них. Розбіжність винесена в
    /// <c>Q-066</c>; тут вона видима, а не прихована мовчазним нулем.
    /// </remarks>
    public List<string> SkippedFormulas { get; } = [];

    /// <summary>Завантажує фікстуру і рахує всі формули діалекту шаблонів.</summary>
    public static FixtureWorkbook Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "water-demo.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));

        var workbook = new FixtureWorkbook(document.RootElement.Clone());
        workbook.LoadStructure();
        workbook.LoadDocument();
        workbook.Recalculate();
        return workbook;
    }

    /// <summary>Очікуване значення з розділу <c>expected</c> фікстури.</summary>
    public JsonElement Expected(params string[] path)
    {
        var node = _root.GetProperty("expected");
        foreach (var step in path)
        {
            node = node.GetProperty(step);
        }

        return node;
    }

    /// <summary>Пораховане значення комірки.</summary>
    public ExpressionValue Cell(string sheet, string table, string row, string column)
        => Context.Cells.GetValueOrDefault(
            TestEvaluationContext.Key(sheet, table, row, column), ExpressionValue.Null);

    private void LoadStructure()
    {
        foreach (var sheet in _root.GetProperty("template").GetProperty("sheets").EnumerateArray())
        {
            var sheetCode = sheet.GetProperty("code").GetString()!;

            foreach (var table in sheet.GetProperty("tables").EnumerateArray())
            {
                var tableCode = table.GetProperty("code").GetString()!;

                // Рядки реєструються НАПЕРЕД і в порядку Ordinal: без цього
                // діапазон 7001001:7001003 не має за чим розкриватися, а рядки
                // C001…C009 не існували б узагалі — у них немає жодної комірки
                // з даними, лише формули.
                var rows = new List<string>();
                if (table.TryGetProperty("rows", out var rowArray))
                {
                    rows.AddRange(rowArray.EnumerateArray()
                        .OrderBy(r => r.GetProperty("ordinal").GetInt32())
                        .Select(r => r.GetProperty("rowKey").GetString()!));
                }

                Context.Rows[$"{sheetCode}|{tableCode}"] = rows;

                var months = table.GetProperty("columns").EnumerateArray()
                    .Where(c => c.TryGetProperty("isMonthColumn", out var m) && m.GetBoolean())
                    .Select(c => c.GetProperty("code").GetString()!)
                    .ToList();

                if (table.TryGetProperty("formulas", out var formulas))
                {
                    foreach (var formula in formulas.EnumerateArray())
                    {
                        AddTargets(sheetCode, tableCode, formula, rows, months);
                    }
                }
            }
        }

        // Коефіцієнти конверсії й константи — рівно ті, що потрібні фікстурі.
        Context.Conversions["t|kg"] = 1000m;
        Context.Conversions["kg|t"] = 0.001m;
        Context.Constants["CST_WATER_DENSITY"] = ExpressionValue.Number(1000m);
    }

    private void AddTargets(
        string sheet, string table, JsonElement formula, List<string> rows, List<string> months)
    {
        var id = formula.GetProperty("id").GetString()!;
        var expression = formula.GetProperty("expression").GetString()!;
        var scope = formula.GetProperty("scope").GetString()!;

        var parsed = Expr.Parse(expression);
        if (!parsed.IsSuccess)
        {
            SkippedFormulas.Add($"{id}: {string.Join("; ", parsed.Diagnostics.Select(d => d.Message))}");
            return;
        }

        switch (scope)
        {
            case "Column":
            {
                var column = formula.GetProperty("column").GetString()!;
                foreach (var row in rows)
                {
                    _targets.Add(new FormulaTarget(sheet, table, row, column, column, parsed.Expression!.Root));
                }

                break;
            }

            case "Row":
            {
                // Формула рівня рядка живе в кожній МІСЯЧНІЙ колонці: саме для
                // цього існує плейсхолдер {Month} — інакше довелося б писати
                // дванадцять однакових формул.
                var row = formula.GetProperty("row").GetString()!;
                foreach (var column in months)
                {
                    _targets.Add(new FormulaTarget(sheet, table, row, column, column, parsed.Expression!.Root));
                }

                break;
            }

            default:
            {
                var row = formula.GetProperty("row").GetString()!;
                var column = formula.GetProperty("column").GetString()!;
                _targets.Add(new FormulaTarget(sheet, table, row, column, column, parsed.Expression!.Root));
                break;
            }
        }
    }

    private void LoadDocument()
    {
        var types = ColumnTypes();

        foreach (var table in _root.GetProperty("document").GetProperty("tables").EnumerateObject())
        {
            var parts = table.Name.Split('.');
            var sheet = parts[0];
            var tableCode = parts[1];
            var rows = Context.Rows.TryGetValue($"{sheet}|{tableCode}", out var known) ? known : [];

            foreach (var row in table.Value.EnumerateArray())
            {
                var rowKey = row.GetProperty("rowKey").GetString()!;
                if (!rows.Contains(rowKey, StringComparer.Ordinal))
                {
                    // Динамічна таблиця: рядків у шаблоні немає, вони живуть
                    // у документі.
                    rows.Add(rowKey);
                }

                foreach (var cell in row.GetProperty("cells").EnumerateObject())
                {
                    Context.Cells[TestEvaluationContext.Key(sheet, tableCode, rowKey, cell.Name)] =
                        ToValue(cell.Value, types.GetValueOrDefault($"{sheet}|{tableCode}|{cell.Name}", "String"));
                }
            }

            Context.Rows[$"{sheet}|{tableCode}"] = rows;
        }
    }

    private Dictionary<string, string> ColumnTypes()
    {
        var types = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var sheet in _root.GetProperty("template").GetProperty("sheets").EnumerateArray())
        {
            var sheetCode = sheet.GetProperty("code").GetString()!;
            foreach (var table in sheet.GetProperty("tables").EnumerateArray())
            {
                var tableCode = table.GetProperty("code").GetString()!;
                foreach (var column in table.GetProperty("columns").EnumerateArray())
                {
                    types[$"{sheetCode}|{tableCode}|{column.GetProperty("code").GetString()}"] =
                        column.GetProperty("dataType").GetString()!;
                }
            }
        }

        return types;
    }

    private static ExpressionValue ToValue(JsonElement cell, string dataType)
    {
        // ⚠ {"isEmpty": true} — це ЯВНА порожнеча, а не відсутність комірки:
        // DefaultValue до неї не застосовується (02b §6.3).
        if (cell.ValueKind == JsonValueKind.Object)
        {
            return ExpressionValue.Null;
        }

        var text = cell.GetString()!;
        return dataType == "Decimal"
            ? ExpressionValue.Number(decimal.Parse(text, CultureInfo.InvariantCulture))
            : ExpressionValue.Text(text);
    }

    private void Recalculate()
    {
        var evaluator = new Evaluator(new FunctionRegistry());

        for (var pass = 0; pass < MaxPasses; pass++)
        {
            var changed = false;

            foreach (var target in _targets)
            {
                Context.CurrentSheet = target.Sheet;
                Context.CurrentTable = target.Table;
                Context.CurrentRow = target.Row;
                Context.CurrentMonthColumn = target.MonthColumn;

                var value = evaluator.Evaluate(target.Root, Context, ExpressionDialect.Template);
                var key = TestEvaluationContext.Key(target.Sheet, target.Table, target.Row, target.Column);

                if (!Context.Cells.TryGetValue(key, out var previous) || !previous.Equals(value))
                {
                    Context.Cells[key] = value;
                    changed = true;
                }
            }

            if (!changed)
            {
                return;
            }
        }

        throw new InvalidOperationException(
            "Формули фікстури не збіглися за десять проходів — схоже на цикл у залежностях.");
    }

    private sealed record FormulaTarget(
        string Sheet, string Table, string Row, string Column, string MonthColumn, Ecr.Expressions.Ast.AstNode Root);
}
