// tests/Ecr.Expressions.Tests/DiagnosticLocalizationTests.cs
using Ecr.Domain.Enums;
using Ecr.Expressions.Binding;
using Ecr.Expressions.Graph;
using Ecr.Expressions.Parsing;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Expressions.Tests;

/// <summary>
/// Діагностики редактора виразів несуть ключ каталогу і англійський запасний
/// текст, а не українське речення (V-20, третій раунд UX, 2026-09-24).
/// </summary>
/// <remarks>
/// ⛔ Відтворено на стенді: діалект «Methodology formula»,
/// <c>IF(1 &gt; 0, 'a', 'b')</c> і <c>ROUND(1.2345)</c> давали «ECR-TMPL-0422
/// Функція 'IF' недоступна в діалекті Methodology. У наборі чинного рушія
/// (NCalc 1.3.8) регістр значущий…» — без ключа, за будь-якої мови.
/// Мутація: повернути в <c>Parser.ReportUnknownFunction</c> гілку з
/// українським реченням без ключа — перші три рядки червоні.
/// </remarks>
public sealed class DiagnosticLocalizationTests
{
    private static readonly TypeChecker Types = new();
    private static readonly UnitChecker Units = new();

    [Theory]
    [InlineData("IF(1 > 0, 'a', 'b')", "expr.unknownFunctionCase", "exact", "if")]
    [InlineData("ROUND(1.2345)", "expr.unknownFunctionCase", "exact", "Round")]
    [InlineData("POWER(2, 3)", "expr.unknownFunctionReplacement.POWER", "name", "POWER")]
    [InlineData("SWITCH(1, 2)", "expr.unknownFunctionReplacement.SWITCH", "dialect", "Methodology")]
    [InlineData("FOO(1)", "expr.unknownFunction", "name", "FOO")]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Невідома_функція_діалекту_методологій_несе_ключ_і_англійський_текст(
        string expression, string key, string param, string value)
    {
        var parsed = Expr.Parse(expression, ExpressionDialect.Methodology);

        var diagnostic = Assert.Single(parsed.Diagnostics);
        Assert.Equal(key, diagnostic.MessageKey);
        Assert.Equal(value, diagnostic.MessageParams![param]);
        AssertEnglish(diagnostic);
    }

    [Theory]
    [InlineData("-(1 > 0)", "expr.type.signNeedsNumber")]
    [InlineData("1 + 'a'", "expr.type.arithmeticNeedsNumber")]
    [InlineData("1 > 'a'", "expr.type.incomparable")]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Діагностика_типів_несе_ключ_і_англійський_текст(string expression, string key)
    {
        var parsed = Expr.Parse(expression);
        Assert.True(parsed.IsSuccess);

        var diagnostics = new List<ExpressionDiagnostic>();
        Types.Check(parsed.Expression!.Root, new TestBindingContext(), diagnostics);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(key, diagnostic.MessageKey);
        AssertEnglish(diagnostic);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Діагностика_одиниць_несе_ключ_і_англійський_текст()
    {
        var context = new TestBindingContext();
        context.ColumnUnits["A"] = 1;
        context.ColumnUnits["B"] = 2;
        context.Dimensions[1] = 1;
        context.Dimensions[2] = 1;

        var parsed = Expr.Parse("[A] + [B]");
        var diagnostics = new List<ExpressionDiagnostic>();
        Units.Check(parsed.Expression!.Root, context, diagnostics);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("expr.unit.addNeedsConvert", diagnostic.MessageKey);
        AssertEnglish(diagnostic);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Цикл_формул_несе_ключ_і_шлях_параметром()
    {
        var diagnostic = CycleDescription.Diagnostic([1, 2, 1], id => id == 1 ? "F_A" : "F_B");

        Assert.Equal("expr.cycle", diagnostic.MessageKey);
        Assert.Equal("F_A → F_B → F_A", diagnostic.MessageParams!["path"]);
        AssertEnglish(diagnostic);

        var self = CycleDescription.Diagnostic([7, 7], _ => "Total");
        Assert.Equal("expr.cycleSelf", self.MessageKey);
        AssertEnglish(self);
    }

    private static void AssertEnglish(ExpressionDiagnostic diagnostic)
        => Assert.False(
            diagnostic.Message.Any(c => c is >= 'Ѐ' and <= 'ӿ'),
            $"Запасний текст діагностики українською: {diagnostic.Message}");
}
