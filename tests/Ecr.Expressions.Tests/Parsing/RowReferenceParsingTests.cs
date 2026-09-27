// tests/Ecr.Expressions.Tests/Parsing/RowReferenceParsingTests.cs
using System.Collections.ObjectModel;
using Ecr.Domain.Enums;
using Ecr.Expressions.Ast;
using Ecr.Expressions.Evaluation;
using Ecr.Expressions.Parsing;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Expressions.Tests.Parsing;

/// <summary>
/// <c>ROW.</c> і <c>THIS</c> — поле рядка довідника й запис, що перевіряє
/// правило (FEATURE-REGISTRY-TABLES §5.2, крок RT-07).
/// </summary>
/// <remarks>
/// Тут перевіряється лише мова: дерево, друк, область і діагностики з позицією.
/// Обчислення функцій довідників — <c>RegistryLookupFunctionTests</c> (RT-20a) і
/// <c>RegistryAggregateFunctionTests</c> (RT-20b).
/// </remarks>
public sealed class RowReferenceParsingTests
{
    /// <remarks>
    /// Мутація: прибрати гілку <c>ROW</c> у <c>Parser.ParseIdentifier</c> — тест
    /// червоний («невідомий ідентифікатор ROW»).
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void ROW_COMPONENT_MW_розбирається_у_шлях()
    {
        var result = Rule("ROW.COMPONENT.MW");

        Assert.True(result.IsSuccess, Reasons(result));
        var row = Assert.IsType<RowFieldNode>(result.Expression!.Root);
        Assert.Equal(["COMPONENT", "MW"], row.Path);
        Assert.Equal(0, row.Position);

        // Префікс — без урахування регістру, як `CST.`/`HDR.`; коди — як написано.
        var lower = Assert.IsType<RowFieldNode>(Rule("row.Mol_Pct").Expression!.Root);
        Assert.Equal(["Mol_Pct"], lower.Path);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [InlineData("ROW.COMPONENT.MW")]
    [InlineData("THIS")]
    [InlineData("(ROW.CASE = THIS)")]
    [InlineData("(ROW.MOL_PCT * ROW.COMPONENT.MW)")]
    [InlineData("(ABS((ROW.MOL_PCT - 100)) <= 0.5)")]
    [InlineData("(REGFIELD(THIS, 'NUMBER') = ROW.NUMBER)")]
    public void Друк_дерева_повертає_той_самий_текст(string text)
    {
        var result = Rule(text);
        Assert.True(result.IsSuccess, Reasons(result));

        Assert.Equal(text, AstPrinter.Print(result.Expression!.Root));
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [InlineData("[A] + THIS", ExpressionDialect.Template, 6)]
    [InlineData("@Stream = THIS", ExpressionDialect.Methodology, 10)]
    [InlineData("this", ExpressionDialect.Template, 0)]
    public void THIS_поза_правилом_довідника_діагностика_з_позицією(
        string text, ExpressionDialect dialect, int position)
    {
        var result = new Parser().Parse(text, dialect);

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("expr.thisOutsideRule", diagnostic.MessageKey);
        Assert.Equal(ExpressionErrors.Unresolved, diagnostic.Code);
        Assert.Equal(position, diagnostic.Position);
        Assert.Equal(4, diagnostic.Length);
        AssertEnglish(diagnostic);

        // ⚠ Розбір не обривається: редактор отримує дерево й підсвічує саме THIS.
        Assert.False(result.IsSuccess);
        Assert.NotNull(result.Expression);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void ROW_поза_агрегатом_і_правилом_діагностика_з_позицією()
    {
        var result = Expr.Parse("[A] * ROW.COMPONENT.MW");

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("expr.rowReferenceOutsideScope", diagnostic.MessageKey);
        Assert.Equal(ExpressionErrors.Unresolved, diagnostic.Code);
        Assert.Equal("ROW.COMPONENT.MW", diagnostic.MessageParams!["construct"]);
        Assert.Equal(6, diagnostic.Position);
        Assert.Equal("ROW.COMPONENT.MW".Length, diagnostic.Length);
        AssertEnglish(diagnostic);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Аргументи_агрегату_з_другого_це_область_рядка()
    {
        // `ROW.` в умові й виразі агрегату стоїть у своїй області — жодної
        // відмови (з RT-20b `REGSUM` — відома функція).
        var inside = Expr.Parse("REGSUM('GAS_COMPOSITION', ROW.CASE = 1, ROW.MOL_PCT * ROW.COMPONENT.MW)");
        Assert.Empty(inside.Diagnostics);

        // Перший аргумент — код довідника, і він ПОЗА областю; після дужки — теж.
        var code = Expr.Parse("REGCOUNT(ROW.X, TRUE)");
        Assert.Contains(code.Diagnostics, d => d.MessageKey == "expr.rowReferenceOutsideScope");

        var after = Expr.Parse("REGCOUNT('R', TRUE) + ROW.X");
        var outside = Assert.Single(after.Diagnostics, d => d.MessageKey == "expr.rowReferenceOutsideScope");
        Assert.Equal(22, outside.Position);

        // REGFIND не відкриває області: його частини — значення зовнішнього виразу.
        var find = Expr.Parse("REGFIND('COMPONENT', ROW.X)");
        Assert.Contains(find.Diagnostics, d => d.MessageKey == "expr.rowReferenceOutsideScope");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void У_діалекті_звітів_ROW_і_THIS_заборонені_як_будь_яке_чуже_посилання()
    {
        foreach (var text in new[] { "ROW.X", "THIS" })
        {
            var result = Expr.Parse(text, ExpressionDialect.Report);

            var diagnostic = Assert.Single(result.Diagnostics);
            Assert.Equal("expr.referenceForbiddenInReport", diagnostic.MessageKey);
        }
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [InlineData("CST.ROW + HDR.THIS", ExpressionDialect.Methodology)]
    [InlineData("@This + !Row", ExpressionDialect.Methodology)]
    public void Коди_ROW_і_THIS_після_наявних_префіксів_лишаються_символами(string text, ExpressionDialect dialect)
    {
        var result = Expr.Parse(text, dialect);
        Assert.True(result.IsSuccess, Reasons(result));

        var sum = Assert.IsType<BinaryNode>(result.Expression!.Root);
        Assert.IsType<SymbolReferenceNode>(sum.Left);
        Assert.IsType<SymbolReferenceNode>(sum.Right);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Голе_This_в_імпорті_лишається_параметром_чинної_системи()
    {
        var result = Expr.Parse("This * 2", ExpressionDialect.Methodology, ExpressionParseMode.Import);

        Assert.True(result.IsSuccess, Reasons(result));
        var product = Assert.IsType<BinaryNode>(result.Expression!.Root);
        var symbol = Assert.IsType<SymbolReferenceNode>(product.Left);
        Assert.Equal((SymbolKind.Argument, "This"), (symbol.Kind, symbol.Name));
    }

    /// <remarks>
    /// Обов'язкова умова CAL-05: вузол із <c>ROW.</c> іде в кеш розбору, тож
    /// мусить бути незмінним; господар виразу — частина ключа кеша.
    /// Мутація: прибрати <c>Host</c> з <c>Parser.CacheKey</c> — тест червоний.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Вираз_із_ROW_кешується_а_дерево_незмінне()
    {
        var parser = new Parser();
        const string Text = "ABS(REGFIELD(THIS, 'SUM') - ROW.MOL_PCT) <= 0.5";

        var first = parser.Parse(Text, ExpressionDialect.Template, ExpressionParseMode.Editor, ExpressionHost.RegistryRule);
        var second = parser.Parse(Text, ExpressionDialect.Template, ExpressionParseMode.Editor, ExpressionHost.RegistryRule);

        Assert.True(first.IsSuccess, Reasons(first));
        Assert.Same(first, second);

        // Еквівалентність зі свіжим розбором — той самий текст дерева.
        var fresh = Rule(Text);
        Assert.NotSame(first, fresh);
        Assert.Equal(AstPrinter.Print(fresh.Expression!.Root), AstPrinter.Print(first.Expression!.Root));

        var row = Assert.IsType<RowFieldNode>(
            Assert.IsType<BinaryNode>(
                Assert.IsType<FunctionNode>(Assert.IsType<BinaryNode>(first.Expression!.Root).Left).Arguments[0]).Right);

        Assert.IsType<ReadOnlyCollection<string>>(row.Path);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)row.Path).Add("X"));

        // Вузол копіює шлях: зміна списку, з якого його створено, його не торкається.
        var source = new List<string> { "A" };
        var node = new RowFieldNode(source);
        source.Add("B");
        Assert.Equal(["A"], node.Path);

        // ⛔ Господар — частина ключа кеша, в обох порядках.
        Assert.False(parser.Parse("THIS", ExpressionDialect.Template).IsSuccess);
        Assert.True(parser.Parse("THIS", ExpressionDialect.Template, host: ExpressionHost.RegistryRule).IsSuccess);

        var other = new Parser();
        Assert.True(other.Parse("THIS", ExpressionDialect.Template, host: ExpressionHost.RegistryRule).IsSuccess);
        Assert.False(other.Parse("THIS", ExpressionDialect.Template).IsSuccess);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Правило_довідника_розбирається_лише_діалектом_шаблонів()
    {
        var parser = new Parser();

        Assert.Throws<ArgumentException>(
            () => parser.Parse("THIS", ExpressionDialect.Methodology, host: ExpressionHost.RegistryRule));
        Assert.Throws<ArgumentException>(
            () => parser.Parse("THIS", ExpressionDialect.Report, host: ExpressionHost.RegistryRule));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Контекст_без_знімка_довідників_не_ламається_і_віддає_null()
    {
        // Член інтерфейсу за замовчуванням: наявні контексти не змінювались.
        IEvaluationContext context = new TestEvaluationContext();

        Assert.Null(context.Registries);
        Assert.NotEqual(ExpressionErrors.NotAvailable, ExpressionErrors.MultipleMatches);
    }

    private static ParseResult Rule(string text)
        => new Parser().Parse(text, ExpressionDialect.Template, ExpressionParseMode.Editor, ExpressionHost.RegistryRule);

    private static string Reasons(ParseResult result)
        => string.Join("; ", result.Diagnostics.Select(d => $"{d.MessageKey} @{d.Position}: {d.Message}"));

    private static void AssertEnglish(ExpressionDiagnostic diagnostic)
        => Assert.False(
            diagnostic.Message.Any(c => c is >= 'Ѐ' and <= 'ӿ'),
            $"Запасний текст діагностики українською: {diagnostic.Message}");
}
