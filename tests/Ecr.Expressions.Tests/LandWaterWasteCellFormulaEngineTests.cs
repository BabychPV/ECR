using System.Text.Json;
using System.Text.RegularExpressions;
using Ecr.Expressions.Evaluation;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Expressions.Tests;

/// <summary>
/// WW-02: усі комірочні формули Water Report / 7a / 7b / Waste з моделі шаблону Land виконуються СПРАВЖНІМ рушієм
/// (діалект Template, десяткова арифметика) і дають те, що Excel: порожня клітинка = 0, тому за відсутнього операнда
/// результат - число, а не «немає значення». Очікуване значення рахується незалежно від тексту виразу - за знаковими
/// доданками <c>terms</c> і масштабом <c>scale</c>, які генератор моделі звіряє з власним Excel-обчисленням.
/// </summary>
public sealed partial class LandWaterWasteCellFormulaEngineTests
{
    private static readonly string[] ReportSheets =
        ["S07_WATER_REPORT", "S07A_WATER_MEASUREMENTS", "S07B_WATER_MEASUREMENTS", "S08_WASTE_REPORT"];

    [GeneratedRegex(@"\[(?<s>[^\]]+)\]\.\[(?<t>[^\]]+)\]\.\[(?<r>[^\]]+)\]\.\[(?<c>[^\]]+)\]")]
    private static partial Regex Reference();

    private sealed record Item(string Table, string Cell, string Expression, List<(int Sign, string Ref)> Terms, List<string> Scale);

