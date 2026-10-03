using Ecr.Domain.Enums;
using Ecr.Expressions.Ast;
using Ecr.Expressions.Binding;
using Ecr.Expressions.Parsing;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Expressions.Tests.Binding;

/// <summary>
/// Рекурсивні обходи дерева після розбору не валять процес на дереві будь-якої глибини (L7-01).
/// </summary>
/// <remarks>
/// ⛔ Дефект (аудит 2026-10-03, L7-01 critical): <c>1+1+…+1</c> на ~32 000
/// доданків розбирався ЦИКЛОМ, а обходи дерева — рекурсією без жодної межі.
/// Процес-зонд (потік зі стеком 1 МБ, <c>PublishChecks.CheckExpression</c>
/// без структури) на коді до фіксу: <c>Stack overflow.</c> у
/// <c>PredicateValidator.FindPredicates</c>, код виходу 134 — падав увесь
/// процес. <c>StackOverflowException</c> не перехоплюється, тому на
/// несправленому коді ці тести не червоніють, а ВБИВАЮТЬ хост тестів — і це
/// теж не «зелено».
///
/// ⚠ Дерево будується В КОДІ, повз парсер: парсер тепер сам обмежує ланцюги
/// (<see cref="Parser.MaxChainLinks"/>), і тест через нього перевіряв би
/// парсер, а не сторожа обходів. Сторож мусить тримати будь-яке дерево —
/// з кешу, з побудови в коді, з майбутнього джерела.
///
/// ⚠ Потік із явним стеком 1 МБ (як у пулі потоків Windows), а не головний:
/// на Linux головний потік має 8 МБ, і тест там перевіряв би інше число.
/// </remarks>
public sealed class TraversalStackGuardTests
{
    /// <summary>Глибина — утричі більша за ту, що валила процес (32 000).</summary>
    private const int Depth = 100_000;

    private static AstNode LeftComb(int depth)
    {
        AstNode node = new LiteralNode(1m, ExpressionValueType.Number);
        for (var i = 0; i < depth; i++)
        {
            node = new BinaryNode(BinaryOperator.Add, node, new LiteralNode(1m, ExpressionValueType.Number));
        }

        return node;
    }

    private static T OnSmallStack<T>(Func<T> work)
    {
        T? result = default;
        Exception? failure = null;
        var thread = new Thread(
            () =>
            {
                try
                {
                    result = work();
                }
                catch (InsufficientExecutionStackException ex)
                {
                    // ⚠ Перехоплюється ЛИШЕ керована відмова сторожа: переповнення
                    // стека сюди не дійде — процес завершить CLR.
                    failure = ex;
                }
            },
            maxStackSize: 1024 * 1024);

        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "Обхід не завершився за 60 с.");

        if (failure is not null)
        {
            throw failure;
        }

        return result!;
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void PredicateValidator_обходить_дерево_глибини_сто_тисяч_без_рекурсії()
    {
        var root = LeftComb(Depth);

        var diagnostics = OnSmallStack(() =>
        {
            var found = new List<ExpressionDiagnostic>();
            PredicateValidator.Validate(root, found);
            return found;
        });

        // Предикатів у дереві немає — і обхід дійшов до кінця, а не здався.
        Assert.Empty(diagnostics);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void TypeChecker_на_надглибокому_дереві_дає_зауваження_а_не_падіння()
    {
        var root = LeftComb(Depth);

        var diagnostics = OnSmallStack(() =>
        {
            var found = new List<ExpressionDiagnostic>();
            new TypeChecker().Check(root, new TestBindingContext(), found);
            return found;
        });

        var diagnostic = Assert.Single(diagnostics, d => d.MessageKey == TraversalStackGuard.MessageKey);
        Assert.Equal(ExpressionErrors.Syntax, diagnostic.Code);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void UnitChecker_на_надглибокому_дереві_дає_зауваження_а_не_падіння()
    {
        var root = LeftComb(Depth);

        var diagnostics = OnSmallStack(() =>
        {
            var found = new List<ExpressionDiagnostic>();
            new UnitChecker().Check(root, new TestBindingContext(), found);
            return found;
        });

        Assert.Single(diagnostics, d => d.MessageKey == TraversalStackGuard.MessageKey);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void ReportExpressionChecker_на_надглибокому_дереві_дає_зауваження_а_не_падіння()
    {
        var root = LeftComb(Depth);
        var parsed = new ParsedExpression("1+1", ExpressionDialect.Report, root, ExpressionValueType.Number);

        var diagnostics = OnSmallStack(() =>
        {
            var found = new List<ExpressionDiagnostic>();
            ReportExpressionChecker.Check(parsed, new ReportExpressionScope([], []), null, found);
            return found;
        });

        Assert.Contains(diagnostics, d => d.MessageKey == TraversalStackGuard.MessageKey);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void DependencyExtractor_на_надглибокому_дереві_дає_зауваження_а_не_падіння()
    {
        var builder = new TemplateBuilder();
        var table = builder.Table(builder.Sheet("Water"), "Main");
        builder.Column(table, "Jan");
        builder.Row(table, "7001001", 1);
        var extractor = new DependencyExtractor(new ReferenceResolver(builder.Build()), new RangeExpander());
        var root = LeftComb(Depth);

        var diagnostics = OnSmallStack(() =>
        {
            var found = new List<ExpressionDiagnostic>();
            extractor.Extract(root, table.Id, "7001001", diagnostics: found);
            return found;
        });

        Assert.Single(diagnostics, d => d.MessageKey == TraversalStackGuard.MessageKey);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Обходи_без_переліку_зауважень_відмовляють_керованим_винятком()
    {
        // ⛔ Мовчки обірваний обхід віддав би неповне (вживання аргументів,
        // текст предиката) як повне. Тому без переліку зауважень — виняток,
        // який, на відміну від переповнення стека, перехоплюється.
        var root = LeftComb(Depth);

        Assert.Throws<InsufficientExecutionStackException>(() => OnSmallStack(() => AstPrinter.Print(root)));
        Assert.Throws<InsufficientExecutionStackException>(() => OnSmallStack(() => ArgumentDeclarationChecker.Used(root)));
    }
}
