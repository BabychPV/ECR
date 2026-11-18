// tests/Ecr.Architecture.Tests/DependencyGraphMethodologyReadGuardTests.cs
using System.Text.RegularExpressions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// B179 L1 (дизайн §8): у функції графа залежностей (<c>src/*/DocumentDependencies/**</c>) таблиці методологій читає
/// ЛИШЕ адаптер <c>MethodologyRegistryEdges</c> (<c>IMethodologyRegistryEdges</c>), і жоден файл графа не читає
/// <c>cfg.DocumentSourceBindingConsumer</c> (дизайн §7: ручний перелік споживачів виводиться з ужитку).
/// </summary>
/// <remarks>
/// ⛔ Модель методологій переосмислюється окремою роботою (рішення людини 18.11); якщо граф почне читати
/// <c>calc.MethodologyVersion</c>/<c>cfg.CalculationBinding</c>/<c>cfg.RegistryUse</c> вид 1 в обхід адаптера, заміна
/// адаптера перестане бути заміною моделі. Коментарі знімаються до пошуку.
/// Мутація (локально, B179 L1): додати <c>db.MethodologyVersions.Count()</c> у <c>DependencyGraphProvider.cs</c> → червоний.
/// </remarks>
[Trait(TestCategories.Stage, TestCategories.Stage8)]
[Trait(TestCategories.Category, TestCategories.Architecture)]
[Trait(TestCategories.Check, TestCategories.Static)]
[Trait("Directive", "B179")]
public sealed partial class DependencyGraphMethodologyReadGuardTests
{
    private const string Adapter = "src/Ecr.Infrastructure/DocumentDependencies/MethodologyRegistryEdges.cs";

    [Fact]
    public void Таблиці_методологій_у_графі_читає_лише_адаптер()
    {
        var feature = Feature();
        Assert.Contains(Adapter, feature.Keys);
        Assert.Contains("src/Ecr.Infrastructure/DocumentDependencies/DependencyGraphProvider.cs", feature.Keys);

        // Не порожня перевірка: адаптер справді читає те, що заборонено решті.
        Assert.True(MethodologyRead().Count(feature[Adapter]) >= 3, "Адаптер більше не читає таблиць методологій — перевір сторожа.");

        var bypass = feature
            .Where(f => f.Key != Adapter && MethodologyRead().IsMatch(f.Value))
            .Select(f => $"{f.Key}: {string.Join(", ", MethodologyRead().Matches(f.Value).Select(m => m.Value.Trim()).Distinct())}")
            .ToList();
        Assert.True(
            bypass.Count == 0,
            "Граф залежностей читає таблиці методологій в обхід IMethodologyRegistryEdges — перенеси читання в адаптер:"
            + Environment.NewLine + string.Join(Environment.NewLine, bypass));
    }

    [Fact]
    public void Граф_не_читає_ручного_переліку_споживачів_зв_язки()
    {
        var readers = Feature().Where(f => ConsumerList().IsMatch(f.Value)).Select(f => f.Key).ToList();
        Assert.True(readers.Count == 0, "Граф читає cfg.DocumentSourceBindingConsumer (дизайн B179 §7):" + Environment.NewLine + string.Join(Environment.NewLine, readers));
    }

    /// <summary>Сито бачить EF-ланцюг, константу виду 1 і сирий SQL, але не коментарі й імена портів.</summary>
    [Theory]
    [InlineData("db.MethodologyVersions.AsNoTracking()", true)]
    [InlineData("from b in db.CalculationBindings", true)]
    [InlineData("db.Methodologies\n  .Where(m => m.IsActive)", true)]
    [InlineData("u.SourceKind == RegistryUse.MethodologyVersionSource", true)]
    [InlineData("where use.SourceKind == 1", true)]
    [InlineData("\"SELECT 1 FROM calc.MethodologyVersion\"", true)]
    [InlineData("// db.MethodologyVersions — лише адаптер", false)]
    [InlineData("var rows = await methodologies.ListReadsAsync(ct);", false)]
    [InlineData("IMethodologyRegistryEdges edges", false)]
    [InlineData("u.SourceKind == RegistryUse.RegistryRuleSource", false)]
    public void Сито_впізнає_читання_методологій(string code, bool expected)
        => Assert.Equal(expected, MethodologyRead().IsMatch(WithoutComments(code)));

    private static Dictionary<string, string> Feature()
        => SourceTree.Production()
            .Where(f => f.Path.Contains("/DocumentDependencies/", StringComparison.Ordinal))
            .ToDictionary(f => f.Path, f => WithoutComments(f.Text), StringComparer.Ordinal);

    private static string WithoutComments(string text)
        => LineComment().Replace(BlockComment().Replace(text, " "), " ");

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline, matchTimeoutMilliseconds: 2000)]
    private static partial Regex BlockComment();

    [GeneratedRegex(@"//[^\n]*", RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex LineComment();

    [GeneratedRegex(
        @"\.\s*(?:MethodologyVersions|Methodologies|MethodologyFormulas|CalculationBindings)\b|\bMethodologyVersionSource\b|SourceKind\s*==\s*1\b|\b(?:calc\]?\.\[?Methodology\w*|cfg\]?\.\[?CalculationBinding)\b",
        RegexOptions.None,
        matchTimeoutMilliseconds: 2000)]
    private static partial Regex MethodologyRead();

    [GeneratedRegex(@"DocumentSourceBindingConsumer", RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex ConsumerList();
}
