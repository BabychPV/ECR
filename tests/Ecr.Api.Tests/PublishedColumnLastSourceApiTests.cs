// tests/Ecr.Api.Tests/PublishedColumnLastSourceApiTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// HSE301 C5b, <c>D-215</c>: відв'язка останнього джерела колонки типу <c>Formula</c>
/// опублікованої версії шаблону — <c>409 ECR-TMPL-4091</c>; заміна «прив'язати нове →
/// відв'язати старе» проходить.
/// </summary>
/// <remarks>
/// ⚠ Справжня база, а не фейк: правило рахує джерела запитом ПІСЛЯ <c>SaveChanges</c> у
/// тій самій транзакції, і лише база показує, що запит бачить власний незакомічений
/// запис, а відмова відкочує його цілком.
/// </remarks>
[Collection("SqlServer")]
public sealed class PublishedColumnLastSourceApiTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 9, 29, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Відв_язка_останнього_джерела_на_опублікованій_версії_409_і_прив_язка_лишається()
    {
        var stand = await ArrangeAsync();
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app);
        await PublishAsync(app, client, stand);

        using var response = await PutAsync(client, stand, "OUT1", isActive: false);
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(response.StatusCode == HttpStatusCode.Conflict, $"{(int)response.StatusCode}: {body}\n{app.ErrorsText}");
        using var problem = JsonDocument.Parse(body);
        Assert.Equal("ECR-TMPL-4091", problem.RootElement.GetProperty("errorCode").GetString());
        Assert.Equal(
            "err.ECR-TMPL-4091.lastSourceOfPublishedColumn",
            problem.RootElement.GetProperty("messageKey").GetString());

        Assert.True(await IsActiveAsync(stand, "OUT1"), "Відмовлена відв'язка мала відкотитися.");
    }

    /// <remarks>
    /// Той самий шлях, що в клієнта (<c>MethodologyBindingsPanel</c>): два окремі <c>PUT</c>,
    /// спершу нове джерело, потім стара прив'язка без «активна».
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Заміна_джерела_на_опублікованій_версії_прив_язати_нове_потім_відв_язати_старе()
    {
        var stand = await ArrangeAsync();
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app);
        await PublishAsync(app, client, stand);

        using (var added = await PutAsync(client, stand, "OUT2", isActive: true))
        {
            Assert.True(added.StatusCode == HttpStatusCode.OK, $"{(int)added.StatusCode}: {await added.Content.ReadAsStringAsync()}\n{app.ErrorsText}");
        }

        using (var removed = await PutAsync(client, stand, "OUT1", isActive: false))
        {
            Assert.True(removed.StatusCode == HttpStatusCode.OK, $"{(int)removed.StatusCode}: {await removed.Content.ReadAsStringAsync()}\n{app.ErrorsText}");
        }

        Assert.True(await IsActiveAsync(stand, "OUT2"));
        Assert.False(await IsActiveAsync(stand, "OUT1"));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Відв_язка_останнього_джерела_на_чернетці_проходить()
    {
        var stand = await ArrangeAsync();
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app);

        using var response = await PutAsync(client, stand, "OUT1", isActive: false);

        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}\n{app.ErrorsText}");
        Assert.False(await IsActiveAsync(stand, "OUT1"));
    }

    private Task<HttpClient> SignedInAsync(EcrApiFactory app)
        => SystemHealthControllerTests.SignedInAsync(
            sql, app, "Template.View", "Template.Edit", "Template.Publish", "Calculation.EditRule");

    private static async Task PublishAsync(EcrApiFactory app, HttpClient client, Stand stand)
    {
        using var published = await client.PostAsJsonAsync(
            new Uri($"/api/v1/template-versions/{stand.VersionId}/publish", UriKind.Relative),
            new { reason = "D-215" });
        Assert.True(
            published.IsSuccessStatusCode,
            $"Публікація: {(int)published.StatusCode} {await published.Content.ReadAsStringAsync()}\n{app.ErrorsText}");
    }

    private static Task<HttpResponseMessage> PutAsync(HttpClient client, Stand stand, string outputCode, bool isActive)
        => client.PutAsJsonAsync(
            new Uri($"/api/v1/methodologies/{stand.MethodologyId}/bindings/{stand.ColumnId}/{outputCode}", UriKind.Relative),
            new { matchJson = "{}", isActive });

    private async Task<bool> IsActiveAsync(Stand stand, string outputCode)
    {
        await using var db = Context();
        return await db.CalculationBindings
            .Where(b => b.ColumnDefId == stand.ColumnId && b.MethodologyId == stand.MethodologyId
                        && b.OutputCode == outputCode)
            .Select(b => b.IsActive)
            .SingleAsync();
    }

    /// <summary>
    /// Чернетка з колонкою типу <c>Formula</c> без формули шаблону; її єдине джерело —
    /// активна прив'язка <c>OUT1</c>. Методологія оголошує <c>OUT1</c> і <c>OUT2</c>.
    /// </summary>
    private async Task<Stand> ArrangeAsync()
    {
        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        await using var db = Context();

        var template = new Template(EcrCode.Create($"D215{tag}"), Name("D-215"), 1, Now);
        db.Templates.Add(template);
        await db.SaveChangesAsync();

        var version = new TemplateVersion(template.Id, "1.0.0.0", 1, Now);
        db.TemplateVersions.Add(version);
        await db.SaveChangesAsync();

        var sheet = new SheetDef(version.Id, EcrCode.Create($"S{tag}"), Name("Sheet"), 1);
        db.SheetDefs.Add(sheet);
        await db.SaveChangesAsync();

        var table = new TableDef(
            sheet.Id, EcrCode.Create($"T{tag}"), Name("Table"), 1,
            TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed);
        db.TableDefs.Add(table);
        await db.SaveChangesAsync();

        var column = new ColumnDef(table.Id, EcrCode.Create("CF"), Name("CF"), 1, CellDataType.Formula);
        db.ColumnDefs.Add(column);
        db.RowDefs.Add(new RowDef(table.Id, RowKey.Create("R1"), 1, Name("R1"), RowKind.Item));

        var methodology = new Methodology(EcrCode.Create($"M{tag}"), Name("M"));
        db.Methodologies.Add(methodology);
        await db.SaveChangesAsync();

        var methodologyVersion = new MethodologyVersion(methodology.Id, "1.0", CalculationLevel.Configuration, 9, Now);
        db.MethodologyVersions.Add(methodologyVersion);
        await db.SaveChangesAsync();

        var unitId = await db.Units.OrderBy(u => u.Id).Select(u => u.Id).FirstAsync();
        db.MethodologyOutputs.Add(new MethodologyOutput(methodologyVersion.Id, EcrCode.Create("OUT1"), unitId));
        db.MethodologyOutputs.Add(new MethodologyOutput(methodologyVersion.Id, EcrCode.Create("OUT2"), unitId));
        await db.SaveChangesAsync();

        // ⚠ D-R2: публікація шаблону відхиляє активну прив'язку до методології без
        // опублікованої версії — версія стенду публікується (після виходів: вміст
        // опублікованої версії незмінний).
        methodology.PublishVersion(
            methodologyVersion,
            publishedByUserId: 10,
            changeReason: "D-R2",
            effectiveFrom: new DateOnly(2026, 1, 1),
            testsPassed: true,
            utcNow: Now);
        await db.SaveChangesAsync();

        db.CalculationBindings.Add(new CalculationBinding(table.Id, column.Id, methodology.Id, "OUT1", "{}"));
        await db.SaveChangesAsync();

        return new Stand(version.Id, column.Id, methodology.Id);
    }

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

    private static LocalizedText Name(string value) => new(new Dictionary<string, string> { ["en"] = value });

    private sealed record Stand(int VersionId, int ColumnId, int MethodologyId);
}
