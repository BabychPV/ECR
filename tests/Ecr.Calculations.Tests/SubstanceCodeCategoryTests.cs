// tests/Ecr.Calculations.Tests/SubstanceCodeCategoryTests.cs
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Calculations;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Calculations.Tests;

/// <summary>
/// LP-07, форма B: старий HSE складає ключ категорії як <c>{група/NAE}_{код речовини}</c>. Ключ правила
/// версії доповнюється кодом запису речовини ітерації: <c>{ключ}_{код}</c> виграє над <c>{ключ}</c>, над
/// <c>Common</c>; без такої константи — як було (форма A / <c>#REF</c>), без підміни чужою речовиною.
/// </summary>
/// <remarks>
/// Мутаційні докази: прибрати крок суфікса в <c>ConstantResolver.Resolve</c> — червоні всі, що чекають
/// різні значення по речовинах; віддавати суфікс іншої речовини — червоний
/// <see cref="Речовина_без_своєї_константи_дає_REF_а_не_чужу"/>.
/// </remarks>
public sealed class SubstanceCodeCategoryTests
{
    private static decimal ValueOf(CalculationOutput output, long substanceId)
        => output.Values.Single(v => v.OutputCode == "Total" && v.SubstanceEntryId == substanceId).Value;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Дві_речовини_беруть_константи_свого_коду()
    {
        var stand = new RuleStand(rule: "@Nae")
            .WithSubstanceFormula("Total", "@X * CST.EF")
            .WithSubstanceCodes("333", "2754")
            .WithConstants(RuleStand.Number("EF", 2m, "6600_333"), RuleStand.Number("EF", 5m, "6600_2754"));

        var output = await stand.RunAsync(RuleStand.In("X", 10m), RuleStand.In("Nae", "6600"));

        Assert.Equal(20m, ValueOf(output, RuleStand.SubstanceA));
        Assert.Equal(50m, ValueOf(output, RuleStand.SubstanceB));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Форма_A_без_суфікса_лишається_як_була()
    {
        var stand = new RuleStand(rule: "@Nae")
            .WithSubstanceFormula("Total", "@X * CST.EF")
            .WithSubstanceCodes("333", "2754")
            .WithConstants(RuleStand.Number("EF", 2m, "6600"), RuleStand.Number("EF", 9m, "6607"));

        var output = await stand.RunAsync(RuleStand.In("X", 10m), RuleStand.In("Nae", "6600"));

        Assert.Equal(20m, ValueOf(output, RuleStand.SubstanceA));
        Assert.Equal(20m, ValueOf(output, RuleStand.SubstanceB));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Категорія_з_кодом_виграє_над_категорією_і_над_Common()
    {
        var stand = new RuleStand(rule: "@Nae")
            .WithSubstanceFormula("Total", "@X * CST.EF")
            .WithSubstanceCodes("333", "2754")
            .WithConstants(
                RuleStand.Number("EF", 1m, MethodologyConstant.CommonCategory),
                RuleStand.Number("EF", 2m, "6600"),
                RuleStand.Number("EF", 7m, "6600_333"));

        var output = await stand.RunAsync(RuleStand.In("X", 10m), RuleStand.In("Nae", "6600"));

        Assert.Equal(70m, ValueOf(output, RuleStand.SubstanceA)); // {ключ}_{код}
        Assert.Equal(20m, ValueOf(output, RuleStand.SubstanceB)); // {ключ}, а не Common
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Речовина_без_своєї_константи_дає_REF_а_не_чужу()
    {
        var stand = new RuleStand(rule: "@Nae")
            .WithSubstanceFormula("Total", "@X * CST.EF")
            .WithSubstanceCodes("333", "2754")
            .WithConstants(RuleStand.Number("EF", 2m, "6600_333"));

        var output = await stand.RunAsync(RuleStand.In("X", 10m), RuleStand.In("Nae", "6600"));

        Assert.Equal(20m, ValueOf(output, RuleStand.SubstanceA));
        Assert.DoesNotContain(
            output.Values, v => v.OutputCode == "Total" && v.SubstanceEntryId == RuleStand.SubstanceB);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Код_із_провідним_нулем_знаходить_суфікс_без_нуля_і_пробіл_у_ключі_не_заважає()
    {
        var stand = new RuleStand(rule: "@Nae")
            .WithSubstanceFormula("Total", "@X * CST.EF")
            .WithSubstanceCodes("0301", "0304")
            .WithConstants(RuleStand.Number("EF", 3m, "6585(001)_301"), RuleStand.Number("EF", 4m, "6585(001)_304"));

        var output = await stand.RunAsync(RuleStand.In("X", 10m), RuleStand.In("Nae", "6585 (001)"));

        Assert.Equal(30m, ValueOf(output, RuleStand.SubstanceA));
        Assert.Equal(40m, ValueOf(output, RuleStand.SubstanceB));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Без_кодів_речовин_суфікс_не_застосовується_а_неоднозначність_лишається()
    {
        var stand = new RuleStand(rule: "@Nae")
            .WithSubstanceFormula("Total", "@X * CST.EF")
            .WithConstants(RuleStand.Number("EF", 2m, "6600_333"), RuleStand.Number("EF", 5m, "6600_2754"));

        // Ключ «6600» не збігається ні з якою категорією, тож константи немає (#REF), а не підміна.
        var output = await stand.RunAsync(RuleStand.In("X", 10m), RuleStand.In("Nae", "6600"));

        Assert.DoesNotContain(output.Values, v => v.OutputCode == "Total");
    }
}
