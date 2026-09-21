using System.Net;
using System.Text.Json;
using Ecr.Application.Calculations;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// Матриця покриття «рядки реальних даних × правила» справжнім HTTP на реальній базі (ФВ-13.9).
/// </summary>
[Collection("SqlServer")]
public sealed class RuleCoverageTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-13.9")]
    public async Task Два_документи_дають_розрив_конфлікт_і_покриття_у_вікні_періодів()
    {
        var stand = await ArrangeAsync().ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SystemHealthControllerTests
            .SignedInAsync(sql, app, RuleCoverageHandler.Permission).ConfigureAwait(true);

        var response = await client.GetAsync(new Uri(
            $"/api/v1/methodologies/{stand.MethodologyId}/versions/{stand.VersionId}/rule-coverage?periodFrom=202601&periodTo=202612",
            UriKind.Relative)).ConfigureAwait(true);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {app.ErrorsText}");

        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(true)).RootElement;
        Assert.Equal([stand.ColumnId], body.GetProperty("columnDefIds").EnumerateArray().Select(c => c.GetInt32()));
        Assert.False(body.GetProperty("truncated").GetBoolean());

        // Неактивне правило SO2 не рахується; CH4 лежить у 202501 — поза вікном.
        var rows = body.GetProperty("combinations").EnumerateArray()
            .Select(c => (
                Value: c.GetProperty("values")[0].GetString(),
                State: c.GetProperty("state").GetString(),
                Winner: c.GetProperty("winnerRuleCode").ValueKind == JsonValueKind.Null ? null : c.GetProperty("winnerRuleCode").GetString(),
                Rows: c.GetProperty("rows").GetInt64(),
                Documents: c.GetProperty("documents").GetInt32()))
            .ToList();

        Assert.Equal(
            [("SO2", "Gap", null, 1L, 1), ("CO2", "Conflict", "CO2_A", 3L, 2), ("NOX", "Covered", "NOX", 1L, 1)],
            rows);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Без_права_перегляду_403()
    {
        var stand = await ArrangeAsync().ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SystemHealthControllerTests.SignedInAsync(sql, app).ConfigureAwait(true);

        var response = await client.GetAsync(new Uri(
            $"/api/v1/methodologies/{stand.MethodologyId}/versions/{stand.VersionId}/rule-coverage",
            UriKind.Relative)).ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Порожнє_вікно_періодів_422_з_нейтральним_заголовком_і_власною_подробицею()
    {
        var stand = await ArrangeAsync().ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SystemHealthControllerTests
            .SignedInAsync(sql, app, RuleCoverageHandler.Permission).ConfigureAwait(true);

        var response = await client.GetAsync(new Uri(
            $"/api/v1/methodologies/{stand.MethodologyId}/versions/{stand.VersionId}/rule-coverage?periodFrom=202612&periodTo=202601",
            UriKind.Relative)).ConfigureAwait(true);

        var text = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(response.StatusCode == HttpStatusCode.UnprocessableEntity, $"{response.StatusCode}: {text}");

        // Заголовок коду спільний із відмовами публікації: про публікацію над
        // порожнім вікном він говорити не може — причину каже messageKey.
        var problem = JsonDocument.Parse(text).RootElement;
        Assert.Equal("ECR-CALC-0422", problem.GetProperty("errorCode").GetString());
        Assert.Equal("err.ECR-CALC-0422.coverageWindow", problem.GetProperty("messageKey").GetString());
        var title = problem.GetProperty("title").GetString();
        Assert.Equal("Invalid methodology request", title);
        Assert.DoesNotContain("publish", title, StringComparison.OrdinalIgnoreCase);
    }

    private sealed record Stand(int MethodologyId, int VersionId, int ColumnId);

    /// <summary>
    /// Таблиця з текстовою колонкою: документ A — CO2, NOX, SO2; документ B — CO2, CO2 у
    /// 202601 і CH4 у 202501. Правила: CO2_A і CO2_B (обидва 10), NOX (10), SO2 (1, вимкнене).
    /// </summary>
    private async Task<Stand> ArrangeAsync()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var a = await builder.BuildAsync(columnCount: 1, rowCount: 3).ConfigureAwait(false);
        var b = await builder.BuildAsync(columnCount: 1, rowCount: 1).ConfigureAwait(false);
        var column = a.ColumnDefIds[0];

        await using var db = builder.CreateContext();
        var loader = new BulkCellLoader(sql.ConnectionString, 1000);

        Cell(db, a, a.RowIds[0], "CO2");
        Cell(db, a, a.RowIds[1], "NOX");
        Cell(db, a, a.RowIds[2], "SO2");
        await InstanceAsync(db, loader, a, b.DocumentId, 202601, "CO2", "CO2").ConfigureAwait(false);
        await InstanceAsync(db, loader, a, b.DocumentId, 202501, "CH4").ConfigureAwait(false);

        var methodology = new Methodology(
            EcrCode.Create($"RC{Guid.NewGuid():N}"[..20]), new LocalizedText(new Dictionary<string, string> { ["en"] = "rules" }));
        db.Methodologies.Add(methodology);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var version = new MethodologyVersion(methodology.Id, "1.0", CalculationLevel.Configuration, createdByUserId: 1, Now);
        db.MethodologyVersions.Add(version);
        await db.SaveChangesAsync().ConfigureAwait(false);

        string Match(string v) => $$"""{"{{column}}":"{{v}}"}""";
        db.MethodologyRules.Add(new MethodologyRule(version.Id, EcrCode.Create("CO2_A"), Match("CO2"), 10));
        await db.SaveChangesAsync().ConfigureAwait(false);
        db.MethodologyRules.Add(new MethodologyRule(version.Id, EcrCode.Create("CO2_B"), Match("CO2"), 10));
        db.MethodologyRules.Add(new MethodologyRule(version.Id, EcrCode.Create("NOX"), Match("NOX"), 10));
        var inactive = new MethodologyRule(version.Id, EcrCode.Create("SO2"), Match("SO2"), 1);
        inactive.SetActive(false);
        db.MethodologyRules.Add(inactive);
        db.CalculationBindings.Add(new CalculationBinding(a.TableDefId, column, methodology.Id, "tons", "{}"));
        await db.SaveChangesAsync().ConfigureAwait(false);

        return new Stand(methodology.Id, version.Id, column);
    }

    /// <summary>Ще один примірник тієї самої таблиці в документі B з рядками-значеннями.</summary>
    private static async Task InstanceAsync(
        EcrDbContext db, BulkCellLoader loader, TestDocument table, long documentId, int period, params string[] values)
    {
        var key = new PeriodKey(period);
        var instanceId = await loader.ReserveIdsAsync("doc.TableInstanceSeq", 1, default).ConfigureAwait(false);
        var firstRow = await loader.ReserveIdsAsync("doc.TableRowSeq", values.Length, default).ConfigureAwait(false);
        db.TableInstances.Add(new TableInstance(key, instanceId, documentId, table.TableDefId, Now));
        for (var i = 0; i < values.Length; i++)
        {
            db.TableRows.Add(new TableRow(key, firstRow + i, instanceId, RowKey.Create($"B{i}"), i + 1, Now));
            db.CellValues.Add(new CellValue(
                new CellAddress(key, firstRow + i, table.ColumnDefIds[0]), table.TableDefId, new CellValueData { ValueString = values[i] }));
        }

        await db.SaveChangesAsync().ConfigureAwait(false);
    }

    private static void Cell(EcrDbContext db, TestDocument doc, long rowId, string value)
        => db.CellValues.Add(new CellValue(
            new CellAddress(doc.PeriodKey, rowId, doc.ColumnDefIds[0]), doc.TableDefId, new CellValueData { ValueString = value }));
}
