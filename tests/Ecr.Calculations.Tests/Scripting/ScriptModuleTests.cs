using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using NSubstitute;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Calculations.Tests.Scripting;

/// <summary>
/// Виконання скрипта на рядок (MS T2.4, план §3.5–3.8, §4.4): мапи, власні функції, гілки, відмова рядка, речовини,
/// поля рядка, помилки виходів, бібліотеки.
/// </summary>
public sealed class ScriptModuleTests
{
    private const string Rows = "rows from T1;\n";

    private static ScriptStand Stand(string body, NumericMode mode = NumericMode.Strict, string imports = "")
    {
        var stand = new ScriptStand(imports + Rows + body, mode);
        stand.AddTable("T1").Column("S", CellDataType.String).Column("A").Column("B").Column("Opt");
        return stand;
    }

    // ───── мапи ─────

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Мапа_знайдений_ключ_дає_значення_а_невідомий_дає_NA()
    {
        var stand = Stand("const EF = { Summer: { NOx: 1.2 }, Winter: { NOx: 1.4 } };\noutput Y: kg = EF[row.S].NOx * row.A;");
        stand.Main.Row("R1", ("S", "Winter"), ("A", 10m)).Row("R2", ("S", "Autumn"), ("A", 10m));

        var (found, _) = await stand.RunAsync("R1");
        var (missing, _) = await stand.RunAsync("R2");

        Assert.Equal(14.0m, ScriptStand.Value(found, "Y"));
        Assert.Empty(missing.Values);
        Assert.Equal("#N/A", missing.StepError!.ErrorCode);
        Assert.Equal("Y", missing.StepError.StepCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Числовий_ключ_мапи_порівнюється_як_інваріантний_текст()
    {
        var stand = Stand("const K = { 1: 10, 2.5: 20 };\noutput Y: kg = K[row.A];");
        stand.Main.Row("R1", ("A", 2.50m)).Row("R2", ("A", 1m));

        Assert.Equal(20m, ScriptStand.Value((await stand.RunAsync("R1")).Output, "Y"));
        Assert.Equal(10m, ScriptStand.Value((await stand.RunAsync("R2")).Output, "Y"));
    }

    // ───── власна функція ─────

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Власна_функція_викликає_іншу_і_бачить_константи()
    {
        var stand = Stand(
            "const K = 3;\nfunction Half(x) = x / 2;\nfunction HalfPlusK(x) = Half(x) + K;\n"
            + "var h = Half(row.A);\noutput Y: kg = HalfPlusK(h);");
        stand.Main.Row("R1", ("A", 8m));

        var (output, _) = await stand.RunAsync("R1");

        Assert.Equal(5m, ScriptStand.Value(output, "Y"));
    }

    // ───── if / else ─────

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task If_істина_хибність_і_небулева_умова()
    {
        var stand = Stand(
            "if (row.A > 5) { output Hi: kg = 1; } else { output Lo: kg = 2; }\n"
            + "if (row.B) { output Num: kg = 3; }\n"
            + "if (row.Opt?) { output Nul: kg = 4; }");
        stand.Main.Row("R1", ("A", 9m), ("B", 1m)).Row("R2", ("A", 1m), ("B", 1m));

        var high = (await stand.RunAsync("R1")).Output;
        var low = (await stand.RunAsync("R2")).Output;

        Assert.Equal(1m, ScriptStand.Value(high, "Hi"));
        Assert.Null(ScriptStand.Value(high, "Lo"));
        Assert.Equal(2m, ScriptStand.Value(low, "Lo"));

        // Число в умові — не булеве: гілка не виконується, вихід лишається без результату з #VALUE.
        Assert.Null(ScriptStand.Value(high, "Num"));
        Assert.Equal("#VALUE", high.StepError!.ErrorCode);

        // null — «немає значення» (#NULL): гілки немає, а помилки рядка — теж.
        Assert.Null(ScriptStand.Value(high, "Nul"));
    }

    // ───── reject ─────

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Reject_кидає_відмову_рядка_того_самого_ключа_що_й_правило_категорії()
    {
        var stand = Stand("reject when row.A < 0;\noutput Y: kg = row.A;");
        stand.Main.Row("R1", ("A", -1m)).Row("R2", ("A", 3m));

        var error = await Assert.ThrowsAsync<DomainException>(() => stand.RunAsync("R1"));

        Assert.Equal("ECR-CALC-0422", error.ErrorCode);
        Assert.Equal("err.ECR-CALC-0422.categoryRuleFailed", error.Details!["messageKey"]);
        Assert.Equal("reject", error.Details["reason"]);
        Assert.Contains("row.A", (string)error.Details["expression"]!, StringComparison.Ordinal);
        Assert.Equal(3m, ScriptStand.Value((await stand.RunAsync("R2")).Output, "Y"));
    }

    // ───── речовини ─────

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Foreach_substance_виходи_per_S_skip_when_і_CURRENT_SUBSTANCE()
    {
        var stand = Stand(
            "foreach substance S in (\"0301\", \"0304\") {\n"
            + "    skip when S.code == \"0304\" && row.A > 100;\n"
            + "    output Q per S: g_per_s = row.A * (S.code == \"0301\" ? 2 : 3);\n"
            + "    output Z per S: kg = CURRENT_SUBSTANCE() == \"0301\" ? 1 : 2;\n"
            + "}\n"
            + "output Total: kg = row.A;");
        stand.WithSubstances(("0301", 9001), ("0304", 9002));
        stand.Main.Row("R1", ("A", 10m)).Row("R2", ("A", 200m));

        var normal = (await stand.RunAsync("R1")).Output;
        var skipped = (await stand.RunAsync("R2")).Output;

        Assert.Equal(20m, ScriptStand.Value(normal, "Q", 9001));
        Assert.Equal(30m, ScriptStand.Value(normal, "Q", 9002));
        Assert.Equal(1m, ScriptStand.Value(normal, "Z", 9001));
        Assert.Equal(2m, ScriptStand.Value(normal, "Z", 9002));
        Assert.Equal(10m, ScriptStand.Value(normal, "Total"));

        // skip when: друга речовина пропущена, перша порахована.
        Assert.Equal(400m, ScriptStand.Value(skipped, "Q", 9001));
        Assert.Null(ScriptStand.Value(skipped, "Q", 9002));
        Assert.Contains(skipped.Trace, s => s.StepCode == "SubstanceNotApplicable");
        Assert.Contains(normal.Trace, s => s.SubstanceEntryId == 9002);
    }

    // ───── поля рядка ─────

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Обовязкове_поле_без_значення_дає_MissingArguments()
    {
        var stand = Stand("output Y: kg = row.A * 2;");
        stand.Main.Row("R1", ("B", 1m));

        var (output, _) = await stand.RunAsync("R1");

        Assert.Empty(output.Values);
        Assert.Equal(["A"], output.MissingArguments);
        Assert.Equal("#ARG", output.StepError!.ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Необовязкове_поле_без_значення_дає_null_і_рядок_рахується()
    {
        var stand = Stand("var o = row.Opt?;\noutput Y: kg = row.B + 1;");
        stand.Main.Row("R1", ("B", 1m));

        var (output, values) = await stand.RunAsync("R1");

        Assert.Equal(2m, ScriptStand.Value(output, "Y"));
        Assert.Null(output.MissingArguments);
        var optional = Assert.Single(values, v => v.Name == "o");
        Assert.Equal("Null", optional.ValueType);
        Assert.Null(optional.Text);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Вихід_із_помилкою_дає_StepError_із_кодом_кроку_і_помилки()
    {
        var stand = Stand("output Y: kg = row.A / row.B;\noutput Z: kg = row.A;");
        stand.Main.Row("R1", ("A", 5m), ("B", 0m));

        var (output, _) = await stand.RunAsync("R1");

        Assert.Equal(5m, ScriptStand.Value(output, "Z"));
        Assert.Null(ScriptStand.Value(output, "Y"));
        Assert.Equal(new CalculationStepError("Y", "#DIV/0", 1), output.StepError);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Живий_перегляд_повертає_var_visible_і_output_із_типами()
    {
        var stand = Stand("var t = row.A * 2;\nvisible var v: kg = t + 1;\noutput Y: kg = v * 2;");
        stand.Main.Row("R1", ("A", 3m));

        var (output, values) = await stand.RunAsync("R1");

        Assert.Equal(14m, ScriptStand.Value(output, "Y"));
        Assert.Equal(7m, output.Values.Single(v => v.OutputCode == "v").Value);
        Assert.Equal(CalculationResultKind.Intermediate, output.Values.Single(v => v.OutputCode == "v").Kind);
        Assert.Equal(
            [("t", ScriptValueKind.Var, "6"), ("v", ScriptValueKind.Visible, "7"), ("Y", ScriptValueKind.Output, "14")],
            values.Select(v => (v.Name, v.Kind, v.Text)).ToArray());
        Assert.All(values, v => Assert.Equal("Number", v.ValueType));
    }

    // ───── бібліотеки ─────

    private const string Common =
        "const K = 2;\nfunction Twice(x) = x * K;\nvar baseValue = row.A + 1;\nvar scaled = Twice(baseValue);\n"
        + "var never = row.Missing * 2;";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Бібліотека_ліниво_рахує_величини_памятає_їх_і_пише_джерело_у_трейс()
    {
        var stand = Stand(
                "output Y: kg = Common.scaled + Common.scaled;\noutput Z: kg = Common.K + Common.Twice(row.A);",
                imports: "import Common;\n")
            .WithLibrary("Common", 8001, Common);
        stand.Main.Row("R1", ("A", 3m));

        var (output, _) = await stand.RunAsync("R1");

        Assert.Equal(16m, ScriptStand.Value(output, "Y"));
        Assert.Equal(8m, ScriptStand.Value(output, "Z"));

        // Пам'ять в межах рядка: `scaled` порахована один раз; `never` не потрібна — не рахувалась.
        Assert.Single(output.Trace, s => s.StepCode == "scaled");
        Assert.DoesNotContain(output.Trace, s => s.StepCode == "never");
        var step = output.Trace.Single(s => s.StepCode == "scaled");
        Assert.Contains("\"methodology\":\"Common\"", step.Detail, StringComparison.Ordinal);
        Assert.Contains("\"versionId\":8001", step.Detail, StringComparison.Ordinal);
        Assert.Null(output.StepError);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Величина_бібліотеки_памятається_окремо_для_кожної_речовини()
    {
        var stand = new ScriptStand(
            "import Common;\nrows from T1;\nforeach substance S in (\"0301\", \"0304\") { output Q per S: kg = Common.cur; }",
            NumericMode.Strict)
            .WithLibrary("Common", 8002, "var cur = CURRENT_SUBSTANCE() == \"0301\" ? 10 : 20;")
            .WithSubstances(("0301", 9001), ("0304", 9002));
        stand.AddTable("T1").Column("A");
        stand.Main.Row("R1", ("A", 1m));

        var (output, _) = await stand.RunAsync("R1");

        Assert.Equal(10m, ScriptStand.Value(output, "Q", 9001));
        Assert.Equal(20m, ScriptStand.Value(output, "Q", 9002));
        Assert.Equal(2, output.Trace.Count(s => s.StepCode == "cur"));
    }

    // ───── Legacy: маскування NaN/∞ у кроках скрипта й бібліотеки; масштаб виходу ─────

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Legacy_крок_скрипта_з_нескінченністю_стає_нулем_із_записом_у_трейс_а_Strict_дає_помилку()
    {
        const string body = "var d = row.A / row.B;\noutput Y: kg = d + 5;";
        var legacy = Stand(body, NumericMode.Legacy);
        legacy.Main.Row("R1", ("A", 1m), ("B", 0m));
        var strict = Stand(body);
        strict.Main.Row("R1", ("A", 1m), ("B", 0m));

        var (masked, _) = await legacy.RunAsync("R1");
        var (failed, _) = await strict.RunAsync("R1");

        // 1/0 у Legacy → +∞ → 0 (крок `d` маскований і записаний), далі 0 + 5.
        Assert.Equal(5m, ScriptStand.Value(masked, "Y"));
        Assert.Contains(masked.Trace, s => s.StepCode == "d" && s.Masked != MaskedZeroReason.None);
        Assert.Null(masked.StepError);

        Assert.Empty(failed.Values);
        Assert.Equal("#DIV/0", failed.StepError!.ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Legacy_величина_бібліотеки_з_нескінченністю_теж_стає_нулем_з_записом_у_трейс()
    {
        var legacy = Stand("output Y: kg = Common.q + 5;", NumericMode.Legacy, imports: "import Common;\n")
            .WithLibrary("Common", 8020, "var q = row.A / row.B;");
        legacy.Main.Row("R1", ("A", 1m), ("B", 0m));

        var (output, _) = await legacy.RunAsync("R1");

        Assert.Equal(5m, ScriptStand.Value(output, "Y"));
        Assert.Contains(output.Trace, s => s.StepCode == "q" && s.Masked != MaskedZeroReason.None);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Масштаб_колонки_приймача_округлює_вихід_а_без_нього_несе_шістнадцять_знаків()
    {
        var scaled = Stand("output Y: kg = 1 / 3;").WithOutputScale("Y", 2);
        scaled.Main.Row("R1", ("A", 1m));
        var plain = Stand("output Y: kg = 1 / 3;");
        plain.Main.Row("R1", ("A", 1m));

        var (withScale, _) = await scaled.RunAsync("R1");
        var (withoutScale, _) = await plain.RunAsync("R1");

        Assert.Equal(0.33m, ScriptStand.Value(withScale, "Y"));
        Assert.Equal(0.3333333333333333m, ScriptStand.Value(withoutScale, "Y"));
    }

    private const string ParamLibrary =
        "param Category;\nconst EF = { Summer: 1.5, Winter: 3 };\nvar factor = EF[Category];";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Param_бібліотеки_бере_змінну_імпортера_і_мапа_за_категорією_дає_значення()
    {
        var stand = Stand(
                "var Category = row.S;\noutput Y: kg = row.A * Common.factor;",
                imports: "import Common;\n")
            .WithLibrary("Common", 8010, ParamLibrary);
        stand.Main.Row("R1", ("S", "Winter"), ("A", 2m)).Row("R2", ("S", "Summer"), ("A", 2m));

        var (winter, _) = await stand.RunAsync("R1");
        var (summer, _) = await stand.RunAsync("R2");

        Assert.Equal(6m, ScriptStand.Value(winter, "Y"));
        Assert.Equal(3m, ScriptStand.Value(summer, "Y"));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Param_Category_у_циклі_речовин_бере_SubstanceCategory_а_поза_циклом_Category()
    {
        var stand = new ScriptStand(
                "import Common;\nrows from T1;\nvar Category = \"Winter\";\noutput Top: kg = Common.factor;\n"
                + "foreach substance S in (\"0301\", \"0304\") {\n"
                + "  var SubstanceCategory = S.code == \"0301\" ? \"Summer\" : \"Winter\";\n"
                + "  output Q per S: kg = Common.factor;\n}",
                NumericMode.Strict)
            .WithLibrary("Common", 8011, ParamLibrary)
            .WithSubstances(("0301", 9001), ("0304", 9002));
        stand.AddTable("T1").Column("A");
        stand.Main.Row("R1", ("A", 1m));

        var (output, _) = await stand.RunAsync("R1");

        // Пам'ять бібліотечної величини — окремо для рядка й для кожної речовини.
        Assert.Equal(3m, ScriptStand.Value(output, "Top"));
        Assert.Equal(1.5m, ScriptStand.Value(output, "Q", 9001));
        Assert.Equal(3m, ScriptStand.Value(output, "Q", 9002));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Param_довільного_імені_береться_зі_змінної_блоку_речовини()
    {
        var stand = new ScriptStand(
                "import Common;\nrows from T1;\nforeach substance S in (\"0301\") {\n"
                + "  var Weight = 4;\n  output Q per S: kg = Common.weighted;\n}\n",
                NumericMode.Strict)
            .WithLibrary("Common", 8012, "param Weight;\nvar weighted = Weight * 2;")
            .WithSubstances(("0301", 9001));
        stand.AddTable("T1").Column("A");
        stand.Main.Row("R1", ("A", 1m));

        var (output, _) = await stand.RunAsync("R1");

        Assert.Equal(8m, ScriptStand.Value(output, "Q", 9001));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Імпортер_без_змінної_param_дає_null_і_пошук_у_мапі_повертає_NA()
    {
        var stand = Stand("output Y: kg = Common.factor;", imports: "import Common;\n")
            .WithLibrary("Common", 8013, ParamLibrary);
        stand.Main.Row("R1", ("S", "Winter"), ("A", 2m));

        var (output, _) = await stand.RunAsync("R1");

        Assert.Empty(output.Values);
        Assert.Equal("#N/A", output.StepError!.ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Бібліотека_без_версії_зі_скриптом_відмовляє_підготовці()
    {
        var stand = Stand("output Y: kg = Common.K;", imports: "import Common;\n").WithLibrary("Common", 8003, "const K = 1;");
        stand.Main.Row("R1", ("A", 1m));
        _ = stand.Module;
        stand.Store.ResolveImportsAsync(ScriptStand.VersionId, Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns([new MethodologyLibrary(1, "Common", null, [])]);

        var error = await Assert.ThrowsAsync<DomainException>(() => stand.RunAsync("R1"));

        Assert.Equal("err.ECR-CALC-0422.scriptLibraryUnavailable", error.Details!["messageKey"]);
    }
}
