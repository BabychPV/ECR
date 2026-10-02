// tests/Ecr.Api.Tests/ReportSeedEcr230HttpTests.cs

using System.Net.Http.Json;
using System.Text.Json;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// Сід форм ECR230 (AN-14, секція <c>COLL:an14-ecr230</c>) — так, як його бачить
/// тестувальник: перелік <c>GET /api/v1/reports</c> (сценарій Н-С1,
/// <c>docs/build/TESTER-SCENARIOS-2026-10-01.md</c>). Наявний <c>SeedTests</c> дивиться
/// в таблиці напряму; тут — те, що дійде до екрана «Report snapshots».
/// </summary>
[Collection("SqlServer")]
public sealed class ReportSeedEcr230HttpTests(SqlServerFixture sql)
{
    [Theory]
    [InlineData("ECR230_A1", "Unit A1-230 Flares (quarterly)")]
    [InlineData("ECR230_B1", "Unit B1-230 Flares (quarterly)")]
    [InlineData("ECR230_B4", "Unit B4-230 Flares (quarterly)")]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-10.4")]
    public async Task Перелік_звітів_віддає_засіяну_форму_230_регуляторною_з_опублікованою_версією(string code, string name)
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SystemHealthControllerTests.SignedInAsync(sql, app, "Report.ViewRegulatory").ConfigureAwait(true);

        var list = await client.GetFromJsonAsync<JsonElement>(new Uri("/api/v1/reports", UriKind.Relative)).ConfigureAwait(true);
        var report = list.EnumerateArray().SingleOrDefault(r => r.GetProperty("code").GetString() == code);

        Assert.True(report.ValueKind == JsonValueKind.Object, $"{code} немає в переліку: {list.GetRawText()[..Math.Min(400, list.GetRawText().Length)]}");
        Assert.Equal(name, report.GetProperty("nameL10n").GetProperty("values").GetProperty("en").GetString());
        Assert.True(report.GetProperty("isRegulatory").GetBoolean());
        Assert.True(report.GetProperty("isActive").GetBoolean());

        var version = Assert.Single(report.GetProperty("versions").EnumerateArray());
        Assert.Equal("1.0", version.GetProperty("version").GetString());
        Assert.Equal("Published", version.GetProperty("status").GetString());
        Assert.Contains("CalculationResults", version.GetProperty("rulesJson").GetString(), StringComparison.Ordinal);

        using var columns = JsonDocument.Parse(version.GetProperty("columnsJson").GetString()!);
        Assert.Equal(8, columns.RootElement.GetArrayLength());
    }
}
