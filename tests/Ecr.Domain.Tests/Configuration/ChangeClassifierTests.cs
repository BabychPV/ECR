using Ecr.Domain.Enums;
using Ecr.Domain.Services;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Domain.Tests.Configuration;

/// <summary>
/// Чотири класи змін (ФВ-7.4). Від класу залежить, чи дозволена зміна взагалі:
/// <c>Breaking</c> у версії з документами — **відмова операції**, а не
/// попередження.
/// </summary>
public sealed class ChangeClassifierTests
{
    [Theory]
    [InlineData("HeaderL10n")]
    [InlineData("Ordinal")]
    [InlineData("DisplayFormat")]
    [InlineData("IsHidden")]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Презентаційні_поля_класифікуються_як_Presentation(string field)
        => Assert.Fail("not implemented");

    [Theory]
    [InlineData("DataType")]
    [InlineData("Precision")]
    [InlineData("UnitId")]
    [InlineData("LookupRegistryDefId")]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Зміна_типу_точності_або_одиниці_класифікується_як_Guarded(string field)
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Зміна_коду_колонки_за_наявності_документів_це_Breaking()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Додавання_нової_колонки_це_Safe()
        => Assert.Fail("not implemented");
}
