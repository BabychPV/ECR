using Ecr.Application.Calculations;
using Ecr.Application.Ports;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Calculations.Scripting;

/// <summary>
/// ✎ D-571: золотий набір версії-скрипта рівня <c>Module</c> (HSE400) рахує модуль скрипта на плаcкому вході, як формульну —
/// <c>GenericCalculationModule</c>, а не обгортка місячних колонок (репетиція W15z: перемикання HSE400 відмовляло
/// «виходу не було» на кожен золотий вихід).
/// </summary>
/// <remarks>
/// Мутаційний доказ: у <see cref="GoldenSetModule.ForScript"/> шукати модуль за самим <c>descriptor</c> (без
/// <c>Level = Configuration</c>) — червоніє <see cref="Module_версія_скрипта_рахується_модулем_скрипта_а_не_обгорткою_місяців"/>.
/// </remarks>
public sealed class GoldenSetModuleTests
{
    private static ICalculationModule Fake(string code, Func<MethodologyDescriptor, bool> canHandle)
    {
        var module = Substitute.For<ICalculationModule>();
        module.Code.Returns(code);
        module.CanHandle(Arg.Any<MethodologyDescriptor>()).Returns(call => canHandle(call.Arg<MethodologyDescriptor>()));
        return module;
    }

    // Той самий порядок і ті самі умови CanHandle, що в AddEcrCalculations: місячна обгортка першою.
    private static readonly ICalculationModule MonthColumns = Fake("month-columns", d => d.Level == CalculationLevel.Module);
    private static readonly ICalculationModule Script = Fake(
        "script", d => d.Level == CalculationLevel.Configuration && d.Engine == MethodologyEngine.Script);
    private static readonly ICalculationModule Generic = Fake("generic", d => d.Engine == MethodologyEngine.Formulas);

    private static MethodologyDescriptor Descriptor(CalculationLevel level) => new(
        1, 2, "HSE400", "1.0.1.0", level, NumericMode.Legacy, CalendarMode.Actual, TraceLevel.Off, MethodologyEngine.Script);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Module_версія_скрипта_рахується_модулем_скрипта_а_не_обгорткою_місяців()
        => Assert.Equal("script", GoldenSetModule.ForScript([MonthColumns, Script, Generic], Descriptor(CalculationLevel.Module))?.Code);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Configuration_версія_скрипта_як_і_раніше_модулем_скрипта()
        => Assert.Equal("script", GoldenSetModule.ForScript([MonthColumns, Script, Generic], Descriptor(CalculationLevel.Configuration))?.Code);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Без_модуля_скрипта_null()
        => Assert.Null(GoldenSetModule.ForScript([MonthColumns, Generic], Descriptor(CalculationLevel.Module)));
}
