// tests/Ecr.Api.Tests/PublishBindingRuleSelectedConflictApiTests.cs
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
/// RC14, приймальна Land: кілька методологій аркуша прив'язані до ТИХ САМИХ колонок виходів з
/// предикатом <c>{}</c>, а вибір методології для рядка робить рушій за <c>MethodologyRule</c> /
/// <c>MethodologyCategoryRule</c>. Така конфігурація законна і публікується; без правил вибору
/// той самий конфлікт, як і раніше, дає 422 <c>bindingColumnConflict</c>.
/// </summary>
/// <remarks>
/// Мутація: повернути безумовну відмову в <c>PublishChecks.CheckDuplicateColumnBindings</c>
/// (не зважати на <c>HasSelectionRules</c>) — перший тест червоніє на 422.
/// </remarks>
[Collection("SqlServer")]
public sealed class PublishBindingRuleSelectedConflictApiTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 10, 8, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Land_подібні_методології_на_одній_колонці_з_правилами_вибору_публікуються()
    {
        var versionId = await ArrangeAsync(ruleBased: [true, true, "category"]);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app);

        using var response = await PublishAsync(client, versionId);

        Assert.True(
            response.IsSuccessStatusCode,
            $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}\n{app.ErrorsText}");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Методології_без_правил_вибору_на_одній_колонці_дають_422_bindingColumnConflict()
    {
        var versionId = await ArrangeAsync(ruleBased: [false, false]);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app);

        using var response = await PublishAsync(client, versionId);
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(
            response.StatusCode == HttpStatusCode.UnprocessableEntity,
            $"{(int)response.StatusCode}: {body}\n{app.ErrorsText}");
        using var problem = JsonDocument.Parse(body);
        Assert.Equal("ECR-TMPL-0422", problem.RootElement.GetProperty("errorCode").GetString());
        Assert.Equal(
            "err.ECR-TMPL-0422.bindingColumnConflict",
            problem.RootElement.GetProperty("messageKey").GetString());
    }

    private Task<HttpClient> SignedInAsync(EcrApiFactory app)
        => SystemHealthControllerTests.SignedInAsync(
            sql, app, "Template.View", "Template.Edit", "Template.Publish", "Calculation.EditRule");

    private static Task<HttpResponseMessage> PublishAsync(HttpClient client, int versionId)
        => client.PostAsJsonAsync(
            new Uri($"/api/v1/template-versions/{versionId}/publish", UriKind.Relative),
            new { reason = "RC14" });

    /// <summary>
    /// Чернетка з однією колонкою <c>Calculated</c> і методологіями, прив'язаними до неї з
    /// предикатом <c>{}</c>. Елемент <paramref name="ruleBased"/>: <c>true</c> — правило
    /// <c>MethodologyRule</c>, <c>"category"</c> — правило категорії, <c>false</c> — жодних правил.
    /// </summary>
    private async Task<int> ArrangeAsync(object[] ruleBased)
    {
        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        await using var db = Context();

        var template = new Template(EcrCode.Create($"RC14{tag}"), Name("RC14"), 1, Now);
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
        await db.SaveChangesAsync();

        for (var i = 0; i < ruleBased.Length; i++)
        {
            var methodology = new Methodology(EcrCode.Create($"M{i}{tag}"), Name($"M{i}"));
            db.Methodologies.Add(methodology);
            await db.SaveChangesAsync();

            var mv = new MethodologyVersion(methodology.Id, "1.0", CalculationLevel.Configuration, 9, Now);
            methodology.AddVersion(mv);
            await db.SaveChangesAsync();

            if (ruleBased[i] is true)
            {
                mv.AddRule(EcrCode.Create("all"), "{}", 100);
            }
            else if (ruleBased[i] is "category")
            {
                db.MethodologyCategoryRules.Add(new MethodologyCategoryRule(mv.Id, "!Other", Now));
            }

            await db.SaveChangesAsync();

            methodology.PublishVersion(
                mv,
                publishedByUserId: 10,
                changeReason: "RC14",
                effectiveFrom: new DateOnly(2026, 1, 1),
                testsPassed: true,
                utcNow: Now);
            await db.SaveChangesAsync();

            db.CalculationBindings.Add(new CalculationBinding(table.Id, column.Id, methodology.Id, "EMISSION", "{}"));
            await db.SaveChangesAsync();
        }

        return version.Id;
    }

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

    private static LocalizedText Name(string value) => new(new Dictionary<string, string> { ["en"] = value });
}
