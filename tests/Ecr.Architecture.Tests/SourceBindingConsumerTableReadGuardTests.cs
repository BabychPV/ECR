// tests/Ecr.Architecture.Tests/SourceBindingConsumerTableReadGuardTests.cs
using System.Text.RegularExpressions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// B179 L6 (дизайн §7): ручну таблицю споживачів зв'язки (<c>cfg.DocumentSourceBindingConsumer</c>) не читає жоден
/// продуктовий код — споживачів дає граф залежностей (<c>IDependencyGraphProvider</c>). Таблицю знімає L7 після релізу.
/// </summary>
/// <remarks>
/// <para>
/// Дозволено лише ВИЗНАЧЕННЯ: сутність (<c>Domain/…/DocumentSourceBinding.cs</c>), її конфігурація EF, <c>DbSet</c>
/// контексту й міграції. Сід заводить рядки (<c>.sql</c>, сюди не потрапляє) — для відкату на попередній реліз.
/// </para>
/// <para>
/// Мутація (локально, B179 L6): повернути <c>db.DocumentSourceBindingConsumers</c> у
/// <c>StaleApprovedDependentCheck</c> чи в <c>DocumentSourceMonthGate</c> → червоний.
/// </para>
/// </remarks>
[Trait(TestCategories.Stage, TestCategories.Stage8)]
[Trait(TestCategories.Category, TestCategories.Architecture)]
[Trait(TestCategories.Check, TestCategories.Static)]
[Trait("Directive", "B179")]
public sealed partial class SourceBindingConsumerTableReadGuardTests
{
    private static readonly string[] Definitions =
    [
        "src/Ecr.Domain/Entities/Configuration/DocumentSourceBinding.cs",
        "src/Ecr.Infrastructure/Persistence/Configurations/DocumentSourceBindingConfiguration.cs",
        "src/Ecr.Infrastructure/Persistence/EcrDbContext.cs",
    ];

    [Fact]
    public void Ручної_таблиці_споживачів_зв_язки_не_читає_жоден_продуктовий_код()
    {
        var files = SourceTree.Production().ToList();
        Assert.Contains(files, f => f.Path == Definitions[0]);

        var readers = files
            .Where(f => !Definitions.Contains(f.Path, StringComparer.Ordinal)
                        && !f.Path.Contains("/Migrations/", StringComparison.Ordinal))
            .Where(f => ConsumerTable().IsMatch(WithoutComments(f.Text)))
            .Select(f => f.Path)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            readers.Count == 0,
            "Код читає cfg.DocumentSourceBindingConsumer — споживачів дає IDependencyGraphProvider (B179 L6):"
            + Environment.NewLine + string.Join(Environment.NewLine, readers));
    }

    /// <summary>Сито бачить DbSet, сутність і сирий SQL, але не коментарі.</summary>
    [Theory]
    [InlineData("from c in db.DocumentSourceBindingConsumers.AsNoTracking()", true)]
    [InlineData("var x = new DocumentSourceBindingConsumer(1);", true)]
    [InlineData("\"SELECT TemplateId FROM cfg.DocumentSourceBindingConsumer\"", true)]
    [InlineData("// db.DocumentSourceBindingConsumers — лише визначення", false)]
    [InlineData("/* DocumentSourceBindingConsumer */ var y = 1;", false)]
    [InlineData("var binding = db.DocumentSourceBindings.AsNoTracking();", false)]
    public void Сито_впізнає_читання_таблиці_споживачів(string code, bool expected)
        => Assert.Equal(expected, ConsumerTable().IsMatch(WithoutComments(code)));

    private static string WithoutComments(string text)
        => LineComment().Replace(BlockComment().Replace(text, " "), " ");

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline, matchTimeoutMilliseconds: 2000)]
    private static partial Regex BlockComment();

    [GeneratedRegex(@"//[^\n]*", RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex LineComment();

    [GeneratedRegex(@"\bDocumentSourceBindingConsumers?\b", RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex ConsumerTable();
}
