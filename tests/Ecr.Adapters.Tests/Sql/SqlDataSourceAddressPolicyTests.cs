using Ecr.Adapters.Sql;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using NSubstitute;
using Xunit;

namespace Ecr.Adapters.Tests.Sql;

/// <summary>
/// L3-05 (<c>D-279</c>): адаптер Sql-джерела перевіряє адресу ще раз перед з'єднанням — і для джерел,
/// збережених до появи політики, і для імені, що розв'язалося інакше.
/// </summary>
/// <remarks>
/// ⚠ Без бази: відмова мусить настати ДО спроби з'єднання. Без фіксу ці тести йдуть у
/// <c>connection.OpenAsync</c> на 169.254.169.254 і падають іншим кодом (<c>connectFailed</c>).
/// </remarks>
public sealed class SqlDataSourceAddressPolicyTests
{
    private static readonly DateTime Midnight = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("Server=169.254.169.254;Integrated Security=true;Connect Timeout=1")]
    [InlineData("Data Source=tcp:169.254.169.254,1433;Connect Timeout=1")]
    [InlineData("Address=2852039166;Connect Timeout=1")]
    [InlineData("Server=127.0.0.1,1;Failover Partner=169.254.169.254;Connect Timeout=1")]
    [InlineData("Server=metadata.google.internal;Connect Timeout=1")]
    [InlineData("Server=127.0.0.1,1;AttachDBFilename=\\\\attacker\\share\\x.mdf;Connect Timeout=1")]
    [InlineData("Server=127.0.0.1,1;User Instance=true;Connect Timeout=1")]
    public async Task Заборонена_адреса_відмова_до_з_єднання(string endpoint)
    {
        var adapter = new SqlDataSource(StoreWith(endpoint), Substitute.For<ISecretProvider>(), Settings());

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => adapter.ReadAsync(
                new CollectionRequest(1, 7, "STREAM-1", Midnight, Midnight.AddHours(1), 10),
                CancellationToken.None));

        Assert.Equal("ECR-INT-0503", error.ErrorCode);
        Assert.Equal("err.ECR-INT-0503.endpointForbidden", error.Details!["messageKey"]);
        Assert.Equal("FLERT", error.Details!["dataSource"]);
        Assert.DoesNotContain("169.254", error.Message, StringComparison.Ordinal);
    }

    private static ICollectionStore StoreWith(string endpoint)
    {
        var store = Substitute.For<ICollectionStore>();

        var source = new DataSource(
            EcrCode.Create("FLERT"),
            new LocalizedText(new Dictionary<string, string> { ["uk"] = "FLERT" }),
            ExternalTransport.Sql,
            endpoint,
            "Flert.Primary");

        store.FindDataSourceAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(source);

        return store;
    }

    private static ISecretProvider Settings()
    {
        var provider = Substitute.For<ISecretProvider>();
        provider.Find(Arg.Any<string>()).Returns(call => call.ArgAt<string>(0) == SqlDataSource.ValueQueryKey
            ? "SELECT CAST(NULL AS datetime2) AS Ts, CAST(NULL AS decimal(18,6)) AS Val, CAST(NULL AS nvarchar(16)) AS Uom"
            : null);

        return provider;
    }
}
