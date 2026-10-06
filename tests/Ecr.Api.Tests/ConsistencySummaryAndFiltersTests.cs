using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// <c>GET /api/v1/consistency/summary</c> і фільтри <c>q</c>/<c>severity</c> у
/// <c>GET /api/v1/consistency/issues</c> — основа смуги показників і пошуку
/// екрана «Consistency issues» (UI-20, LS-E).
/// </summary>
/// <remarks>
/// ⛔ Журнал <c>aud.ConsistencyIssue</c> спільний для всієї колекції
/// <c>SqlServer</c>, тож абсолютних чисел тест не стверджує: він міряє
/// ПРИРІСТ лічильників навколо власних рядків із унікальним маркером. Рядки
/// пишуться напряму — задачі перевірки для цього не потрібна, а вага й
/// закритість мають бути рівно такими, як треба твердженню.
/// </remarks>
[Collection("SqlServer")]
public sealed class ConsistencySummaryAndFiltersTests(SqlServerFixture sql)
{
    private const string Password = "Api-Consistency-Summary-2026!";
    private const string ViewHealth = "System.ViewHealth";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Лічильники_зростають_рівно_на_свої_знахідки_за_вагою_і_закритістю()
    {
        var marker = NewMarker();
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, ViewHealth).ConfigureAwait(true);

        var allBefore = await SummaryAsync(client, openOnly: false).ConfigureAwait(true);
        var openBefore = await SummaryAsync(client, openOnly: true).ConfigureAwait(true);

        // 2 інформації, 1 попередження, 3 помилки (одну з них закрито).
        await InsertAsync(marker, severity: 1, resolved: false).ConfigureAwait(true);
        await InsertAsync(marker, severity: 1, resolved: false).ConfigureAwait(true);
        await InsertAsync(marker, severity: 2, resolved: false).ConfigureAwait(true);
        await InsertAsync(marker, severity: 3, resolved: false).ConfigureAwait(true);
        await InsertAsync(marker, severity: 3, resolved: false).ConfigureAwait(true);
        await InsertAsync(marker, severity: 3, resolved: true).ConfigureAwait(true);

        var allAfter = await SummaryAsync(client, openOnly: false).ConfigureAwait(true);
        var openAfter = await SummaryAsync(client, openOnly: true).ConfigureAwait(true);

        Assert.Equal((2, 1, 3, 6), Delta(allBefore, allAfter));
        Assert.Equal((2, 1, 2, 5), Delta(openBefore, openAfter));

