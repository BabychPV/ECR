// tests/Ecr.Api.Tests/CellSourceFoldApiTests.cs
using System.Globalization;
using System.Net;
using System.Text.Json;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.External;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static Ecr.Api.Tests.PiRawDataStand;

namespace Ecr.Api.Tests;

/// <summary>
/// <c>D-463</c> (після <c>D-462</c>): вікно «Звідки це значення» рахує ТЕ САМЕ правило, що й матеріалізація
/// (<c>Period.FoldBounds</c>): колонка місяця річного періоду — лише свій місяць, звичайна — увесь рік; «значення
/// за правилом згортки» — тим самим кодом (<c>CollectedFold</c>), поруч зі значенням комірки з її журналу змін.
/// </summary>
/// <remarks>
/// Мутації (локально): (1) у <c>CellSourceHandler</c> передати <c>monthNumber: null</c> — червоніє «M3 = березень»;
/// (2) у <c>Compare</c> не дивитися на походження — червоніє «ручна правка».
/// </remarks>
[Collection("SqlServer")]
public sealed class CellSourceFoldApiTests(SqlServerFixture sql)
{
    /// <summary>Річний період 2026 у поясі Asia/Atyrau (+05:00).</summary>
    private static readonly DateTime YearFrom = new(2025, 12, 31, 19, 0, 0, DateTimeKind.Utc);

