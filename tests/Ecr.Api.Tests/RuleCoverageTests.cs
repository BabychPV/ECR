using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Application.Calculations;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
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
    private const string Password = "Api-RuleCoverage-2026!";

    private static readonly DateTime Now = new(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-13.9")]
    public async Task Два_документи_дають_розрив_конфлікт_і_покриття_у_вікні_періодів()
    {
        var stand = await ArrangeAsync().ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInWithProjectsAsync(app, stand.ProjectA, stand.ProjectB).ConfigureAwait(true);

        var body = await CoverageAsync(client, app, stand).ConfigureAwait(true);
        Assert.Equal([stand.ColumnId], body.GetProperty("columnDefIds").EnumerateArray().Select(c => c.GetInt32()));
        Assert.False(body.GetProperty("truncated").GetBoolean());

        // Неактивне правило SO2 не рахується; CH4 лежить у 202501 — поза вікном.
        Assert.Equal(
            [("SO2", "Gap", null, 1L, 1), ("CO2", "Conflict", "CO2_A", 3L, 2), ("NOX", "Covered", "NOX", 1L, 1)],
            Rows(body));
    }

    /// <remarks>
    /// Аудит S7: <c>Calculation.View</c> — право на методологію, не на дані
    /// проєктів. До виправлення користувач із грантом лише на проєкт A бачив
    /// <c>CO2 · 3 рядки · 2 документи</c> — тобто і значення, і лічильники проєкту B.
    /// Мутація: прибрати фільтр <c>d.ProjectId IN …</c> у <c>RuleCoverageReader</c>
    /// (або передати всі проєкти з обробника) → тест червоний.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-13.9")]
    public async Task Грант_лише_на_проєкт_A_лічильники_рахуються_без_рядків_проєкту_B()
    {
        var stand = await ArrangeAsync().ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInWithProjectsAsync(app, stand.ProjectA).ConfigureAwait(true);

        var body = await CoverageAsync(client, app, stand).ConfigureAwait(true);

        // Два рядки CO2 документа B не додаються ні до рядків, ні до документів.
        Assert.Equal(
            [("SO2", "Gap", null, 1L, 1), ("CO2", "Conflict", "CO2_A", 1L, 1), ("NOX", "Covered", "NOX", 1L, 1)],
            Rows(body));
    }

    /// <remarks>
    /// Дзеркальний випадок: значення, яких немає в дозволеному проєкті (SO2, NOX
    /// документа A), не з'являються взагалі. Мутація та сама — тест червоний.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-13.9")]
    public async Task Грант_лише_на_проєкт_B_значення_проєкту_A_не_видно()
    {
        var stand = await ArrangeAsync().ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInWithProjectsAsync(app, stand.ProjectB).ConfigureAwait(true);

        var body = await CoverageAsync(client, app, stand).ConfigureAwait(true);

        Assert.Equal([("CO2", "Conflict", "CO2_A", 2L, 1)], Rows(body));
    }

    /// <remarks>
    /// Рішення S7: без жодного гранта — 200 із порожньою матрицею, не 403. Так
    /// поводяться перелік документів, пошук і перелік зрізів звітності: право на
    /// ендпоінт є, видимих даних немає. Правила й осі (метадані методології)
    /// лишаються — їх відкриває саме <c>Calculation.View</c>.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Без_жодного_гранта_на_проєкт_порожня_матриця_а_не_403()
    {
        var stand = await ArrangeAsync().ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInWithProjectsAsync(app).ConfigureAwait(true);

        var body = await CoverageAsync(client, app, stand).ConfigureAwait(true);

        Assert.Empty(Rows(body));
        Assert.False(body.GetProperty("truncated").GetBoolean());
        Assert.Equal([stand.ColumnId], body.GetProperty("columnDefIds").EnumerateArray().Select(c => c.GetInt32()));
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

    /// <remarks>
    /// L-3: <c>doc.CellValue.ValueRegistryEntryId</c> — <c>int</c>, а читач брав <c>GetInt64</c> →
    /// <c>InvalidCastException</c> і <c>500</c> для шаблонів, чиї правила ключуються по Lookup-колонках.
    /// Мутація: повернути <c>reader.GetInt64(o + 2)</c> у <c>RuleCoverageReader</c> → тест червоний.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-13.9")]
    public async Task Правило_по_Lookup_колонці_дає_покриття_а_не_500()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var a = await builder.BuildAsync(columnCount: 1, rowCount: 1).ConfigureAwait(false);
        var column = a.ColumnDefIds[0];

        await using var db = builder.CreateContext();
        var registry = new RegistryDef(
            EcrCode.Create($"L3_{a.TableDefId}"), new LocalizedText(new Dictionary<string, string> { ["en"] = "Substances" }), isTemporal: false);
        db.RegistryDefs.Add(registry);
        await db.SaveChangesAsync().ConfigureAwait(false);
        var entry = new RegistryEntry(
            registry.Id, EcrCode.Create("CO2"), new LocalizedText(new Dictionary<string, string> { ["en"] = "CO2" }));
        db.RegistryEntries.Add(entry);
        await db.SaveChangesAsync().ConfigureAwait(false);

        db.CellValues.Add(new CellValue(
            new CellAddress(a.PeriodKey, a.RowIds[0], column), a.TableDefId, new CellValueData { ValueRegistryEntryId = entry.Id }));

        var methodology = new Methodology(
            EcrCode.Create($"RC{Guid.NewGuid():N}"[..20]), new LocalizedText(new Dictionary<string, string> { ["en"] = "rules" }));
        db.Methodologies.Add(methodology);
        await db.SaveChangesAsync().ConfigureAwait(false);
        var version = new MethodologyVersion(methodology.Id, "1.0", CalculationLevel.Configuration, createdByUserId: 1, Now);
        db.MethodologyVersions.Add(version);
        await db.SaveChangesAsync().ConfigureAwait(false);
        db.MethodologyRules.Add(new MethodologyRule(
            version.Id, EcrCode.Create("CO2_L"), $$"""{"{{column}}":"{{entry.Id}}"}""", 10));
        db.CalculationBindings.Add(new CalculationBinding(a.TableDefId, column, methodology.Id, "tons", "{}"));
        await db.SaveChangesAsync().ConfigureAwait(false);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInWithProjectsAsync(app, a.ProjectId).ConfigureAwait(true);

        var body = await CoverageAsync(client, app, new Stand(methodology.Id, version.Id, column, a.ProjectId, a.ProjectId))
            .ConfigureAwait(true);
        var combination = Assert.Single(body.GetProperty("combinations").EnumerateArray());
        Assert.Equal("Covered", combination.GetProperty("state").GetString());
        Assert.Equal("CO2_L", combination.GetProperty("winnerRuleCode").GetString());
    }

    private sealed record Stand(int MethodologyId, int VersionId, int ColumnId, int ProjectA, int ProjectB);

    private static async Task<JsonElement> CoverageAsync(HttpClient client, EcrApiFactory app, Stand stand)
    {
        var response = await client.GetAsync(new Uri(
            $"/api/v1/methodologies/{stand.MethodologyId}/versions/{stand.VersionId}/rule-coverage?periodFrom=202601&periodTo=202612",
            UriKind.Relative)).ConfigureAwait(false);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {app.ErrorsText}");

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false)).RootElement;
    }

    private static List<(string? Value, string? State, string? Winner, long Rows, int Documents)> Rows(JsonElement body)
        => [.. body.GetProperty("combinations").EnumerateArray()
            .Select(c => (
                Value: c.GetProperty("values")[0].GetString(),
                State: c.GetProperty("state").GetString(),
                Winner: c.GetProperty("winnerRuleCode").ValueKind == JsonValueKind.Null ? null : c.GetProperty("winnerRuleCode").GetString(),
                Rows: c.GetProperty("rows").GetInt64(),
                Documents: c.GetProperty("documents").GetInt32()))];

    /// <summary>Користувач із <c>Calculation.View</c> і грантом <c>Read</c> рівно на названі проєкти.</summary>
    private async Task<HttpClient> SignedInWithProjectsAsync(EcrApiFactory app, params int[] projects)
    {
        var name = $"rcov_{Guid.NewGuid():N}"[..20];

        await using (var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext())
        {
            var user = new User(name, name, AuthProvider.Local);
            user.SetPassword(new PasswordHasher().Hash(Password));
            db.Users.Add(user);

            // Роль своя на кожен прогін: база спільна на всю збірку.
            var role = new Role(
                EcrCode.Create($"R{Guid.NewGuid():N}"[..12]),
                new LocalizedText(new Dictionary<string, string> { ["en"] = "Rule coverage test" }));
            db.Roles.Add(role);
            await db.SaveChangesAsync().ConfigureAwait(false);

            db.RolePermissions.Add(new RolePermission(role.Id, RuleCoverageHandler.Permission));
            db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, principalSid: null));
            foreach (var project in projects)
            {
                db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, project, GrantLevel.Read));
            }

            await db.SaveChangesAsync().ConfigureAwait(false);
        }

        var client = app.CreateClient();
        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative),
            new { userName = name, password = Password }).ConfigureAwait(false);
        Assert.True(login.IsSuccessStatusCode, $"{login.StatusCode}: {app.ErrorsText}");

        return client;
    }

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

        return new Stand(methodology.Id, version.Id, column, a.ProjectId, b.ProjectId);
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
