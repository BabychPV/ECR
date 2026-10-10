using Ecr.Adapters.PiAf;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using NSubstitute;
using Xunit;

namespace Ecr.Adapters.Tests.PiAf;

/// <summary>
/// L3-09: адаптер PiSqlClient перевіряє адресу ще раз перед з'єднанням — як <c>SqlDataSource</c> (L3-05):
/// джерело, збережене до появи політики (D-245: наявні ретроспективно не перевіряються), не ходить
/// службовим обліковим записом на link-local/metadata.
/// </summary>
/// <remarks>
/// ⚠ Без бази: відмова мусить настати ДО спроби з'єднання. Без фіксу ці тести йдуть у
/// <c>OdbcConnection.OpenAsync</c> (драйвера немає) і падають іншим кодом.
/// </remarks>
public sealed class PiSqlClientAddressPolicyTests
{
    [Theory]
    [InlineData("Driver={PI SQL Client};Server=169.254.169.254")]
    [InlineData("Driver={PI SQL Client};Server=tcp:169.254.169.254,80")]
    [InlineData("Driver={PI SQL Client};Server=2852039166")]
    [InlineData("Driver={PI SQL Client};Server=pi.example;Address=169.254.169.254")]
    [InlineData("Driver={PI SQL Client};Server=metadata.google.internal")]
    public async Task Заборонена_адреса_відмова_до_з_єднання(string endpoint)
    {
        var adapter = new PiSqlClientDataSource(StoreWith(endpoint), Substitute.For<ISecretProvider>());

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => adapter.DiscoverAsync(1, CancellationToken.None));

        Assert.Equal("ECR-INT-0503", error.ErrorCode);
        Assert.Equal("err.ECR-INT-0503.endpointForbidden", error.Details!["messageKey"]);
        Assert.Equal("PIAF", error.Details["dataSource"]);
        Assert.DoesNotContain("169.254", error.Message, StringComparison.Ordinal);
    }

    private static ICollectionStore StoreWith(string endpoint)
    {
        var store = Substitute.For<ICollectionStore>();

        var source = new DataSource(
            EcrCode.Create("PIAF"),
            new LocalizedText(new Dictionary<string, string> { ["uk"] = "PIAF" }),
            ExternalTransport.PiSqlClient,
            endpoint,
            "PiAf.Primary");

        store.FindDataSourceAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(source);

        return store;
    }
}
