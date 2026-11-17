using Ecr.Calculations;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.MethodologyScriptMigration.Conversion;
using Ecr.Scripting;
using Xunit;

namespace Ecr.MethodologyScriptMigration.Tests;

public sealed class ConstantSimplificationTests
{
    private const MethodologyFormulaScope Row = MethodologyFormulaScope.Row;

    private static readonly DateOnly D2025 = new(2025, 1, 1);

    private static ConversionResult Convert(VersionSource source) => Sup.ConvertOk(source);

    private static IEnumerable<SourceFormula> OneFormula(string expr, MethodologyFormulaScope scope = MethodologyFormulaScope.Substance)
        => [Sup.F("Calc", expr, 1, scope)];

    [Fact]
    public void Однакове_для_всіх_комбінацій_дає_скаляр()
    {
        var result = Convert(Sup.Version(OneFormula("CST.K * 2"), [SourceConstant.OfNumber("K", 5)]));
        Assert.Contains("const K = 5;", result.Text);
        Assert.Contains("var Calc = K * 2;", result.Text);
        Assert.Equal(5m, Sup.Compile(result.Text!).Constants["K"].Scalar!.Value.Value);
    }

    [Fact]
    public void Залежне_лише_від_категорії_дає_мапу_за_категорією()
    {
        var result = Convert(Sup.Version(
            OneFormula("CST.K * 2"),
            [SourceConstant.OfNumber("K", 1.2m, "Summer"), SourceConstant.OfNumber("K", 1.4m, "Winter")],
            categoryRule: "if(@Month > 3, 'Summer', 'Winter')",
            categories: ["Summer", "Winter"]));

        Assert.Contains("const K = {\n    Summer: 1.2,\n    Winter: 1.4,\n};", result.Text);
        Assert.Contains("var Calc = K[Category] * 2;", result.Text);
        Assert.Contains("var Category = key(row.Month) > 3 ? \"Summer\" : \"Winter\";", result.Text);
        Assert.Contains("reject when Category == null || Category == \"\" || !(Category in (\"Summer\", \"Winter\"));", result.Text);
        Sup.Compile(result.Text!);
    }

    [Fact]
    public void Спільна_Common_категорія_покриває_категорії_без_власного_значення()
    {
        var result = Convert(Sup.Version(
            OneFormula("CST.K"),
            [SourceConstant.OfNumber("K", 7, "Common")],
            categoryRule: "'A'",
            categories: ["A", "B"]));

        // Для A і B резолвер дає одне й те саме (Common) — скаляр.
        Assert.Contains("const K = 7;", result.Text);
        Assert.Contains("var Calc = K;", result.Text);
    }

    [Fact]
    public void Залежне_від_категорії_й_речовини_і_форма_категорія_підкреслення_код()
    {
        // Форма B: правило дає «6640», константи лежать під «6640_0301» / «6640_0304» (код речовини з провідним нулем
        // і без нього).
        var result = Convert(Sup.Version(
            OneFormula("CST.K"),
            [
                SourceConstant.OfNumber("K", 10, "6640_0301"),
                SourceConstant.OfNumber("K", 20, "6640_304"),
            ],
            categoryRule: "'6640'",
            categories: ["6640_0301", "6640_304"],
            substances: ["0301", "0304"]));

        Assert.Contains("var Calc = K[Category][S.code];", result.Text);
        Assert.Contains("foreach substance S in (\"0301\", \"0304\") {", result.Text);
        var map = Sup.Compile(result.Text!).Constants["K"].Map!;
        Assert.Equal(10m, map["6640"].Map!["0301"].Scalar!.Value.Value);
        Assert.Equal(20m, map["6640"].Map!["0304"].Scalar!.Value.Value);
    }

    [Fact]
    public void Залежне_лише_від_речовини_індексується_кодом_речовини()
    {
        var result = Convert(Sup.Version(
            OneFormula("CST.K"),
            [SourceConstant.OfNumber("K", 1, null, "0301"), SourceConstant.OfNumber("K", 2, null, "0304")],
            substances: ["0301", "0304"]));

        Assert.Contains("const K = {\n    \"0301\": 1,\n    \"0304\": 2,\n};", result.Text);
        Assert.Contains("var Calc = K[S.code];", result.Text);
    }

