using Ecr.Domain.Enums;
using Ecr.Expressions;
using Ecr.Expressions.Ast;
using Ecr.Expressions.Parsing;
using Ecr.MethodologyScriptMigration.Conversion;
using Ecr.Scripting;
using Xunit;

namespace Ecr.MethodologyScriptMigration.Tests;

public sealed class ExpressionTranslationTests
{
    private static ExpressionTranslator Translator(bool key = false)
        => new(code => code, name => name, key);

    /// <summary>Переклад без констант: текст і збіг дерева зі старим розбором.</summary>
    [Theory]
    [InlineData("@A + @B * 2", "row.A + row.B * 2")]
    [InlineData("(@A + @B) * 2", "(row.A + row.B) * 2")]
    [InlineData("@A - (@B - @C)", "row.A - (row.B - row.C)")]
    [InlineData("@A - @B - @C", "row.A - row.B - row.C")]
    [InlineData("@A / (@B * @C)", "row.A / (row.B * row.C)")]
    [InlineData("@A * @B / @C", "row.A * row.B / row.C")]
    [InlineData("@A % 3 + 1", "row.A % 3 + 1")]
    [InlineData("-@A", "-row.A")]
    [InlineData("-(@A + @B)", "-(row.A + row.B)")]
    [InlineData("-(-@A)", "-(-row.A)")]
    [InlineData("2 * -@A", "2 * -row.A")]
    [InlineData("not (@A = 1)", "!(row.A == 1)")]
    [InlineData("@A = 1 and @B <> 2", "row.A == 1 && row.B != 2")]
    [InlineData("@A = 1 or @B = 2 and @C = 3", "row.A == 1 || row.B == 2 && row.C == 3")]
    [InlineData("(@A = 1 or @B = 2) and @C = 3", "(row.A == 1 || row.B == 2) && row.C == 3")]
    [InlineData("(@A < @B) = (@C < @D)", "row.A < row.B == row.C < row.D")]
    [InlineData("@A = (@B = @C)", "row.A == (row.B == row.C)")]
    [InlineData("@A >= 1 and @A <= 9", "row.A >= 1 && row.A <= 9")]
    [InlineData("if(@A > 1, 2, 3)", "row.A > 1 ? 2 : 3")]
    [InlineData("if (@A = 0 and @B = 0, 0, @C * 2)", "row.A == 0 && row.B == 0 ? 0 : row.C * 2")]
    [InlineData("if(@A > 1, 1, if(@A > 0, 2, 3))", "row.A > 1 ? 1 : row.A > 0 ? 2 : 3")]
    [InlineData("if(@A > 1, if(@B > 1, 1, 2), 3)", "row.A > 1 ? (row.B > 1 ? 1 : 2) : 3")]
    [InlineData("if(if(@A > 1, 1, 0) = 1, 5, 6)", "(row.A > 1 ? 1 : 0) == 1 ? 5 : 6")]
    [InlineData("1 + if(@A > 1, 2, 3)", "1 + (row.A > 1 ? 2 : 3)")]
    [InlineData("if(@A > 1, 2, 3) * 4", "(row.A > 1 ? 2 : 3) * 4")]
    [InlineData("@A? + 1", "row.A? + 1")]
    [InlineData("if(@A? > 0, @A?, 0)", "row.A? > 0 ? row.A? : 0")]
    [InlineData("in(@A, 1, 2, 3)", "row.A in (1, 2, 3)")]
    [InlineData("in(@A + 1, 1, 2)", "row.A + 1 in (1, 2)")]
    [InlineData("in(@A, 'x', 'y') and @B = 1", "row.A in (\"x\", \"y\") && row.B == 1")]
    [InlineData("not in(@A, 1, 2)", "!(row.A in (1, 2))")]
    [InlineData("[Period].Days * @A", "period.Days * row.A")]
    [InlineData("[Period].Sequence >= 4 and [Period].Sequence <= 9", "period.Month >= 4 && period.Month <= 9")]
    [InlineData("[Period].Year + [Period].Hours", "period.Year + period.Hours")]
    [InlineData("HDR.Name = 'x'", "header.Name == \"x\"")]
    [InlineData("Max(@A, Min(@B, 2))", "Max(row.A, Min(row.B, 2))")]
    [InlineData("Round(@A * 2, 3)", "Round(row.A * 2, 3)")]
    [InlineData("!F1 + !F2 * @A", "F1 + F2 * row.A")]
    [InlineData("'It''s'", "\"It's\"")]
    [InlineData("@A = 'say \"hi\"'", "row.A == \"say \\\"hi\\\"\"")]
    [InlineData("@A = 0.0000417600", "row.A == 0.0000417600")]
    [InlineData("@A * 123456789.123456789", "row.A * 123456789.123456789")]
    [InlineData("@A = NULL", "row.A == null")]
    [InlineData("if(TRUE, 1, 0)", "true ? 1 : 0")]
    public void Вираз_перекладається_і_дерево_збігається(string old, string expected)
    {
        var translated = Translator().Translate(old);
        Assert.Equal(expected, translated.Text);

        // Збіг дерева: друк AST скомпільованого скрипта дорівнює друку AST старого розбору.
        var oldTree = new Parser().Parse(old, ExpressionDialect.Methodology).Expression!.Root;
        var compiled = ScriptCompiler.Compile(
            "rows from T1;\nvar F1 = 1;\nvar F2 = 2;\nvar x = " + translated.Text + ";\n",
            new ScriptCompileOptions(NumericMode.Legacy, false));
        Assert.True(compiled.Diagnostics.Count == 0, string.Join("; ", compiled.Diagnostics.Select(d => d.Code + ": " + d.Message)));
        var newTree = compiled.Script!.Statements.OfType<VarStatement>().Last().Expr;
        Assert.Equal(AstPrinter.Print(oldTree), AstPrinter.Print(newTree));
    }

