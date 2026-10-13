// tests/Ecr.Calculations.Tests/SubstanceApplicabilityModuleTests.cs
using Ecr.Application.Ports;
using Ecr.Expressions;
using Ecr.Expressions.Evaluation;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Calculations.Tests;

/// <summary>
/// Застосовність речовини на рядок крізь справжній <see cref="GenericCalculationModule"/> (02b): склад версії -
/// три речовини, а КІЛЬКІСТЬ рядків результату визначає довідник застосовності, а не склад версії.
/// </summary>
/// <remarks>
/// Мутаційні докази (лише цей клас): не перераховувати правило по речовинах (прибрати
/// <c>AppliesPerSubstance</c>) - червоніють усі «рахується лише застосовне»; пропуск без <c>continue</c> -
/// червоніє <see cref="Речовина_без_запису_пропускається_без_виходу_і_без_помилки"/>.
/// </remarks>
public sealed class SubstanceApplicabilityModuleTests
{
    private const string WithApplicability =
        "if(SUBSTANCE_APPLICABLE('APPL', 'M1', 'Stream', @Stream), @Stream, '')";

    private static InMemoryRegistrySnapshot Applicability(bool marker = true)
    {
        var snapshot = new InMemoryRegistrySnapshot()
            .AddRegistry(
                "APPL", ["METHODOLOGY", "SELECTOR_KIND", "SELECTOR_VALUE", "SUBSTANCE_CODE"],
                primaryKey: ["METHODOLOGY", "SELECTOR_KIND", "SELECTOR_VALUE", "SUBSTANCE_CODE"]);

        long id = 700;
        void Add(string scope, string kind, string value, string code)
            => snapshot.AddEntry(
                "APPL", ++id, $"A{id}",
                new()
                {
                    ["METHODOLOGY"] = ExpressionValue.Text(scope),
                    ["SELECTOR_KIND"] = ExpressionValue.Text(kind),
                    ["SELECTOR_VALUE"] = ExpressionValue.Text(value),
                    ["SUBSTANCE_CODE"] = ExpressionValue.Text(code),
                },
                ordinal: (int)id);

        if (marker)
        {
            Add("M1", "*", "*", "*");
        }

        // Два селектори з РІЗНИМИ наборами речовин: S1 -> 301, 304; S2 -> 304, 415.
        Add("M1", "Stream", "S1", "301");
        Add("M1", "Stream", "S1", "304");
        Add("M1", "Stream", "S2", "304");
        Add("M1", "Stream", "S2", "415");
        return snapshot;
    }

    /// <summary>
    /// Стенд: три речовини версії (301, 304, 415), формула <c>Total = @X * CST.EF</c> зі спільною константою.
    /// </summary>
    /// <remarks>
    /// ⚠ Спільна константа не випадкова: без застосовності вона дала б значення КОЖНІЙ речовині складу - саме
    /// ці зайві рядки результату й відрізняють ECR від старої системи.
    /// </remarks>
    private static RuleStand Stand(string rule, InMemoryRegistrySnapshot? snapshot = null)
        => new RuleStand(rule)
            .WithSubstanceFormula("Total", "@X * CST.EF")
            .WithConstants(RuleStand.Number("EF", 2m, "Common"))
            .WithThreeSubstances("301", "304", "415")
            .WithRegistries(snapshot ?? Applicability(), 202601, "APPL");

    private static List<long> Substances(CalculationOutput output)
        => [.. output.Values.Where(v => v.OutputCode == "Total").Select(v => (long)v.SubstanceEntryId!.Value).Order()];