        // ⚠ Знахідка щойно записана, тож «остання» не може бути старшою за неї.
        Assert.NotNull(allAfter.LastDetectedAt);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Без_права_лічильники_відмовляють_а_не_віддають_нулі()
    {
        using var app = new EcrApiFactory(sql);
        var address = new Uri("/api/v1/consistency/summary", UriKind.Relative);

        using (var stranger = await SignedInAsync(app).ConfigureAwait(true))
        {
            var denied = await stranger.GetAsync(address).ConfigureAwait(true);

            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            Assert.Contains(
                ViewHealth,
                await denied.Content.ReadAsStringAsync().ConfigureAwait(true),
                StringComparison.Ordinal);
        }

        // Друга половина доказу: з правом та сама адреса дає 200.
        using var allowed = await SignedInAsync(app, ViewHealth).ConfigureAwait(true);
        var granted = await allowed.GetAsync(address).ConfigureAwait(true);

        Assert.True(granted.IsSuccessStatusCode, $"{granted.StatusCode}: {app.ErrorsText}");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Фільтр_ваги_лишає_лише_свою_вагу_а_невідома_вага_відхиляється()
    {
        var marker = NewMarker();
        await InsertAsync(marker, severity: 1, resolved: false).ConfigureAwait(true);
        await InsertAsync(marker, severity: 2, resolved: false).ConfigureAwait(true);
        await InsertAsync(marker, severity: 3, resolved: false).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, ViewHealth).ConfigureAwait(true);

        var errors = await IssuesAsync(client, $"q={marker}&severity=3&limit=50").ConfigureAwait(true);

        // Лічильники — за фільтрами без ваги (вкладки), totalCount — з вагою.
        Assert.Equal(1, errors.Root.GetProperty("totalCount").GetInt32());
        var totals = errors.Root.GetProperty("totals");
        Assert.Equal(1, totals.GetProperty("info").GetInt32());
        Assert.Equal(1, totals.GetProperty("warnings").GetInt32());
        Assert.Equal(1, totals.GetProperty("errors").GetInt32());

        var only = Assert.Single(errors.Items);
        Assert.Equal(3, only.GetProperty("severity").GetInt32());

        // Без ваги — усі три власні рядки.
        var all = await IssuesAsync(client, $"q={marker}&limit=50").ConfigureAwait(true);
        Assert.Equal(3, all.Items.Count);

        // ⛔ Невідома вага — 422 із ключем каталогу, а не мовчазне «усі»: порожній
        // екран на друкарську помилку читався б як «таких знахідок немає».
        foreach (var bad in new[] { "0", "4", "-1" })
        {
            var response = await client
                .GetAsync(new Uri($"/api/v1/consistency/issues?severity={bad}", UriKind.Relative))
                .ConfigureAwait(true);

            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

            var problem = JsonDocument
                .Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(true)).RootElement;

            Assert.Equal("ECR-REQ-0422", problem.GetProperty("errorCode").GetString());
            Assert.Equal("err.ECR-REQ-0422.consistencySeverity", problem.GetProperty("messageKey").GetString());
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Пошук_іде_запитом_екранує_шаблон_і_не_ламає_курсор()
    {
        var marker = NewMarker();

        // Два рядки відрізняються лише символом після «50»: без екранування
        // `%` у запиті «50%» знайшов би обидва.
        var percent = await InsertAsync(marker, 2, false, $"{marker} 50%").ConfigureAwait(true);
        var plain = await InsertAsync(marker, 2, false, $"{marker} 50X").ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, ViewHealth).ConfigureAwait(true);

        var literal = await IssuesAsync(
            client, $"q={Uri.EscapeDataString(marker + " 50%")}&limit=50").ConfigureAwait(true);

        var hit = Assert.Single(literal.Items);
        Assert.Equal(percent, hit.GetProperty("id").GetInt64());

        // `_` теж буквальний: «5_» не повинно збігатися з «50».
        var underscore = await IssuesAsync(
            client, $"q={Uri.EscapeDataString(marker + " 5_")}&limit=50").ConfigureAwait(true);
        Assert.Empty(underscore.Items);

        // Пошук за номером сутності.
        var byEntity = await IssuesAsync(
            client, $"q={plain}&limit=50").ConfigureAwait(true);
        Assert.Contains(byEntity.Items, i => i.GetProperty("id").GetInt64() == plain);

        // ⛔ Фільтр у запиті, а не по сторінці: два збіги при limit=1 дають
        // сторінку з одним елементом І курсор далі. Постфільтр по сторінці
        // віддав би хибну порожню/неповну сторінку без курсора.
        var firstPage = await IssuesAsync(client, $"q={marker}&limit=1").ConfigureAwait(true);
        Assert.Single(firstPage.Items);
        Assert.NotNull(firstPage.NextCursor);

        var secondPage = await IssuesAsync(
            client, $"q={marker}&limit=1&cursor={Uri.EscapeDataString(firstPage.NextCursor!)}").ConfigureAwait(true);
        Assert.Single(secondPage.Items);
        Assert.NotEqual(
            firstPage.Items[0].GetProperty("id").GetInt64(),
            secondPage.Items[0].GetProperty("id").GetInt64());

        // Довгий рядок обрізається, а не валить запит.
        var longQuery = await client
            .GetAsync(new Uri($"/api/v1/consistency/issues?q={new string('x', 400)}&limit=5", UriKind.Relative))
            .ConfigureAwait(true);
        Assert.True(longQuery.IsSuccessStatusCode, $"{longQuery.StatusCode}: {app.ErrorsText}");
    }

    private static string NewMarker() => $"LSE{Guid.NewGuid():N}"[..16];

    private static (int Info, int Warnings, int Errors, int Total) Delta(Summary before, Summary after)
        => (after.Info - before.Info,
            after.Warnings - before.Warnings,
            after.Errors - before.Errors,
            after.Total - before.Total);

    private sealed record Summary(int Info, int Warnings, int Errors, int Total, DateTime? LastDetectedAt);

    private sealed record Page(List<JsonElement> Items, string? NextCursor, JsonElement Root);