    [Fact]
    public void Посилання_на_формули_збираються_у_порядку_появи()
    {
        var t = Translator().Translate("!B + !A * !B");
        Assert.Equal(["B", "A", "B"], t.Formulas);
    }

    [Fact]
    public void Константа_і_формула_підставляються_делегатами()
    {
        var translator = new ExpressionTranslator(c => c + "[Category]", f => "Common." + f);
        var t = translator.Translate("CST.EF * !F + 1");
        Assert.Equal("EF[Category] * Common.F + 1", t.Text);
    }

    [Fact]
    public void Правило_категорії_читає_аргументи_ключем()
    {
        var t = Translator(key: true).Translate("if(@A = 'x', 'k1', if(@B? = 'y', 'k2', ''))");
        Assert.Equal("key(row.A) == \"x\" ? \"k1\" : key(row.B?) == \"y\" ? \"k2\" : \"\"", t.Text);
    }

    [Fact]
    public void Голі_імена_параметрів_читаються_в_режимі_імпорту()
    {
        var t = Translator().Translate("Duration * 2");
        Assert.Equal("row.Duration * 2", t.Text);
        Assert.True(t.ParsedInImportMode);
        Assert.False(Translator().Translate("@Duration * 2").ParsedInImportMode);
    }

    [Theory]
    [InlineData("2 ^ 3")]
    [InlineData("@A & @B")]
    [InlineData("[Period:-1].Days")]
    [InlineData("Max(@A,")]
    [InlineData("@A +")]
    public void Непереносимий_вираз_кидає_ConversionException(string old)
        => Assert.Throws<ConversionException>(() => Translator().Translate(old));

    [Fact]
    public void Хибна_арність_if_і_in_відхиляється()
    {
        Assert.Throws<ConversionException>(() => Translator().Translate("if(@A, 1)"));
        Assert.Throws<ConversionException>(() => Translator().Translate("in(@A)"));
    }

    [Fact]
    public void Аргумент_з_ключовим_словом_не_переноситься()
    {
        Assert.Throws<ConversionException>(() => Translator().Translate("@key + 1"));
    }
}