    private static async Task<List<long>> Row(RuleStand stand, string stream)
        => Substances(await stand.RunAsync(RuleStand.In("X", 5m), RuleStand.In("Stream", stream)));

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Без_застосовності_кожна_речовина_складу_дає_рядок_результату_це_і_є_розбіжність()
    {
        // Те, що було ДО: правило без SUBSTANCE_APPLICABLE, спільна константа - три речовини, три рядки на рядок
        // документа, хоча стара система рахувала лише ті, що належать селектору рядка.
        var stand = Stand("@Stream");

        Assert.Equal(
            new long[] { RuleStand.SubstanceA, RuleStand.SubstanceB, RuleStand.SubstanceC },
            await Row(stand, "S1"));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Два_рядки_з_різними_наборами_застосовності_дають_рівно_свої_рядки_результату()
    {
        var stand = Stand(WithApplicability);

        var first = await Row(stand, "S1");
        var second = await Row(stand, "S2");

        // S1: 301 і 304; S2: 304 і 415 - по два рядки результату з трьох речовин складу.
        Assert.Equal(new long[] { RuleStand.SubstanceA, RuleStand.SubstanceB }, first);
        Assert.Equal(new long[] { RuleStand.SubstanceB, RuleStand.SubstanceC }, second);
        Assert.Equal(4, first.Count + second.Count);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Застосовна_речовина_рахується_з_тим_самим_значенням_що_й_без_правила()
    {
        var stand = Stand(WithApplicability);

        var output = await stand.RunAsync(RuleStand.In("X", 5m), RuleStand.In("Stream", "S1"));

        // 5 * 2 = 10 для кожної застосовної; застосовність значення не змінює.
        Assert.All(output.Values.Where(v => v.OutputCode == "Total"), v => Assert.Equal(10m, v.Value));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Речовина_без_запису_пропускається_без_виходу_і_без_помилки()
    {
        var stand = Stand(WithApplicability);

        var output = await stand.RunAsync(RuleStand.In("X", 5m), RuleStand.In("Stream", "S1"));

        // Речовини 415 (SubstanceC) для S1 немає: виходу немає (не #REF і не нуль), а в трейсі - пропуск.
        Assert.DoesNotContain(
            output.Values, v => v.OutputCode == "Total" && v.SubstanceEntryId == (int)RuleStand.SubstanceC);
        Assert.Contains(
            output.Trace,
            s => s.StepCode == "SubstanceNotApplicable" && s.SubstanceEntryId == RuleStand.SubstanceC && s.Expression == "415");
        Assert.DoesNotContain(output.Trace, s => s.TraceJson is not null && s.TraceJson.Contains("#REF", StringComparison.Ordinal));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Селектор_без_жодного_запису_при_наявному_маркері_не_дає_жодної_речовини()
    {
        var stand = Stand(WithApplicability);

        Assert.Empty(await Row(stand, "S3"));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Методологія_без_жодного_запису_застосовності_правило_не_діє_як_раніше()
    {
        // У довіднику взагалі немає M1 (ні маркера, ні записів): правило не діє - усі три речовини, як до появи довідника.
        var empty = new InMemoryRegistrySnapshot()
            .AddRegistry(
                "APPL", ["METHODOLOGY", "SELECTOR_KIND", "SELECTOR_VALUE", "SUBSTANCE_CODE"],
                primaryKey: ["METHODOLOGY", "SELECTOR_KIND", "SELECTOR_VALUE", "SUBSTANCE_CODE"]);
        var stand = Stand(WithApplicability, empty);

        Assert.Equal(
            new long[] { RuleStand.SubstanceA, RuleStand.SubstanceB, RuleStand.SubstanceC },
            await Row(stand, "S1"));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Правило_без_функції_застосовності_не_перераховується_по_речовинах()
    {
        // Версія з наявним правилом (ключ за аргументом) - побітно як було: один ключ на рядок, усі речовини.
        var stand = Stand("@Stream");

        var output = await stand.RunAsync(RuleStand.In("X", 5m), RuleStand.In("Stream", "S2"));

        Assert.Equal(3, output.Values.Count(v => v.OutputCode == "Total"));
        Assert.Single(output.Trace, s => s.StepCode == "CategoryRule");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Порожній_ключ_правила_у_Row_фазі_лишається_відмовою_рядка()
    {
        // Власний «невідомий» гілка правила (порожній ключ) відмовляє РЯДКУ, як і раніше; пропуск речовини - не про це.
        const string rule = "if(in(@Stream, 'S1', 'S2'), if(SUBSTANCE_APPLICABLE('APPL', 'M1', 'Stream', @Stream), @Stream, ''), '')";
        var stand = Stand(rule);

        var error = await Assert.ThrowsAsync<Ecr.Domain.Abstractions.DomainException>(() => Row(stand, "Невідомий"));

        Assert.Equal("err.ECR-CALC-0422.categoryRuleFailed", error.Details?["messageKey"]);
    }
}
