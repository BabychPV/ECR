// tests/Ecr.Application.Tests/Documents/SheetChangeMarkerTests.cs
using Ecr.Application.Documents.SheetChanges;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Documents;

/// <summary>Маркер опитування змін аркуша: круговий шлях і зіпсовані значення.</summary>
public sealed class SheetChangeMarkerTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public void Маркер_проходить_круговий_шлях_до_секунди()
    {
        var at = new DateTime(2026, 10, 30, 12, 34, 56, 789, DateTimeKind.Utc);
        var encoded = SheetChangeMarker.Encode(0x0000_0000_0049_1A2Bul, at);

        var decoded = SheetChangeMarker.Decode(encoded);

        Assert.NotNull(decoded);
        Assert.Equal(0x0000_0000_0049_1A2Bul, decoded.Value.Version);
        Assert.Equal(new DateTime(2026, 10, 30, 12, 34, 56, DateTimeKind.Utc), decoded.Value.IssuedAtUtc);
        Assert.Equal(DateTimeKind.Utc, decoded.Value.IssuedAtUtc.Kind);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-marker")]
    [InlineData("00000000004912ab")]
    [InlineData("00000000004912ab-")]
    [InlineData("0000000004912ab-6721f0a0")]
    [InlineData("zz00000000004912-6721f0a0")]
    [InlineData("00000000004912ab-ffffffffffffffffffff")]
    public void Зіпсований_маркер_читається_як_відсутній(string? marker)
        => Assert.Null(SheetChangeMarker.Decode(marker));
}
