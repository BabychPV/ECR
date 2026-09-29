using System.Data;
using Ecr.Adapters.PiAf;
using Ecr.Adapters.Sql;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Adapters.Tests.PiAf;

/// <summary>
/// Події джерела (HSE301 F4e, §4.7): типова відмова порту та читання PI SQL Client.
/// </summary>
/// <remarks>
/// ⚠ Живого RTQP у контурі розробки немає: відмова «не налаштовано» перевіряється
/// до з'єднання, а читання рядків — через <see cref="PiSqlClientDataSource.ReadEventRowsAsync"/>
/// на <see cref="DataTableReader"/> у формі уявлення (довга форма, рядок на атрибут).
/// </remarks>
public sealed class PiSqlClientEventTests
{
    private static readonly DateTime From = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static readonly DateTime Start = new(2026, 1, 28, 9, 9, 20, DateTimeKind.Utc);

    private static readonly DateTime End = new(2026, 1, 28, 9, 24, 50, DateTimeKind.Utc);

    /// <summary>Рядки у формі уявлення: <c>AttrValue</c> — текст (<c>CAST(Value AS String)</c>).</summary>
    private static DataTable View()
    {
        var table = new DataTable();
        table.Columns.Add("EventId", typeof(Guid));
        table.Columns.Add("EventName", typeof(string));
        table.Columns.Add("Template", typeof(string));
        table.Columns.Add("StartTime", typeof(object));
        table.Columns.Add("EndTime", typeof(object));
        table.Columns.Add("PrimaryElement", typeof(string));
        table.Columns.Add("AttrScope", typeof(string));
        table.Columns.Add("AttrName", typeof(string));
        table.Columns.Add("AttrValue", typeof(string));
        table.Columns.Add("AttrUom", typeof(string));
        return table;
    }

    private static readonly Guid Ef1 = Guid.Parse("11111111-0000-4000-8000-000000000001");

    private static readonly Guid Ef2 = Guid.Parse("22222222-0000-4000-8000-000000000002");

    private static async Task<SourceEventResult> Read(DataTable table, int maxEvents = 10)
    {
        using var reader = table.CreateDataReader();
        return await PiSqlClientDataSource.ReadEventRowsAsync(
            reader, "AF_HP", "FlareEvent", maxEvents, [], CancellationToken.None);
    }

