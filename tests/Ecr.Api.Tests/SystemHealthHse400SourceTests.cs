using Ecr.Api.Startup;
using Ecr.Application.Health;
using Ecr.Application.Ports;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>B80 (N1, RW.5): факти зв'язки HSE400 → Land у блоці <c>calculations</c> фактів здоров'я.</summary>
public sealed class SystemHealthHse400SourceTests
{
    /// <remarks>
    /// Мутація (B80-M12, попередження): <c>!facts.ApprovedOnly || …</c> → <c>facts.UnapprovedMonthWithData</c> — зв'язка на Any
    /// з документом без незатверджених даних більше не попереджає, червоний.
    /// </remarks>
    [Theory]
    [InlineData(false, false, false, null, null, false)] // зв'язки немає — стан і місяці null
    [InlineData(true, false, false, null, "any", false)] // зв'язка є, документа року немає — місяці null, без попередження
    [InlineData(true, false, false, 0, "any", true)] // Any з документом — попередження, доки не перемкнуто
    [InlineData(true, true, false, 12, "approvedOnly", false)] // усе затверджено
    [InlineData(true, true, true, 3, "approvedOnly", true)] // незатверджений місяць із даними
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Directive", "B80")]
    public void Факти_зв_язки_null_семантика_і_попередження(
        bool hasBinding, bool approvedOnly, bool unapprovedWithData, int? approvedMonths, string? expectedState, bool expectedWarning)
    {
        var snapshot = new SystemHealthSnapshot(
            new HealthJobCounts(0, 0, 0),
            new HealthSourceCounts(0, 0, 0),
            null,
            null,
            null,
            Hse400DocumentInCurrentYear: approvedMonths is not null,
            hasBinding ? new HealthHse400SourceFacts(approvedOnly, true, approvedMonths, unapprovedWithData) : null);

        var dto = SystemFactsCalculationsDto.Of(true, ConfigurationSourceLabel.AppSettings, snapshot);

        Assert.Equal(expectedState, dto.Hse400SourceState);
        Assert.Equal(approvedMonths, dto.Hse400ApprovedMonths);
        Assert.Equal(expectedWarning, dto.Hse400SourceWarning);
        Assert.True(dto.Hse400CompositionFromDocument);
        Assert.Equal(ConfigurationSourceLabel.AppSettings, dto.Hse400CompositionSource);
    }

    /// <remarks>Мутація (B80-M13, перекриття в фактах): прибрати заміну за <c>Default</c> — мітка лишається Default, червоний.</remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Directive", "B80")]
    public void Ключ_не_задано_дієве_значення_дає_біт_зв_язки_з_міткою_Binding()
    {
        var snapshot = new SystemHealthSnapshot(
            new HealthJobCounts(0, 0, 0), new HealthSourceCounts(0, 0, 0), null, null, null,
            Hse400DocumentInCurrentYear: true,
            new HealthHse400SourceFacts(ApprovedOnly: false, ReadFromDocument: true, ApprovedMonths: 0, UnapprovedMonthWithData: true));

        var unset = SystemFactsCalculationsDto.Of(false, SystemHostFacts.SourceDefault, snapshot);
        Assert.True(unset.Hse400CompositionFromDocument);
        Assert.Equal(SystemFactsCalculationsDto.SourceBinding, unset.Hse400CompositionSource);
        Assert.False(unset.Hse400CompositionWarning);

        // Ключ задано явно — він і діє, біт зв'язки не підміняє.
        var explicitOff = SystemFactsCalculationsDto.Of(false, ConfigurationSourceLabel.Environment, snapshot);
        Assert.False(explicitOff.Hse400CompositionFromDocument);
        Assert.True(explicitOff.Hse400CompositionWarning);
    }
}
