using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Domain.Tests.ValueObjects;

/// <summary>
/// Три різні стани комірки, які **не можна зводити один до одного** (R-B4):
/// «не заповнювали», «заповнили порожнім», «є значення».
/// </summary>
public sealed class CellValueDataTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Заповнене_рівно_одним_значенням_вважається_коректним()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Заповнене_двома_значеннями_одночасно_вважається_некоректним()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Явна_порожнеча_не_має_жодного_значення()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Явна_порожнеча_і_відсутність_комірки_це_різні_стани()
        => Assert.Fail("not implemented");
}
