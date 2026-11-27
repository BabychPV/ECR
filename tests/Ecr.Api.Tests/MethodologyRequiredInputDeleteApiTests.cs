using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// ✎ P-1124-1 (UI-прохід редактора скрипта 24.11): <c>DELETE …/required-inputs/{columnDefId}</c> — обов'язкову
/// вхідну колонку чернетки можна прибрати (до цього лише завести й змінити). Право те саме, що на запис
/// (<c>Calculation.ManageRequiredInputs</c>); вимоги немає — 404 з ключем; опублікована версія — 409; без права — 403.
/// </summary>
/// <remarks>
/// Червоне до фіксу: без дії контролера маршрут відповідав 405 (Method Not Allowed) — перший тест червоний;
/// мутація «прибрати <c>drafts.Remove(existing)</c>» — 204, але вимога лишається в переліку, тест червоний.
/// </remarks>
[Collection("SqlServer")]
public sealed class MethodologyRequiredInputDeleteApiTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "MethodologyRequiredInput")]
    public async Task Видалення_вимоги_чернетки_204_зникає_з_переліку_повтор_404()
    {
        var stand = await ArrangeAsync(published: false);
        using var app = new EcrApiFactory(sql);
        using var client = await SystemHealthControllerTests.SignedInAsync(
            sql, app, "Calculation.View", "Calculation.ManageRequiredInputs");

        var url = new Uri(
            $"/api/v1/methodologies/{stand.MethodologyId}/versions/{stand.VersionId}/required-inputs/{stand.ColumnDefId}",
            UriKind.Relative);
        var deleted = await client.DeleteAsync(url);
        Assert.True(deleted.StatusCode == HttpStatusCode.NoContent, $"{deleted.StatusCode}: {await deleted.Content.ReadAsStringAsync()}");

        var list = await client.GetFromJsonAsync<JsonElement>(
            new Uri($"/api/v1/methodologies/{stand.MethodologyId}/versions/{stand.VersionId}/required-inputs", UriKind.Relative));
        Assert.Equal(0, list.GetArrayLength());

        var again = await client.DeleteAsync(url);
        var body = await again.Content.ReadAsStringAsync();
        Assert.True(again.StatusCode == HttpStatusCode.NotFound, $"{again.StatusCode}: {body}");
        using var problem = JsonDocument.Parse(body);
        Assert.Equal("err.ECR-CALC-0404.requiredInput", problem.RootElement.GetProperty("messageKey").GetString());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "MethodologyRequiredInput")]
    public async Task Вимогу_опублікованої_версії_не_прибрати_409()
    {
        var stand = await ArrangeAsync(published: true);
        using var app = new EcrApiFactory(sql);
        using var client = await SystemHealthControllerTests.SignedInAsync(
            sql, app, "Calculation.View", "Calculation.ManageRequiredInputs");

        var deleted = await client.DeleteAsync(new Uri(
            $"/api/v1/methodologies/{stand.MethodologyId}/versions/{stand.VersionId}/required-inputs/{stand.ColumnDefId}",
            UriKind.Relative));
        Assert.True(deleted.StatusCode == HttpStatusCode.Conflict, $"{deleted.StatusCode}: {await deleted.Content.ReadAsStringAsync()}");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "MethodologyRequiredInput")]
    public async Task Без_права_на_вимоги_403()
    {
        var stand = await ArrangeAsync(published: false);
        using var app = new EcrApiFactory(sql);
        using var client = await SystemHealthControllerTests.SignedInAsync(sql, app, "Calculation.View");

        var deleted = await client.DeleteAsync(new Uri(
            $"/api/v1/methodologies/{stand.MethodologyId}/versions/{stand.VersionId}/required-inputs/{stand.ColumnDefId}",
            UriKind.Relative));
        Assert.Equal(HttpStatusCode.Forbidden, deleted.StatusCode);
    }

    private async Task<Stand> ArrangeAsync(bool published)
    {
        var document = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync();
        var columnDefId = document.ColumnDefIds[0];

        await using var db = new EcrDbContext(
            new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

        var methodology = new Methodology(
            EcrCode.Create($"RID{Guid.NewGuid():N}"[..20]),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Required input delete" }));
        var created = new DateTime(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc);
        var version = new MethodologyVersion(
            methodology.Id, "1.0", CalculationLevel.Configuration, createdByUserId: 1, created, MethodologyEngine.Script);
        methodology.AddVersion(version);
        db.Methodologies.Add(methodology);
        await db.SaveChangesAsync();

        db.MethodologyRequiredInputs.Add(version.AddRequiredInput(columnDefId, RequiredInputSeverity.Block, hint: null));
        await db.SaveChangesAsync();

        if (published)
        {
            methodology.PublishVersion(version, 1, "P-1124-1 arrange", new DateOnly(2020, 1, 1), testsPassed: true, created);
            await db.SaveChangesAsync();
        }

        return new Stand(methodology.Id, version.Id, columnDefId);
    }

    private sealed record Stand(int MethodologyId, int VersionId, int ColumnDefId);
}
