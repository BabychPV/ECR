// tests/Ecr.Application.Tests/Calculations/MethodologyKeyLocalizerTests.cs
using System.Text.Json;
using Ecr.Application.Calculations;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Calculations;

/// <summary>C1: переклад ключів правил і обов'язкових входів на колонки версії шаблону документа.</summary>
public sealed class MethodologyKeyLocalizerTests
{
    private const int TargetVersion = 7;

    private static MethodologyRule Rule(string code, string json, int priority = 10)
        => new(methodologyVersionId: 1, EcrCode.Create(code), json, priority);

    private static IColumnPathMapper Mapper(params (int Source, int Target)[] pairs)
    {
        var mapper = Substitute.For<IColumnPathMapper>();
        mapper.MapToVersionAsync(Arg.Any<IReadOnlyCollection<int>>(), TargetVersion, Arg.Any<CancellationToken>())
            .Returns(pairs.ToDictionary(p => p.Source, p => p.Target));
        return mapper;
    }

    [Fact]
    [Trait("Finding", "C1")]
    public async Task Ключі_правила_перекладаються_а_значення_й_порядок_лишаються()
    {
        var rules = new[] { Rule("A", """{"11":"Running","12":"Gas"}""") };

        var result = await MethodologyKeyLocalizer.LocalizeAsync(
            Mapper((11, 111), (12, 112)), TargetVersion, rules, null, CancellationToken.None);

        var rule = Assert.Single(result.Rules);
        Assert.Equal("""{"111":"Running","112":"Gas"}""", rule.MatchJson);
        Assert.Empty(rule.UnmappedColumnIds);
    }

    [Fact]
    [Trait("Finding", "C1")]
    public async Task Ключ_без_відповідника_робить_правило_явно_незбіжним_і_називає_Id()
    {
        var rules = new[] { Rule("A", """{"11":"Running","99":"X"}""") };

        var result = await MethodologyKeyLocalizer.LocalizeAsync(
            Mapper((11, 111)), TargetVersion, rules, null, CancellationToken.None);

        var rule = Assert.Single(result.Rules);
        Assert.Equal([99], rule.UnmappedColumnIds);

        // Жодне значення рядка не має ключа «!99»: правило не збігається ні з чим (а не стає ширшим).
        var values = new Dictionary<string, string?> { ["111"] = "Running" };
        var compiled = MethodologyRuleMatcher.Compile(result.Predicates);
        Assert.Null(MethodologyRuleMatcher.Classify(compiled, values).Winner);
    }

    [Fact]
    [Trait("Finding", "C1")]
    public async Task Предикат_вся_таблиця_і_нечислові_ключі_не_змінюються()
    {
        var rules = new[] { Rule("ALL", "{}"), Rule("TXT", """{"code":"X"}""") };

        var result = await MethodologyKeyLocalizer.LocalizeAsync(
            Mapper(), TargetVersion, rules, null, CancellationToken.None);

        Assert.Equal(["{}", """{"code":"X"}"""], result.Rules.Select(r => r.MatchJson));
    }

    [Fact]
    [Trait("Finding", "C1")]
    public async Task Обов_язковий_вхід_перекладається_або_позначається_нерозв_язаним()
    {
        var inputs = new[]
        {
            new MethodologyRequiredInput(1, 11, RequiredInputSeverity.Block, hint: null),
            new MethodologyRequiredInput(1, 99, RequiredInputSeverity.Warn, hint: null),
        };

        var result = await MethodologyKeyLocalizer.LocalizeAsync(
            Mapper((11, 111)), TargetVersion, [], inputs, CancellationToken.None);

        Assert.Collection(
            result.RequiredInputs,
            first =>
            {
                Assert.True(first.IsMapped);
                Assert.Equal(111, first.ColumnDefId);
                Assert.Equal(RequiredInputSeverity.Block, first.Severity);
            },
            second =>
            {
                Assert.False(second.IsMapped);
                Assert.Equal(99, second.SourceColumnDefId);
            });
    }

    [Fact]
    [Trait("Finding", "C1")]
    public async Task Без_перекладача_Id_вважаються_локальними_і_сховище_не_питається()
    {
        var rules = new[] { Rule("A", """{"11":"Running"}""") };
        var inputs = new[] { new MethodologyRequiredInput(1, 11, RequiredInputSeverity.Block, hint: null) };

        var result = await MethodologyKeyLocalizer.LocalizeAsync(null, TargetVersion, rules, inputs, CancellationToken.None);

        Assert.Equal("""{"11":"Running"}""", Assert.Single(result.Rules).MatchJson);
        Assert.Equal(11, Assert.Single(result.RequiredInputs).ColumnDefId);
    }

    [Fact]
    [Trait("Finding", "C1")]
    public async Task Порожній_набір_ключів_не_питає_сховище()
    {
        var mapper = Substitute.For<IColumnPathMapper>();

        await MethodologyKeyLocalizer.LocalizeAsync(
            mapper, TargetVersion, [Rule("ALL", "{}")], [], CancellationToken.None);

        await mapper.DidNotReceiveWithAnyArgs().MapToVersionAsync(default!, default, default);
    }

    [Theory]
    [InlineData("""{"11":"A"}""", """{"22":"A"}""")]
    [InlineData("""{"11":1,"x":null}""", """{"22":1,"x":null}""")]
    [InlineData("[]", "[]")]
    [InlineData("не json", "не json")]
    [Trait("Finding", "C1")]
    public void RewriteKeys_зберігає_значення_і_не_ламається_на_битому_JSON(string json, string expected)
    {
        var actual = MethodologyRuleMatcher.RewriteKeys(json, id => id == 11 ? 22 : null);

        Assert.Equal(expected, actual);
        if (expected.StartsWith('{'))
        {
            using var _ = JsonDocument.Parse(actual);
        }
    }

    [Fact]
    [Trait("Finding", "C1")]
    public async Task Локалізація_ідентифікаторів_відкидає_колонки_без_відповідника()
    {
        var local = await MethodologyKeyLocalizer.LocalizeColumnIdsAsync(
            Mapper((11, 111)), TargetVersion, [11, 99], CancellationToken.None);

        Assert.Equal([111], local);
    }
}
