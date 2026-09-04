// tests/Ecr.Expressions.Tests/Functions/MethodologyFunctionTests.cs
using Ecr.TestKit;
using Xunit;

namespace Ecr.Expressions.Tests.Functions;

/// <summary>Додаткові функції діалекту методологій.</summary>
public sealed class MethodologyFunctionTests
{
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void SWITCH_без_збігу_і_без_default_дає_null()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void SWITCH_повертає_перший_збіг_а_не_останній()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void CONVERT_між_різними_розмірностями_дає_помилку()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void CONVERT_однакових_одиниць_не_змінює_значення()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void PeriodContext_дає_тривалість_за_CalendarMode_методології()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Fixed365_і_Actual_дають_різні_числа_у_високосний_рік()
        => Assert.Fail("not implemented");
}
