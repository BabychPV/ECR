// tests/Ecr.Calculations.Tests/IntermediateResultsTests.cs
using Ecr.Domain.Enums;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Calculations.Tests;

/// <summary>
/// Видима формула → рядок результату з <c>Kind = Intermediate</c> (HSE301 A3a,
/// <c>D-175</c>, V-6; FEATURE-HSE301-VIEW §7.1).
/// </summary>
/// <remarks>
/// Мутаційні докази: писати проміжні з <c>Kind = Output</c> —
/// <see cref="Видимі_формули_пишуться_проміжними_з_одиницею_і_областю"/> червоний; писати
/// проміжним і видиму формулу-вихід — <see cref="Видима_формула_що_є_виходом_лягає_один_раз"/>
/// червоний (два рядки <c>M_t</c>).
/// </remarks>
public sealed class IntermediateResultsTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.13")]
    public async Task Видимі_формули_пишуться_проміжними_з_одиницею_і_областю()
    {
        var output = await new ScopeStand().RunAsync(TraceLevel.ErrorsOnly);

        // Row-формула — один рядок без речовини, в одиниці формули.
        var volume = Assert.Single(output.Values, v => v.OutputCode == "V_Sm3");
        Assert.Equal(CalculationResultKind.Intermediate, volume.Kind);
        Assert.Null(volume.SubstanceEntryId);
        Assert.Equal(ScopeStand.Sm3Unit, volume.UnitId);
        Assert.Equal(269.258m, volume.Value);

        // Формула речовини — рядок на кожну речовину.
        var composition = output.Values.Where(v => v.OutputCode == "W_COMP").ToList();
        Assert.Equal(ScopeStand.SubstanceCount, composition.Count);
        Assert.All(composition, v =>
        {
            Assert.Equal(CalculationResultKind.Intermediate, v.Kind);
            Assert.Equal(ScopeStand.PercentUnit, v.UnitId);
        });
        Assert.Equal(ScopeStand.SubstanceIds, composition.Select(v => (long)v.SubstanceEntryId!.Value));
        Assert.Equal(17.5m, Assert.Single(composition, v => v.SubstanceEntryId == 905).Value);
    }

    /// <remarks>
    /// ⛔ <c>M_t</c> — «видимий і вихід» (§6.3). Другий рядок з тим самим кодом
    /// (<c>Intermediate</c>) подвоїв би масу в сітці, яка сумує рядки за кодом.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public async Task Видима_формула_що_є_виходом_лягає_один_раз()
    {
        var output = await new ScopeStand().RunAsync(TraceLevel.ErrorsOnly);

        Assert.Equal(CalculationResultKind.Output, Assert.Single(output.Values, v => v.OutputCode == "M_t").Kind);
        Assert.All(
            output.Values.Where(v => v.OutputCode == "tons"),
            v => Assert.Equal(CalculationResultKind.Output, v.Kind));
    }

    /// <remarks>
    /// ⛔ Типове <c>IsVisible = false</c> не пише нічого проміжного — обсяг <c>calc.*</c>
    /// наявних методологій не росте (HR-8). І виходи побітно ті самі, що з видимими
    /// формулами: поява прапорця не змінює жодного числа виходу.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public async Task Невидимі_формули_не_пишуться_а_виходи_ті_самі()
    {
        var hidden = await new ScopeStand(visible: false).RunAsync(TraceLevel.ErrorsOnly);
        var shown = await new ScopeStand(visible: true).RunAsync(TraceLevel.ErrorsOnly);

        Assert.All(hidden.Values, v => Assert.Equal(CalculationResultKind.Output, v.Kind));
        Assert.Equal(1 + ScopeStand.SubstanceCount, hidden.Values.Count);

        Assert.Equal(
            hidden.Values,
            shown.Values.Where(v => v.Kind == CalculationResultKind.Output));

        Assert.Equal(1 + ScopeStand.SubstanceCount, shown.Values.Count(v => v.Kind == CalculationResultKind.Intermediate));
    }

    /// <remarks>
    /// ⚠ Видимої формули без одиниці публікація не пропускає (<c>visibleFormulaNoUnit</c>);
    /// до рушія така доходить лише з чернетки. Результат без одиниці заборонений
    /// (ФВ-16.6), тож рушій її не пише — а не вигадує одиницю.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public async Task Видима_формула_без_одиниці_не_пишеться()
    {
        var output = await new ScopeStand(visibleWithoutUnit: true).RunAsync(TraceLevel.ErrorsOnly);

        Assert.DoesNotContain(output.Values, v => v.OutputCode == "W_COMP");
        Assert.Single(output.Values, v => v.OutputCode == "V_Sm3");
    }
}
