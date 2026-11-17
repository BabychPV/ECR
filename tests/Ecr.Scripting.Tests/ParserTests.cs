using Ecr.Scripting;
using Ecr.Scripting.Parsing;
using Ecr.Scripting.Syntax;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Scripting.Tests;

public sealed class ParserTests
{
    internal const string Example31 = """
        // Які рядки рахуємо
        rows from T3_00
            where row.Land_TypeFuel == entry("LAND_FUEL", "Gasoline")
               && row.Land_TypeEquipment in (entry("LAND_EQUIPMENT", "Compressor"), entry("LAND_EQUIPMENT", "Generator"))
            active when row.Land_Status == entry("LAND_GENERIC_EQUIPSTATUS", "Running");

        // Коефіцієнти
        const k1 = 5;
        const k2 = 0.25;
        const EF_gsec = {
            Summer: { NOx: 1.2, s330: 0.3, s337: 7.5, s2704: 0.1 },
            Winter: { NOx: 1.4, s330: 0.3, s337: 9.3, s2704: 0.1 },
        };

        // Розрахунок
        var hours  = row.Land_RunningHoursMonthly;
        var fuel   = row.Land_Measure_FuelSubstanceConsumptionOrPumpedVolume;
        var season = period.Month >= 4 && period.Month <= 9 ? "Summer" : "Winter";
        var idle   = hours == 0 && fuel == 0;
        var nox_gs = k1 * k2 * EF_gsec[season].NOx / 3600;

        output ECW_EC_gsec_301 : g_per_s = idle ? 0 : nox_gs * 0.8;
        output ECW_EC_gsec_304 : g_per_s = idle ? 0 : nox_gs * 0.13;
        output ECW_EC_tons_337 : t       = hours * k1 * k2 * EF_gsec[season].s337 / 1000000;
        """;