    [Fact]
    public void Вікно_чинності_розбиває_константу_за_датою()
    {
        var result = Convert(Sup.Version(
            OneFormula("CST.K"),
            [
                SourceConstant.OfNumber("K", 1, validTo: D2025),
                SourceConstant.OfNumber("K", 2, validFrom: D2025),
            ]));

        Assert.Contains("const K_from_20240101 = 1;", result.Text);
        Assert.Contains("const K_from_20250101 = 2;", result.Text);
        Assert.Contains("var K = period.EndKey >= 20250101 ? K_from_20250101 : K_from_20240101;", result.Text);
        Assert.Contains("var Calc = K;", result.Text);
        Sup.Compile(result.Text!);
    }

    [Fact]
    public void Межі_поза_життям_версії_не_розбивають_константу()
    {
        var result = Convert(Sup.Version(
            OneFormula("CST.K"),
            [SourceConstant.OfNumber("K", 1, validTo: new DateOnly(2030, 1, 1)), SourceConstant.OfNumber("K", 9, validFrom: new DateOnly(2030, 1, 1))],
            next: new DateOnly(2026, 1, 1)));

        Assert.Contains("const K = 1;", result.Text);
        Assert.DoesNotContain("period.EndKey", result.Text);
    }

    [Fact]
    public void Мапа_що_залежить_від_дати_стає_функцією()
    {
        var result = Convert(Sup.Version(
            OneFormula("CST.K"),
            [
                SourceConstant.OfNumber("K", 1, "A", validTo: D2025),
                SourceConstant.OfNumber("K", 2, "B", validTo: D2025),
                SourceConstant.OfNumber("K", 3, "A", validFrom: D2025),
                SourceConstant.OfNumber("K", 4, "B", validFrom: D2025),
            ],
            categoryRule: "'A'",
            categories: ["A", "B"]));

        Assert.Contains("function K(k_category) = period.EndKey >= 20250101 ? K_from_20250101[k_category] : K_from_20240101[k_category];", result.Text);
        Assert.Contains("var Calc = K(Category);", result.Text);
        Sup.Compile(result.Text!);
    }

    [Fact]
    public void Відсутня_комбінація_лишає_ключ_відсутнім()
    {
        var result = Convert(Sup.Version(
            OneFormula("CST.K"),
            [SourceConstant.OfNumber("K", 1, "A"), SourceConstant.OfNumber("K", 2, "B")],
            categoryRule: "'A'",
            categories: ["A", "B", "C"]));

        var map = Sup.Compile(result.Text!).Constants["K"].Map!;
        Assert.Equal(["A", "B"], map.Keys);
        Assert.False(map.ContainsKey("C"));
    }

    [Fact]
    public void Відсутня_у_будь_якій_комбінації_стає_null_із_попередженням()
    {
        var result = Convert(Sup.Version(OneFormula("CST.Missing + 1"), []));
        Assert.Contains("var Missing = null;", result.Text);
        Assert.True(Sup.HasIssue(result, "constantUnresolvable", IssueSeverity.Warning));
    }

    [Fact]
    public void Неоднозначний_вибір_у_Row_контексті_дає_попередження_і_відсутнє_значення()
    {
        // Row-фаза резолвить без категорії: дві категорії — «неоднозначно».
        var result = Convert(Sup.Version(
            OneFormula("CST.K", MethodologyFormulaScope.Row),
            [SourceConstant.OfNumber("K", 1, "A"), SourceConstant.OfNumber("K", 2, "B")]));

        Assert.True(Sup.HasIssue(result, "constantAmbiguous", IssueSeverity.Warning));
        Assert.Contains("var K = null;", result.Text);
    }

