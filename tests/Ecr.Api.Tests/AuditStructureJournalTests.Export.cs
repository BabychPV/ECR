using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Ecr.TestKit;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>`BE-16`: експорт журналу структурних змін у CSV — той самий фільтр і право, що в переліку.</summary>
public sealed partial class AuditStructureJournalTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "BE-16")]
    public async Task Експорт_віддає_CSV_файлом_лише_з_рядками_фільтра_і_пише_подію()
    {
        using var app = new EcrApiFactory(sql);
        var client = await LoginAsync(app, ["Security.ViewAudit"]);

        var typeA = $"t.{_tag}.XA";
        await WriteAsync(typeA, 1, operation: "First");
        await WriteAsync(typeA, 1, operation: "Second");
        await WriteAsync($"t.{_tag}.XB", 1, operation: "Other");

        var response = await client.GetAsync(new Uri(ExportUrl($"entityType={typeA}"), UriKind.Relative));
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {Encoding.UTF8.GetString(bytes)} {app.ErrorsText}");

        Assert.Equal("text/csv", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("utf-8", response.Content.Headers.ContentType?.CharSet);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Matches("^audit-structure-\\d{8}T\\d{6}Z-\\d{8}T\\d{6}Z\\.csv$", response.Content.Headers.ContentDisposition?.FileName?.Trim('"'));

        // BOM — щоб Excel розпізнав UTF-8.
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);

        // ⛔ Мутація: прибрати `AND EntityType = @entityType` — приїде рядок «Other».
        var lines = Encoding.UTF8.GetString(bytes[3..]).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("changedAtUtc,entityType,entityId,operation,changeReason,changedByUserId,oldJson,newJson", lines[0]);
        Assert.Equal(["First", "Second"], lines[1..].Select(l => l.Split(',')[3]));
        Assert.All(lines[1..], l => Assert.Matches("^\\d{4}-\\d\\d-\\d\\dT\\d\\d:\\d\\d:\\d\\d\\.\\d{3}Z,", l));

        // Факт експорту — у журналі безпеки: хто, фільтр, кількість.
        var details = JsonDocument.Parse(await ExportEventAsync(typeA)).RootElement;
        Assert.Equal(2, details.GetProperty("rows").GetInt32());
        Assert.Equal(typeA, details.GetProperty("entityType").GetString());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "BE-16")]
    public async Task Експорт_екранує_лапки_коми_переноси_й_нейтралізує_формули()
    {
        using var app = new EcrApiFactory(sql);
        var client = await LoginAsync(app, ["Security.ViewAudit"]);

        var type = $"t.{_tag}.XE";
        await WriteAsync(type, 1, operation: "=HYPERLINK(\"x\")", reason: "a,b \"c\"\r\nd");

        var body = await client.GetStringAsync(new Uri(ExportUrl($"entityType={type}"), UriKind.Relative));

        // ⛔ Мутація: прибрати префікс `'` у `CsvFormat.Field` — Excel виконає формулу.
        Assert.Contains(",\"'=HYPERLINK(\"\"x\"\")\",\"a,b \"\"c\"\"\r\nd\",1,,\r\n", body, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "BE-16")]
    public async Task Експорт_понад_стелю_рядків_відхиляється_422_без_файлу()
    {
        using var app = new EcrApiFactory(sql);
        using var limited = app.WithWebHostBuilder(b => b.UseSetting("Audit:ExportMaxRows", "2"));
        var client = await LoginAsync(limited, ["Security.ViewAudit"]);

        var type = $"t.{_tag}.XL";
        for (var i = 0; i < 3; i++)
        {
            await WriteAsync(type, 1);
        }

        var response = await client.GetAsync(new Uri(ExportUrl($"entityType={type}"), UriKind.Relative));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("ECR-REQ-0422", problem.GetProperty("errorCode").GetString());
        Assert.Contains("auditExportTooLarge", problem.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "BE-16")]
    public async Task Без_Security_ViewAudit_експорт_не_віддається()
    {
        using var app = new EcrApiFactory(sql);
        var client = await LoginAsync(app, ["Template.Edit"]);

        var response = await client.GetAsync(new Uri(ExportUrl(string.Empty), UriKind.Relative));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private static string ExportUrl(string filters)
        => "/api/v1/audit/structure/export.csv"
           + $"?from={Uri.EscapeDataString(DateTime.UtcNow.AddDays(-1).ToString("O", CultureInfo.InvariantCulture))}"
           + $"&to={Uri.EscapeDataString(DateTime.UtcNow.AddMinutes(1).ToString("O", CultureInfo.InvariantCulture))}"
           + (filters.Length == 0 ? string.Empty : "&" + filters);

    private async Task<string> ExportEventAsync(string entityType)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT TOP (1) DetailsJson FROM aud.SecurityEvent
             WHERE EventType = N'AuditStructureExported' AND DetailsJson LIKE N'%' + @type + N'%'
             ORDER BY Id DESC;
            """;
        command.Parameters.AddWithValue("@type", entityType);

        return (string?)await command.ExecuteScalarAsync() ?? "{}";
    }
}