    private static readonly DateTime YearTo = new(2026, 12, 31, 19, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime MarchFrom = new(2026, 2, 28, 19, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime MarchTo = new(2026, 3, 31, 19, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-463")]
    public async Task Річний_період_колонка_M3_бачить_лише_березень_звичайна_колонка_увесь_рік()
    {
        // 3 звичайні колонки + M1..M3; мапінг — на M3 (індекс 5).
        var s = await ArrangeAsync(sql, columnCount: 3, monthColumns: 3, mappedColumn: 5);
        await MakeAnnualAsync(s);

        // Звичайна колонка C2 — другий тег тієї ж сутності.
        var plainField = s.Field + "_plain";
        await AddMapAsync(s.EntityId, plainField, s.Doc.ColumnDefIds[1], s.RowKey);

        // Щодня о 00:00Z весь 2026 рік, значення = номер дня (0..364); той самий ряд для обох тегів.
        var day0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        await InsertPointsAsync(sql, s.EntityId, s.Field, s.RunId, day0, TimeSpan.FromDays(1), 365);
        await InsertPointsAsync(sql, s.EntityId, plainField, s.RunId, day0, TimeSpan.FromDays(1), 365);

        using var app = new EcrApiFactory(sql);
        using var client = await SignInAsync(app, await UserAsync(sql, ["Document.View"], [s.Doc.ProjectId]));

        // M3: вікно — березень (у поясі проєкту), точки — лише березня (1..31 березня = дні 59..89).
        var m3 = await GetAsync(client, s.CellSourceUrl());
        Assert.Equal(MarchFrom, m3.GetProperty("periodFromUtc").GetDateTime().ToUniversalTime());
        Assert.Equal(MarchTo, m3.GetProperty("periodToUtc").GetDateTime().ToUniversalTime());
        Assert.Equal(3, m3.GetProperty("windowMonth").GetInt32());
        var m3Source = m3.GetProperty("sources")[0];
        Assert.Equal(31, m3Source.GetProperty("pointCount").GetInt64());

        var m3Points = (await GetAsync(client, PointsUrl(s, s.CellSourceUrl(), m3Source.GetProperty("mapId").GetInt32())))
            .GetProperty("items").EnumerateArray().Select(p => p.GetProperty("timestamp").GetDateTime().ToUniversalTime()).ToList();
        Assert.Equal(31, m3Points.Count);
        Assert.All(m3Points, t => Assert.Equal(3, t.Month));

        // Значення за правилом (Sum) — тим самим кодом, що матеріалізація: 59 + … + 89 = 2294; записів у комірці немає.
        var m3Fold = m3Source.GetProperty("fold");
        Assert.Equal("Value", m3Fold.GetProperty("state").GetString());
        Assert.Equal(2294m, Dec(m3Fold.GetProperty("value")));
        Assert.Equal("NotWritten", m3Fold.GetProperty("comparison").GetString());

        // Звичайна колонка: увесь рік, 365 точок; сума 0..364 = 66430.
        var plainUrl = s.CellSourceUrl(columnId: s.Doc.ColumnDefIds[1]);
        var plain = await GetAsync(client, plainUrl);
        Assert.Equal(YearFrom, plain.GetProperty("periodFromUtc").GetDateTime().ToUniversalTime());
        Assert.Equal(YearTo, plain.GetProperty("periodToUtc").GetDateTime().ToUniversalTime());
        Assert.Equal(JsonValueKind.Null, plain.GetProperty("windowMonth").ValueKind);
        var plainSource = plain.GetProperty("sources")[0];
        Assert.Equal(365, plainSource.GetProperty("pointCount").GetInt64());
        Assert.Equal(66430m, Dec(plainSource.GetProperty("fold").GetProperty("value")));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-463")]
    public async Task Значення_за_правилом_поруч_із_коміркою_збіг_ручна_правка_не_оновлено()
    {
        var s = await ArrangeAsync(sql);
        await InsertPointsAsync(sql, s.EntityId, s.Field, s.RunId, PeriodFromUtc, TimeSpan.FromHours(1), 10); // 0..9 → 45

        using var app = new EcrApiFactory(sql);
        using var client = await SignInAsync(app, await UserAsync(sql, ["Document.View"], [s.Doc.ProjectId]));

        // Інтеграція записала 45 — збіг.
        await WriteAuditAsync(s, "Integration", "45", PeriodFromUtc.AddDays(1));
        Assert.Equal("Match", await ComparisonAsync(client, s));

        // Людина пізніше поставила 50 — ручна правка.
        await WriteAuditAsync(s, "UserEdit", "50", PeriodFromUtc.AddDays(2));
        Assert.Equal("ManualEdit", await ComparisonAsync(client, s));

        // Інтеграція записала 45, а потім прийшла нова точка (-1.5: сума вже 43.5) — не оновлено.
        await WriteAuditAsync(s, "Integration", "45", PeriodFromUtc.AddDays(3));
        await InsertPointsAsync(sql, s.EntityId, s.Field, s.RunId, PeriodFromUtc.AddDays(5), TimeSpan.FromHours(1), 1, _ => null);
        var fold = (await GetAsync(client, s.CellSourceUrl())).GetProperty("sources")[0].GetProperty("fold");
        Assert.Equal("NotUpdated", fold.GetProperty("comparison").GetString());
        Assert.Equal("45", fold.GetProperty("storedValue").GetString());
        Assert.Equal("Integration", fold.GetProperty("storedOrigin").GetString());
    }

    private async Task<string?> ComparisonAsync(HttpClient client, Stand s)
        => (await GetAsync(client, s.CellSourceUrl())).GetProperty("sources")[0].GetProperty("fold").GetProperty("comparison").GetString();

    private static async Task<JsonElement> GetAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(new Uri(url, UriKind.Relative));
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{url}: {response.StatusCode} {body}");
        return JsonDocument.Parse(body).RootElement;
    }

    private static string PointsUrl(Stand s, string summaryUrl, int mapId)
        => summaryUrl.Replace("/cell-source?", "/cell-source/points?", StringComparison.Ordinal)
           + $"&mapId={mapId.ToString(CultureInfo.InvariantCulture)}&limit=500";

    private static decimal Dec(JsonElement e)
        => e.ValueKind == JsonValueKind.String ? decimal.Parse(e.GetString()!, CultureInfo.InvariantCulture) : e.GetDecimal();

    /// <summary>Період документа — увесь 2026 рік (річний період PI).</summary>
    private async Task MakeAnnualAsync(Stand s)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE p SET PeriodStart = '2026-01-01', PeriodEnd = '2026-12-31'
            FROM doc.Period AS p JOIN doc.Document AS d ON d.ProjectId = p.ProjectId
            WHERE d.Id = @d AND p.PeriodKey = @k;
            """;
        command.Parameters.AddWithValue("@d", s.Doc.DocumentId);
        command.Parameters.AddWithValue("@k", s.Doc.PeriodKey.Value);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private async Task AddMapAsync(int entityId, string field, int columnId, string rowKey)
    {
        await using var db = new EcrDbContext(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);
        var map = EntityFieldMap.ToColumn(entityId, field, columnId);
        map.SetMaterialization(rowKey, AggregationKind.Sum);
        db.EntityFieldMaps.Add(map);
        await db.SaveChangesAsync();
    }

    /// <summary>Запис журналу змін комірки (рядок 1, колонка мапінгу) — як його пише патчер.</summary>
    private async Task WriteAuditAsync(Stand s, string origin, string value, DateTime at)
    {
        await using var db = new EcrDbContext(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);
        await new AuditWriter(db).WriteCellChangesAsync(
            [
                new CellChangeRecord(
                    at,
                    new CellAddress(s.Doc.PeriodKey, s.Doc.RowIds[0], s.MappedColumnId),
                    s.Doc.DocumentId,
                    s.RowKey,
                    OldValue: null,
                    NewValue: value,
                    ChangedByUserId: 1,
                    Origin: origin,
                    IsLateEdit: false,
                    CorrelationId: null),
            ],
            CancellationToken.None);
    }
}
