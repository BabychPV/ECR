using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Domain.Tests.ValueObjects;

/// <summary>
/// Той самий контракт, що <see cref="CellValueDataTests"/> (R-B4): «не
/// заповнювали», «заповнили порожнім», «є значення» — три різні стани поля
/// шапки, які не зводяться один до одного.
/// </summary>
public sealed class DocumentHeaderValueDataTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Заповнене_рівно_одним_значенням_вважається_коректним()
    {
        Assert.True(new DocumentHeaderValueData { ValueNumeric = 12500.000m }.IsWellFormed());
        Assert.True(new DocumentHeaderValueData { ValueString = "Свердловина 7" }.IsWellFormed());
        Assert.True(new DocumentHeaderValueData { ValueBool = false }.IsWellFormed());
        Assert.True(new DocumentHeaderValueData { ValueDate = new DateTime(2026, 1, 31) }.IsWellFormed());
        Assert.True(new DocumentHeaderValueData { ValueRegistryEntryId = 42 }.IsWellFormed());
        Assert.True(new DocumentHeaderValueData { ValueUnitId = 7 }.IsWellFormed());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Заповнене_двома_значеннями_одночасно_вважається_некоректним()
    {
        Assert.False(new DocumentHeaderValueData { ValueNumeric = 1m, ValueString = "1" }.IsWellFormed());
        Assert.False(new DocumentHeaderValueData().IsWellFormed());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Явна_порожнеча_не_має_жодного_значення()
    {
        var empty = DocumentHeaderValueData.Empty;

        Assert.True(empty.IsEmpty);
        Assert.True(empty.IsWellFormed());
        Assert.Null(empty.ValueString);
        Assert.Null(empty.ValueNumeric);

        Assert.False((empty with { ValueNumeric = 0m }).IsWellFormed());
    }
}
