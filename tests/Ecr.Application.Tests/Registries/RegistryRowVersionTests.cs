// tests/Ecr.Application.Tests/Registries/RegistryRowVersionTests.cs
using Ecr.Application.Registries.Rows;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Registries;

/// <summary>
/// Версія рядка довідника (<c>D-166</c>, RT-13): те, що віддав <c>GET …/rows</c>, RT-14 має
/// розібрати назад у той самий <c>PeriodStart</c> — інакше кожен <c>baseVersion</c> був би конфліктом.
/// </summary>
public sealed class RegistryRowVersionTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public void Версія_розбирається_в_той_самий_момент_з_точністю_datetime2_3()
    {
        var periodStart = new DateTime(2026, 9, 27, 14, 3, 11, 457, DateTimeKind.Utc);

        var version = RegistryRowVersion.Encode(periodStart);

        Assert.True(RegistryRowVersion.TryDecode(version, out var decoded));
        Assert.Equal(periodStart, decoded);
        Assert.Equal(DateTimeKind.Utc, decoded.Kind);

        // Сусідня мілісекунда — інша версія: інакше дві правки в одну секунду не розрізнялися б.
        Assert.NotEqual(version, RegistryRowVersion.Encode(periodStart.AddMilliseconds(1)));
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not base64!")]
    [InlineData("AAAA")]
    [InlineData("AAAAAAAAAAAAAAAA")]
    [InlineData("//////////8=")]
    public void Чужий_рядок_не_є_версією(string? version)
        => Assert.False(RegistryRowVersion.TryDecode(version, out _));
}