    private static (PiSqlClientDataSource Source, ICollectionStore Store) Adapter(ISecretProvider settings)
    {
        var dataSource = new DataSource(
            EcrCode.Create("AF_HP"),
            new LocalizedText(new Dictionary<string, string> { ["uk"] = "PI AF" }),
            ExternalTransport.PiSqlClient,
            "Driver={PI SQL Client};Server=pi.example",
            "PiAf.Primary");

        var store = Substitute.For<ICollectionStore>();
        store.FindDataSourceAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(dataSource);

        return (new PiSqlClientDataSource(store, Substitute.For<ISecretProvider>(), settings), store);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Подія_з_трьома_атрибутами_з_уявлення_це_одна_подія()
    {
        using var table = View();
        table.Rows.Add(Ef1, "HP flare", "FlareEvent", Start, End, @"\\AF\HP", "E", "Category", "V8", null);
        table.Rows.Add(Ef1, "HP flare", "FlareEvent", Start, End, @"\\AF\HP", "E", "Volume", "269.258", "Sm3");
        table.Rows.Add(Ef1, "HP flare", "FlareEvent", Start, End, @"\\AF\HP", "P", "Season", "Winter", null);

        var result = await Read(table);

        var single = Assert.Single(result.Events);
        Assert.False(result.Truncated);
        Assert.Equal(Ef1.ToString(), single.EventId);
        Assert.Equal(("HP flare", Start, End, @"\\AF\HP"), (single.Name, single.StartUtc, single.EndUtc, single.PrimaryElementPath));
        Assert.Equal(["Category", "Volume", "Season"], single.Attributes.Select(a => a.Name));
        Assert.Equal(269.258m, single.Attributes[1].ValueNumeric);
        Assert.Equal("Sm3", single.Attributes[1].SourceUnitSymbol);
        Assert.Equal(SourceEventAttributeScope.PrimaryElement, single.Attributes[2].Scope);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Незакрита_подія_NULL_чи_9999_12_31_має_EndUtc_null(bool sentinel)
    {
        using var table = View();
        table.Rows.Add(
            Ef1, "HP flare", "FlareEvent", Start,
            sentinel ? new DateTime(9999, 12, 31, 23, 59, 59) : (object)DBNull.Value,
            null, null, null, null, null);

        var single = Assert.Single((await Read(table)).Events);

        Assert.Null(single.EndUtc);
        Assert.Empty(single.Attributes);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Час_події_з_поясом_конвертується_в_UTC()
    {
        using var table = View();
        table.Rows.Add(
            Ef1, "HP flare", "FlareEvent",
            new DateTimeOffset(2026, 1, 28, 14, 9, 20, TimeSpan.FromHours(5)),
            new DateTimeOffset(2026, 1, 28, 14, 24, 50, TimeSpan.FromHours(5)),
            null, "E", "Category", "V8", null);

        var single = Assert.Single((await Read(table)).Events);

        Assert.Equal((Start, (DateTime?)End), (single.StartUtc, single.EndUtc));
        Assert.Equal(DateTimeKind.Utc, single.StartUtc.Kind);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Нечисловий_атрибут_текстом_кома_не_число_крапка_число()
    {
        using var table = View();
        table.Rows.Add(Ef1, null, null, Start, End, null, "E", "State", "Active", null);
        table.Rows.Add(Ef1, null, null, Start, End, null, "E", "Comma", "1,5", null);
        table.Rows.Add(Ef1, null, null, Start, End, null, "E", "Dot", "1.5", null);

        var attributes = Assert.Single((await Read(table)).Events).Attributes;

        (decimal?, string?)[] expected = [(null, "Active"), (null, "1,5"), (1.5m, null)];
        Assert.Equal(expected, attributes.Select(a => (a.ValueNumeric, a.ValueString)));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Подій_більше_за_стелю_Truncated()
    {
        using var table = View();
        table.Rows.Add(Ef1, "A", null, Start, End, null, "E", "Category", "V8", null);
        table.Rows.Add(Ef2, "B", null, Start.AddHours(1), End.AddHours(1), null, "E", "Category", "V9", null);

        var cut = await Read(table, maxEvents: 1);
        var full = await Read(table, maxEvents: 2);

        Assert.True(cut.Truncated);
        Assert.Equal([Ef1.ToString()], cut.Events.Select(e => e.EventId));
        Assert.False(full.Truncated);
        Assert.Equal(2, full.Events.Count);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Подія_без_читабельного_StartTime_це_відмова_0422()
    {
        using var table = View();
        table.Rows.Add(Ef1, "A", null, DBNull.Value, End, null, "E", "Category", "V8", null);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(() => Read(table));

        Assert.Equal("ECR-INT-0422", error.ErrorCode);
        Assert.Equal("err.ECR-INT-0422.timestampUnreadable", error.Details!["messageKey"]);
        Assert.Equal($"{Ef1}|StartTime", error.Details["sourcePath"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Без_ключа_EventQuery_відмова_0422_і_чужий_власний_ключ_не_рятує()
    {
        var settings = Substitute.For<ISecretProvider>();
        settings.Find("PiSqlClient:AF_LP:EventQuery").Returns("SELECT 1");
        var (source, store) = Adapter(settings);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(() => source.ReadEventsAsync(
            new SourceEventQuery(1, 7, "FlareEvent", From, From.AddDays(1), []), CancellationToken.None));

        Assert.Equal("ECR-INT-0422", error.ErrorCode);
        Assert.Equal("err.ECR-INT-0422.queryKindNotConfigured", error.Details!["messageKey"]);
        Assert.Equal(("Event", PiSqlClientDataSource.EventQueryKey), (error.Details["queryKind"], error.Details["configKey"]));
        await store.Received(1).FindDataSourceAsync(1, Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Без_ключа_EventTemplateQuery_каталог_відмовляє_0422()
    {
        var (source, _) = Adapter(Substitute.For<ISecretProvider>());

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => source.DiscoverEventTemplatesAsync(1, CancellationToken.None));

        Assert.Equal("err.ECR-INT-0422.queryKindNotConfigured", error.Details!["messageKey"]);
        Assert.Equal("EventTemplate", error.Details["queryKind"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Шаблон_підставляється_літералом_без_ін_єкції()
    {
        Assert.Equal(
            "SELECT … N'O''Tpl''; --' …",
            PiSqlClientDataSource.EventQuery("SELECT … N'{template}' …", "O'Tpl'; --"));

        var error = Assert.Throws<BusinessRuleException>(
            () => PiSqlClientDataSource.EventQuery("N'{template}'", "Flare\nEvent"));
        Assert.Equal("err.ECR-INT-0503.controlCharacterInName", error.Details!["messageKey"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Перевернуте_вікно_відхиляється_до_джерела()
    {
        var (source, store) = Adapter(Substitute.For<ISecretProvider>());

        await Assert.ThrowsAsync<ArgumentException>(() => source.ReadEventsAsync(
            new SourceEventQuery(1, 7, "FlareEvent", From, From, []), CancellationToken.None));
        await store.DidNotReceiveWithAnyArgs().FindDataSourceAsync(default, default);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Каталог_шаблонів_групує_атрибути_за_шаблоном()
    {
        using var table = new DataTable();
        table.Columns.Add("Template", typeof(string));
        table.Columns.Add("AttrScope", typeof(string));
        table.Columns.Add("AttrName", typeof(string));
        table.Columns.Add("AttrUom", typeof(string));
        table.Columns.Add("AttrType", typeof(string));
        table.Rows.Add("FlareEvent", "E", "Category", null, "String");
        table.Rows.Add("Trip", null, null, null, null);
        table.Rows.Add("FlareEvent", "P", "Volume", "Sm3", "Double");

        using var reader = table.CreateDataReader();
        var templates = await PiSqlClientDataSource.ReadEventTemplatesAsync(reader, CancellationToken.None);

        Assert.Equal(["FlareEvent", "Trip"], templates.Select(t => t.TemplateName));
        (string, SourceEventAttributeScope, string?)[] expected =
        [
            ("Category", SourceEventAttributeScope.Event, null),
            ("Volume", SourceEventAttributeScope.PrimaryElement, "Sm3"),
        ];
        Assert.Equal(expected, templates[0].Attributes.Select(a => (a.Name, a.Scope, a.SourceUnitSymbol)));
        Assert.Empty(templates[1].Attributes);
    }

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
