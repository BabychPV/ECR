using Ecr.Application.Ports;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Calculations.Tests;

/// <summary>
/// ✎ D-571 (репетиція фази W15z на копії live, ECW_C07_07_01 V2, рядок LTD-A07): у <c>Legacy</c> формула не бачить id запису
/// довідника (<c>D-161</c>), тож ЗАПОВНЕНА Lookup-комірка (лише <c>EntryId</c>) для формули — <c>null</c>. Діагностика
/// «відсутні аргументи» рядка без жодного значення називала цю комірку відсутньою, хоча її заповнено (і саме за нею правило
/// відібрало рядок); скрипт називав лише справді порожнє поле — розбіжність паритету <c>missingArguments.set</c>.
/// </summary>
/// <remarks>
/// Мутаційний доказ: у <c>GenericCalculationModule.MissingArgumentsOf</c> прибрати пропуск аргументу з <c>EntryId</c> —
/// червоніє <see cref="Заповнена_Lookup_комірка_в_Legacy_не_називається_відсутньою"/>.
/// </remarks>
public sealed class LegacyLookupMissingArgumentTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Заповнена_Lookup_комірка_в_Legacy_не_називається_відсутньою()
    {
        var stand = new RuleStand(rule: null) { Mode = NumericMode.Legacy }
            .WithSubstanceFormula("Total", "if(@Material = 55, 1, 2) * @OperTime")
            .WithSubstanceCodes("0301", "0304");

        // Material заповнено записом довідника (лише EntryId, як Lookup-комірка), OperTime немає.
        var output = await stand.RunAsync(new CalculationArgument("Material", null, null, null, 56));

        Assert.Empty(output.Values);
        Assert.Equal(["OperTime"], output.MissingArguments);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Порожня_Lookup_комірка_в_Legacy_як_і_раніше_відсутня()
    {
        var stand = new RuleStand(rule: null) { Mode = NumericMode.Legacy }
            .WithSubstanceFormula("Total", "if(@Material = 55, 1, 2) * @OperTime")
            .WithSubstanceCodes("0301", "0304");

        var output = await stand.RunAsync(new CalculationArgument("Other", 1m, null, null));

        Assert.Equal(["Material", "OperTime"], output.MissingArguments);
    }
}
