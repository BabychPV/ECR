using System.Text.Json;
using System.Text.RegularExpressions;
using Ecr.Domain.Enums;
using Ecr.Expressions;
using Ecr.Expressions.Ast;
using Ecr.Expressions.Binding;
using Ecr.Expressions.Evaluation;
using Ecr.Expressions.Parsing;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// GTU / ECW_C12_02 (GTU-C12_02-DESIGN-2026-10-13, варіант б'): аргументи <c>Land_Measure_Component_{301,304,330,337,O2}</c>
/// старий код бере з ІНШОГО рядка таблиці (словник «NAE → GTU{n}_{code}» за місяцем із <c>Land_Measure_GTU_Month</c>).
/// У моделі це допоміжна колонка <c>Land_GTU_MonthNo</c> (назва місяця → число, бо предикат не викликає функцій) і п'ять
/// мостів на таблицю з предикатним агрегатом <c>MAX([T].[WHERE [Land_GTU_MonthNo] = [Period].Sequence].[GTU{n}_{code}])</c>.
/// Тест читає лише файли моделі; без перегенерації (<c>tools/land/model-gen/run-model-gen.ps1</c>) він червоний.
/// </summary>
public sealed class LandGtuBridgesModelTests
{
    private static readonly string Dir = Path.Combine(
        SourceTree.Root, "docs", "delivery", "reference-data", "land-template");

    /// <summary>Словник NAE → індекс GTU дослівно з ECW_C12_02.cs (за таблицею), у порядку коду.</summary>
    private static readonly Dictionary<string, (string Nae, int N)[]> NaeToGtu = new()
    {
        ["T10_03"] = [("0035", 1), ("0037", 2), ("0039", 3), ("0041", 4)],
        ["T11_03"] = [("0009", 1), ("0013", 2), ("0019", 3), ("0025", 4), ("0029", 5)],
    };

    private static readonly string[] Codes = ["301", "304", "330", "337", "O2"];

    private static readonly string[] Months =
    [
        "January", "February", "March", "April", "May", "June", "July", "August", "September", "October",
        "November", "December",
    ];

    private static JsonDocument LoadModel()
        => JsonDocument.Parse(File.ReadAllText(Path.Combine(Dir, "land-template.model.json")));

    private static Dictionary<string, JsonElement> Columns(JsonDocument model, string table)
    {
        var t = model.RootElement.GetProperty("sheets").EnumerateArray()
            .SelectMany(s => s.GetProperty("tables").EnumerateArray())
            .First(x => x.GetProperty("code").GetString() == table);
        return t.GetProperty("columns").EnumerateArray().ToDictionary(c => c.GetProperty("code").GetString()!, c => c);
    }

    private static string Expression(JsonElement column) => column.GetProperty("expression").GetString()!;

    public static TheoryData<string> Tables => new() { "T10_03", "T11_03" };

    [Theory]
    [MemberData(nameof(Tables))]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Є_допоміжна_колонка_й_п_ять_мостів_формулами_лише_для_читання_до_2000_символів(string table)
    {
        using var model = LoadModel();
        var cols = Columns(model, table);

        var helper = cols["Land_GTU_MonthNo"];
        Assert.Equal("Formula", helper.GetProperty("dataType").GetString());
        Assert.True(helper.GetProperty("readOnly").GetBoolean());
        Assert.True(Expression(helper).Length <= 2000);

        foreach (var code in Codes)
        {
            var bridge = cols[$"Land_Measure_Component_{code}"];
            Assert.Equal("Formula", bridge.GetProperty("dataType").GetString());
            Assert.True(bridge.GetProperty("readOnly").GetBoolean());
            var length = Expression(bridge).Length;
            Assert.True(length <= 2000, $"{table}.Land_Measure_Component_{code}: {length} символів.");
        }
    }

    [Theory]
    [MemberData(nameof(Tables))]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Словник_NAE_у_GTU_збігається_з_кодом_старої_системи_побітно_для_кожного_коду(string table)
    {
        using var model = LoadModel();
        var cols = Columns(model, table);

        foreach (var code in Codes)
        {
            var expression = Expression(cols[$"Land_Measure_Component_{code}"]);
            var branches = Regex.Matches(
                    expression,
                    @"\[Land_NAE\] = '(?<nae>\d{4})' \? MAX\(\[" + table
                    + @"\]\.\[WHERE \[Land_GTU_MonthNo\] = \[Period\]\.Sequence\]\.\[Land_Measure_GTU(?<n>\d)_(?<code>[0-9O]+)\]\) : ")
                .Select(m => (Nae: m.Groups["nae"].Value, N: int.Parse(m.Groups["n"].Value, System.Globalization.CultureInfo.InvariantCulture), Code: m.Groups["code"].Value))
                .ToArray();

            Assert.Equal(NaeToGtu[table].Length, branches.Length);
            for (var i = 0; i < branches.Length; i++)
            {
                Assert.Equal(NaeToGtu[table][i].Nae, branches[i].Nae);
                Assert.Equal(NaeToGtu[table][i].N, branches[i].N);
                Assert.Equal(code, branches[i].Code);
            }

            // невідомий або порожній NAE -> NULL (старий код: type2)
            Assert.EndsWith(" : NULL", expression, StringComparison.Ordinal);
        }
    }

