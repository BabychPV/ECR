// tests/Ecr.Calculations.Tests/CurrentSubstanceModuleTests.cs
using Ecr.Application.Ports;
using Ecr.Expressions;
using Ecr.Expressions.Evaluation;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Calculations.Tests;

/// <summary>
/// Символ «поточна речовина» (<c>CURRENT_SUBSTANCE()</c>) крізь справжній <see cref="GenericCalculationModule"/>:
/// той самий рядок, дві речовини складу версії — два різні результати; символ усередині вкладеного
/// <c>REGFIND</c>; визначена поведінка без складу речовин і без коду речовини.
/// </summary>
/// <remarks>
/// Мутаційні докази (лише цей клас): не передавати <c>currentSubstanceCode</c> у
/// <c>MethodologyEvaluationContext</c> циклу речовин — червоні всі, що чекають різні числа; завжди
/// питати коди речовин (прибрати <c>UsesCurrentSubstance</c>) — червоніє
/// <see cref="Версія_без_символу_не_питає_коди_речовин"/>.
/// </remarks>
public sealed class CurrentSubstanceModuleTests
{
    private const string Mass =
        "REGFIELD(REGFIND('COMP', [Period].Year, [Period].Sequence, @Stream, CURRENT_SUBSTANCE() + '_mass'), 'VALUE')";

    private static InMemoryRegistrySnapshot Composition()
    {
        var snapshot = new InMemoryRegistrySnapshot()
            .AddRegistry("COMP", ["YEAR", "MONTH", "STREAM", "INDICATOR", "VALUE"], primaryKey: ["YEAR", "MONTH", "STREAM", "INDICATOR"]);

        long id = 500;
        void Add(int month, string stream, string indicator, decimal value)
            => snapshot.AddEntry(
                "COMP", ++id, $"C{id}",
                new()
                {
                    ["YEAR"] = ExpressionValue.Number(2026),
                    ["MONTH"] = ExpressionValue.Number(month),
                    ["STREAM"] = ExpressionValue.Text(stream),
                    ["INDICATOR"] = ExpressionValue.Text(indicator),
                    ["VALUE"] = ExpressionValue.Number(value),
                },
                ordinal: (int)id);

        Add(1, "FG", "Methane_mass", 0.9m);
        Add(1, "FG", "Ethane_mass", 0.1m);
        Add(2, "FG", "Methane_mass", 0.7m);
        return snapshot;
    }

    private static RuleStand Stand(string expression = Mass)
        => new RuleStand(rule: null)
            .WithSubstanceFormula("Total", expression)
            .WithSubstanceCodes("Methane", "Ethane")
            .WithRegistries(Composition(), 202601, "COMP");

    private static decimal? ValueOf(CalculationOutput output, long substanceId)
        => output.Values.SingleOrDefault(v => v.OutputCode == "Total" && v.SubstanceEntryId == substanceId)?.Value;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Дві_речовини_одного_рядка_дають_різні_результати()
    {
        var stand = Stand("@X * " + Mass);

        var output = await stand.RunAsync(RuleStand.In("X", 100m), RuleStand.In("Stream", "FG"));

        Assert.Equal(90m, ValueOf(output, RuleStand.SubstanceA));
        Assert.Equal(10m, ValueOf(output, RuleStand.SubstanceB));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Період_рядка_вибирає_місяць_складу_а_речовина_без_показника_не_рахується()
    {
        var stand = new RuleStand(rule: null)
            .WithSubstanceFormula("Total", Mass)
            .WithSubstanceCodes("Methane", "Ethane")
            .WithRegistries(Composition(), 202602, "COMP");

        var output = await stand.RunAsync(RuleStand.In("Stream", "FG"));

        // Лютий: є лише Methane_mass; Ethane_mass немає — #N/A, речовина без результату, а не нуль.
        Assert.Equal(0.7m, ValueOf(output, RuleStand.SubstanceA));
        Assert.Null(ValueOf(output, RuleStand.SubstanceB));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Речовина_без_коду_в_довіднику_не_отримує_ключа_і_не_бере_чужий()
    {
        var stand = new RuleStand(rule: null)
            .WithSubstanceFormula("Total", Mass)
            .WithSubstanceCodes("Methane", null)
            .WithRegistries(Composition(), 202601, "COMP");

        var output = await stand.RunAsync(RuleStand.In("Stream", "FG"));

        Assert.Equal(0.9m, ValueOf(output, RuleStand.SubstanceA));
        Assert.Null(ValueOf(output, RuleStand.SubstanceB));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Версія_без_складу_речовин_дає_REF_у_трейсі_а_не_нуль_і_решта_формул_рахуються()
    {
        var stand = new RuleStand(rule: null)
            .WithSubstanceFormula("Total", Mass)
            .WithSubstanceFormula("Other", "@X * 2")
            .WithoutSubstances()
            .WithRegistries(Composition(), 202601, "COMP");

        var output = await stand.RunAsync(RuleStand.In("X", 4m), RuleStand.In("Stream", "FG"));

        // Формула з символом - #REF: виходу немає (а не 0), причина видна в трейсі.
        Assert.DoesNotContain(output.Values, v => v.OutputCode == "Total");
        var steps = output.Trace.Where(s => s.StepCode == "Total").ToList();
        Assert.NotEmpty(steps);
        Assert.All(steps, s => Assert.Equal("#REF", s.TraceJson));

        // Формула без символу в тій самій версії рахується, як і раніше (прохід із речовиною null).
        Assert.Equal(8m, output.Values.Single(v => v.OutputCode == "Other").Value);

        // Кодів речовин ніхто не питав: складу немає.
        Assert.Equal(0, stand.SubstanceCodeReads);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Версія_без_символу_не_питає_коди_речовин()
    {
        var stand = new RuleStand(rule: null)
            .WithSubstanceFormula("Total", "@X * 2")
            .WithSubstanceCodes("Methane", "Ethane");

        var output = await stand.RunAsync(RuleStand.In("X", 3m));

        Assert.Equal(6m, ValueOf(output, RuleStand.SubstanceA));
        Assert.Equal(0, stand.SubstanceCodeReads);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Версія_з_символом_питає_коди_рівно_раз_на_прив_язку()
    {
        var stand = Stand();

        await stand.RunAsync(RuleStand.In("Stream", "FG"));

        Assert.Equal(1, stand.SubstanceCodeReads);
    }
}
