using Ecr.Adapters.Sql;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Adapters.Tests.PiAf;

/// <summary>
/// Події джерела (HSE301 F4e, §4.7): типова відмова порту та читання PI SQL Client.
/// </summary>
public sealed class PiSqlClientEventTests
{
    private static readonly DateTime From = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Транспорт_без_подій_відмовляє_0422_а_не_повертає_порожній_список()
    {
        var store = Substitute.For<ICollectionStore>();
        IExternalDataSource adapter = new SqlDataSource(store, Substitute.For<ISecretProvider>());

        var events = await Assert.ThrowsAsync<BusinessRuleException>(() => adapter.ReadEventsAsync(
            new SourceEventQuery(1, 7, "FlareEvent", From, From.AddDays(1), []), CancellationToken.None));
        var templates = await Assert.ThrowsAsync<BusinessRuleException>(
            () => adapter.DiscoverEventTemplatesAsync(1, CancellationToken.None));

        Assert.Equal(
            ("ECR-INT-0422", "err.ECR-INT-0422.queryKindNotSupported", "Event", "Sql"),
            (events.ErrorCode, events.Details!["messageKey"], events.Details["queryKind"], events.Details["transport"]));
        Assert.Equal(
            ("ECR-INT-0422", "err.ECR-INT-0422.queryKindNotSupported", "EventTemplate", "Sql"),
            (templates.ErrorCode, templates.Details!["messageKey"], templates.Details["queryKind"], templates.Details["transport"]));
        await store.DidNotReceiveWithAnyArgs().FindDataSourceAsync(default, default);
    }
}
