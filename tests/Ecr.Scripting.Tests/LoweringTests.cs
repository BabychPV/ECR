using Ecr.Domain.Enums;
using Ecr.Expressions.Ast;
using Ecr.Scripting;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Scripting.Tests;

/// <summary>Опускання виразів у AST діалекту методологій (таблиця §3.5) через AstPrinter.</summary>
public sealed class LoweringTests
{
    [Theory] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [InlineData("1 + 2 * 3", "(1 + (2 * 3))")]
    [InlineData("10 % 3 != 1", "((10 % 3) <> 1)")]
    [InlineData("1 - 2 - 3", "((1 - 2) - 3)")]
    [InlineData("-row.A + +row.B", "(-@A + +@B)")]
    [InlineData("!row.A && row.B || row.C", "((NOT @A AND @B) OR @C)")]
    [InlineData("row.A >= 1 && row.A <= 9", "((@A >= 1) AND (@A <= 9))")]
    [InlineData("row.A > 1 ? 2 : 3", "if((@A > 1), 2, 3)")]
    [InlineData("row.A in (1, 2, 3)", "in(@A, 1, 2, 3)")]
    [InlineData("Max(row.A, 2)", "Max(@A, 2)")]
    [InlineData("row.X?", "@X?")]
    [InlineData("key(row.X)", "$key('X')")]
    [InlineData("key(row.X?)", "$key('X')")]
    [InlineData("\"a\" == 'b'", "('a' = 'b')")]
    [InlineData("true && !false", "(TRUE AND NOT FALSE)")]
    [InlineData("null == row.A?", "(NULL = @A?)")]
    [InlineData("1.50 + 2.5e-1", "(1.50 + 0.25)")]
    [InlineData("period.Month", "[Period].Sequence")]
    [InlineData("period.Days + period.Hours + period.Seconds + period.Year", "((([Period].Days + [Period].Hours) + [Period].Seconds) + [Period].Year)")]
    [InlineData("period.Start == period.End", "([Period].Start = [Period].End)")]
    [InlineData("period.EndKey", "$periodEndKey()")]
    [InlineData("header.Name", "HDR.Name")]
    [InlineData("entry(\"REG\", \"CODE\")", "$entry('REG', 'CODE')")]
    public void Вираз_опускається_у_очікуване_AST(string expr, string expected)
    {
        var printed = T.Expr(string.Empty, expr);

        Assert.Equal(expected, printed);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Числовий_літерал_є_decimal_типу_Number()
    {
        var c = T.WithRows("var x = 0.25;");

        var literal = Assert.IsType<LiteralNode>(((VarStatement)c.Script!.Statements[0]).Expr);
        Assert.Equal(ExpressionValueType.Number, literal.Type);
        Assert.IsType<decimal>(literal.Value);
        Assert.Equal(0.25m, literal.Value);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Const_скаляр_var_і_output_читаються_як_Formula()
    {
        var c = T.WithRows("const k = 2; var a = k * 3; output O : t = a + k; var b = O + 1;");

        Assert.Empty(c.Diagnostics);
        var statements = c.Script!.Statements;
        Assert.Equal("(!k * 3)", AstPrinter.Print(((VarStatement)statements[0]).Expr));
        Assert.Equal("(!a + !k)", AstPrinter.Print(((OutputStatement)statements[1]).Expr));
        Assert.Equal("(!O + 1)", AstPrinter.Print(((VarStatement)statements[2]).Expr));
        Assert.All(statements.OfType<VarStatement>().Select(s => s.Expr), e => Assert.NotNull(e));
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Мапа_опускається_у_lookup_у_трьох_формах()
    {
        const string prelude = "const EF = { a: { N: 1, M: 2 }, b: { N: 3, M: 4 } }; var season = \"a\"; var other = \"N\";";

        Assert.Equal("$lookup('EF', !season, 'N')", T.Expr(prelude, "EF[season].N"));
        Assert.Equal("$lookup('EF', !season, !other)", T.Expr(prelude, "EF[season][other]"));
        Assert.Equal("$lookup('EF', !season, !other)", T.Expr(prelude, "EF[season, other]"));
        Assert.Equal("$lookup('EF', 'a', 'M')", T.Expr(prelude, "EF.a.M"));
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Власна_функція_опускається_у_call()
    {
        Assert.Equal("$call:sq(3)", T.Expr("function sq(a) = a * a;", "sq(3)"));

        var c = T.WithRows("function sq(a) = a * a;");
        Assert.Equal("(!a * !a)", AstPrinter.Print(c.Script!.Functions["sq"].Body));
        Assert.Equal(["a"], c.Script.Functions["sq"].Parameters);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Параметр_функції_є_Formula_а_член_бібліотеки_має_префікс()
    {
        var lib = T.Lib("const K = 5; var Base = K + 1; function f(a) = a * K;");
        var c = T.Compile("import Common; rows from T1; var x = Common.K + Common.Base + Common.f(2);", import: _ => lib);

        Assert.Empty(c.Diagnostics);
        Assert.Equal(["Common"], c.Script!.Imports);
        Assert.Equal("((!Common.K + !Common.Base) + $call:Common.f(2))",
            AstPrinter.Print(((VarStatement)c.Script.Statements[0]).Expr));
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Бібліотечна_мапа_індексується_через_lookup_з_префіксом()
    {
        var lib = T.Lib("const M = { a: { n: 1 } };");
        var c = T.Compile("import Lib; rows from T1; var s = \"a\"; var x = Lib.M[s].n;", import: _ => lib);

        Assert.Empty(c.Diagnostics);
        Assert.Equal("$lookup('Lib.M', !s, 'n')", AstPrinter.Print(((VarStatement)c.Script!.Statements[1]).Expr));
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Змінна_речовини_дає_код_а_output_per_лишає_змінну()
    {
        var c = T.WithRows("foreach substance S in (\"0301\", \"0304\") { output Q per S : t = S.code == \"0301\" ? 1 : 2; }");

        Assert.Empty(c.Diagnostics);
        var loop = Assert.IsType<ForeachSubstanceStatement>(Assert.Single(c.Script!.Statements));
        var output = Assert.IsType<OutputStatement>(Assert.Single(loop.Body.Statements));
        Assert.Equal("S", output.SubstanceVar);
        Assert.Equal("if((!S.code = '0301'), 1, 2)", AstPrinter.Print(output.Expr));
        Assert.Equal(["0301", "0304"], loop.Codes);
        Assert.Equal(["0301", "0304"], c.Script.Substances);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void If_збирає_коди_виходів_обох_гілок()
    {
        var c = T.WithRows("if (row.A > 0) { output A1 : t = 1; } else { output A2 : t = 2; }");

        Assert.Empty(c.Diagnostics);
        var ifs = Assert.IsType<IfStatement>(Assert.Single(c.Script!.Statements));
        Assert.Equal(["A1", "A2"], ifs.OutputCodes);
        Assert.NotNull(ifs.Else);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Той_самий_вихід_у_гілках_if_і_else_дозволений_і_рахується_раз()
    {
        var c = T.WithRows("if (row.A > 0) { output Z : t = 1; } else { output Z : t = 2; }");

        Assert.Empty(c.Diagnostics);
        Assert.Single(c.Script!.Outputs);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Reject_skip_і_текст_виразу_зберігаються()
    {
        var c = T.WithRows("reject when row.A < 0;\nvar t = row.A  *  2;");

        var reject = Assert.IsType<RejectStatement>(c.Script!.Statements[0]);
        var v = Assert.IsType<VarStatement>(c.Script.Statements[1]);
        Assert.Equal("row.A < 0", reject.SourceText);
        Assert.Equal("row.A  *  2", v.SourceText);
        Assert.Equal(3, v.Line);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Hash_нормалізує_переводи_рядків_і_не_залежить_від_BOM()
    {
        var a = ScriptCompiler.Hash("a\r\nb\rc");
        var b = ScriptCompiler.Hash("a\nb\nc");
        var other = ScriptCompiler.Hash("a\nb\nd");

        Assert.Equal(32, a.Length);
        Assert.Equal(a, b);
        Assert.NotEqual(a, other);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Функції_Legacy_каталогу_дозволені_у_Legacy()
    {
        var c = T.WithRows("var x = Round(Pow(row.A, 2), 3) + CONTAINS(\"a\", \"b\") ? 1 : 0;", NumericMode.Legacy);

        Assert.Empty(c.Diagnostics);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void StripSpaces_власна_функція_скрипта_один_аргумент()
    {
        var ok = T.WithRows("var x = StripSpaces(key(row.A));");
        Assert.Empty(ok.Diagnostics);

        var bad = T.WithRows("var x = StripSpaces(key(row.A), \"b\");");
        Assert.Contains(bad.Diagnostics, d => d.Code == ScriptDiagnosticCodes.ArgumentCount);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Довідникові_функції_збирають_коди_довідників()
    {
        var c = T.WithRows("var x = REGFIND(\"COMP\", \"k\", row.A) + REGSUM(\"OTHER\", \"k\", \"v\");");

        Assert.Empty(c.Diagnostics);
        Assert.Equal(["COMP", "OTHER"], c.Script!.RegistryCodes);
        Assert.True(c.Script.NeedsRegistries);
    }
}
