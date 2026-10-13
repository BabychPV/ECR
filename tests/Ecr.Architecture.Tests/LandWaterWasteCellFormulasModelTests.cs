using System.Text.Json;
using System.Text.RegularExpressions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// WW-02: модель шаблону Land несе комірочні формули (<c>cellFormulas</c>) для 7. Water Report (групові підсумки й баланси),
/// 7a/7b («Volume of the water discharged») і 8. Waste Report (залишок 8001002, Input/Output/Balance, «залишок з минулого
/// місяця»). До WW-02 у цих таблицях було 0 формул (Excel: 394 + 191 + 100 + 6 503, з яких ~6 700 числових), тому
/// 7.0 R02 «Zhayk» і баланси лишались порожніми. Тест читає лише файли репозиторію (без API/БД).
/// </summary>
public sealed partial class LandWaterWasteCellFormulasModelTests
{
    private static readonly string[] Months =
    [
        "January", "February", "March", "April", "May", "June",
        "July", "August", "September", "October", "November", "December",
    ];

    private static JsonDocument Model()
        => JsonDocument.Parse(File.ReadAllText(Path.Combine(
            SourceTree.Root, "docs", "delivery", "reference-data", "land-template", "land-template.model.json")));

    private static IEnumerable<(JsonElement Sheet, JsonElement Table)> Tables(JsonElement model)
        => model.GetProperty("sheets").EnumerateArray()
            .SelectMany(s => s.GetProperty("tables").EnumerateArray().Select(t => (s, t)));

    private static string Code(JsonElement e) => e.GetProperty("code").GetString()!;

    private static List<JsonElement> Formulas(JsonElement table)
        => table.TryGetProperty("cellFormulas", out var f) ? f.EnumerateArray().ToList() : [];

    private static readonly string[] ReportSheets =
        ["S07_WATER_REPORT", "S07A_WATER_MEASUREMENTS", "S07B_WATER_MEASUREMENTS", "S08_WASTE_REPORT"];

