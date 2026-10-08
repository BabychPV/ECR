// tests/Ecr.Api.Tests/Security/DocumentStaleResultsScopeTests.cs
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests.Security;

/// <summary>
/// <c>resultsStale</c> у переліку, картці, фільтрі й зведенні: читач, якому схований аркуш (звуження ролі
/// аркушами D-214 або <c>Deny</c>), не отримує значення взагалі, а фільтр і лічильник його документ не бачать.
/// </summary>
/// <remarks>
/// «Застаріло за видимими входами» не доводить, що застарів видимий вихід - це оракул про схований аркуш.
/// Мутаційні докази: прибрати обнулення в <c>DocumentSheetVisibility.For</c> → список/картка червоні;
/// прибрати <c>NarrowedProjectIds</c> у <c>HiddenFilterAsync</c> → фільтр червоний; прибрати
/// <c>!narrowed.Contains</c> у <c>DocumentListSummaryStore</c> → зведення червоне.
/// </remarks>
[Collection("SqlServer")]
public sealed class DocumentStaleResultsScopeTests(SqlServerFixture sql)
{
    private const string Password = "Stale-Results-Scope-B!";
    private static readonly DateTime RunAt = new(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("scope")]
    [InlineData("deny")]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.14")]
    public async Task Звужений_читач_не_отримує_ні_значення_ні_документа_у_фільтрі_ні_числа_у_зведенні(string how)
    {
        var s = await ArrangeAsync(how).ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var item = (await ListAsync(client, s, string.Empty).ConfigureAwait(true)).Single(d => Id(d) == s.DocumentId);
        Assert.Equal(JsonValueKind.Null, Field(item, "resultsStale").ValueKind);
        Assert.Equal(JsonValueKind.Null, Field(item, "resultsStaleSince").ValueKind);

        var card = JsonDocument.Parse(await client.GetStringAsync(
            new Uri($"/api/v1/documents/{s.DocumentId}?periodKey={s.PeriodKey}", UriKind.Relative)).ConfigureAwait(true)).RootElement;
        Assert.Equal(JsonValueKind.Null, Field(card, "resultsStale").ValueKind);

        // Обидва напрями фільтра порожні: «не застарілі» = решта розкрило б ту саму відомість навиворіт.
        Assert.Empty(await ListAsync(client, s, "&resultsStale=true").ConfigureAwait(true));
        Assert.Empty(await ListAsync(client, s, "&resultsStale=false").ConfigureAwait(true));
        Assert.Empty(await ListAsync(client, s, "&resultsStale=true&staleBy=me").ConfigureAwait(true));

        var summary = JsonDocument.Parse(await client.GetStringAsync(
            new Uri($"/api/v1/documents/summary?periodKey={s.PeriodKey}&projectId={s.ProjectId}", UriKind.Relative)).ConfigureAwait(true)).RootElement;
        Assert.Equal(0, summary.GetProperty("staleResultsCount").GetInt32());
        Assert.Equal(0, summary.GetProperty("staleResultsMineCount").GetInt32());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.14")]
    public async Task Читач_без_обмежень_бачить_значення_фільтр_і_лічильники_контроль_сценарію()
    {
        var s = await ArrangeAsync("none").ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var item = (await ListAsync(client, s, string.Empty).ConfigureAwait(true)).Single(d => Id(d) == s.DocumentId);
        Assert.Equal(JsonValueKind.True, Field(item, "resultsStale").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, Field(item, "resultsStaleSince").ValueKind);

        Assert.Contains(await ListAsync(client, s, "&resultsStale=true").ConfigureAwait(true), d => Id(d) == s.DocumentId);
        Assert.DoesNotContain(await ListAsync(client, s, "&resultsStale=false").ConfigureAwait(true), d => Id(d) == s.DocumentId);

        // Правку зробив користувач сценарію: «мої» її бачать.
        Assert.Contains(await ListAsync(client, s, "&resultsStale=true&staleBy=me").ConfigureAwait(true), d => Id(d) == s.DocumentId);

        var summary = JsonDocument.Parse(await client.GetStringAsync(
            new Uri($"/api/v1/documents/summary?periodKey={s.PeriodKey}&projectId={s.ProjectId}", UriKind.Relative)).ConfigureAwait(true)).RootElement;
        Assert.Equal(1, summary.GetProperty("staleResultsCount").GetInt32());
        Assert.Equal(1, summary.GetProperty("staleResultsMineCount").GetInt32());
    }

    [Theory]
    [InlineData("&resultsStale=true", "ECR-REQ-0422", "resultsStaleNeedsPeriod", false)]
    [InlineData("&staleBy=me", "ECR-REQ-0422", "staleBy", true)]
    [InlineData("&resultsStale=true&staleBy=all", "ECR-REQ-0422", "staleBy", true)]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Хибні_параметри_фільтра_відхиляються_422_з_ключем_каталогу(
        string query, string code, string key, bool withPeriod)
    {
        var s = await ArrangeAsync("none").ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var period = withPeriod ? $"&periodKey={s.PeriodKey}" : string.Empty;
        using var response = await client.GetAsync(
            new Uri($"/api/v1/documents?limit=50&projectId={s.ProjectId}{period}{query}", UriKind.Relative)).ConfigureAwait(true);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);

        Assert.Equal(System.Net.HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains(code, body, StringComparison.Ordinal);
        Assert.Contains($"err.{code}.{key}", body, StringComparison.Ordinal);
    }

    private static long Id(JsonElement doc) => doc.GetProperty("id").GetInt64();

    /// <summary>Поле відповіді; відсутнє розглядається як <c>null</c> (серіалізатор може пропускати null).</summary>
    private static JsonElement Field(JsonElement doc, string name)
        => doc.TryGetProperty(name, out var value) ? value : JsonDocument.Parse("null").RootElement;

    private static async Task<List<JsonElement>> ListAsync(HttpClient client, Scenario s, string extra)
    {
        var body = await client.GetStringAsync(
            new Uri($"/api/v1/documents?limit=200&projectId={s.ProjectId}&periodKey={s.PeriodKey}{extra}", UriKind.Relative))
            .ConfigureAwait(false);
        return [.. JsonDocument.Parse(body).RootElement.GetProperty("items").EnumerateArray().Select(e => e.Clone())];
    }

    private sealed record Scenario(long DocumentId, int ProjectId, int PeriodKey, string UserName);

    private async Task<Scenario> ArrangeAsync(string how)
    {
        var b = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync().ConfigureAwait(false);
        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var userName = $"stl{Guid.NewGuid():N}"[..20];

        await using var db = Context();

        db.DocumentSheets.Add(new DocumentSheet(b.DocumentId, b.SheetDefId));
        var sheetB = new SheetDef(b.TemplateVersionId, EcrCode.Create($"STLSH{tag}"), Name($"StaleSheet{tag}"), 2);
        db.SheetDefs.Add(sheetB);
        await db.SaveChangesAsync().ConfigureAwait(false);
        db.DocumentSheets.Add(new DocumentSheet(b.DocumentId, sheetB.Id));

        var user = new User(userName, userName, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));
        db.Users.Add(user);
        var role = new Role(EcrCode.Create($"R{Guid.NewGuid():N}"[..12]), Name("STALE"));
        db.Roles.Add(role);
        await db.SaveChangesAsync().ConfigureAwait(false);

        db.RolePermissions.Add(new RolePermission(role.Id, "Document.View"));
        db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, b.ProjectId, GrantLevel.Read));
        if (how == "deny")
        {
            db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Sheet, sheetB.Id, GrantLevel.Read, isDeny: true));
        }

        var assignment = new RoleAssignment(role.Id, user.Id, principalSid: null);
        db.RoleAssignments.Add(assignment);
        await db.SaveChangesAsync().ConfigureAwait(false);

        if (how == "scope")
        {
            var scope = RoleAssignmentScope.Create([b.ProjectId], [b.SheetCode], null, null).ToJson();
            await db.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE sec.RoleAssignment SET ScopeJson = {scope} WHERE Id = {assignment.Id}")
                .ConfigureAwait(false);
        }

        // Прогін методології з числом документа, потім правка входу КОРИСТУВАЧЕМ сценарію - результати застаріли.
        var methodology = new Methodology(EcrCode.Create($"STM_{tag}"), Name("m"));
        db.Methodologies.Add(methodology);
        await db.SaveChangesAsync().ConfigureAwait(false);
        var version = new MethodologyVersion(methodology.Id, "1.0", CalculationLevel.Configuration, createdByUserId: 1, RunAt);
        db.MethodologyVersions.Add(version);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var run = new CalculationRun(b.ProjectId, b.PeriodKey.Value, triggeredByUserId: null, RunAt);
        run.Complete("Succeeded", RunAt.AddMinutes(1), "{}", errorMessage: null);
        run.MakeCurrent();
        db.CalculationRuns.Add(run);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var unitId = await db.Units.AsNoTracking().OrderBy(u => u.Id).Select(u => u.Id).FirstAsync().ConfigureAwait(false);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO calc.CalculationResult
                (Id, CalculationRunId, MethodologyVersionId, PeriodKey, DocumentId, SourceRowKey, OutputCode, Value, UnitId)
            VALUES (NEXT VALUE FOR calc.CalculationResultSeq, {run.Id}, {version.Id},
                    {b.PeriodKey.Value}, {b.DocumentId}, N'row-1', N'E_CO2', CAST(1 AS decimal(34,16)), {unitId})
            """).ConfigureAwait(false);

        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO aud.CellChange
                (ChangedAt, PeriodKey, DocumentId, TableRowId, RowKey, ColumnDefId,
                 OldValue, NewValue, ChangedByUserId, Origin, IsLateEdit)
            VALUES (@at, @period, @document, 1, N'R1', @column, N'1', N'2', @user, N'UserEdit', 0);
            """;
        command.Parameters.AddWithValue("@at", RunAt.AddHours(1));
        command.Parameters.AddWithValue("@period", b.PeriodKey.Value);
        command.Parameters.AddWithValue("@document", b.DocumentId);
        command.Parameters.AddWithValue("@column", b.ColumnDefIds[1]);
        command.Parameters.AddWithValue("@user", user.Id);
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);

        return new Scenario(b.DocumentId, b.ProjectId, b.PeriodKey.Value, userName);
    }

    private static async Task<HttpClient> SignedInAsync(EcrApiFactory app, string userName)
    {
        var client = app.CreateClient();
        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative),
            new { userName, password = Password }).ConfigureAwait(false);
        Assert.True(login.IsSuccessStatusCode, $"{login.StatusCode}: {app.ErrorsText}");

        return client;
    }

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

    private static LocalizedText Name(string value)
        => new(new Dictionary<string, string> { ["en"] = value });
}