    private static async Task<Summary> SummaryAsync(HttpClient client, bool openOnly)
    {
        var response = await client
            .GetAsync(new Uri(
                $"/api/v1/consistency/summary?openOnly={openOnly.ToString().ToLowerInvariant()}",
                UriKind.Relative))
            .ConfigureAwait(false);

        Assert.True(response.IsSuccessStatusCode, response.StatusCode.ToString());

        var root = JsonDocument
            .Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false)).RootElement;

        // ⚠ Імена полів — частина контракту; зміна імені тут червоніє.
        var last = root.GetProperty("lastDetectedAt");

        return new Summary(
            root.GetProperty("info").GetInt32(),
            root.GetProperty("warnings").GetInt32(),
            root.GetProperty("errors").GetInt32(),
            root.GetProperty("total").GetInt32(),
            last.ValueKind == JsonValueKind.Null ? null : last.GetDateTime());
    }

    private static async Task<Page> IssuesAsync(HttpClient client, string query)
    {
        var response = await client
            .GetAsync(new Uri($"/api/v1/consistency/issues?{query}", UriKind.Relative))
            .ConfigureAwait(false);

        Assert.True(
            response.IsSuccessStatusCode,
            $"{response.StatusCode}: {await response.Content.ReadAsStringAsync().ConfigureAwait(false)}");

        var root = JsonDocument
            .Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false)).RootElement;

        var cursor = root.GetProperty("nextCursor");

        return new Page(
            root.GetProperty("items").EnumerateArray().ToList(),
            cursor.ValueKind == JsonValueKind.Null ? null : cursor.GetString(),
            root.Clone());
    }

    /// <summary>Вставляє знахідку напряму і повертає її <c>Id</c> (він же <c>EntityId</c>).</summary>
    private async Task<long> InsertAsync(string marker, byte severity, bool resolved, string? message = null)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync(CancellationToken.None).ConfigureAwait(false);

        await using var command = connection.CreateCommand();

        // ⚠ `EntityId` = власний `Id` через другий крок: так пошук за номером
        // сутності має що знаходити, а номер унікальний для рядка.
        command.CommandText = """
            INSERT aud.ConsistencyIssue
                (DetectedAt, Severity, RuleCode, EntityType, EntityId, Message, ResolvedAt)
            VALUES (SYSUTCDATETIME(), @severity, @rule, N'test.Entity', NULL, @message,
                    CASE WHEN @resolved = 1 THEN SYSUTCDATETIME() END);
            DECLARE @id bigint = CAST(SCOPE_IDENTITY() AS bigint);
            UPDATE aud.ConsistencyIssue SET EntityId = @id WHERE Id = @id;
            SELECT @id;
            """;
        command.Parameters.AddWithValue("@severity", severity);
        command.Parameters.AddWithValue("@rule", "TEST_" + marker);
        command.Parameters.AddWithValue("@message", message ?? $"{marker} знахідка");
        command.Parameters.AddWithValue("@resolved", resolved ? 1 : 0);

        return (long)(await command.ExecuteScalarAsync(CancellationToken.None).ConfigureAwait(false))!;
    }

    private async Task<HttpClient> SignedInAsync(EcrApiFactory app, params string[] permissions)
    {
        var name = $"cons_{Guid.NewGuid():N}"[..20];

        var options = new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString)
            .Options;

        await using (var db = new EcrDbContext(options))
        {
            var user = new User(name, name, AuthProvider.Local);
            user.SetPassword(new PasswordHasher().Hash(Password));

            db.Users.Add(user);
            await db.SaveChangesAsync().ConfigureAwait(false);

            if (permissions.Length > 0)
            {
                var role = new Role(
                    Ecr.Domain.ValueObjects.EcrCode.Create($"R{Guid.NewGuid():N}"[..12]),
                    new Ecr.Domain.ValueObjects.LocalizedText(
                        new Dictionary<string, string> { ["en"] = "Consistency summary test" }));
                db.Roles.Add(role);
                await db.SaveChangesAsync().ConfigureAwait(false);

                foreach (var permission in permissions)
                {
                    db.RolePermissions.Add(new RolePermission(role.Id, permission));
                }

                db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, principalSid: null));
                await db.SaveChangesAsync().ConfigureAwait(false);
            }
        }

        var client = app.CreateClient();

        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative),
            new { userName = name, password = Password }).ConfigureAwait(false);

        Assert.True(login.IsSuccessStatusCode, $"{login.StatusCode}: {app.ErrorsText}");

        return client;
    }
}
