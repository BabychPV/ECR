using System.Data;
using Ecr.Adapters.PiAf;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Sources;
using Ecr.Domain.Entities.External;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Adapters.Tests.PiAf;

/// <summary>
/// PI SQL Client: інтерпольований і summary-запит без типового тексту, якість
/// точок (HSE301 F4, §4.3, §4.6, <c>D-172</c>).
/// </summary>
/// <remarks>
/// ⚠ Живого RTQP у контурі розробки немає. Тому відмова «не налаштовано»
/// перевіряється до з'єднання (так і задумано: джерело не чіпається), а читання
/// рядків — через <see cref="PiSqlClientDataSource.ReadPointsAsync"/> на
/// <see cref="DataTableReader"/>: той самий <c>DbDataReader</c>, що й ODBC.
/// </remarks>
public sealed class PiSqlClientWindowTests
{
    private static readonly DateTime From = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

    private static readonly string[] ExpectedQuality = ["Good", "Bad", "Good"];

    private static readonly bool[] ExpectedIsGood = [true, false, true];

    private static CollectionRequest Interpolated(TimeSpan? step)
        => new(1, 7, "EL|Flow", From, From.AddHours(1), 100, SourceQueryKind.Interpolated, step);

    private static (PiSqlClientDataSource Source, ICollectionStore Store) Adapter(string? interpolatedQuery = null)
    {
        var store = Substitute.For<ICollectionStore>();
        store.FindDataSourceAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns((DataSource?)null);

        var settings = Substitute.For<ISecretProvider>();
        settings.Find(PiSqlClientDataSource.InterpolatedQueryKey).Returns(interpolatedQuery);

        return (new PiSqlClientDataSource(store, Substitute.For<ISecretProvider>(), settings), store);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Інтерпольований_без_ключа_відмовляє_0422_з_ключем_каталогу_і_не_чіпає_джерело()
    {
        // Запис джерела читається (ключ джерела, рішення людини 2026-09-29), з'єднання — ні:
        // рядок з'єднання не розбирається, і дійди адаптер до нього — була б 0503, не 0422.
        var (source, store) = Adapter();
        store.FindDataSourceAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(PiSqlClientSourceKeyTests.Rtqp("PIAF"));

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => source.ReadAsync(Interpolated(TimeSpan.FromMinutes(1)), CancellationToken.None));

        Assert.Equal("ECR-INT-0422", error.ErrorCode);
        Assert.Equal("err.ECR-INT-0422.queryKindNotConfigured", error.Details!["messageKey"]);
        Assert.Equal("Interpolated", error.Details["queryKind"]);
        Assert.Equal(PiSqlClientDataSource.InterpolatedQueryKey, error.Details["sharedConfigKey"]);
        Assert.Equal("PiSqlClient:PIAF:InterpolatedQuery", error.Details["sourceConfigKey"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Інтерпольований_із_ключем_іде_до_джерела_а_не_відмовляє()
    {
        // Контраст до попереднього: з налаштованим текстом відмови конфігурації
        // немає — далі звична відмова «джерела немає» (підставне сховище).
        var (source, store) = Adapter("SELECT 1 -- {template} {attribute}");

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => source.ReadAsync(Interpolated(TimeSpan.FromMinutes(1)), CancellationToken.None));

        Assert.Equal("ECR-INT-0503", error.ErrorCode);
        await store.Received(1).FindDataSourceAsync(1, Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Інтерпольований_без_кроку_відхиляється()
    {
        var (source, _) = Adapter("SELECT 1");

        await Assert.ThrowsAsync<ArgumentException>(
            () => source.ReadAsync(Interpolated(null), CancellationToken.None));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Сирий_запит_без_ключа_інтерпольованого_працює_як_досі()
    {
        var (source, store) = Adapter();

        var error = await Assert.ThrowsAsync<BusinessRuleException>(() => source.ReadAsync(
            new CollectionRequest(1, 7, "EL|Flow", From, From.AddHours(1), 100), CancellationToken.None));

        Assert.Equal("ECR-INT-0503", error.ErrorCode);
        await store.Received(1).FindDataSourceAsync(1, Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Якість_береться_з_колонки_Quality_і_Bad_не_проходить_у_згортку()
    {
        using var table = new DataTable();
        table.Columns.Add("Ts", typeof(DateTime));
        table.Columns.Add("Val", typeof(double));
        table.Columns.Add("Uom", typeof(string));
        table.Columns.Add("Quality", typeof(string));
        table.Rows.Add(From, 10.5d, "Sm3/h", "Good");
        table.Rows.Add(From.AddMinutes(1), 999d, "Sm3/h", "Bad");
        table.Rows.Add(From.AddMinutes(2), 11d, "Sm3/h", DBNull.Value);

        using var reader = table.CreateDataReader();
        var points = await PiSqlClientDataSource.ReadPointsAsync(reader, "EL|Flow", 100, CancellationToken.None);

        // ⛔ До F4 тут стояло завжди "Good": точка з Bad лягала в згортку як придатна.
        Assert.Equal(ExpectedQuality, points.Select(p => p.Quality));
        Assert.Equal(ExpectedIsGood, points.Select(WindowFold.IsGood));
        Assert.Equal(10.5m, points[0].ValueNumeric);
        Assert.Equal(DateTimeKind.Utc, points[0].Timestamp.Kind);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public async Task Без_колонки_Quality_точка_Good_як_у_SqlDataSource()
    {
        using var table = new DataTable();
        table.Columns.Add("Val", typeof(string));
        table.Columns.Add("Ts", typeof(DateTime));
        table.Rows.Add("I/O Timeout", From);

        using var reader = table.CreateDataReader();
        var point = Assert.Single(
            await PiSqlClientDataSource.ReadPointsAsync(reader, "EL|Flow", 100, CancellationToken.None));

        // Системний стан — текстом, не числом (§4.6); згортка його однаково не бере.
        Assert.Equal("Good", point.Quality);
        Assert.Equal("I/O Timeout", point.ValueString);
        Assert.Null(point.SourceUnitSymbol);
        Assert.False(WindowFold.IsGood(point));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Summary_запит_підставляє_літерали_й_ім_я_типу_PI()
    {
        var text = PiSqlClientDataSource.SummaryQuery(
            "SUMMARY N'{template}' N'{attribute}' {summary}", "O'Tpl", "Flow", SourceSummaryKind.Total);

        Assert.Equal("SUMMARY N'O''Tpl' N'Flow' Total", text);
        Assert.Throws<ArgumentOutOfRangeException>(() => PiSqlClientDataSource.SummaryQuery(
            "{summary}", "T", "A", (SourceSummaryKind)99));
    }
}