    private static List<Item> Load()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            RepositoryRoot(), "docs", "delivery", "reference-data", "land-template", "land-template.model.json")));
        var items = new List<Item>();
        foreach (var sheet in doc.RootElement.GetProperty("sheets").EnumerateArray()
                     .Where(s => ReportSheets.Contains(s.GetProperty("code").GetString()!)))
        {
            foreach (var table in sheet.GetProperty("tables").EnumerateArray())
            {
                foreach (var f in table.GetProperty("cellFormulas").EnumerateArray())
                {
                    var terms = f.GetProperty("terms").EnumerateArray().Select(t => t.GetString()!)
                        .Select(t => (t[0] == '+' ? 1 : -1, t[1..])).ToList();
                    items.Add(new Item(
                        $"{sheet.GetProperty("code").GetString()}/{table.GetProperty("code").GetString()}",
                        $"{f.GetProperty("row").GetString()}/{f.GetProperty("column").GetString()}",
                        f.GetProperty("expression").GetString()!,
                        terms,
                        f.GetProperty("scale").EnumerateArray().Select(s => s.GetString()!).ToList()));
                }
            }
        }

        return items;
    }

    private static decimal Expected(Item item, IReadOnlyDictionary<string, decimal> values)
    {
        var sum = item.Terms.Sum(t => t.Sign * values.GetValueOrDefault(t.Ref));
        foreach (var s in item.Scale)
        {
            var k = decimal.Parse(s[1..], System.Globalization.CultureInfo.InvariantCulture);
            sum = s[0] == '/' ? sum / k : sum * k;
        }

        return sum;
    }

    private static TestEvaluationContext Context(Item item, IReadOnlyDictionary<string, decimal> values)
    {
        var context = new TestEvaluationContext();
        foreach (Match r in Reference().Matches(item.Expression))
        {
            var key = $"{r.Groups["s"].Value}|{r.Groups["t"].Value}";
            var list = context.Rows.TryGetValue(key, out var l) ? l : context.Rows[key] = [];
            if (!list.Contains(r.Groups["r"].Value, StringComparer.Ordinal))
            {
                list.Add(r.Groups["r"].Value);
            }
        }

        foreach (var (reference, value) in values)
        {
            var m = Reference().Match(reference);
            context.SetCell(m.Groups["s"].Value, m.Groups["t"].Value, m.Groups["r"].Value, m.Groups["c"].Value,
                ExpressionValue.Number(value));
        }

        return context;
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "WW-02")]
    public void Усі_6669_формул_нових_класів_дають_значення_Excel_при_повних_одному_та_жодному_операнді()
    {
        var items = Load();
        Assert.Equal(120 + 191 + 12 + 6346, items.Count);

        foreach (var item in items)
        {
            var refs = item.Terms.Select(t => t.Ref).Distinct().ToList();
            Assert.NotEmpty(refs);

            // A: усі операнди заповнені різними числами.
            var all = refs.Select((r, i) => (r, v: (i + 1) * 3m)).ToDictionary(x => x.r, x => x.v);
            // B: заповнено лише перший операнд, решта порожні (Excel: порожнє = 0).
            var first = new Dictionary<string, decimal> { [refs[0]] = 5000m };
            // C: заповнено лише останній.
            var last = new Dictionary<string, decimal> { [refs[^1]] = 70m };

            foreach (var values in new[] { all, first, last })
            {
                var value = Expr.Eval(item.Expression, Context(item, values));
                Assert.False(value.IsNull, $"{item.Table} {item.Cell}: «немає значення» замість числа за порожніх операндів");
                Assert.False(value.IsError, $"{item.Table} {item.Cell}: помилка {value.ErrorCode}");
                Assert.Equal(Expected(item, values), value.AsNumber());
            }

            // D: усі порожні - Excel дає 0 (баланс/сума порожнього = 0), а не «немає значення».
            var empty = Expr.Eval(item.Expression, Context(item, new Dictionary<string, decimal>()));
            Assert.Equal(0m, empty.AsNumber());
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "WW-02")]
    public void Баланс_із_плюс_і_мінус_доданків_дорівнює_різниці_сум_навіть_якщо_частина_порожня()
    {
        // Fresh water distribution, січень: (OpeningBalance(42) + група(22)) - (38 + 40 + 41 + 42), заповнено лише частину.
        var item = Load().Single(i => i.Table == "S07_WATER_REPORT/T7_00" && i.Cell == "R7006999/January");
        string R(string row, string column = "January") => $"[S07_WATER_REPORT].[T7_00].[{row}].[{column}]";

        var values = new Dictionary<string, decimal>
        {
            [R("R7006005", "OpeningBalance")] = 100m,
            [R("R7005000")] = 40m,
            [R("R7006003")] = 30m, // R7006001, R7006004 і R7006005.January - порожні
        };

        // (100 + 40) - 30 = 110
        Assert.Equal(110m, Expr.Eval(item.Expression, Context(item, values)).AsNumber());

        // Лише мінусові доданки: від'ємний залишок, не «немає значення».
        var onlyMinus = new Dictionary<string, decimal> { [R("R7006004")] = 25m };
        Assert.Equal(-25m, Expr.Eval(item.Expression, Context(item, onlyMinus)).AsNumber());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "WW-02")]
    public void Volume_7a_ділить_на_чотири_а_7b_додає_два_рядки_Water_Report()
    {
        var items = Load();
        var quarter = items.First(i => i.Table.StartsWith("S07A_", StringComparison.Ordinal) && i.Scale.SequenceEqual(["/4"]));
        var value = Expr.Eval(quarter.Expression, Context(quarter, new Dictionary<string, decimal> { [quarter.Terms[0].Ref] = 1000m }));
        Assert.Equal(250m, value.AsNumber());

        var sevenB = items.Single(i => i.Table == "S07B_WATER_MEASUREMENTS/T7b_00" && i.Cell == "S3_9/January");
        Assert.Equal(2, sevenB.Terms.Count);
        var onlyOne = new Dictionary<string, decimal> { [sevenB.Terms[1].Ref] = 7m };
        Assert.Equal(7m, Expr.Eval(sevenB.Expression, Context(sevenB, onlyOne)).AsNumber());
    }

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Ecr.sln")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Не знайдено корінь репозиторію (Ecr.sln).");
    }
}
