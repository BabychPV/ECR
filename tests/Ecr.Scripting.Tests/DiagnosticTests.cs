using Ecr.Domain.Enums;
using Ecr.Scripting;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Scripting.Tests;

/// <summary>По тесту на кожен код діагностики (METHODOLOGY-SCRIPT-PLAN §3.9 і §13.7).</summary>
public sealed class DiagnosticTests
{
    private static void Has(ScriptCompilation c, string code)
        => Assert.True(c.Diagnostics.Any(d => d.Code == code), $"очікувався {code}, є: {T.Describe(c)}");

    private static ScriptDiagnostic One(ScriptCompilation c, string code)
        => Assert.Single(c.Diagnostics, d => d.Code == code);

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Діагностика_має_ключ_параметри_і_позицію()
    {
        var c = T.WithRows("var x = y;");

        var d = One(c, ScriptDiagnosticCodes.UnknownName);
        Assert.Equal("script.unknownName", d.MessageKey);
        Assert.Equal("y", d.Params["name"]);
        Assert.Equal((2, 9, 1), (d.Line, d.Column, d.Length));
        Assert.False(c.IsSuccess);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void unexpectedToken() => Has(T.WithRows("var x = ) ;"), ScriptDiagnosticCodes.UnexpectedToken);

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void expected() => Has(T.Compile("rows from T1 var x = 1;"), ScriptDiagnosticCodes.Expected);

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void unterminatedString() => Has(T.WithRows("var x = \"abc;"), ScriptDiagnosticCodes.UnterminatedString);

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void unterminatedComment() => Has(T.WithRows("/* x"), ScriptDiagnosticCodes.UnterminatedComment);

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void invalidNumber() => Has(T.WithRows("var x = .5;"), ScriptDiagnosticCodes.InvalidNumber);

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void caretNotPower() => Has(T.WithRows("var x = 2 ^ 3;"), ScriptDiagnosticCodes.CaretNotPower);

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void assignInExpression() => Has(T.WithRows("reject when row.A = 1;"), ScriptDiagnosticCodes.AssignInExpression);

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void duplicateName_без_урахування_регістру()
    {
        var c = T.WithRows("const a = 1; const A = 2;");

        Has(c, ScriptDiagnosticCodes.DuplicateName);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void duplicateName_імя_блока_збігається_із_зовнішнім()
    {
        var c = T.WithRows("var a = 1; if (a > 0) { var a2 = 1; var A = 2; }");

        Assert.Equal(ScriptDiagnosticCodes.DuplicateName, Assert.Single(c.Diagnostics).Code);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Змінна_блока_поза_блоком_невидима()
    {
        var c = T.WithRows("if (true) { var inner = 1; } var y = inner;");

        Has(c, ScriptDiagnosticCodes.UnknownName);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void unknownName() => Has(T.WithRows("var x = nothing;"), ScriptDiagnosticCodes.UnknownName);

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void forwardReference() => Has(T.WithRows("var x = y; var y = 1;"), ScriptDiagnosticCodes.ForwardReference);

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Const_і_функція_видимі_до_свого_оголошення()
    {
        var c = T.WithRows("var x = k + f(1); const k = 1; function f(a) = a;");

        Assert.Empty(c.Diagnostics);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void unknownFunction_з_порадою_про_регістр()
    {
        var c = T.WithRows("var x = POW(2, 3);");

        var d = One(c, ScriptDiagnosticCodes.UnknownFunction);
        Assert.Equal("Pow", d.Params["suggestion"]);
        Assert.Contains("Pow", d.Params["advice"], StringComparison.Ordinal);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void argumentCount()
    {
        Has(T.WithRows("var x = Pow(1);"), ScriptDiagnosticCodes.ArgumentCount);
        Has(T.WithRows("function f(a, b) = a; var x = f(1);"), ScriptDiagnosticCodes.ArgumentCount);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void functionNotAllowedInMode_у_Legacy()
    {
        var legacy = T.WithRows("var x = Ln(1);", NumericMode.Legacy);
        var strict = T.WithRows("var x = Ln(1);", NumericMode.Strict);

        Has(legacy, ScriptDiagnosticCodes.FunctionNotAllowedInMode);
        Assert.Empty(strict.Diagnostics);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void recursiveFunction_пряма_і_через_іншу()
    {
        Has(T.WithRows("function f(a) = f(a);"), ScriptDiagnosticCodes.RecursiveFunction);
        var c = T.WithRows("function f(a) = g(a); function g(a) = f(a);");
        Assert.Equal(2, c.Diagnostics.Count(d => d.Code == ScriptDiagnosticCodes.RecursiveFunction));
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Тіло_функції_не_бачить_var()
    {
        var c = T.WithRows("var v = 1; function f(a) = a + v;");

        Has(c, ScriptDiagnosticCodes.UnknownName);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void outputOutsideSubstanceLoop() => Has(T.WithRows("output X per S : t = 1;"), ScriptDiagnosticCodes.OutputOutsideSubstanceLoop);

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void outputNeedsPer()
        => Has(T.WithRows("foreach substance S in (\"1\") { output X : t = 1; }"), ScriptDiagnosticCodes.OutputNeedsPer);

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void nestedSubstanceLoop()
        => Has(T.WithRows("foreach substance S in (\"1\") { foreach substance Q in (\"2\") { var a = 1; } }"), ScriptDiagnosticCodes.NestedSubstanceLoop);

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void skipOutsideSubstanceLoop()
    {
        Has(T.WithRows("skip when true;"), ScriptDiagnosticCodes.SkipOutsideSubstanceLoop);
        Has(T.WithRows("foreach substance S in (\"1\") { if (true) { skip when true; } }"), ScriptDiagnosticCodes.SkipOutsideSubstanceLoop);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void rowsInLibrary_і_outputInLibrary()
    {
        var c = T.Compile("rows from T1; output X : t = 1;", library: true);

        Has(c, ScriptDiagnosticCodes.RowsInLibrary);
        Has(c, ScriptDiagnosticCodes.OutputInLibrary);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void missingRows_лише_для_не_бібліотеки()
    {
        Has(T.Compile("var x = 1;"), ScriptDiagnosticCodes.MissingRows);
        Assert.Empty(T.Compile("var x = 1;", library: true).Diagnostics);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void missingRows_немає_для_тексту_лише_з_коментарів()
    {
        // ✎ P-1124-1 (людина 24.11: «свіжа чернетка — 0 проблем»): заготовка з коментарів — без проблем; щойно
        // з'являється бодай одна конструкція без rows — missingRows, як і раніше (`output` чи `const`).
        Assert.Empty(T.Compile("// rows from T1;\n// output X : t = 1;\n").Diagnostics);
        Assert.Empty(T.Compile("").Diagnostics);
        Has(T.Compile("// header\noutput X : t = 1;"), ScriptDiagnosticCodes.MissingRows);
        Has(T.Compile("const k = 1;"), ScriptDiagnosticCodes.MissingRows);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void duplicateTable()
    {
        Has(T.Compile("rows from T1, T2; rows from T2;"), ScriptDiagnosticCodes.DuplicateTable);
        Has(T.Compile("rows from T1, T1;"), ScriptDiagnosticCodes.DuplicateTable);
        Assert.Empty(T.Compile("rows from T1; rows from T2;").Diagnostics);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void mapNotScalar()
    {
        Has(T.WithRows("const M = { a: 1 }; var x = M;"), ScriptDiagnosticCodes.MapNotScalar);
        Has(T.WithRows("const M = { a: { b: 1 } }; var x = M[\"a\"];"), ScriptDiagnosticCodes.MapNotScalar);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void indexOnNonMap()
    {
        Has(T.WithRows("const k = 1; var x = k[1];"), ScriptDiagnosticCodes.IndexOnNonMap);
        Has(T.WithRows("var v = 1; var x = v.name;"), ScriptDiagnosticCodes.IndexOnNonMap);
        Has(T.WithRows("const M = { a: 1 }; var x = M[\"a\"][\"b\"];"), ScriptDiagnosticCodes.IndexOnNonMap);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void unknownMapKey_лише_для_літеральних_ключів()
    {
        Has(T.WithRows("const M = { a: 1 }; var x = M[\"b\"];"), ScriptDiagnosticCodes.UnknownMapKey);
        Has(T.WithRows("const M = { a: 1 }; var x = M.b;"), ScriptDiagnosticCodes.UnknownMapKey);
        Assert.Empty(T.WithRows("const M = { a: 1 }; var k = \"b\"; var x = M[k];").Diagnostics);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void duplicateMapKey() => Has(T.WithRows("const M = { a: 1, a: 2 };"), ScriptDiagnosticCodes.DuplicateMapKey);

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void visibleNeedsUnit()
    {
        Has(T.WithRows("visible var v = 1;"), ScriptDiagnosticCodes.VisibleNeedsUnit);
        Assert.Empty(T.WithRows("visible var v : g = 1;").Diagnostics);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void unknownImport()
    {
        Has(T.Compile("import Nope; rows from T1;"), ScriptDiagnosticCodes.UnknownImport);
        Has(T.Compile("import Nope; rows from T1;", import: _ => null), ScriptDiagnosticCodes.UnknownImport);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void unknownImportMember()
    {
        var lib = T.Lib("const K = 1;");
        var c = T.Compile("import Common; rows from T1; var a = Common.Nope; var b = Common.nofunc(1);", import: _ => lib);

        Assert.Equal(2, c.Diagnostics.Count(d => d.Code == ScriptDiagnosticCodes.UnknownImportMember));
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void importCycle_є_лише_константою()
        => Assert.Equal("importCycle", ScriptDiagnosticCodes.ImportCycle);

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void tooLarge()
    {
        var c = T.Compile(new string(' ', ScriptCompiler.MaxTextLength + 1));

        Assert.Equal(ScriptDiagnosticCodes.TooLarge, Assert.Single(c.Diagnostics).Code);
        Assert.Null(c.Script);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void tooDeep()
    {
        var c = T.WithRows("var x = " + new string('(', 70) + "1" + new string(')', 70) + ";");

        Has(c, ScriptDiagnosticCodes.TooDeep);
        Assert.Null(c.Script);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Довгий_ланцюг_додавання_не_валить_процес()
    {
        var c = T.WithRows("var x = 1" + string.Concat(Enumerable.Repeat(" + 1", 20000)) + ";");

        Assert.True(c.Script is not null || c.Diagnostics.Any(d => d.Code == ScriptDiagnosticCodes.TooDeep));
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Синтаксична_помилка_не_породжує_семантичних_каскадів()
    {
        var c = T.WithRows("var a = ;\nvar b = a;");

        Assert.Equal(ScriptDiagnosticCodes.UnexpectedToken, Assert.Single(c.Diagnostics).Code);
        Assert.Null(c.Script);
    }

    // ───── §13.7 ─────

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void tableColumnOutsideAggregate()
    {
        Has(T.WithRows("table G = T2; var x = G.Col;"), ScriptDiagnosticCodes.TableColumnOutsideAggregate);
        Has(T.WithRows("table G = T2; var x = G;"), ScriptDiagnosticCodes.TableColumnOutsideAggregate);
        Has(T.WithRows("table G = T2; table H = T3; var x = Sum(G.A where H.B == 1);"), ScriptDiagnosticCodes.TableColumnOutsideAggregate);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void aggregateNeedsTable() => Has(T.WithRows("var x = Sum(row.A);"), ScriptDiagnosticCodes.AggregateNeedsTable);

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void aggregateManyTables()
        => Has(T.WithRows("table G = T2; table H = T3; var x = Sum(G.A + H.B);"), ScriptDiagnosticCodes.AggregateManyTables);

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void nestedAggregateSameTable()
    {
        Has(T.WithRows("table G = T2; var x = Sum(G.A where Count(G where G.B == 1) > 0);"), ScriptDiagnosticCodes.NestedAggregateSameTable);
        Assert.Empty(T.WithRows("table G = T2; table H = T3; var x = Sum(G.A where Count(H where H.B == G.B) > 0);").Diagnostics);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void aggregateNotAllowedHere() => Has(T.WithRows("table G = T2; function f(a) = Sum(G.A);"), ScriptDiagnosticCodes.AggregateNotAllowedHere);

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void unknownTableAlias()
    {
        Has(T.WithRows("var x = Count(Nope);"), ScriptDiagnosticCodes.UnknownTableAlias);
        Has(T.WithRows("var x = Any(1 + 2);"), ScriptDiagnosticCodes.UnknownTableAlias);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void foreachNameNotIdentifier()
        => Has(T.WithRows("foreach S in (\"a b\") { var x_{S} = 1; }"), ScriptDiagnosticCodes.ForeachNameNotIdentifier);

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void foreachTooLarge_за_глибиною_і_за_обсягом()
    {
        var deep = T.WithRows("foreach A in (1) { foreach B in (1) { foreach C in (1) { foreach D in (1) { var v = 1; } } } }");
        var many = T.WithRows("foreach N in (" + string.Join(", ", Enumerable.Range(1, 2001)) + ") { var v{N} = N; }");

        Has(deep, ScriptDiagnosticCodes.ForeachTooLarge);
        Has(many, ScriptDiagnosticCodes.ForeachTooLarge);
        Assert.Empty(T.WithRows("foreach A in (1) { foreach B in (1) { foreach C in (1) { var v = 1; } } }").Diagnostics);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void unitMismatch_є_лише_константою()
        => Assert.Equal("unitMismatch", ScriptDiagnosticCodes.UnitMismatch);
}