    private static ScriptParseResult Parse(string text) => ScriptParser.Parse(text);

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Приклад_з_розділу_3_1_розбирається_без_діагностик()
    {
        var result = Parse(Example31);

        Assert.Empty(result.Diagnostics);
        Assert.Single(result.Program.Items.OfType<RowsSyntax>());
        Assert.Equal(3, result.Program.Items.OfType<ConstSyntax>().Count());
        Assert.Equal(5, result.Program.Items.OfType<VarSyntax>().Count());
        Assert.Equal(3, result.Program.Items.OfType<OutputSyntax>().Count());
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Кожна_конструкція_граматики_розбирається()
    {
        const string text = """
            import Common;
            import Other;
            rows from T1, Tpl.T2 where !(row.A == "x" || row.B != null) && row.C in ("a", "b") active when row.D == "1";
            rows from T3;
            table Self = T1;
            const a : kg = 1;
            const b = -2.5;
            const s = 'text';
            const t = true;
            const f = false;
            const m = { x: 1, "y z": 2, 3: { k: "v", }, };
            function sq(v) = v * v;
            function two(p, q) = p + q;
            function zero() = 0;
            var v1 = 1;
            visible var v2 : g = Pow(v1, 2);
            output O1 : t = v2 > 1 ? 1 : 2;
            reject when v1 < 0;
            if (v1 == 1) { var inner = 1; } else if (v1 == 2) { var inner2 = 2; } else { var inner3 = 3; }
            foreach substance S in ("0301", "0304") {
                skip when S.code == "0301";
                output O2 per S : t = m["x"];
            }
            var e1 = row.X? ;
            var e2 = row.X ? 1 : 2;
            var e3 = key(row.X?) ;
            var e4 = a in (1, 2, 3) && !(b <= 3) || -a >= +b;
            var e5 = Common.f(1) + Common.v + m.x + m["x"] + m[1, 2];
            var e6 = period.Days + header.N + entry("R", "C");
            var e7 = 1 % 2 - 3 / 4 * 5;
            """;

        var result = Parse(text);

        Assert.Empty(result.Diagnostics);
        Assert.Equal(2, result.Program.Imports.Count);
        Assert.Equal(2, result.Program.Items.OfType<RowsSyntax>().Count());
        Assert.Single(result.Program.Items.OfType<TableSyntax>());
        Assert.Equal(3, result.Program.Items.OfType<FunctionSyntax>().Count());
        Assert.Single(result.Program.Items.OfType<IfSyntax>());
        Assert.Single(result.Program.Items.OfType<ForeachSubstanceSyntax>());
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Необовязкове_поле_упритул_відрізняється_від_тернарного_знака_питання()
    {
        var tight = Parse("var a = row.X?;").Program.Items.OfType<VarSyntax>().Single().Init;
        var spaced = Parse("var a = row.X ? 1 : 2;").Program.Items.OfType<VarSyntax>().Single().Init;

        Assert.True(Assert.IsType<RowExpr>(tight).IsOptional);
        Assert.IsType<TernaryExpr>(spaced);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Пріоритет_множення_вищий_за_додавання()
    {
        var init = Parse("var a = 1 + 2 * 3;").Program.Items.OfType<VarSyntax>().Single().Init;

        var add = Assert.IsType<BinaryExpr>(init);
        Assert.IsType<NumberExpr>(add.Left);
        Assert.IsType<BinaryExpr>(add.Right);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Відновлення_після_помилки_показує_кілька_помилок()
    {
        var result = Parse("const a = ;\nconst b = ;\nvar c = 1 +;\nvar ok = 1;");

        Assert.Equal(3, result.Diagnostics.Count);
        Assert.Equal([1, 2, 3], result.Diagnostics.Select(d => d.Line));
        Assert.Contains(result.Program.Items, i => i is VarSyntax { Name: "ok" });
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Відновлення_всередині_блока_не_ламає_решту_блока()
    {
        var result = Parse("if (a) { var x = ; var y = 2; } var z = 3;");

        Assert.Single(result.Diagnostics);
        var ifs = result.Program.Items.OfType<IfSyntax>().Single();
        Assert.Contains(ifs.Then.Statements, s => s is VarSyntax { Name: "y" });
        Assert.Contains(result.Program.Items, i => i is VarSyntax { Name: "z" });
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Пропущена_крапка_з_комою_дає_expected_у_позиції_наступної_лексеми()
    {
        var result = Parse("var a = 1\nvar b = 2;");

        var d = Assert.Single(result.Diagnostics);
        Assert.Equal(ScriptDiagnosticCodes.Expected, d.Code);
        Assert.Equal((2, 1), (d.Line, d.Column));
        Assert.Contains(result.Program.Items, i => i is VarSyntax { Name: "b" });
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Зарезервоване_слово_як_імя_дає_expected()
    {
        var d = Assert.Single(Parse("var row = 1;").Diagnostics);

        Assert.Equal(ScriptDiagnosticCodes.Expected, d.Code);
        Assert.Equal((1, 5), (d.Line, d.Column));
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Одинарне_дорівнює_у_виразі_дає_assignInExpression_без_каскаду()
    {
        var result = Parse("var a = b = 1;\nreject when row.X = 1;");

        Assert.Equal(2, result.Diagnostics.Count(d => d.Code == ScriptDiagnosticCodes.AssignInExpression));
        Assert.Equal(2, result.Diagnostics.Count);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Імпорт_після_інших_оголошень_є_помилкою()
    {
        var d = Assert.Single(Parse("var a = 1; import X;").Diagnostics);

        Assert.Equal(ScriptDiagnosticCodes.UnexpectedToken, d.Code);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Const_усередині_блока_заборонений()
    {
        var d = Assert.Single(Parse("if (a) { const k = 1; }").Diagnostics);

        Assert.Equal(ScriptDiagnosticCodes.UnexpectedToken, d.Code);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Зайва_закрита_дужка_на_верхньому_рівні_не_зациклює_розбір()
    {
        var result = Parse("} var a = 1; )");

        Assert.Equal(2, result.Diagnostics.Count);
        Assert.Contains(result.Program.Items, i => i is VarSyntax { Name: "a" });
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Надмірна_вкладеність_виразу_перериває_розбір_з_tooDeep()
    {
        var text = "var a = " + new string('(', 80) + "1" + new string(')', 80) + ";";

        var result = Parse(text);

        Assert.True(result.Aborted);
        Assert.Equal(ScriptDiagnosticCodes.TooDeep, Assert.Single(result.Diagnostics).Code);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Агрегат_з_where_і_Max_з_двома_аргументами_розрізняються()
    {
        var agg = Parse("var a = Max(T.X where T.Y == 1);").Program.Items.OfType<VarSyntax>().Single().Init;
        var call = Parse("var a = Max(1, 2);").Program.Items.OfType<VarSyntax>().Single().Init;

        Assert.IsType<AggregateExpr>(agg);
        Assert.IsType<CallExpr>(call);
    }
}
