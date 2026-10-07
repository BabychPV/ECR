// tests/Ecr.Api.Tests/PublishBindingUnpublishedMethodologyApiTests.cs
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
/// Land-регресія 2026-10-07, D-R2: публікація шаблону з активною прив'язкою до
/// методології БЕЗ опублікованої версії.
/// </summary>
/// <remarks>
/// ⛔ Публікація приймала таку прив'язку, а далі кожен перерахунок документа падав
/// цілком: <c>ECR-CALC-0422</c> «bound to a table but has no published version»
/// (<c>MethodologyResolver.ResolveVersionAsync</c>) — не рахувалася жодна методологія
/// документа, навіть коректна. Публікація має відмовляти заздалегідь, названо
/// методологію й колонки; вимкнена прив'язка нічого не рахує і публікації не заважає.
/// </remarks>
[Collection("SqlServer")]
public sealed class PublishBindingUnpublishedMethodologyApiTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 10, 7, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Публікація_з_активною_прив_язкою_до_неопублікованої_методології_422_з_її_кодом()
    {
        var stand = await ArrangeAsync(publishedMethodology: false, bindingActive: true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app);

        using var response = await PublishAsync(client, stand);
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(
            response.StatusCode == HttpStatusCode.UnprocessableEntity,
            $"{(int)response.StatusCode}: {body}\n{app.ErrorsText}");
        using var problem = JsonDocument.Parse(body);
        Assert.Equal("ECR-TMPL-0422", problem.RootElement.GetProperty("errorCode").GetString());
        Assert.Equal(
            "err.ECR-CALC-0422.bindingMethodologyNotPublished",
            problem.RootElement.GetProperty("messageKey").GetString());
        Assert.Equal(stand.MethodologyCode, problem.RootElement.GetProperty("methodologyCode").GetString());

        // Версія лишилась чернеткою: відмова відкотила все.
        await using var db = Context();
        var status = await db.TemplateVersions
            .Where(v => v.Id == stand.VersionId).Select(v => v.Status).SingleAsync();
        Assert.Equal(TemplateVersionStatus.Draft, status);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Публікація_з_вимкненою_прив_язкою_до_неопублікованої_методології_проходить()
    {
        var stand = await ArrangeAsync(publishedMethodology: false, bindingActive: false);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app);

        using var response = await PublishAsync(client, stand);

        Assert.True(
            response.IsSuccessStatusCode,
            $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}\n{app.ErrorsText}");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Публікація_з_активною_прив_язкою_до_опублікованої_методології_проходить()
    {
        var stand = await ArrangeAsync(publishedMethodology: true, bindingActive: true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app);

        using var response = await PublishAsync(client, stand);

        Assert.True(
            response.IsSuccessStatusCode,
            $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}\n{app.ErrorsText}");
    }

    /// <remarks>
    /// Вимкнення прив'язки — спосіб зняти відмову публікації. Наявна прив'язка вимикається,
    /// навіть коли жодна версія методології вже не оголошує її виходу.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Вимкнення_наявної_прив_язки_до_неопублікованої_методології_проходить_і_знімає_відмову()
    {
        var stand = await ArrangeAsync(publishedMethodology: false, bindingActive: true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app);

        using (var blocked = await PublishAsync(client, stand))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, blocked.StatusCode);
        }

        using (var off = await client.PutAsJsonAsync(
            new Uri($"/api/v1/methodologies/{stand.MethodologyId}/bindings/{stand.ColumnId}/GONE", UriKind.Relative),
            new { matchJson = "{}", isActive = false }))
        {
            Assert.True(
                off.StatusCode == HttpStatusCode.OK,
                $"{(int)off.StatusCode}: {await off.Content.ReadAsStringAsync()}\n{app.ErrorsText}");
        }

        using var published = await PublishAsync(client, stand);
        Assert.True(
            published.IsSuccessStatusCode,
            $"{(int)published.StatusCode}: {await published.Content.ReadAsStringAsync()}\n{app.ErrorsText}");
    }

    private Task<HttpClient> SignedInAsync(EcrApiFactory app)
        => SystemHealthControllerTests.SignedInAsync(
            sql, app, "Template.View", "Template.Edit", "Template.Publish", "Calculation.EditRule");

    private static Task<HttpResponseMessage> PublishAsync(HttpClient client, Stand stand)
        => client.PostAsJsonAsync(
            new Uri($"/api/v1/template-versions/{stand.VersionId}/publish", UriKind.Relative),
            new { reason = "D-R2" });

    /// <summary>
    /// Чернетка з колонкою <c>Calculated</c>, прив'язаною до виходу <c>GONE</c> методології,
    /// яка цього виходу НЕ оголошує (як осиротіла прив'язка на чернетках методологій 25–27).
    /// </summary>
    private async Task<Stand> ArrangeAsync(bool publishedMethodology, bool bindingActive)
    {
        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        await using var db = Context();

        var template = new Template(EcrCode.Create($"DR2{tag}"), Name("D-R2"), 1, Now);
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

        var column = new ColumnDef(table.Id, EcrCode.Create("CC"), Name("CC"), 1, CellDataType.Calculated);
        db.ColumnDefs.Add(column);
        db.RowDefs.Add(new RowDef(table.Id, RowKey.Create("R1"), 1, Name("R1"), RowKind.Item));

        var methodologyCode = $"M{tag}";
        var methodology = new Methodology(EcrCode.Create(methodologyCode), Name("M"));
        db.Methodologies.Add(methodology);
        await db.SaveChangesAsync();

        // Версія методології є завжди; питання лише в тому, чи вона опублікована.
        var methodologyVersion = new MethodologyVersion(methodology.Id, "1.0", CalculationLevel.Configuration, 9, Now);
        methodology.AddVersion(methodologyVersion);
        if (publishedMethodology)
        {
            methodology.PublishVersion(
                methodologyVersion,
                publishedByUserId: 10,
                changeReason: "D-R2",
                effectiveFrom: new DateOnly(2026, 1, 1),
                testsPassed: true,
                utcNow: Now);
        }

        await db.SaveChangesAsync();

        var binding = new CalculationBinding(table.Id, column.Id, methodology.Id, "GONE", "{}");
        db.CalculationBindings.Add(binding);
        await db.SaveChangesAsync();

        if (!bindingActive)
        {
            binding.Update("{}", false);
            await db.SaveChangesAsync();
        }

        return new Stand(version.Id, column.Id, methodology.Id, methodologyCode);
    }

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

    private static LocalizedText Name(string value) => new(new Dictionary<string, string> { ["en"] = value });

    private sealed record Stand(int VersionId, int ColumnId, int MethodologyId, string MethodologyCode);
}
