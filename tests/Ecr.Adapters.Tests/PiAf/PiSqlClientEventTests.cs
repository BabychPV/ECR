using System.Data;
using Ecr.Adapters.PiAf;
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

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Ключ_на_джерело_переважає_спільний_і_не_тече_в_інше_джерело()
    {
        const string key = PiSqlClientDataSource.ValueQueryKey;
        var settings = Substitute.For<ISecretProvider>();
        settings.Find("PiSqlClient:ValueQuery").Returns("SHARED");
        settings.Find("PiSqlClient:AF-HP:ValueQuery").Returns("OWN");
        settings.Find("PiSqlClient:AF-LP:ValueQuery").Returns("   ");

        Assert.Equal("PiSqlClient:AF-HP:ValueQuery", PiSqlClientDataSource.ScopedKey("AF-HP", key));
        Assert.Equal("OWN", PiSqlClientDataSource.ConfiguredQuery(settings, "AF-HP", key));

        // Порожній власний ключ — не налаштування; джерело без власного — спільний,
        // тобто точкові запити без власного ключа читаються як до F4e.
        Assert.Equal("SHARED", PiSqlClientDataSource.ConfiguredQuery(settings, "AF-LP", key));
        Assert.Equal("SHARED", PiSqlClientDataSource.ConfiguredQuery(settings, "AF-X", key));
        Assert.Null(PiSqlClientDataSource.ConfiguredQuery(settings, "AF-HP", PiSqlClientDataSource.CatalogQueryKey));
        Assert.Null(PiSqlClientDataSource.ConfiguredQuery(null, "AF-HP", key));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Ts_з_поясом_конвертується_в_UTC_без_поясу_вважається_UTC()
    {
        using var table = new DataTable();
        table.Columns.Add("Ts", typeof(object));
        table.Columns.Add("Val", typeof(double));
        table.Rows.Add(new DateTimeOffset(2026, 1, 28, 14, 9, 20, TimeSpan.FromHours(5)), 1d);
        table.Rows.Add(new DateTime(2026, 1, 28, 9, 10, 0, DateTimeKind.Unspecified), 2d);

        using var reader = table.CreateDataReader();
        var points = await PiSqlClientDataSource.ReadSourcePointsAsync(reader, "AF-HP", "EL|Flow", 100, CancellationToken.None);

        Assert.Equal(
            [new DateTime(2026, 1, 28, 9, 9, 20, DateTimeKind.Utc), new DateTime(2026, 1, 28, 9, 10, 0, DateTimeKind.Utc)],
            points.Select(p => p.Timestamp));
        Assert.All(points, p => Assert.Equal(DateTimeKind.Utc, p.Timestamp.Kind));
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [InlineData(null, "NULL")]
    [InlineData("2026-01-28 09:10", "String")]
    public async Task Рядок_без_читабельного_Ts_це_відмова_0422_а_не_пропуск(string? ts, string valueType)
    {
        using var table = new DataTable();
        table.Columns.Add("Ts", typeof(object));
        table.Columns.Add("Val", typeof(double));
        table.Rows.Add(From, 1d);
        table.Rows.Add((object?)ts ?? DBNull.Value, 2d);
        table.Rows.Add(From.AddMinutes(2), 3d);

        using var reader = table.CreateDataReader();
        var error = await Assert.ThrowsAsync<BusinessRuleException>(() => PiSqlClientDataSource.ReadSourcePointsAsync(
            reader, "AF-HP", "EL|Flow", 100, CancellationToken.None));

        // ⛔ До F4e рядок мовчки відкидався — дві точки з трьох і покриття «зібрано повністю».
        Assert.Equal("ECR-INT-0422", error.ErrorCode);
        Assert.Equal("err.ECR-INT-0422.timestampUnreadable", error.Details!["messageKey"]);
        Assert.Equal(("AF-HP", "EL|Flow", valueType), (error.Details["dataSource"], error.Details["sourcePath"], error.Details["valueType"]));
    }
}
