using System.Reflection;
using Ecr.Application.Ports;
using Ecr.Application.Recalculation;
using Ecr.Application.Templates;
using Ecr.Domain.Enums;
using Ecr.Domain.Entities.Configuration;
using Ecr.Expressions.Ast;
using Ecr.Expressions.Evaluation;
using Ecr.Expressions.Parsing;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Expressions;

/// <summary>
/// L7-01: рекурсивні обходи дерева виразу поза <c>Ecr.Expressions</c> на стеку потоку
/// меншому за 1 МБ (Windows) — найглибше дерево, яке пропускає парсер, проходить, а
/// глибше (побудоване в коді) дає перехоплюваний виняток, а не падіння процесу.
/// </summary>
/// <remarks>
/// ⚠ 768 КБ, а не 1 МБ: у справжньому запиті під обходом лежать кадри ASP.NET,
/// MediatR і EF (або Quartz і черги задач), тож обходу дістається не весь стек
/// потоку. Чверть мегабайта — запас під кадри хоста.
/// <para>
/// ⛔ Обходи кличуться на ОКРЕМОМУ потоці з явним <c>maxStackSize</c>: стек
/// тестового потоку xUnit на Linux — 8 МБ, і на ньому все пройшло б і без межі.
/// </para>
/// </remarks>
[Trait(TestCategories.Stage, TestCategories.Stage2)]
[Trait("Requirement", "L7-01")]
public sealed class ExpressionTraversalStackTests
{
    private const int HostStack = 768 * 1024;

    /// <summary>Обходи продукту, що рекурсують уздовж лівого гребеня ланцюга.</summary>
    public static TheoryData<string> Traversals =>
        ["MentionsCells", "IsRowLocal", "RegistryRuleBind", "RequireSupportedReferences"];

    [Theory]
    [MemberData(nameof(Traversals))]
    public void Ланцюг_на_200_і_1000_ланок_і_на_межі_парсера_проходить_на_стеку_768_КБ(string traversal)
    {
        foreach (var links in new[] { 200, 1000, Parser.MaxChainLinks })
        {
            var expression = "1" + string.Concat(Enumerable.Repeat("+1", links));
            var parsed = new RealFormulaEngine().Parse(expression, ExpressionDialect.Template);
            Assert.True(parsed.IsSuccess, $"{links} ланок: вираз не розібрався.");

            var error = RunOnThread(() => Run(traversal, parsed.Expression!.Root, expression), HostStack);

            Assert.True(error is null, $"{traversal}, {links} ланок: {error}");
        }
    }

    [Theory]
    [MemberData(nameof(Traversals))]
    public void Дерево_на_10000_рівнів_дає_перехоплюваний_виняток_а_не_падіння_процесу(string traversal)
    {
        // Парсер такого дерева не пропускає (MaxChainLinks), але обходи приймають і
        // дерево з кешу чи побудоване в коді. 10 000 рівнів не вміщаються в 768 КБ
        // жодним із обходів: результат — InsufficientExecutionStackException (його
        // ExceptionHandlingMiddleware віддає як 422 ECR-EXPR-0422, а JobRetryPolicy
        // не повторює). Переповнення стека вбило б увесь процес тестів — тобто й тут
        // «зелено» означає, що процес живий.
        var root = Chain(10_000);

        var error = RunOnThread(() => Run(traversal, root, expression: null), HostStack);

        Assert.True(
            error is null or InsufficientExecutionStackException,
            $"{traversal}: очікувався успіх або InsufficientExecutionStackException, а не {error}");
    }

    [Fact]
    public void Процес_живий_після_100000_рівнів_на_стеку_256_КБ_у_кожному_обході()
    {
        // ⛔ Окремий доказ «процес не падає»: найменший стек і найглибше дерево.
        // Без сторожа будь-який із обходів переповнив би стек — а
        // StackOverflowException у .NET не перехоплюється, і тестовий хост помер би
        // разом із цим тестом (червоний прогін, а не зелений).
        var root = Chain(100_000);

        foreach (var traversal in Traversals)
        {
            var error = RunOnThread(() => Run(traversal, root, expression: null), 256 * 1024);

            Assert.IsType<InsufficientExecutionStackException>(error);
        }
    }

    private static void Run(string traversal, AstNode root, string? expression)
    {
        switch (traversal)
        {
            case "MentionsCells":
                RecalculationReadScope.MentionsCells(root);
                break;

            case "IsRowLocal":
                RowLocalFormulaClassifier.IsRowLocal(
                    new FormulaDef(1, FormulaScope.Column, expression ?? "1", ExpressionDialect.Template), root);
                break;

            case "RegistryRuleBind":
                // ⚠ Тип внутрішній (`internal sealed class RegistryRuleContext`), тож рефлексією.
                var bind = typeof(RecalculationReadScope).Assembly
                    .GetType("Ecr.Application.Registries.Rules.RegistryRuleContext", throwOnError: true)!
                    .GetMethod("Bind", BindingFlags.Public | BindingFlags.Static)!;
                try
                {
                    bind.Invoke(null, [root, 1L]);
                }
                catch (TargetInvocationException e) when (e.InnerException is not null)
                {
                    throw e.InnerException;
                }

                break;

            case "RequireSupportedReferences":
                var engine = Substitute.For<IFormulaEngine>();
                engine.Parse(Arg.Any<string>(), ExpressionDialect.Template).Returns(
                    new ParseResult(
                        true,
                        new ParsedExpression(expression ?? "1", ExpressionDialect.Template, root, ExpressionValueType.Number),
                        []));
                RuleExpressionChecks.RequireSupportedReferences(engine, expression ?? "1");
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(traversal), traversal, "Невідомий обхід.");
        }
    }

    /// <summary>Лівий гребінь <c>1+1+…</c> глибиною <paramref name="depth"/>, побудований у коді.</summary>
    private static AstNode Chain(int depth)
    {
        AstNode root = new LiteralNode(1m, ExpressionValueType.Number);
        for (var i = 1; i < depth; i++)
        {
            root = new BinaryNode(BinaryOperator.Add, root, new LiteralNode(1m, ExpressionValueType.Number));
        }

        return root;
    }

    /// <summary>Виконує дію на потоці з заданим стеком; повертає виняток або <c>null</c>.</summary>
    private static Exception? RunOnThread(Action action, int maxStackSize)
    {
        Exception? error = null;
        var thread = new Thread(
            () =>
            {
                try
                {
                    action();
                }
                catch (Exception e)
                {
                    error = e;
                }
            },
            maxStackSize);

        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "Обхід не завершився за 60 с.");

        return error;
    }
}
