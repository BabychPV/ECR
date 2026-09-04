using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Domain.Tests.ValueObjects;

/// <summary>
/// <see cref="RowKey"/> — не <see cref="EcrCode"/>: він допускає цифрові ключі
/// (<c>"7001001"</c>) і GUID, бо стоїть в окремій позиції граматики.
/// </summary>
public sealed class RowKeyTests
{
    [Theory]
    [InlineData("7001001")]
    [InlineData("C009")]
    [InlineData("a1b2c3d4e5f60718293a4b5c6d7e8f90")]
    [InlineData("row-1.2")]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Допустимий_ключ_приймається(string key) => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Ключ_динамічного_рядка_це_GUID_у_форматі_N_без_дефісів()
        => Assert.Fail("not implemented");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Два_виклики_NewDynamic_дають_різні_ключі() => Assert.Fail("not implemented");
}