    [Theory]
    [MemberData(nameof(Tables))]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Місяць_із_назви_у_число_охоплює_дванадцять_місяців_а_інше_дає_NULL(string table)
    {
        using var model = LoadModel();
        var expression = Expression(Columns(model, table)["Land_GTU_MonthNo"]);

        var pairs = Regex.Matches(expression, @"\[Land_Measure_GTU_Month\] = '(?<m>[A-Za-z]+)' \? (?<i>\d+) : ")
            .Select(m => (Month: m.Groups["m"].Value, No: int.Parse(m.Groups["i"].Value, System.Globalization.CultureInfo.InvariantCulture)))
            .ToArray();

        Assert.Equal(Months.Select((m, i) => (m, i + 1)).ToArray(), pairs);
        Assert.EndsWith(" : NULL", expression, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Tables))]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Допоміжна_колонка_названа_у_залежностях_моста_поза_предикатом(string table)
    {
        // Колонки всередині [WHERE ...] граф залежностей не бачить: без посилання поза предикатом правка
        // Land_Measure_GTU_Month не перерахує міст, а порядок обчислення Land_GTU_MonthNo -> міст не гарантований.
        using var model = LoadModel();
        var cols = Columns(model, table);
        const string predicate = "[WHERE [Land_GTU_MonthNo] = [Period].Sequence]";

        foreach (var code in Codes)
        {
            var outside = Expression(cols[$"Land_Measure_Component_{code}"]).Replace(predicate, string.Empty, StringComparison.Ordinal);
            Assert.Contains("[Land_GTU_MonthNo]", outside, StringComparison.Ordinal);
        }
    }

    [Theory]
    [MemberData(nameof(Tables))]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Усі_посилання_виразів_є_колонками_таблиці_і_предикат_проходить_перевірку_публікації(string table)
    {
        using var model = LoadModel();
        var cols = Columns(model, table);
        var expressions = new List<string> { Expression(cols["Land_GTU_MonthNo"]) };
        expressions.AddRange(Codes.Select(c => Expression(cols[$"Land_Measure_Component_{c}"])));

        foreach (var text in expressions)
        {
            foreach (Match m in Regex.Matches(text, @"\[([^\]\[]+)\]"))
            {
                var token = m.Groups[1].Value;
                Assert.True(
                    token == table || token.StartsWith("Period", StringComparison.Ordinal)
                    || token.StartsWith("WHERE ", StringComparison.Ordinal) || cols.ContainsKey(token),
                    $"{table}: [{token}] не колонка таблиці.");
            }

            var parsed = Expr.Parse(text);
            Assert.True(parsed.IsSuccess, string.Join("; ", parsed.Diagnostics.Select(d => d.Message)));

            var diagnostics = new List<ExpressionDiagnostic>();
            PredicateValidator.Validate(parsed.Expression!.Root, diagnostics);
            Assert.Empty(diagnostics);
        }
    }

    [Theory]
    [MemberData(nameof(Tables))]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Одиниці_компоненти_мг_на_м3_O2_відсотки_об_єму_на_мостах_і_колонках_джерела(string table)
    {
        using var model = LoadModel();
        var cols = Columns(model, table);
        var map = model.RootElement.GetProperty("units").GetProperty("map");
        var n = NaeToGtu[table].Length;

        foreach (var code in Codes)
        {
            var expected = code == "O2" ? "%vol" : "mg/m3";
            Assert.Equal(expected, cols[$"Land_Measure_Component_{code}"].GetProperty("unit").GetString());
            for (var g = 1; g <= n; g++)
            {
                Assert.Equal(expected, cols[$"Land_Measure_GTU{g}_{code}"].GetProperty("unit").GetString());
            }
        }

        // обидві одиниці - з базового сіду (09-seed.sql), нових не заведено
        Assert.Equal("mg_per_m3", map.GetProperty("mg/m3").GetString());
        Assert.Equal("pct_vol", map.GetProperty("%vol").GetString());
        var added = model.RootElement.GetProperty("units").GetProperty("new").EnumerateArray()
            .Select(u => u.GetProperty("code").GetString()).ToArray();
        Assert.DoesNotContain("mg_per_m3", added);
        Assert.DoesNotContain("pct_vol", added);
    }

    [Theory]
    [MemberData(nameof(Tables))]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void GTU_аргументи_C12_02_більше_не_у_unresolved(string table)
    {
        using var model = LoadModel();
        var unresolved = model.RootElement.GetProperty("argumentBridges").GetProperty("unresolved");

        foreach (var code in Codes)
        {
            var arg = $"Land_Measure_Component_{code}";
            var listed = unresolved.TryGetProperty(table, out var t) && t.TryGetProperty(arg, out _);
            Assert.False(listed, $"{table}.{arg} лишився в unresolved.");
        }

        var bridged = model.RootElement.GetProperty("argumentBridges").GetProperty("bridged").GetProperty(table)
            .EnumerateArray().Select(x => x.GetString()).ToHashSet();
        Assert.All(Codes, c => Assert.Contains($"Land_Measure_Component_{c}", bridged));
    }
}

