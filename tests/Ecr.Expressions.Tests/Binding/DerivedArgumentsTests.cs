// tests/Ecr.Expressions.Tests/Binding/DerivedArgumentsTests.cs
using Ecr.Domain.Enums;
using Ecr.Expressions.Ast;
using Ecr.Expressions.Binding;
using Ecr.Expressions.Lexing;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Expressions.Tests.Binding;

/// <summary>
/// Похідні аргументи (2026-11-02): маркер «необов'язковий» живе у виразі (<c>@Name?</c>), а список
/// <c>ArgumentsCsv</c> виводиться з тексту.
/// </summary>
/// <remarks>
/// ⛔ Мутації (лише цей клас): у лексері прибрати умову <c>syntax.OptionalArgumentMarker</c> — червоніє
/// <see cref="У_шаблонах_питання_за_аргументом_лишається_тернарним"/>; у <c>ApplyOptionalMarkers</c> брати позицію з
/// тексту <c>IndexOf("@A")</c> замість лексем — червоніє <see cref="Маркер_ставиться_лише_на_ціле_імя_і_не_в_рядку"/>.
/// </remarks>
public sealed class DerivedArgumentsTests
{
    private static readonly Lexer Methodology = new(DialectSyntax.Of(ExpressionDialect.Methodology));
    private static readonly Lexer Template = new(DialectSyntax.Of(ExpressionDialect.Template));

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Питання_впритул_за_аргументом_маркер_а_з_пробілом_тернарний_оператор()
    {
        var marked = Methodology.Tokenize("@Land_Measure_RunningHours? * 2");
        Assert.Equal(
            [TokenType.At, TokenType.Identifier, TokenType.OptionalMarker, TokenType.Star, TokenType.Number, TokenType.EndOfInput],
            marked.Select(t => t.Type));

        var ternary = Methodology.Tokenize("@A ? 1 : 2");
        Assert.Contains(ternary, t => t.Type == TokenType.Question);
        Assert.DoesNotContain(ternary, t => t.Type == TokenType.OptionalMarker);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void У_шаблонах_питання_за_аргументом_лишається_тернарним()
    {
        // Маркер — властивість ЛИШЕ діалекту методологій.
        var tokens = Template.Tokenize("@A?1:2");

        Assert.Contains(tokens, t => t.Type == TokenType.Question);
        Assert.DoesNotContain(tokens, t => t.Type == TokenType.OptionalMarker);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Парсер_позначає_аргумент_необовязковим_і_друкує_маркер_назад()
    {
        var parsed = Expr.Parse("@Land.Measure.RunningHours? + @Other", ExpressionDialect.Methodology);
        Assert.True(parsed.IsSuccess, string.Join("; ", parsed.Diagnostics.Select(d => d.Message)));

        var symbols = Nodes(parsed.Expression!.Root).OfType<SymbolReferenceNode>().ToList();
        Assert.Contains(symbols, s => s is { Name: "Land.Measure.RunningHours", IsOptional: true });
        Assert.Contains(symbols, s => s is { Name: "Other", IsOptional: false });

        Assert.Equal("(@Land.Measure.RunningHours? + @Other)", AstPrinter.Print(parsed.Expression.Root));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Тернарний_над_необовязковим_аргументом_розбирається()
    {
        var parsed = Expr.Parse("@A? ? 1 : 2", ExpressionDialect.Methodology);

        Assert.True(parsed.IsSuccess, string.Join("; ", parsed.Diagnostics.Select(d => d.Message)));
        Assert.IsType<ConditionalNode>(parsed.Expression!.Root);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Похідний_список_у_порядку_першої_появи_з_константами_і_формулами()
    {
        var derived = ArgumentDeclarationChecker.Derive(
            "if(@Power < CST.k1_, !ECW_Location, @Rotation? * CST.k1_ + @power)");

        Assert.Equal(["@Power", "CST.k1_", "!ECW_Location", "@Rotation?"], derived.Select(a => a.Text));
        Assert.Equal("@Power;CST.k1_;!ECW_Location;@Rotation?", ArgumentDeclarationChecker.Format(derived));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Те_саме_імя_з_маркером_і_без_один_необовязковий_елемент()
    {
        var derived = ArgumentDeclarationChecker.Derive("@Jan + @Jan? + @jan");

        var single = Assert.Single(derived);
        Assert.True(single.IsOptional);
        Assert.Equal("@Jan?", single.Text);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Рядковий_літерал_і_виклик_функції_не_є_аргументами()
    {
        // `!IsNull(...)` — заперечення виклику, а не посилання на формулу; `'@X'` — текст.
        var derived = ArgumentDeclarationChecker.Derive("if(!IsNull(@A), '@X CST.Y', 0)");

        Assert.Equal(["@A"], derived.Select(a => a.Text));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Недописаний_вираз_чернетки_дає_список_до_помилки()
    {
        // Чернетка зберігає недописане; список похідний і від нього, без винятку.
        var derived = ArgumentDeclarationChecker.Derive("@A + @B? + 'незакритий");

        Assert.Equal(["@A", "@B?"], derived.Select(a => a.Text));
        Assert.Empty(ArgumentDeclarationChecker.Derive(null));
        Assert.Equal(string.Empty, ArgumentDeclarationChecker.DeriveCsv("1 + 2"));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Маркер_ставиться_лише_на_ціле_імя_і_не_в_рядку()
    {
        var optional = new HashSet<string>(["A", "Land_Measure_RunningHours"], StringComparer.OrdinalIgnoreCase);

        var result = ArgumentDeclarationChecker.ApplyOptionalMarkers(
            "@A + @AB + '@A' + @a? + @Land.Measure.RunningHours", optional);

        Assert.Equal("@A? + @AB + '@A' + @a? + @Land.Measure.RunningHours?", result);

        // Ідемпотентно: повторний прохід нічого не додає.
        Assert.Equal(result, ArgumentDeclarationChecker.ApplyOptionalMarkers(result, optional));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Старий_список_розбирається_на_види_і_маркери()
    {
        var items = ArgumentDeclarationChecker.FromCsv("@Land_Power;\nCST.k1_; !ECW_Category;Duration?;");

        Assert.Equal(
            [
                (DeclaredArgumentKind.Argument, "Land_Power", false),
                (DeclaredArgumentKind.Constant, "k1_", false),
                (DeclaredArgumentKind.Formula, "ECW_Category", false),
                (DeclaredArgumentKind.Argument, "Duration", true),
            ],
            items.Select(i => (i.Kind, i.Name, i.IsOptional)));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Похідний_список_ніколи_не_дає_ні_0432_ні_невжитого()
    {
        // Звірка пастки 2 над похідним списком — твердження: лексеми й дерево не розходяться.
        const string text = "if(@A? > CST.K, !F + @B.C, @D) + @A";
        var parsed = Expr.Parse(text, ExpressionDialect.Methodology);
        Assert.True(parsed.IsSuccess, string.Join("; ", parsed.Diagnostics.Select(d => d.Message)));

        var audit = ArgumentDeclarationChecker.Audit(
            parsed.Expression!.Root,
            ArgumentDeclarationChecker.ParseDeclaration(ArgumentDeclarationChecker.DeriveCsv(text)));

        Assert.Empty(audit.Undeclared);
        Assert.Empty(audit.UnusedDeclared);
        Assert.Equal(["A"], ArgumentDeclarationChecker.OptionalNamesFromCsv(ArgumentDeclarationChecker.DeriveCsv(text)));
    }

    private static IEnumerable<AstNode> Nodes(AstNode root)
    {
        var pending = new Stack<AstNode>([root]);
        while (pending.Count > 0)
        {
            var node = pending.Pop();
            yield return node;
            switch (node)
            {
                case BinaryNode b:
                    pending.Push(b.Left);
                    pending.Push(b.Right);
                    break;
                case UnaryNode u:
                    pending.Push(u.Operand);
                    break;
                case ConditionalNode c:
                    pending.Push(c.Condition);
                    pending.Push(c.WhenTrue);
                    pending.Push(c.WhenFalse);
                    break;
                case FunctionNode f:
                    foreach (var a in f.Arguments)
                    {
                        pending.Push(a);
                    }

                    break;
            }
        }
    }
}
