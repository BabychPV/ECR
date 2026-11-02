// tests/Ecr.Application.Tests/Calculations/MethodologyDerivedArgumentsTests.cs
using Ecr.Application.Calculations;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Calculations;

/// <summary>
/// Похідні аргументи (2026-11-02): перенос старих маркерів <c>Name?</c> зі списків у вирази — по ВСІЙ версії —
/// і звіт про те, що зникло.
/// </summary>
/// <remarks>
/// ⛔ Мутаційний доказ (лише цей клас): у <c>MethodologyDerivedArguments.Normalize</c> ставити маркери лише з ВЛАСНОГО
/// списку формули (<c>OptionalNamesFromCsv(formula.ArgumentsCsv)</c> замість множини версії) — червоніють
/// <see cref="Маркер_зі_списку_однієї_формули_стає_у_кожну_формулу_версії_що_вживає_імя"/> і
/// <see cref="Маркер_у_формулі_що_імені_не_вживає_переходить_туди_де_воно_є"/>.
/// </remarks>
public sealed class MethodologyDerivedArgumentsTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Маркер_зі_списку_однієї_формули_стає_у_кожну_формулу_версії_що_вживає_імя()
    {
        // Land, ECW_C11_10: `?` стоїть у списку однієї формули, а рантайм робить ім'я необов'язковим для ВСІЄЇ версії.
        var result = MethodologyDerivedArguments.Normalize(
        [
            new(1, "ECW_EC_tons_2754", "@Land_Measure_RunningHours * CST.EF_2754_", "@Land_Measure_RunningHours?;CST.EF_2754_"),
            new(2, "ECW_EC_gsec_2754", "@Land_Measure_RunningHours / 3600", "@Land_Measure_RunningHours"),
            new(3, "ECW_Other", "@Land_Power * 2", "@Land_Power"),
        ]);

        Assert.Equal("@Land_Measure_RunningHours? * CST.EF_2754_", result.Formulas[0].Expression);
        Assert.Equal("@Land_Measure_RunningHours? / 3600", result.Formulas[1].Expression);
        Assert.Equal("@Land_Power * 2", result.Formulas[2].Expression);

        Assert.Equal("@Land_Measure_RunningHours?;CST.EF_2754_", result.Formulas[0].ArgumentsCsv);
        Assert.Equal("@Land_Measure_RunningHours?", result.Formulas[1].ArgumentsCsv);
        Assert.Equal([1, 1, 0], result.Formulas.Select(f => f.MarkersAdded));
        Assert.Empty(result.DroppedOptional);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Маркер_у_формулі_що_імені_не_вживає_переходить_туди_де_воно_є()
    {
        // Land, ECW_C12_02 v1: `@Land_Measure_MaximumConsumption?` оголошено у формулі, яка його не вживає.
        var result = MethodologyDerivedArguments.Normalize(
        [
            new(1, "ECW_EC_gsec_415_type1", "CST.K4 * @Density0", "@Density0;@Land_Measure_MaximumConsumption?;CST.K4"),
            new(2, "ECW_EC_gsec_415_type2", "@Land_Measure_MaximumConsumption * 2", "@Land_Measure_MaximumConsumption"),
        ]);

        Assert.Equal("@Land_Measure_MaximumConsumption? * 2", result.Formulas[1].Expression);
        Assert.Equal(["@Land_Measure_MaximumConsumption?"], result.Formulas[0].DroppedDeclared);
        Assert.Empty(result.DroppedOptional);

        // Множина необов'язкових версії не змінилась — рантайм рахує так само.
        Assert.Contains(
            "Land_Measure_MaximumConsumption",
            result.Formulas.SelectMany(f => Ecr.Expressions.Binding.ArgumentDeclarationChecker.OptionalNamesFromCsv(f.ArgumentsCsv)));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Маркер_імені_якого_не_вживає_жодна_формула_версії_зникає_і_названий_у_звіті()
    {
        var result = MethodologyDerivedArguments.Normalize(
        [
            new(1, "A", "@X + 1", "@X;@Ghost?"),
        ]);

        Assert.Equal(["Ghost"], result.DroppedOptional);
        Assert.Equal(["@Ghost?"], result.Formulas[0].DroppedDeclared);
        Assert.Equal("@X", result.Formulas[0].ArgumentsCsv);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Невжиті_обовязкові_елементи_списку_прибираються_і_названі_поформульно()
    {
        // Land, ECW_C05_04 v1 / ECW_EC_tons_330: `CST.K5_ECW_C05_04_` у списку, у виразі — ні; плюс описка `(CST…`.
        var result = MethodologyDerivedArguments.Normalize(
        [
            new(1, "ECW_EC_tons_330", "@Land_Power * CST.EF_330_", "@Land_Power;CST.EF_330_;CST.K5_ECW_C05_04_;(CST.k5_ECW_C05_04_"),
        ]);

        var formula = Assert.Single(result.Formulas);
        Assert.Equal(["CST.K5_ECW_C05_04_", "@(CST.k5_ECW_C05_04_"], formula.DroppedDeclared);
        Assert.Equal("@Land_Power;CST.EF_330_", formula.ArgumentsCsv);
        Assert.False(formula.ExpressionChanged);
        Assert.True(formula.ArgumentsChanged);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Вирівняна_версія_вирівняна_назавжди()
    {
        var first = MethodologyDerivedArguments.Normalize(
        [
            new(1, "A", "@Jan + @Feb", "Jan?;Feb;Unused"),
            new(2, "B", "@Jan * 2", "@Jan"),
        ]);

        Assert.All(first.Formulas, f => Assert.True(MethodologyDerivedArguments.IsDerived(f.Expression, f.ArgumentsCsv)));

        var second = MethodologyDerivedArguments.Normalize(
            [.. first.Formulas.Select(f => new MethodologyDerivedArguments.Input(f.Id, f.Code, f.Expression, f.ArgumentsCsv))]);

        Assert.All(second.Formulas, f => Assert.False(f.ExpressionChanged || f.ArgumentsChanged));
        Assert.Empty(second.Formulas.SelectMany(f => f.DroppedDeclared));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Застаріле_поле_запиту_переносить_лише_маркери()
    {
        // `tools/land` (етап OptionalArguments) шле `argumentsCsv` із `?` і незмінний вираз — маркер іде у вираз.
        Assert.Equal(
            "@Land_Measure_RunningHours? * CST.K",
            SaveMethodologyFormulaHandler.LegacyMarkers("@Land_Measure_RunningHours * CST.K", "@Land_Measure_RunningHours?;@Density20;"));

        // Без маркерів — вираз як є; `null` — теж.
        Assert.Equal("@A + 1", SaveMethodologyFormulaHandler.LegacyMarkers("@A + 1", "A;B"));
        Assert.Equal("@A + 1", SaveMethodologyFormulaHandler.LegacyMarkers("@A + 1", null));
    }
}