/// <summary>
/// Семантика мостів GTU на рушії шаблонів: ТОЙ САМИЙ текст виразів, що в моделі, обчислюється над документом із
/// 12 рядками блоку «Component: Month» (різні місяці, у різному порядку). Доводить рішення (б'): предикат
/// <c>Land_GTU_MonthNo = [Period].Sequence</c> усередині документа, MAX по не-null з БУДЬ-ЯКОГО рядка, NULL для
/// невідомого/порожнього NAE.
/// </summary>
public sealed class LandGtuBridgesEvaluationTests
{
    private const string Sheet = "S";
    private static readonly string[] Months =
    [
        "January", "February", "March", "April", "May", "June", "July", "August", "September", "October",
        "November", "December",
    ];

    private static string ExpressionOf(JsonDocument model, string table, string column)
    {
        var t = model.RootElement.GetProperty("sheets").EnumerateArray()
            .SelectMany(s => s.GetProperty("tables").EnumerateArray())
            .First(x => x.GetProperty("code").GetString() == table);
        return t.GetProperty("columns").EnumerateArray()
            .First(c => c.GetProperty("code").GetString() == column).GetProperty("expression").GetString()!;
    }

    private static TestEvaluationContext Document(
        JsonDocument model, string table, byte sequence, Action<TestEvaluationContext, Func<string, string, ExpressionValue, string>> fill)
    {
        var context = new TestEvaluationContext
        {
            CurrentSheet = Sheet,
            CurrentTable = table,
            Period = new PeriodContext(new DateOnly(2026, sequence, 1), new DateOnly(2026, sequence, 28), CalendarMode.Actual, 2026, sequence),
        };

        // рядки й вхідні комірки; формульні колонки (Land_NAE, Land_GTU_MonthNo) обчислюються ТИМИ САМИМИ виразами з моделі
        fill(context, (row, column, value) =>
        {
            context.SetCell(Sheet, table, row, column, value);
            return row;
        });

        var monthNo = ExpressionOf(model, table, "Land_GTU_MonthNo");
        var nae = ExpressionOf(model, table, "Land_NAE");
        foreach (var row in context.Rows[$"{Sheet}|{table}"].ToArray())
        {
            context.CurrentRow = row;
            context.SetCell(Sheet, table, row, "Land_GTU_MonthNo", Expr.Eval(monthNo, context));
            context.SetCell(Sheet, table, row, "Land_NAE", Expr.Eval(nae, context));
        }

        return context;
    }

    private static ExpressionValue Bridge(JsonDocument model, TestEvaluationContext context, string table, string row, string code)
    {
        context.CurrentRow = row;
        return Expr.Eval(ExpressionOf(model, table, $"Land_Measure_Component_{code}"), context);
    }

    private static JsonDocument Load() => JsonDocument.Parse(File.ReadAllText(Path.Combine(
        SourceTree.Root, "docs", "delivery", "reference-data", "land-template", "land-template.model.json")));

