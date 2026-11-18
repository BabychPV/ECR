using Ecr.Expressions.Ast;
using Ecr.Expressions.Evaluation;
using Ecr.Scripting;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Scripting.Tests;

public sealed class MetadataAndFeaturesTests
{
    private static string Print(CompiledScript s, int statement)
        => AstPrinter.Print(s.Statements[statement] switch
        {
            VarStatement v => v.Expr,
            OutputStatement o => o.Expr,
            _ => throw new InvalidOperationException(),
        });

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Приклад_3_1_компілюється_без_діагностик_і_дає_очікувані_метадані()
    {
        var c = T.Compile(ParserTests.Example31);

        Assert.True(c.IsSuccess, T.Describe(c));
        var s = c.Script!;

        Assert.Equal(
            [("ECW_EC_gsec_301", "g_per_s", 1), ("ECW_EC_gsec_304", "g_per_s", 2), ("ECW_EC_tons_337", "t", 3)],
            s.Outputs.Select(o => (o.Code, o.UnitCode, o.Ordinal)));
        Assert.All(s.Outputs, o => Assert.False(o.IsPerSubstance));
        Assert.Equal(
            ["Land_RunningHoursMonthly", "Land_Measure_FuelSubstanceConsumptionOrPumpedVolume"],
            s.RowFields.Select(f => f.Name));
        Assert.All(s.RowFields, f => Assert.False(f.IsOptional));
        Assert.Equal(["Land_TypeFuel", "Land_TypeEquipment", "Land_Status"], s.MatchColumns);
        Assert.Equal(
            [("LAND_FUEL", "Gasoline"), ("LAND_EQUIPMENT", "Compressor"), ("LAND_EQUIPMENT", "Generator"),
             ("LAND_GENERIC_EQUIPSTATUS", "Running")],
            s.RegistryEntries.Select(e => (e.RegistryCode, e.EntryCode)));
        Assert.Equal(["LAND_FUEL", "LAND_EQUIPMENT", "LAND_GENERIC_EQUIPSTATUS"], s.RegistryCodes);
        Assert.True(s.NeedsRegistries);
        Assert.Empty(s.Imports);
        Assert.Empty(s.Visibles);
        Assert.Empty(s.Substances);
        var clause = Assert.Single(s.Rows);
        Assert.Equal("T3_00", Assert.Single(clause.Tables).TableCode);
        Assert.NotNull(clause.Where);
        Assert.NotNull(clause.Active);
        Assert.Equal(["k1", "k2", "EF_gsec"], s.Constants.Keys);
        Assert.Equal(["g_per_s", "g_per_s", "t"], s.Units.Select(u => u.Code));
        Assert.Contains(s.Members, m => m is { Name: "EF_gsec", Kind: MemberKind.Const, IsMap: true });
        Assert.Contains(s.Members, m => m is { Name: "nox_gs", Kind: MemberKind.Var });
        Assert.Equal(
            "(((!k1 * !k2) * $lookup('EF_gsec', !season, 'NOx')) / 3600)",
            Print(s, 4));
        Assert.Equal("if(!idle, 0, (!nox_gs * 0.8))", Print(s, 5));
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void RowFields_обовязкове_якщо_хоч_одне_звернення_без_знака_питання()
    {
        var c = T.WithRows("var a = row.X?; var b = row.X; var c = row.Y?; var d = key(row.Z);");

        var fields = c.Script!.RowFields.ToDictionary(f => f.Name, f => f.IsOptional);
        Assert.False(fields["X"]);
        Assert.True(fields["Y"]);
        Assert.False(fields["Z"]);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Visibles_substances_і_одиниці_збираються()
    {
        var c = T.WithRows(
            "visible var v : g = 1; foreach substance S in (\"1\", \"2\") { output O per S : t = 1; }" +
            " foreach substance Q in (\"2\", \"3\") { output P per Q : t = 1; }");

        Assert.Empty(c.Diagnostics);
        Assert.Equal([("v", "g")], c.Script!.Visibles.Select(v => (v.Code, v.UnitCode)));
        Assert.Equal(["1", "2", "3"], c.Script.Substances);
        Assert.All(c.Script.Outputs, o => Assert.True(o.IsPerSubstance));
        Assert.Equal(["g", "t", "t"], c.Script.Units.Select(u => u.Code));
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Library_містить_члени_для_імпортерів()
    {
        var lib = T.Lib("const K : kg = 1; function f(a, b) = a + b; var v = K;");

        Assert.True(lib.IsLibrary);
        Assert.Equal(
            [("K", MemberKind.Const, 0), ("f", MemberKind.Function, 2), ("v", MemberKind.Var, 0)],
            lib.Members.Select(m => (m.Name, m.Kind, m.ParameterCount)));
        Assert.Equal("kg", lib.Members[0].UnitCode);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Бібліотека_може_імпортувати_бібліотеку_і_передає_довідники()
    {
        var inner = T.Lib("const K = 1; var e = entry(\"R\", \"C\");");
        var outer = T.Compile("import Inner; var v = Inner.K;", library: true, import: _ => inner);
        Assert.Empty(outer.Diagnostics);

        var main = T.Compile("import Outer; rows from T1; var x = Outer.v;", import: _ => outer.Script);

        Assert.Empty(main.Diagnostics);
        Assert.True(main.Script!.NeedsRegistries);
    }

    // ───── Match ─────

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void MatchEvaluator_таблиця_істинності()
    {
        var cells = new Dictionary<string, string?> { ["A"] = "x", ["B"] = "7", ["N"] = null };
        string? Column(string c) => cells.GetValueOrDefault(c);
        string? Entry(string r, string c) => (r, c) is ("REG", "SEVEN") ? "7" : null;

        bool Eval(MatchNode n) => MatchEvaluator.Evaluate(n, Column, Entry);

        var xText = new MatchValue.Text("x");
        var seven = new MatchValue.Entry("REG", "SEVEN");
        var missingEntry = new MatchValue.Entry("REG", "NOPE");
        var nul = new MatchValue.Null();

        Assert.True(Eval(new MatchEquals("A", xText, false)));
        Assert.False(Eval(new MatchEquals("A", new MatchValue.Text("X"), false))); // регістр важливий
        Assert.False(Eval(new MatchEquals("A", xText, true)));
        Assert.True(Eval(new MatchEquals("A", new MatchValue.Text("y"), true)));
        Assert.True(Eval(new MatchEquals("B", seven, false)));
        Assert.False(Eval(new MatchEquals("B", missingEntry, false)));
        Assert.True(Eval(new MatchEquals("B", missingEntry, true)));
        Assert.True(Eval(new MatchEquals("N", nul, false)));
        Assert.True(Eval(new MatchEquals("NoSuchColumn", nul, false)));
        Assert.False(Eval(new MatchEquals("A", nul, false)));
        Assert.True(Eval(new MatchEquals("A", nul, true)));
        Assert.False(Eval(new MatchEquals("NoSuchColumn", xText, false)));
        Assert.True(Eval(new MatchIn("A", [new MatchValue.Text("q"), xText])));
        Assert.False(Eval(new MatchIn("A", [new MatchValue.Text("q")])));
        Assert.True(Eval(new MatchIn("B", [seven])));

        var t = new MatchEquals("A", xText, false);
        var f = new MatchEquals("A", new MatchValue.Text("zzz"), false);
        Assert.True(Eval(new MatchAnd(t, t)));
        Assert.False(Eval(new MatchAnd(t, f)));
        Assert.False(Eval(new MatchAnd(f, t)));
        Assert.False(Eval(new MatchAnd(f, f)));
        Assert.True(Eval(new MatchOr(t, f)));
        Assert.True(Eval(new MatchOr(f, t)));
        Assert.False(Eval(new MatchOr(f, f)));
        Assert.False(Eval(new MatchNot(t)));
        Assert.True(Eval(new MatchNot(f)));
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Where_і_active_when_з_тексту_обчислюються_MatchEvaluator()
    {
        var c = T.Compile("rows from T1 where row.A == \"x\" && !(row.B in (\"1\", \"2\")) active when row.S != null;");

        var clause = Assert.Single(c.Script!.Rows);
        var cells = new Dictionary<string, string?> { ["A"] = "x", ["B"] = "3", ["S"] = "on" };
        bool Eval(MatchNode n) => MatchEvaluator.Evaluate(n, k => cells.GetValueOrDefault(k), (_, _) => null);

        Assert.True(Eval(clause.Where!));
        Assert.True(Eval(clause.Active!));
        cells["B"] = "2";
        Assert.False(Eval(clause.Where!));
        cells["S"] = null;
        Assert.False(Eval(clause.Active!));
    }

    // ───── ScriptFormatter ─────

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void FormatConst_скаляри_і_плоска_мапа()
    {
        var c = T.WithRows("const n = 1.50; const s = \"a\\\"b\"; const b = true; const m = { x: 1, \"y z\": 2.5, 3: 1, \"0301\": 4 };");

        var k = c.Script!.Constants;
        Assert.Equal("1.5", ScriptFormatter.FormatConst(k["n"]));
        Assert.Equal("\"a\\\"b\"", ScriptFormatter.FormatConst(k["s"]));
        Assert.Equal("true", ScriptFormatter.FormatConst(k["b"]));
        Assert.Equal("{\n    x: 1,\n    \"y z\": 2.5,\n    3: 1,\n    \"0301\": 4,\n}", ScriptFormatter.FormatConst(k["m"]));
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void FormatConst_вкладені_мапи_з_рівними_ключами_вирівнюються_в_один_рядок()
    {
        var c = T.WithRows("const EF = { Summer: { NOx: 1.2, s337: 7.5 }, Winter: { NOx: 14, s337: 9.3 } };");

        var text = ScriptFormatter.FormatConst(c.Script!.Constants["EF"]);

        Assert.Equal(
            "{\n    Summer: { NOx: 1.2, s337: 7.5 },\n    Winter: { NOx: 14,  s337: 9.3 },\n}",
            text);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void FormatConst_результат_компілюється_назад_у_ту_саму_константу()
    {
        const string source = "const M = { a: { x: 1, y: 2 }, b: { x: 3, y: 4 }, c: { z: 5 }, \"0301\": { x: 1.5 } };";
        var first = T.WithRows(source).Script!.Constants["M"];

        var text = ScriptFormatter.FormatConst(first);
        var second = T.WithRows("const M = " + text + ";");

        Assert.Empty(second.Diagnostics);
        Assert.Equal(text, ScriptFormatter.FormatConst(second.Script!.Constants["M"]));
    }

    // ───── 13.1 агрегати і таблиці ─────

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Агрегат_Max_з_where_із_прикладу_плану_опускається_в_agg()
    {
        var c = T.WithRows(
            "table Gtu = T10_03;\n" +
            "var no2 = Max(Gtu.Land_Measure_GTU1_301 where Gtu.Land_GTU_MonthNo == period.Month);");

        Assert.Empty(c.Diagnostics);
        var s = c.Script!;
        Assert.Equal("$agg(0)", Print(s, 0));
        var agg = Assert.Single(s.Aggregates);
        Assert.Equal(AggregateKind.Max, agg.Kind);
        Assert.Equal("Gtu", agg.TableAlias);
        Assert.Equal("$cell('Gtu', 'Land_Measure_GTU1_301')", AstPrinter.Print(agg.Value!));
        Assert.Equal("($cell('Gtu', 'Land_GTU_MonthNo') = [Period].Sequence)", AstPrinter.Print(agg.Where!));
        Assert.Equal("T10_03", s.Tables["Gtu"].TableCode);
        Assert.Equal(["T10_03"], s.ReadTables);
        Assert.Equal(["Land_Measure_GTU1_301", "Land_GTU_MonthNo"], s.TableColumns["Gtu"]);
    }

    [Theory] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [InlineData("Sum(G.A)", AggregateKind.Sum)]
    [InlineData("Min(G.A)", AggregateKind.Min)]
    [InlineData("Max(G.A)", AggregateKind.Max)]
    [InlineData("Avg(G.A * 2)", AggregateKind.Avg)]
    [InlineData("First(G.A)", AggregateKind.First)]
    public void Агрегати_значення_без_умови(string expr, AggregateKind kind)
    {
        var c = T.WithRows("table G = Tpl.T2; var x = " + expr + ";");

        Assert.Empty(c.Diagnostics);
        var agg = Assert.Single(c.Script!.Aggregates);
        Assert.Equal(kind, agg.Kind);
        Assert.Null(agg.Where);
        Assert.Equal("Tpl.T2", c.Script.Tables["G"].ToString());
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Count_і_Any_приймають_псевдонім_без_значення()
    {
        var c = T.WithRows("table G = T2; var n = Count(G where G.A > row.B); var any = Any(G);");

        Assert.Empty(c.Diagnostics);
        Assert.Equal([AggregateKind.Count, AggregateKind.Any], c.Script!.Aggregates.Select(a => a.Kind));
        Assert.All(c.Script.Aggregates, a => Assert.Null(a.Value));
        Assert.Equal("($cell('G', 'A') > @B)", AstPrinter.Print(c.Script.Aggregates[0].Where!));
        Assert.Contains(c.Script.RowFields, f => f.Name == "B");
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Місячна_колонка_таблиці_опускається_у_cellMonth_і_вкладений_агрегат_над_іншою_таблицею_дозволений()
    {
        var c = T.WithRows(
            "table G = T2; table H = T3; table Self = T1;\n" +
            "var a = Sum(G.Hours[period.Month]);\n" +
            "var b = Sum(G.A where Any(H where H.K == G.K));\n" +
            "var c = Count(Self);");

        Assert.Empty(c.Diagnostics);
        Assert.Equal("($agg(0))", "(" + Print(c.Script!, 0) + ")");
        Assert.Equal("$cellMonth('G', 'Hours', [Period].Sequence)", AstPrinter.Print(c.Script!.Aggregates[0].Value!));
        Assert.Equal(AggregateKind.Any, c.Script.Aggregates[1].Kind); // вкладений реєструється першим
        Assert.Equal(AggregateKind.Sum, c.Script.Aggregates[2].Kind);
        Assert.Equal(["T2", "T3", "T1"], c.Script.ReadTables);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Max_з_двома_аргументами_лишається_функцією_каталогу()
    {
        var c = T.WithRows("var x = Max(1, 2);");

        Assert.Empty(c.Diagnostics);
        Assert.Empty(c.Script!.Aggregates);
        Assert.Equal("Max(1, 2)", Print(c.Script, 0));
    }

    // ───── 13.2 місячні колонки ─────

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Місячна_колонка_рядка_опускається_у_month()
    {
        var c = T.WithRows("var a = row.Hours[period.Month]; var b = row.Opt?[3];");

        Assert.Empty(c.Diagnostics);
        Assert.Equal("$month('Hours', [Period].Sequence, FALSE)", Print(c.Script!, 0));
        Assert.Equal("$month('Opt', 3, TRUE)", Print(c.Script!, 1));
        Assert.All(c.Script!.RowFields, f => Assert.True(f.IsMonthly));
        Assert.False(c.Script.RowFields.Single(f => f.Name == "Hours").IsOptional);
        Assert.True(c.Script.RowFields.Single(f => f.Name == "Opt").IsOptional);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Місячний_індекс_має_бути_одним_ключем()
    {
        var c = T.WithRows("var a = row.Hours[1, 2];");

        Assert.Contains(c.Diagnostics, d => d.Code == ScriptDiagnosticCodes.ArgumentCount);
    }

    // ───── 13.3 foreach для повторів ─────

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Foreach_вставляє_значення_в_імена_рядки_й_замінює_змінну_літералом()
    {
        var c = T.WithRows(
            "const EF = { s301: 1, s304: 2 };\n" +
            "foreach S in (301, 304) {\n" +
            "    output ECW_EC_gsec_{S} : g_per_s = S * EF[\"s{S}\"];\n" +
            "}");

        Assert.True(c.IsSuccess, T.Describe(c));
        Assert.Equal(["ECW_EC_gsec_301", "ECW_EC_gsec_304"], c.Script!.Outputs.Select(o => o.Code));
        Assert.Equal("(301 * $lookup('EF', 's301'))", Print(c.Script, 0));
        Assert.Equal("(304 * $lookup('EF', 's304'))", Print(c.Script, 1));
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Foreach_за_рядками_і_оператори_потрапляють_у_зовнішню_область()
    {
        var c = T.WithRows(
            "foreach K in (\"a\", \"b\") { var x_{K} = \"k{K}\"; }\n" +
            "var total = x_a + x_b;");

        Assert.True(c.IsSuccess, T.Describe(c));
        Assert.Equal(["x_a", "x_b", "total"], c.Script!.Statements.Cast<VarStatement>().Select(v => v.Name));
        Assert.Equal("'ka'", Print(c.Script, 0));
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Foreach_вкладений_у_foreach_substance_і_подвійний_foreach()
    {
        var c = T.WithRows(
            "foreach substance S in (\"0301\") {\n" +
            "    foreach N in (1, 2) { output Q{N} per S : t = N; }\n" +
            "}\n" +
            "foreach A in (1, 2) { foreach B in (\"x\", \"y\") { var v{A}{B} = A; } }");

        Assert.True(c.IsSuccess, T.Describe(c));
        Assert.Equal(["Q1", "Q2"], c.Script!.Outputs.Select(o => o.Code));
        var names = c.Script.Statements.OfType<VarStatement>().Select(v => v.Name).ToList();
        Assert.Equal(["v1x", "v1y", "v2x", "v2y"], names);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Foreach_дублі_імен_після_розгортання_є_помилкою_а_позиція_вказує_на_джерело()
    {
        var c = T.WithRows("foreach N in (1, 1) { var v{N} = 1; }");

        var d = Assert.Single(c.Diagnostics, x => x.Code == ScriptDiagnosticCodes.DuplicateName);
        Assert.Equal(2, d.Line); // рядок вихідного тексту, а не розгорнутого
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Foreach_допускає_ключове_слово_лише_якщо_результат_ідентифікатор()
    {
        var c = T.WithRows("foreach S in (1) { var a_{S}x = S; }");

        Assert.Empty(c.Diagnostics);
        Assert.Equal("a_1x", Assert.IsType<VarStatement>(Assert.Single(c.Script!.Statements)).Name);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Param_у_бібліотеці_компілюється_лишається_змінною_і_потрапляє_в_Params()
    {
        var script = T.Lib("param Category;\nparam Other;\nconst EF = { A: 1 };\nvar f = EF[Category];");

        Assert.Equal(["Category", "Other"], script.Params);
        Assert.DoesNotContain(script.Members, m => m.Name == "Category");
        Assert.Equal("$lookup('EF', !Category)", AstPrinter.Print(((VarStatement)script.Statements.Single()).Expr));
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Param_поза_бібліотекою_дає_paramOutsideLibrary()
    {
        var c = T.WithRows("param Category;\nvar x = 1;");

        Assert.Contains(ScriptDiagnosticCodes.ParamOutsideLibrary, T.Codes(c));
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Param_повторне_оголошення_і_використання_до_оголошення_діагностуються()
    {
        Assert.Contains(ScriptDiagnosticCodes.DuplicateName, T.Codes(T.Compile("param A;\nparam A;", library: true)));
        Assert.Contains(ScriptDiagnosticCodes.DuplicateName, T.Codes(T.Compile("var A = 1;\nparam A;", library: true)));
        Assert.Contains(ScriptDiagnosticCodes.ForwardReference, T.Codes(T.Compile("var v = A;\nparam A;", library: true)));
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Param_є_зарезервованим_словом_і_потребує_імені_та_крапки_з_комою()
    {
        Assert.Contains(ScriptDiagnosticCodes.Expected, T.Codes(T.Compile("var param = 1;", library: true)));
        Assert.NotEmpty(T.Compile("param ;", library: true).Diagnostics);
        Assert.NotEmpty(T.Compile("param X", library: true).Diagnostics);
    }
}
