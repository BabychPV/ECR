// tests/Ecr.Api.Tests/WhereUsedTests.cs
using System.Globalization;
using System.Net;
using System.Text.Json;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// «Де використовується» константи методики і колонки шаблону (ФВ-8.14)
/// справжнім HTTP на реальній БД.
/// </summary>
/// <remarks>
/// ⚠ Кожен тест поруч із справжнім посиланням сіє «пастку» — схоже, але чуже
/// (константа <c>K10</c> поруч із <c>K1</c>, id колонки у ЗНАЧЕННІ предиката
/// правила): перелік, що рахує за підрядком, дав би тут зайве.
/// </remarks>
[Collection("SqlServer")]
public sealed class WhereUsedTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Константу_показують_лише_формули_що_посилаються_саме_на_неї()
    {
        var stand = await ConstantStandAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, "Calculation.View").ConfigureAwait(true);

        var usage = await GetOkAsync(client, app, ConstantUrl(stand, "K1")).ConfigureAwait(true);

        // Літералом: одна формула з CST.K1; CST.K10 і вираз без констант — ні.
        Assert.Equal(1, usage.GetProperty("total").GetInt32());
        var item = Assert.Single(usage.GetProperty("items").EnumerateArray());
        Assert.Equal("methodologyFormula", item.GetProperty("kind").GetString());
        Assert.Equal("USES_K1", item.GetProperty("label").GetString());
        Assert.Equal($"/admin/methodologies/{stand.MethodologyId}/versions", item.GetProperty("route").GetString());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Константа_без_права_перегляду_методик_403_невідома_404()
    {
        var stand = await ConstantStandAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);

        using (var stranger = await SignedInAsync(app, "Template.View").ConfigureAwait(true))
        {
            var denied = await stranger.GetAsync(new Uri(ConstantUrl(stand, "K1"), UriKind.Relative)).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }

        using var client = await SignedInAsync(app, "Calculation.View").ConfigureAwait(true);

        // ⛔ Невідомий код і чужа методологія — 404, а не «нуль посилань».
        await AssertNotFoundAsync(client, ConstantUrl(stand, "NOPE"), "ECR-CALC-0404").ConfigureAwait(true);
        await AssertNotFoundAsync(
            client,
            $"/api/v1/methodologies/{stand.MethodologyId + 100000}/versions/{stand.VersionId}/constants/K1/usage",
            "ECR-CALC-0404").ConfigureAwait(true);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Колонку_показують_формула_привязка_вимога_мапінг_і_правило()
    {
        var stand = await ColumnStandAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, "Template.View").ConfigureAwait(true);

        var usage = await GetOkAsync(client, app, $"/api/v1/column-defs/{stand.Subject}/usage").ConfigureAwait(true);

        // По одному на кожне джерело — п'ять; пастки (сусідня колонка, id у
        // значенні предиката, ключ-«продовження» id) не рахуються.
        Assert.Equal(5, usage.GetProperty("total").GetInt32());
        var byKind = usage.GetProperty("items").EnumerateArray()
            .ToDictionary(i => i.GetProperty("kind").GetString()!, i => i.GetProperty("label").GetString());
        Assert.Equal(
            ["calculationBinding", "fieldMap", "methodologyRequiredInput", "methodologyRule", "templateFormula"],
            byKind.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("RULE_HIT", byKind["methodologyRule"]);
        Assert.Equal(stand.FormulaLabel, byKind["templateFormula"]);
        Assert.Equal("tag_hit", byKind["fieldMap"]);

        // Сусідня колонка: на неї посилається лише пастка-правило — ключем.
        var neighbour = await GetOkAsync(client, app, $"/api/v1/column-defs/{stand.Neighbour}/usage").ConfigureAwait(true);
        var only = Assert.Single(neighbour.GetProperty("items").EnumerateArray());
        Assert.Equal("RULE_TRAP", only.GetProperty("label").GetString());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Колонка_без_права_перегляду_шаблонів_403_невідома_404()
    {
        var stand = await ColumnStandAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);

        using (var stranger = await SignedInAsync(app, "Calculation.View").ConfigureAwait(true))
        {
            var denied = await stranger
                .GetAsync(new Uri($"/api/v1/column-defs/{stand.Subject}/usage", UriKind.Relative)).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }

        using var client = await SignedInAsync(app, "Template.View").ConfigureAwait(true);
        await AssertNotFoundAsync(client, $"/api/v1/column-defs/{int.MaxValue}/usage", "ECR-TMPL-0404").ConfigureAwait(true);
    }

    private sealed record ConstantStand(int MethodologyId, int VersionId);

    private sealed record ColumnStand(int Subject, int Neighbour, string FormulaLabel);

    private async Task<ConstantStand> ConstantStandAsync()
    {
        await using var db = Db();
        var unit = await db.Units.OrderBy(u => u.Id).Select(u => u.Id).FirstAsync().ConfigureAwait(false);
        var methodology = await MethodologyAsync(db).ConfigureAwait(false);

        var version = new MethodologyVersion(methodology.Id, "1.0", CalculationLevel.Configuration, createdByUserId: 1, Now);
        db.MethodologyVersions.Add(version);
        await db.SaveChangesAsync().ConfigureAwait(false);

        db.MethodologyConstants.Add(version.AddNumericConstant(EcrCode.Create("K1"), 1m, unit));
        db.MethodologyConstants.Add(version.AddNumericConstant(EcrCode.Create("K10"), 10m, unit));
        db.MethodologyFormulas.Add(version.AddFormula(EcrCode.Create("USES_K1"), "@Fuel * CST.K1", FormulaResultType.Number, unit));
        db.MethodologyFormulas.Add(version.AddFormula(EcrCode.Create("USES_K10"), "@Fuel * CST.K10", FormulaResultType.Number, unit));
        db.MethodologyFormulas.Add(version.AddFormula(EcrCode.Create("PLAIN"), "@Fuel * 3", FormulaResultType.Number, unit));
        await db.SaveChangesAsync().ConfigureAwait(false);

        return new ConstantStand(methodology.Id, version.Id);
    }

    /// <summary>
    /// Колонка-предмет (друга) і сусідня (третя): по одному посиланню кожного
    /// виду на предмет і пастка-правило, що має id предмета лише у значенні.
    /// </summary>
    private async Task<ColumnStand> ColumnStandAsync()
    {
        var document = await new TestDocumentBuilder(sql.ConnectionString)
            .BuildAsync(columnCount: 3, rowCount: 1).ConfigureAwait(false);
        var subject = document.ColumnDefIds[1];
        var neighbour = document.ColumnDefIds[2];
        var key = subject.ToString(CultureInfo.InvariantCulture);
        var neighbourKey = neighbour.ToString(CultureInfo.InvariantCulture);

        await using var db = Db();

        var formula = new FormulaDef(document.TableDefId, FormulaScope.Column, "1", ExpressionDialect.Template);
        formula.AssignColumn(neighbour);
        db.FormulaDefs.Add(formula);
        await db.SaveChangesAsync().ConfigureAwait(false);
        db.FormulaDependencies.Add(FormulaDependency.ForFormula(
            formula.Id, 0, document.TableDefId, rowKey: null, subject, filterJson: null, periodOffset: null, sortOrder: 0));

        var methodology = await MethodologyAsync(db).ConfigureAwait(false);
        db.CalculationBindings.Add(new CalculationBinding(document.TableDefId, subject, methodology.Id, "tons", "{}"));

        var version = new MethodologyVersion(methodology.Id, "1.0", CalculationLevel.Configuration, createdByUserId: 1, Now);
        db.MethodologyVersions.Add(version);
        await db.SaveChangesAsync().ConfigureAwait(false);

        db.MethodologyRequiredInputs.Add(version.AddRequiredInput(subject, RequiredInputSeverity.Block, hint: null));
        db.MethodologyRules.Add(new MethodologyRule(version.Id, EcrCode.Create("RULE_HIT"), $$"""{"{{key}}":"CO2"}""", 10));
        db.MethodologyRules.Add(new MethodologyRule(
            version.Id, EcrCode.Create("RULE_TRAP"), $$"""{"{{neighbourKey}}":"{{key}}","{{key}}0":"x"}""", 20));

        var source = new DataSource(
            EcrCode.Create($"DS{Guid.NewGuid():N}"[..16]),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "FV-8.14" }),
            ExternalTransport.PiWebApi,
            "https://example.invalid",
            "secret");
        db.DataSources.Add(source);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var entity = new SourceEntity(source.Id, "STACK", RegistrySourceKind.External);
        db.SourceEntities.Add(entity);
        await db.SaveChangesAsync().ConfigureAwait(false);

        db.EntityFieldMaps.Add(EntityFieldMap.ToColumn(entity.Id, "tag_hit", subject));
        db.EntityFieldMaps.Add(EntityFieldMap.ToColumn(entity.Id, "tag_other", document.ColumnDefIds[0]));
        await db.SaveChangesAsync().ConfigureAwait(false);

        var table = await db.TableDefs.Where(t => t.Id == document.TableDefId).Select(t => t.Code).FirstAsync().ConfigureAwait(false);
        var column = await db.ColumnDefs.Where(c => c.Id == neighbour).Select(c => c.Code).FirstAsync().ConfigureAwait(false);

        return new ColumnStand(subject, neighbour, $"{table}.{column}");
    }

    private static async Task<Methodology> MethodologyAsync(EcrDbContext db)
    {
        var methodology = new Methodology(
            EcrCode.Create($"WU{Guid.NewGuid():N}"[..20]),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "FV-8.14" }));
        db.Methodologies.Add(methodology);
        await db.SaveChangesAsync().ConfigureAwait(false);
        return methodology;
    }

    private static string ConstantUrl(ConstantStand stand, string code)
        => $"/api/v1/methodologies/{stand.MethodologyId}/versions/{stand.VersionId}/constants/{code}/usage";

    private static async Task<JsonElement> GetOkAsync(HttpClient client, EcrApiFactory app, string url)
    {
        var response = await client.GetAsync(new Uri(url, UriKind.Relative)).ConfigureAwait(false);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {app.ErrorsText}");
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false)).RootElement;
    }

    private static async Task AssertNotFoundAsync(HttpClient client, string url, string errorCode)
    {
        var response = await client.GetAsync(new Uri(url, UriKind.Relative)).ConfigureAwait(false);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false)).RootElement;
        Assert.Equal(errorCode, problem.GetProperty("errorCode").GetString());
    }

    private EcrDbContext Db()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

    private Task<HttpClient> SignedInAsync(EcrApiFactory app, params string[] permissions)
        => SystemHealthControllerTests.SignedInAsync(sql, app, permissions);
}
