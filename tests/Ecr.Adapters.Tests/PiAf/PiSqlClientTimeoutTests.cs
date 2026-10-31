// tests/Ecr.Adapters.Tests/PiAf/PiSqlClientTimeoutTests.cs
using Ecr.Adapters.PiAf;
using Ecr.Application.Ports;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Adapters.Tests.PiAf;

/// <summary>
/// D-464: тайм-аут читання подій — окремий від тайм-ауту команди тега (<c>PiSqlClient:EventQueryTimeoutSeconds</c>,
/// типово 300 с): повна звірка річного періоду — один запит на весь рік.
/// </summary>
public sealed class PiSqlClientTimeoutTests
{
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Decision", "D-464")]
    [InlineData(null, null, 60, 300)]
    [InlineData(90, null, 90, 300)]
    [InlineData(null, 900, 60, 900)]
    [InlineData(0, -5, 60, 300)]
    [InlineData(45, 1200, 45, 1200)]
    public void Тайм_аут_подій_окремий_від_тайм_ауту_команди(
        int? command, int? events, int expectedCommand, int expectedEvents)
    {
        using var adapter = new PiSqlClientDataSource(
            Substitute.For<ICollectionStore>(),
            Substitute.For<ISecretProvider>(),
            options: new PiSqlClientOptions(command, events));

        Assert.Equal((expectedCommand, expectedEvents), (adapter.CommandTimeoutSeconds, adapter.EventQueryTimeoutSeconds));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Decision", "D-464")]
    public void Без_налаштувань_типові_значення()
    {
        using var adapter = new PiSqlClientDataSource(Substitute.For<ICollectionStore>(), Substitute.For<ISecretProvider>());

        Assert.Equal(PiSqlClientOptions.DefaultCommandTimeoutSeconds, adapter.CommandTimeoutSeconds);
        Assert.Equal(PiSqlClientOptions.DefaultEventQueryTimeoutSeconds, adapter.EventQueryTimeoutSeconds);
        Assert.True(PiSqlClientOptions.DefaultEventQueryTimeoutSeconds > PiSqlClientOptions.DefaultCommandTimeoutSeconds);
    }
}