    [Fact]
    public void Константа_читана_в_обох_контекстах_отримує_окреме_оголошення_на_контекст()
    {
        var result = Convert(Sup.Version(
            [Sup.F("R", "CST.K", 1, Row), Sup.F("S", "CST.K", 2)],
            [SourceConstant.OfNumber("K", 1, "A"), SourceConstant.OfNumber("K", 2, "B")],
            categoryRule: "'A'",
            categories: ["A", "B"]));

        Assert.Contains("var K = null;", result.Text);
        Assert.Contains("const K_substance = {", result.Text);
        Assert.Contains("var R = K;", result.Text);
        Assert.Contains("var S = K_substance[Category];", result.Text);
    }

    [Fact]
    public void Колізія_імені_константи_з_формулою_і_ключовим_словом_дає_суфікс_c()
    {
        var result = Convert(Sup.Version(
            [Sup.F("Total", "CST.Total + CST.row", 1)],
            [SourceConstant.OfNumber("Total", 1), SourceConstant.OfNumber("row", 2)]));

        Assert.Contains("const Total_c = 1;", result.Text);
        Assert.Contains("const row_c = 2;", result.Text);
        Assert.Contains("var Total = Total_c + row_c;", result.Text);
        Assert.Equal(2, result.Issues.Count(i => i.Code == "nameCollision" && i.Severity == IssueSeverity.Info));
    }

    [Fact]
    public void Текстова_константа_стає_рядковим_const()
    {
        var result = Convert(Sup.Version(
            OneFormula("if(@A = CST.Label, 1, 0)"),
            [SourceConstant.OfText("Label", "Mangistau \"M\"")]));

        Assert.Contains("const Label = \"Mangistau \\\"M\\\"\";", result.Text);
    }

    [Fact]
    public void Одиниця_константи_переходить_в_const_з_двокрапкою()
    {
        var result = Convert(Sup.Version(
            OneFormula("CST.K"),
            [SourceConstant.OfNumber("K", 3, unitCode: "kg")]));

        Assert.Contains("const K : kg = 3;", result.Text);
    }

    [Fact]
    public void Константи_впорядковані_за_першим_використанням()
    {
        var result = Convert(Sup.Version(
            [Sup.F("A", "CST.Z + CST.M", 1), Sup.F("B", "CST.A", 2)],
            [SourceConstant.OfNumber("A", 1), SourceConstant.OfNumber("M", 2), SourceConstant.OfNumber("Z", 3)]));

        var text = result.Text!;
        Assert.True(text.IndexOf("const Z", StringComparison.Ordinal) < text.IndexOf("const M", StringComparison.Ordinal));
        Assert.True(text.IndexOf("const M", StringComparison.Ordinal) < text.IndexOf("const A", StringComparison.Ordinal));
    }

    [Fact]
    public void Бібліотечна_константа_що_залежить_від_категорії_не_перетворюється()
    {
        var source = Sup.Version(
            [Sup.F("V", "CST.K", 1)],
            [SourceConstant.OfNumber("K", 1, "A"), SourceConstant.OfNumber("K", 2, "B")],
            categories: ["A", "B"]) with { IsLibrary = true, RowsClauses = [] };

        var result = ScriptConverter.Convert(source);
        Assert.Null(result.Text);
        Assert.True(Sup.HasIssue(result, "libraryConstantContextual", IssueSeverity.Error));
    }

    // ---- Прив'язка до справжнього ConstantResolver ----

