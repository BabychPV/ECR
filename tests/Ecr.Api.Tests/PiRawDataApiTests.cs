// tests/Ecr.Api.Tests/PiRawDataApiTests.cs
using System.Globalization;
using System.Net;
using System.Text.Json;
using Ecr.TestKit;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Xunit;
using static Ecr.Api.Tests.PiRawDataStand;

namespace Ecr.Api.Tests;

/// <summary>
/// <c>D-463</c> «Дані з PI»: <c>/api/v1/sources/{id}/raw-data/*</c> — право, межі запиту, keyset-сторінки,
/// згортка графіка, покриття, CSV (екранування, стеля, подія безпеки).
/// </summary>
[Collection("SqlServer")]
public sealed class PiRawDataApiTests(SqlServerFixture sql)
{
    private static readonly DateTime From = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-463")]
    public async Task Право_View_або_Manage_відкриває_дані_без_права_403_анонім_401()
    {
        var s = await ArrangeAsync(sql);
        await InsertPointsAsync(sql, s.EntityId, s.Field, s.RunId, From, TimeSpan.FromHours(1), 5);

        using var app = new EcrApiFactory(sql);
        var url = $"/api/v1/sources/{s.EntityId}/raw-data/points?path={Escape(s.Field)}&fromUtc={Iso(From)}&toUtc={Iso(From.AddDays(1))}";

        using (var anonymous = app.CreateClient())
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(new Uri(url, UriKind.Relative))).StatusCode);
        }

        foreach (var (permissions, expected) in new (string[], HttpStatusCode)[]
                 {
                     ([], HttpStatusCode.Forbidden),
                     (["Document.View"], HttpStatusCode.Forbidden),
                     (["Integration.View"], HttpStatusCode.OK),
                     (["Integration.Manage"], HttpStatusCode.OK),
                 })
        {
            using var client = await SignInAsync(app, await UserAsync(sql, permissions));
            foreach (var path in new[] { "tags", $"points?path={Escape(s.Field)}", $"series?path={Escape(s.Field)}", "coverage", $"export.csv?path={Escape(s.Field)}" })
            {
                var response = await client.GetAsync(new Uri($"/api/v1/sources/{s.EntityId}/raw-data/{path}", UriKind.Relative));
                Assert.True(
                    response.StatusCode == expected,
                    $"[{string.Join(",", permissions)}] {path}: {response.StatusCode} {await response.Content.ReadAsStringAsync()}\n{app.ErrorsText}");
            }
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-463")]
    public async Task Межі_запиту_422_з_ключем_відсутня_сутність_404()
    {
        var s = await ArrangeAsync(sql);
        using var app = new EcrApiFactory(sql);
        using var client = await SignInAsync(app, await UserAsync(sql, ["Integration.View"]));
        var path = Escape(s.Field);
        var baseUrl = $"/api/v1/sources/{s.EntityId}/raw-data";

        foreach (var (url, key) in new[]
                 {
                     ($"{baseUrl}/points?path={path}&fromUtc={Iso(From.AddDays(1))}&toUtc={Iso(From)}", "err.ECR-REQ-0422.rawDataRange"),
                     ($"{baseUrl}/points?path={path}&fromUtc={Iso(From.AddDays(-401))}&toUtc={Iso(From)}", "err.ECR-REQ-0422.rawDataRangeTooLong"),
                     ($"{baseUrl}/points?path=", "err.ECR-REQ-0422.rawDataPath"),
                     ($"{baseUrl}/points?path={new string('x', 401)}", "err.ECR-REQ-0422.rawDataPath"),
                     ($"{baseUrl}/points?path={path}&limit=501", "err.ECR-REQ-0422.pageSizeOutOfRange"),
                     ($"{baseUrl}/series?path={path}&buckets=2001", "err.ECR-REQ-0422.rawDataBuckets"),
                     ($"{baseUrl}/coverage?fromUtc={Iso(From)}&toUtc={Iso(From)}", "err.ECR-REQ-0422.rawDataRange"),
                     ($"{baseUrl}/export.csv?path={path}&fromUtc={Iso(From.AddDays(-500))}&toUtc={Iso(From)}", "err.ECR-REQ-0422.rawDataRangeTooLong"),
                 })
        {
            var response = await client.GetAsync(new Uri(url, UriKind.Relative));
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == HttpStatusCode.UnprocessableEntity, $"{url}: {response.StatusCode} {body}");
            Assert.Equal(key, JsonDocument.Parse(body).RootElement.GetProperty("messageKey").GetString());
        }

        var missing = await client.GetAsync(new Uri("/api/v1/sources/2000000000/raw-data/tags", UriKind.Relative));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("err.ECR-INT-0404.sourceEntity", JsonDocument.Parse(await missing.Content.ReadAsStringAsync()).RootElement.GetProperty("messageKey").GetString());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-463")]
    public async Task Сторінки_keyset_новіші_першими_без_дублів_і_лише_свого_ряду()
    {
        var s = await ArrangeAsync(sql);
        await InsertPointsAsync(sql, s.EntityId, s.Field, s.RunId, From, TimeSpan.FromHours(1), 250);

        // Шум: інший тег тієї ж сутності й та сама година — у відповідь не потрапляє.
        await InsertPointsAsync(sql, s.EntityId, s.Field + "_other", s.RunId, From, TimeSpan.FromHours(1), 50);

        using var app = new EcrApiFactory(sql);
        using var client = await SignInAsync(app, await UserAsync(sql, ["Integration.View"]));

        var seen = new List<DateTime>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var url = $"/api/v1/sources/{s.EntityId}/raw-data/points?path={Escape(s.Field)}&fromUtc={Iso(From)}&toUtc={Iso(From.AddDays(30))}&limit=100"
                      + (cursor is null ? string.Empty : $"&cursor={Escape(cursor)}");
            var root = JsonDocument.Parse(await client.GetStringAsync(new Uri(url, UriKind.Relative))).RootElement;
            var items = root.GetProperty("items").EnumerateArray().ToList();
            Assert.All(items, i => Assert.Equal(s.RunId, i.GetProperty("collectionRunId").GetInt64()));
            seen.AddRange(items.Select(i => i.GetProperty("timestamp").GetDateTime().ToUniversalTime()));
            cursor = root.GetProperty("nextCursor").ValueKind == JsonValueKind.Null ? null : root.GetProperty("nextCursor").GetString();
            pages++;
        }
        while (cursor is not null && pages < 10);

        Assert.Equal(3, pages);
        Assert.Equal(250, seen.Count);
        Assert.Equal(250, seen.Distinct().Count());
        Assert.Equal(seen.OrderByDescending(t => t).ToList(), seen);
        Assert.Equal(From.AddHours(249), seen[0]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-463")]
    public async Task Графік_згортає_рік_у_не_більше_кошиків_ніж_просили_і_нічого_не_губить()
    {
        var s = await ArrangeAsync(sql);

        // Рік щогодини = 8760 точок; кошиків — 50.
        await InsertPointsAsync(sql, s.EntityId, s.Field, s.RunId, From, TimeSpan.FromHours(1), 8760);

        using var app = new EcrApiFactory(sql);
        using var client = await SignInAsync(app, await UserAsync(sql, ["Integration.Manage"]));

        var url = $"/api/v1/sources/{s.EntityId}/raw-data/series?path={Escape(s.Field)}&fromUtc={Iso(From)}&toUtc={Iso(From.AddDays(365))}&buckets=50";
        var root = JsonDocument.Parse(await client.GetStringAsync(new Uri(url, UriKind.Relative))).RootElement;
        var buckets = root.GetProperty("buckets").EnumerateArray().ToList();

        Assert.InRange(buckets.Count, 1, 50);
        Assert.Equal(8760, root.GetProperty("totalPoints").GetInt64());
        Assert.Equal(8760, buckets.Sum(b => b.GetProperty("points").GetInt64()));
        Assert.Equal(0m, Dec(buckets[0].GetProperty("min")));
        Assert.Equal(8759m, Dec(buckets[^1].GetProperty("max")));
        Assert.Equal((long)Math.Ceiling(365d * 86400 / 50), root.GetProperty("bucketSeconds").GetInt64());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-463")]
    public async Task Покриття_зливає_інтервали_і_називає_прогалини_вікна()
    {
        var s = await ArrangeAsync(sql);
        await AddCoveredAsync(sql, s.EntityId, s.RunId, From, From.AddDays(2));
        await AddCoveredAsync(sql, s.EntityId, s.RunId, From.AddDays(1), From.AddDays(3));
        await AddCoveredAsync(sql, s.EntityId, s.RunId, From.AddDays(5), From.AddDays(6));

        using var app = new EcrApiFactory(sql);
        using var client = await SignInAsync(app, await UserAsync(sql, ["Integration.View"]));

        var root = JsonDocument.Parse(await client.GetStringAsync(new Uri(
            $"/api/v1/sources/{s.EntityId}/raw-data/coverage?fromUtc={Iso(From)}&toUtc={Iso(From.AddDays(7))}", UriKind.Relative))).RootElement;

        var covered = root.GetProperty("covered").EnumerateArray()
            .Select(i => (i.GetProperty("fromUtc").GetDateTime().ToUniversalTime(), i.GetProperty("toUtc").GetDateTime().ToUniversalTime())).ToList();
        var gaps = root.GetProperty("gaps").EnumerateArray()
            .Select(i => (i.GetProperty("fromUtc").GetDateTime().ToUniversalTime(), i.GetProperty("toUtc").GetDateTime().ToUniversalTime())).ToList();

        Assert.Equal([(From, From.AddDays(3)), (From.AddDays(5), From.AddDays(6))], covered);
        Assert.Equal([(From.AddDays(3), From.AddDays(5)), (From.AddDays(6), From.AddDays(7))], gaps);
        Assert.False(root.GetProperty("isTruncated").GetBoolean());
        Assert.Equal(s.RunId, root.GetProperty("lastCollection").GetProperty("lastRunId").GetInt64());
        Assert.Equal("Succeeded", root.GetProperty("lastCollection").GetProperty("lastRunStatus").GetString());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-463")]
    public async Task CSV_нейтралізує_формули_тексту_не_псує_від_ємних_чисел_і_пише_подію_безпеки()
    {
        var s = await ArrangeAsync(sql);
        await InsertPointsAsync(
            sql, s.EntityId, s.Field, s.RunId, From, TimeSpan.FromMinutes(1), 4,
            i => i switch { 0 => "=HYPERLINK(\"http://evil\")", 1 => null, 2 => "+1,\"q\"", _ => null });

        using var app = new EcrApiFactory(sql);
        var userName = await UserAsync(sql, ["Integration.View"]);
        using var client = await SignInAsync(app, userName);

        var response = await client.GetAsync(new Uri(
            $"/api/v1/sources/{s.EntityId}/raw-data/export.csv?path={Escape(s.Field)}&fromUtc={Iso(From)}&toUtc={Iso(From.AddHours(1))}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType?.MediaType);

        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.True(bytes is [0xEF, 0xBB, 0xBF, ..], "UTF-8 з BOM");
        var lines = System.Text.Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3)
            .Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal("timestampUtc,valueNumeric,valueString,unit,quality,retrievedAtUtc,collectionRunId", lines[0]);
        Assert.Equal(5, lines.Length);

        // Рядок 1 (i=0): текст-формула — з апострофом і в лапках (кома/лапки всередині).
        Assert.Contains(",\"'=HYPERLINK(\"\"http://evil\"\")\",", lines[1], StringComparison.Ordinal);

        // Рядок 2 (i=1): від'ємне число — без апострофа (число, а не текст), якість «@…» — з апострофом.
        Assert.Contains(",-3.0000000000000000,", lines[2], StringComparison.Ordinal);
        Assert.Contains(",'@Questionable,", lines[2], StringComparison.Ordinal);

        // Рядок 3 (i=2): «+1,"q"» — апостроф і лапки.
        Assert.Contains(",\"'+1,\"\"q\"\"\",", lines[3], StringComparison.Ordinal);

        var details = await ExportEventAsync(userName);
        Assert.NotNull(details);
        var root = JsonDocument.Parse(details!).RootElement;
        Assert.Equal(4, root.GetProperty("rows").GetInt32());
        Assert.Equal(s.EntityId, root.GetProperty("sourceEntityId").GetInt32());
        Assert.DoesNotContain("HYPERLINK", details, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-463")]
    public async Task CSV_понад_стелю_422_до_першого_байта_і_без_події()
    {
        var s = await ArrangeAsync(sql);
        await InsertPointsAsync(sql, s.EntityId, s.Field, s.RunId, From, TimeSpan.FromMinutes(1), 3);

        using var app = new EcrApiFactory(sql);
        using var limited = app.WithWebHostBuilder(b => b.UseSetting("Integration:RawDataExportMaxRows", "2"));
        var userName = await UserAsync(sql, ["Integration.View"]);
        using var client = await SignInAsync(limited, userName);

        var response = await client.GetAsync(new Uri(
            $"/api/v1/sources/{s.EntityId}/raw-data/export.csv?path={Escape(s.Field)}&fromUtc={Iso(From)}&toUtc={Iso(From.AddHours(1))}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("err.ECR-REQ-0422.auditExportTooLarge",
            JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("messageKey").GetString());
        Assert.Null(await ExportEventAsync(userName));
    }

    /// <summary>Десяткове число відповіді: сервер віддає <c>decimal</c> рядком (точність понад double).</summary>
    private static decimal Dec(JsonElement e)
        => e.ValueKind == JsonValueKind.String ? decimal.Parse(e.GetString()!, CultureInfo.InvariantCulture) : e.GetDecimal();

    /// <summary><c>DetailsJson</c> події експорту, яку записав цей користувач; <c>null</c> — немає.</summary>
    private async Task<string?> ExportEventAsync(string userName)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT TOP 1 e.DetailsJson FROM aud.SecurityEvent AS e
            JOIN sec.[User] AS u ON u.Id = e.ChangedByUserId
            WHERE e.EventType = N'PiRawDataExported' AND u.UserName = @u ORDER BY e.Id DESC;
            """;
        command.Parameters.AddWithValue("@u", userName);
        return await command.ExecuteScalarAsync() as string;
    }

}