    [GeneratedRegex(@"\[(?<s>[^\]]+)\]\.\[(?<t>[^\]]+)\]\.\[(?<r>[^\]]+)\]\.\[(?<c>[^\]]+)\]")]
    private static partial Regex Reference();

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [Trait("Requirement", "WW-02")]
    public void Кількість_комірочних_формул_по_таблицях_Water_Report_7a_7b_Waste_збігається_з_Excel()
    {
        using var doc = Model();
        var counts = Tables(doc.RootElement)
            .Where(x => ReportSheets.Contains(Code(x.Sheet)))
            .ToDictionary(x => $"{Code(x.Sheet)}/{Code(x.Table)}", x => Formulas(x.Table).Count);

        // Water Report: 6 групових рядків (7001000, 7002000, 7003000, 7004000, 7005000, 7010000) і 4 баланси × 12 місяців.
        Assert.Equal(120, counts["S07_WATER_REPORT/T7_00"]);

        // 7a: у кожній із 16 таблиць рядок «Volume of the water discharged» × 12 місяців; в Excel у T7a_02 немає
        // формули для грудня (11) - дефект шаблону Excel, не вгадуємо.
        var sevenA = counts.Where(kv => kv.Key.StartsWith("S07A_WATER_MEASUREMENTS/", StringComparison.Ordinal)).ToList();
        Assert.Equal(16, sevenA.Count);
        Assert.Equal(191, sevenA.Sum(kv => kv.Value));
        Assert.Equal(11, counts["S07A_WATER_MEASUREMENTS/T7a_02"]);
        Assert.All(sevenA.Where(kv => !kv.Key.EndsWith("T7a_02", StringComparison.Ordinal)), kv => Assert.Equal(12, kv.Value));

        // 7b: R7002002 + R7002004 за 12 місяців (решта 88 клітинок Excel - за межами звіту, див. нерозв'язані).
        Assert.Equal(12, counts["S07B_WATER_MEASUREMENTS/T7b_00"]);

        // Waste: 12 місяців × (8001002 + 12 рядків Input/Output/Balance) × 38 видів + 11 × «залишок з минулого місяця» × 38.
        Assert.Equal(38 * (12 * 13 + 11), counts["S08_WASTE_REPORT/T8_00"]);
        Assert.Equal(0, counts["S08_WASTE_REPORT/T8_01"]);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [Trait("Requirement", "WW-02")]
    public void Нерозв_язані_формули_лише_88_клітинок_7b_за_межами_місяців_і_кожна_з_причиною()
    {
        using var doc = Model();
        var unresolved = Tables(doc.RootElement)
            .Where(x => ReportSheets.Contains(Code(x.Sheet)))
            .SelectMany(x => x.Table.GetProperty("cellFormulasUnresolved").EnumerateArray()
                .Select(u => (Table: $"{Code(x.Sheet)}/{Code(x.Table)}", Item: u)))
            .ToList();

        Assert.Equal(88, unresolved.Count);
        Assert.All(unresolved, u =>
        {
            Assert.Equal("S07B_WATER_MEASUREMENTS/T7b_00", u.Table);
            Assert.StartsWith("target column ", u.Item.GetProperty("why").GetString(), StringComparison.Ordinal);
            Assert.Contains("is not numeric", u.Item.GetProperty("why").GetString(), StringComparison.Ordinal);
            Assert.Contains("'7. Water Report'!", u.Item.GetProperty("formula").GetString(), StringComparison.Ordinal);
        });

        // Пропущені — лише довідникові підписи (VLOOKUP по Configuration, версія з шапки), не числові розрахунки.
        var skipped = Tables(doc.RootElement)
            .Where(x => ReportSheets.Contains(Code(x.Sheet)))
            .SelectMany(x => x.Table.GetProperty("cellFormulasSkipped").EnumerateArray())
            .ToList();
        Assert.Equal(274 + 121 + 36, skipped.Sum(s => s.GetProperty("count").GetInt32()));
        Assert.All(skipped, s => Assert.StartsWith("label/lookup formula", s.GetProperty("class").GetString(), StringComparison.Ordinal));
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [Trait("Requirement", "WW-02")]
    public void Кожне_посилання_розв_язується_в_моделі_числова_колонка_того_ж_місяця_і_немає_голого_плюса_поза_SUM()
    {
        using var doc = Model();
        var tables = Tables(doc.RootElement).ToDictionary(x => $"{Code(x.Sheet)}|{Code(x.Table)}", x => x.Table);
        var checkedCount = 0;

        foreach (var (sheet, table) in Tables(doc.RootElement).Where(x => ReportSheets.Contains(Code(x.Sheet))))
        {
            var targetColumns = table.GetProperty("columns").EnumerateArray().ToDictionary(Code, c => c);
            foreach (var f in Formulas(table))
            {
                var expression = f.GetProperty("expression").GetString()!;
                var column = f.GetProperty("column").GetString()!;
                var references = Reference().Matches(expression).ToList();
                Assert.NotEmpty(references);

                foreach (var reference in references)
                {
                    var key = $"{reference.Groups["s"].Value}|{reference.Groups["t"].Value}";
                    Assert.True(tables.TryGetValue(key, out var sourceTable), $"{expression}: немає таблиці {key}");
                    Assert.Contains(sourceTable.GetProperty("rows").EnumerateArray(),
                        r => r.GetProperty("key").GetString() == reference.Groups["r"].Value);
                    var source = sourceTable.GetProperty("columns").EnumerateArray()
                        .Single(c => Code(c) == reference.Groups["c"].Value);
                    Assert.Contains(source.GetProperty("dataType").GetString(), new[] { "Decimal", "Int", "Formula" });

                    // Колонка джерела = колонка цілі; у межах Water Report - ще попередній місяць / OpeningBalance (баланс).
                    var sourceColumn = reference.Groups["c"].Value;
                    var sameSheet = reference.Groups["s"].Value == Code(sheet);
                    var allowed = sourceColumn == column
                        || (sameSheet && Array.IndexOf(Months, column) is var mi && mi >= 0
                            && ((mi > 0 && sourceColumn == Months[mi - 1]) || (mi == 0 && sourceColumn == "OpeningBalance")));
                    Assert.True(allowed, $"{Code(sheet)}/{Code(table)} {f.GetProperty("row").GetString()}/{column}: операнд у колонці {sourceColumn}");
                }

                // Null-безпека (LP-31): поза викликами SUM(...) не лишається жодного посилання, лише `-`, дужки й масштаб.
                var outside = Regex.Replace(expression, @"SUM\([^()]*\)", string.Empty);
                Assert.Matches(@"^[()\-]*(/\d+(\.\d+)?|\*\d+(\.\d+)?)?$", outside);
                Assert.DoesNotContain("+", outside, StringComparison.Ordinal);

                // Вираз відтворюється зі знакових доданків: SUM(плюси)[-SUM(мінуси)] і масштаб над усім виразом.
                var terms = f.GetProperty("terms").EnumerateArray().Select(t => t.GetString()!).ToList();
                var plus = terms.Where(t => t[0] == '+').Select(t => t[1..]).ToList();
                var minus = terms.Where(t => t[0] == '-').Select(t => t[1..]).ToList();
                Assert.Equal(references.Count, terms.Count);
                var body = plus.Count > 0 && minus.Count > 0
                    ? $"SUM({string.Join(',', plus)})-SUM({string.Join(',', minus)})"
                    : plus.Count > 0 ? $"SUM({string.Join(',', plus)})" : $"-SUM({string.Join(',', minus)})";
                foreach (var scale in f.GetProperty("scale").EnumerateArray().Select(s => s.GetString()!))
                {
                    body = (plus.Count > 0 && minus.Count > 0 ? $"({body})" : body) + scale;
                }

                Assert.Equal(body, expression);

                // Ціль: колонка обчислювана АБО рядок лише для читання (ECR-TMPL-4227 для Cell); без колонкової формули.
                var row = table.GetProperty("rows").EnumerateArray()
                    .Single(r => r.GetProperty("key").GetString() == f.GetProperty("row").GetString());
                var target = targetColumns[column];
                var rowReadOnly = row.TryGetProperty("readOnly", out var ro) && ro.GetBoolean();
                Assert.True(rowReadOnly || target.GetProperty("dataType").GetString() == "Formula",
                    $"{Code(table)} {f.GetProperty("row").GetString()}/{column}: рядок не лише для читання на колонці ручного вводу");
                Assert.False(target.TryGetProperty("expression", out _), "колонкова формула + комірочна на одній колонці = конфлікт");
                Assert.True(expression.Length <= 2000, "ліміт FormulaDef.MaxExpressionLength");
                checkedCount++;
            }
        }

        Assert.Equal(120 + 191 + 12 + 6346, checkedCount);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [Trait("Requirement", "WW-02")]
    public void Баланси_Water_Report_беруть_залишок_з_попереднього_місяця_а_січень_з_OpeningBalance()
    {
        using var doc = Model();
        var table = Tables(doc.RootElement).Single(x => Code(x.Sheet) == "S07_WATER_REPORT").Table;
        var byCell = Formulas(table).ToDictionary(
            f => (f.GetProperty("row").GetString()!, f.GetProperty("column").GetString()!),
            f => f.GetProperty("expression").GetString()!);

        // Fresh water distribution: Excel (H42 + I22) - (I38 + I40 + I41 + I42); лютий: (I42 + J22) - (J38 + J40 + J41 + J42).
        var january = byCell[("R7006999", "January")];
        Assert.StartsWith("SUM([S07_WATER_REPORT].[T7_00].[R7006005].[OpeningBalance],[S07_WATER_REPORT].[T7_00].[R7005000].[January])-SUM(",
            january, StringComparison.Ordinal);
        Assert.DoesNotContain("R7006002", january, StringComparison.Ordinal); // «name of load receiving» - текстовий рядок
        var february = byCell[("R7006999", "February")];
        Assert.StartsWith("SUM([S07_WATER_REPORT].[T7_00].[R7006005].[January],[S07_WATER_REPORT].[T7_00].[R7005000].[February])-SUM(",
            february, StringComparison.Ordinal);

        // Групи й баланси мають по 12 місяців.
        foreach (var row in new[] { "R7001000", "R7002000", "R7003000", "R7004000", "R7005000", "R7010000",
                                    "R7006999", "R7007999", "R7008999", "R7009999" })
        {
            Assert.Equal(12, Months.Count(m => byCell.ContainsKey((row, m))));
        }
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [Trait("Requirement", "WW-02")]
    public void Груповий_рядок_7003000_який_читає_7_0_R02_тепер_обчислюється_формулою()
    {
        using var doc = Model();
        var consolidation = Tables(doc.RootElement).Single(x => Code(x.Sheet) == "S07_0_WATER_CONSOLIDATION").Table;
        var report = Tables(doc.RootElement).Single(x => Code(x.Sheet) == "S07_WATER_REPORT").Table;
        var produced = Formulas(report).Select(f => (f.GetProperty("row").GetString(), f.GetProperty("column").GetString())).ToHashSet();
        var rowKind = report.GetProperty("rows").EnumerateArray()
            .ToDictionary(r => r.GetProperty("key").GetString()!, r => r.GetProperty("kind").GetString()!);

        var groupReads = 0;
        foreach (var f in Formulas(consolidation))
        {
            foreach (Match reference in Reference().Matches(f.GetProperty("expression").GetString()!))
            {
                var row = reference.Groups["r"].Value;
                if (rowKind[row] is "Group" or "Balance")
                {
                    groupReads++;
                    Assert.Contains((row, reference.Groups["c"].Value), produced);
                }
            }
        }

        // Рядок 2 («Zhayk») 7.0 читає саме груповий 7003000 - 12 місяців.
        Assert.True(groupReads >= 12, $"7.0 читає {groupReads} групових/балансових клітинок");
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [Trait("Requirement", "WW-02")]
    public void Waste_залишок_і_Input_Output_Balance_відповідають_Excel()
    {
        using var doc = Model();
        var table = Tables(doc.RootElement).Single(x => Code(x.Sheet) == "S08_WASTE_REPORT" && Code(x.Table) == "T8_00").Table;
        var column = "W_UsedBatteriesDensity15TM3Hazar";
        var byRow = Formulas(table).Where(f => f.GetProperty("column").GetString() == column)
            .ToDictionary(f => f.GetProperty("row").GetString()!, f => f);

        string Ref(string row) => $"[S08_WASTE_REPORT].[T8_00].[{row}].[{column}]";

        // C16 = C4+C5+C6-C7-C8-C9-C10-C12-C14 (8001002 = залишок на кінець).
        Assert.Equal(
            $"SUM({Ref("M01_8001001")},{Ref("M01_8002001")},{Ref("M01_8002002")})-SUM({Ref("M01_8003001")},{Ref("M01_8004001")}," +
            $"{Ref("M01_8004002")},{Ref("M01_8005001")},{Ref("M01_8005003")},{Ref("M01_8005005")})",
            byRow["M01_8001002"].GetProperty("expression").GetString());

        // C20 = C18-C19; C37 = C16 (залишок минулого місяця = кінцевий залишок попереднього).
        Assert.Equal($"SUM({Ref("M01_Input")})-SUM({Ref("M01_Output")})", byRow["M01_Balance"].GetProperty("expression").GetString());
        Assert.Equal($"SUM({Ref("M01_8001002")})", byRow["M02_8001001"].GetProperty("expression").GetString());
        Assert.False(byRow.ContainsKey("M01_8001001"), "січень вводить людина (початковий залишок року)");

        // 12 місяців × 13 + 11 «залишків минулого місяця».
        Assert.Equal(12 * 13 + 11, byRow.Count);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [Trait("Requirement", "WW-02")]
    public void Build_LandTemplate_ставить_IsReadOnly_рядкам_моделі_і_кладе_формули_комірок_масово_у_Sql_режимі()
    {
        var script = File.ReadAllText(Path.Combine(SourceTree.Root, "tools", "land", "Build-LandTemplate.ps1"));

        Assert.Contains("[bool]$rw.readOnly", script, StringComparison.Ordinal);
        Assert.Contains("UPDATE cfg.RowDef SET IsReadOnly=1", script, StringComparison.Ordinal);
        Assert.Contains("function Put-CellFormulasSql", script, StringComparison.Ordinal);
        Assert.Contains("if ($StructureMode -eq 'Sql') { return (Put-CellFormulasSql $vid $planned) }", script, StringComparison.Ordinal);

        // Дзеркало воріт ECR-TMPL-4227: колонка ручного вводу + рядок не лише для читання = відмова скрипта.
        Assert.Contains("ECR-TMPL-4227", script, StringComparison.Ordinal);
        Assert.Contains("if (-not $c.Computed -and -not $r.ReadOnly)", script, StringComparison.Ordinal);

        // Старий шлях (PUT …/formulas/cell/…) лишається для Api-режиму.
        Assert.Contains("/formulas/cell/", script, StringComparison.Ordinal);
    }
}