    /// <summary>
    /// Повний перебір «категорія × речовина × дата»: згенерована константа дає ТЕ САМЕ, що й
    /// <see cref="ConstantResolver.Resolve"/> напряму, на нетривіальному наборі кандидатів.
    /// </summary>
    [Fact]
    public void Згенерована_константа_збігається_з_ConstantResolver_на_всіх_комбінаціях()
    {
        string[] categories = ["6585 (001)", "6482(003)", "Loc_A", "Loc_B", "7000", "Gone"];
        string[] substances = ["0301", "0304", "2704"];
        var constants = new List<SourceConstant>
        {
            // Спільні й «Common»; точний збіг категорії виграє.
            SourceConstant.OfNumber("K", 1, "Common"),
            SourceConstant.OfNumber("K", 2, "Loc_A"),
            // Пробіли в категорії: «6585(001)» → «6585 (001)» (без урахування пробілів).
            SourceConstant.OfNumber("K", 3, "6585(001)"),
            // Форма B (код із провідним нулем і без).
            SourceConstant.OfNumber("K", 4, "7000_0301"),
            SourceConstant.OfNumber("K", 5, "7000_304"),
            // Речовинна константа виграє над спільною.
            SourceConstant.OfNumber("K", 6, "Common", "0304"),
            // Вікна чинності.
            SourceConstant.OfNumber("K", 7, "Loc_B", validTo: D2025),
            SourceConstant.OfNumber("K", 8, "Loc_B", validFrom: D2025),
            SourceConstant.OfNumber("K", 9, "6482(003)", "2704", validFrom: D2025),
        };

        var source = Sup.Version(
            OneFormula("CST.K"), constants, categoryRule: "@X", categories: categories, substances: substances);
        var result = ScriptConverter.Convert(source);
        Assert.NotNull(result.Text);

        var script = Sup.Compile(result.Text);
        var function = script.Functions.TryGetValue("K", out var f) ? f : null;
        var dates = new[] { source.EffectiveFrom, D2025 };

        var resolver = new ConstantResolver(null!);
        var entities = constants.Select(ToEntity).ToList();
        var comparisons = 0;
        foreach (var date in dates)
        {
            foreach (var category in categories.Append("7000"))
            {
                foreach (var substance in substances)
                {
                    decimal? expected;
                    try
                    {
                        expected = resolver.Resolve(entities, 0, "K", category, SubstanceId(substance, substances), date, substance)?.Number;
                    }
                    catch (DomainException)
                    {
                        expected = null;
                    }

                    var actual = Lookup(script, function, "K", category, substance, date);
                    Assert.True(
                        expected == actual,
                        $"K[{category}][{substance}] on {date}: resolver {expected}, script {actual}\n{result.Text}");
                    comparisons++;
                }
            }
        }

        Assert.Equal(2 * 7 * 3, comparisons);
    }

    private static long SubstanceId(string code, string[] substances) => Array.IndexOf(substances, code) + 1;

    private static MethodologyConstant ToEntity(SourceConstant s)
    {
        var entity = new MethodologyConstant(0, EcrCode.Create("c1"), s.Number!.Value, 1);
        entity.SetScope(s.Category, s.SubstanceCode is null ? null : s.SubstanceCode switch { "0301" => 1, "0304" => 2, _ => 3 });
        entity.SetValidity(s.ValidFrom, s.ValidTo);
        return entity;
    }

    /// <summary>Читає згенеровану константу так, як це зробить скрипт: за категорією, речовиною і датою.</summary>
    private static decimal? Lookup(CompiledScript script, ScriptFunction? function, string name, string category, string substance, DateOnly date)
    {
        // Дата: без функції значення одне; з функцією — вибираємо шматок за початком.
        var constants = script.Constants;
        var candidates = constants.Where(c => c.Key == name || c.Key.StartsWith(name + "_from_", StringComparison.Ordinal))
            .OrderBy(c => c.Key, StringComparer.Ordinal).ToList();
        if (candidates.Count == 0)
        {
            return null;
        }

        KeyValuePair<string, ScriptConst> chosen;
        if (function is null)
        {
            chosen = candidates[0];
        }
        else
        {
            var pieces = candidates.Where(c => c.Key.StartsWith(name + "_from_", StringComparison.Ordinal)).ToList();
            chosen = pieces.Last(p => string.CompareOrdinal(p.Key[(name.Length + 6)..], date.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture)) <= 0);
        }

        var value = chosen.Value;
        var usedCategory = false;
        var usedSubstance = false;
        while (value.IsMap)
        {
            if (!usedCategory && value.Map!.TryGetValue(category, out var byCategory))
            {
                value = byCategory;
                usedCategory = true;
            }
            else if (!usedSubstance && value.Map!.TryGetValue(substance, out var bySubstance))
            {
                value = bySubstance;
                usedSubstance = true;
            }
            else
            {
                return null;
            }
        }

        return (decimal?)value.Scalar!.Value.Value;
    }
}