    /// <summary>
    /// 12 рядків: рядок i має GTU_Month = місяць (i * 5) % 12 (порядок «перемішаний»), NAE чергується. Значення GTU2_301
    /// кладемо лише в рядок із березнем (він має NAE 0041, тобто НЕ «свій» NAE для GTU2): міст NAE 0037 мусить його знайти.
    /// </summary>
    private static void FillT10(TestEvaluationContext c, Func<string, string, ExpressionValue, string> set)
    {
        var nae = new[] { "0035", "0037", "0039", "0041" };
        for (var i = 0; i < 12; i++)
        {
            var row = $"r{i:D2}";
            set(row, "IDCodeNAEProject", ExpressionValue.Text(nae[i % 4]));
            set(row, "Land_Measure_GTU_Month", ExpressionValue.Text(Months[(i * 5) % 12]));
            set(row, "Land_Measure_GTU2_301", ExpressionValue.Null);
            set(row, "Land_Measure_GTU1_301", ExpressionValue.Null);
            set(row, "Land_Measure_GTU2_O2", ExpressionValue.Null);
        }

        // березень (номер 3): i = 10 -> (10 * 5) % 12 = 2 (індекс 2 = March); його NAE = nae[10 % 4] = 0039
        set("r10", "Land_Measure_GTU2_301", ExpressionValue.Number(321.5m));
        set("r10", "Land_Measure_GTU2_O2", ExpressionValue.Number(15.25m));
        // другий рядок березня з ТИМ САМИМ значенням (блок повторено в іншому місці документа): MAX не подвоює
        set("r12", "IDCodeNAEProject", ExpressionValue.Text("0035"));
        set("r12", "Land_Measure_GTU_Month", ExpressionValue.Text("March"));
        set("r12", "Land_Measure_GTU2_301", ExpressionValue.Number(321.5m));
        set("r12", "Land_Measure_GTU1_301", ExpressionValue.Null);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Міст_бере_значення_рядка_документа_де_місяць_блоку_дорівнює_періоду_для_NAE_його_колонки_GTU()
    {
        using var model = Load();
        var c = Document(model, "T10_03", 3, FillT10);

        // рядок r01 має NAE 0037 -> GTU2; значення лежить на r10/r12 (березень), де NAE інший
        Assert.Equal(321.5m, Bridge(model, c, "T10_03", "r01", "301").AsNumber());
        Assert.Equal(15.25m, Bridge(model, c, "T10_03", "r01", "O2").AsNumber());

        // той самий документ, інший NAE (0035 -> GTU1_301): у березні порожньо -> NULL
        Assert.True(Bridge(model, c, "T10_03", "r00", "301").IsNull);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Інший_період_бере_свій_місяць_а_відсутність_значення_дає_NULL()
    {
        using var model = Load();

        // період 4 (квітень, індекс 3): (i * 5) % 12 = 3 -> i = 3 (r03)
        var april = Document(model, "T10_03", 4, (ctx, set) =>
        {
            FillT10(ctx, set);
            set("r03", "Land_Measure_GTU2_301", ExpressionValue.Number(77m));
        });
        Assert.Equal(77m, Bridge(model, april, "T10_03", "r01", "301").AsNumber());

        // період 5 (травень): значення в блоці немає -> NULL, не 0 і не помилка
        var may = Document(model, "T10_03", 5, FillT10);
        Assert.True(Bridge(model, may, "T10_03", "r01", "301").IsNull);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Невідомий_або_порожній_NAE_дає_NULL_а_різні_значення_кандидатів_дають_MAX()
    {
        using var model = Load();
        var c = Document(model, "T10_03", 3, (ctx, set) =>
        {
            FillT10(ctx, set);
            set("r13", "IDCodeNAEProject", ExpressionValue.Text("9999"));
            set("r13", "Land_Measure_GTU_Month", ExpressionValue.Text("March"));
            set("r14", "IDCodeNAEProject", ExpressionValue.Null);
            set("r14", "Land_Measure_GTU_Month", ExpressionValue.Text("March"));
            // другий кандидат березня з БІЛЬШИМ значенням GTU2_301
            set("r14", "Land_Measure_GTU2_301", ExpressionValue.Number(400m));
        });

        Assert.True(Bridge(model, c, "T10_03", "r13", "301").IsNull);
        Assert.True(Bridge(model, c, "T10_03", "r14", "301").IsNull);
        Assert.Equal(400m, Bridge(model, c, "T10_03", "r01", "301").AsNumber());
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void T11_03_п_ятий_GTU_береться_для_NAE_0029()
    {
        using var model = Load();
        var c = Document(model, "T11_03", 3, (ctx, set) =>
        {
            set("a", "IDCodeNAEProject", ExpressionValue.Text("0029"));
            set("a", "Land_Measure_GTU_Month", ExpressionValue.Text("June"));
            set("b", "IDCodeNAEProject", ExpressionValue.Text("0009"));
            set("b", "Land_Measure_GTU_Month", ExpressionValue.Text("March"));
            set("b", "Land_Measure_GTU5_330", ExpressionValue.Number(5.5m));
            set("b", "Land_Measure_GTU1_330", ExpressionValue.Number(1.5m));
        });

        Assert.Equal(5.5m, Bridge(model, c, "T11_03", "a", "330").AsNumber()); // 0029 -> GTU5, березень
        Assert.Equal(1.5m, Bridge(model, c, "T11_03", "b", "330").AsNumber()); // 0009 -> GTU1
    }
}
