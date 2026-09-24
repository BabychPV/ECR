// tests/Ecr.Api.Tests/MethodologyAuthoringValidationTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Domain.Entities.Calculations;
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
/// Зрозумілі відмови замість <c>500</c> і замість мовчазного «збережено» в
/// авторингу методологій — V-17(b, c) і V-18 третього раунду UX (2026-09-24).
/// </summary>
/// <remarks>
/// ⛔ Кожен випадок відтворено на живому стенді <c>EcrUx</c> (лінія FREG):
/// <list type="bullet">
/// <item><c>PUT …/constants/X</c> із <c>substanceEntryId: 999999</c> — <c>500</c>
/// (<c>FK_MC_Substance</c>);</item>
/// <item><c>POST …/simulate</c> із <c>periodKey: 0</c> — <c>500</c>
/// (<c>new DateOnly(0, …)</c>), а <c>202613</c> мовчки ставав груднем;</item>
/// <item>правило з <c>{not json</c>, <c>[1,2]</c>, порожнім рядком, однаковими
/// пріоритетами, <c>{}</c> попереду конкретних — <c>200</c>, і публікація теж
/// це пропускала;</item>
/// <item>публікація не ловила <c>CST.NOPE</c>; цикл із двох формул звався
/// «3 formula(s) involved» і без назв.</item>
/// </list>
/// Мутації — у коментарях кожного тесту; усі проходять на справжньому SQL.
/// </remarks>
[Collection("SqlServer")]
public sealed class MethodologyAuthoringValidationTests(SqlServerFixture sql)
{
    private const string Password = "Api-Methodology-Valid-2026!";

    private static readonly string[] Permissions =
    [
        "Calculation.View", "Calculation.EditConstant", "Calculation.EditRule", "Calculation.Publish",
    ];

    // ── V-17(b) ───────────────────────────────────────────────────────────

    /// <remarks>
    /// Мутація: прибрати перевірку речовини в <c>SaveMethodologyConstantHandler</c>
    /// — тест червоний (<c>500</c>, <c>FK_MC_Substance</c>).
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Константа_з_неіснуючою_речовиною_дає_422_а_не_500()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app).ConfigureAwait(true);
        var stand = await StandAsync().ConfigureAwait(true);

        var response = await client.PutAsJsonAsync(
            new Uri($"{VersionUrl(stand)}/constants/EF_SUB", UriKind.Relative),
            new
            {
                kind = "Numeric",
                value = 1.5m,
                unitId = stand.UnitId,
                textValue = (string?)null,
                validFrom = (string?)null,
                validTo = (string?)null,
                category = (string?)null,
                substanceEntryId = 999_999_999L,
                source = (string?)null,
            }).ConfigureAwait(true);

        var problem = await ProblemAsync(response, HttpStatusCode.UnprocessableEntity).ConfigureAwait(true);

        Assert.Equal("err.ECR-CALC-0422.constantSubstanceNotFound", problem.GetProperty("messageKey").GetString());
        Assert.Equal(
            "Constant \"EF_SUB\": substance (registry entry) 999999999 does not exist.",
            problem.GetProperty("detail").GetString());
    }

    // ── V-17(c) ───────────────────────────────────────────────────────────

    /// <remarks>
    /// Мутація: повернути <c>Math.Clamp</c> без перевірки в
    /// <c>SimulateMethodologyHandler.PeriodDate</c> — <c>0</c> дає <c>500</c>,
    /// <c>202613</c> — <c>200</c>; обидва рядки червоні.
    /// </remarks>
    [Theory]
    [InlineData(0)]
    [InlineData(202613)]
    [InlineData(202600)]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Симуляція_з_ключем_що_не_є_місяцем_дає_422(int periodKey)
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app).ConfigureAwait(true);
        var stand = await StandAsync().ConfigureAwait(true);

        var response = await client.PostAsJsonAsync(
            new Uri($"/api/v1/methodologies/{stand.MethodologyId}/simulate", UriKind.Relative),
            new { methodologyVersionId = stand.VersionId, periodKey }).ConfigureAwait(true);

        var problem = await ProblemAsync(response, HttpStatusCode.UnprocessableEntity).ConfigureAwait(true);

        Assert.Equal("err.ECR-CALC-0422.simulatePeriodInvalid", problem.GetProperty("messageKey").GetString());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Симуляція_з_місяцем_проходить()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app).ConfigureAwait(true);
        var stand = await StandAsync().ConfigureAwait(true);

        var response = await client.PostAsJsonAsync(
            new Uri($"/api/v1/methodologies/{stand.MethodologyId}/simulate", UriKind.Relative),
            new { methodologyVersionId = stand.VersionId, periodKey = 202612 }).ConfigureAwait(true);

        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"{response.StatusCode}: {await response.Content.ReadAsStringAsync().ConfigureAwait(true)}");
    }

    // ── Опора ─────────────────────────────────────────────────────────────

    private static string VersionUrl(Stand stand)
        => $"/api/v1/methodologies/{stand.MethodologyId}/versions/{stand.VersionId}";

    private static Task<HttpResponseMessage> SaveRuleAsync(
        HttpClient client, Stand stand, string code, string matchJson, int priority)
        => client.PutAsJsonAsync(
            new Uri($"{VersionUrl(stand)}/rules/{code}", UriKind.Relative),
            new { matchJson, priority, isActive = true });

    private static async Task<JsonElement> PublishExpecting422Async(HttpClient client, Stand stand)
    {
        var response = await client.PostAsJsonAsync(
            new Uri($"{VersionUrl(stand)}/publish", UriKind.Relative),
            new { changeReason = "V-18", effectiveFrom = "2026-01-01" }).ConfigureAwait(false);

        return await ProblemAsync(response, HttpStatusCode.UnprocessableEntity).ConfigureAwait(false);
    }

    private static async Task OkAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {body}");
    }

    private static async Task<JsonElement> ProblemAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(response.StatusCode == expected, $"{response.StatusCode}: {body}");

        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private async Task<int> RuleCountAsync(int versionId)
    {
        await using var db = new EcrDbContext(Options());
        return await db.MethodologyRules.CountAsync(r => r.MethodologyVersionId == versionId).ConfigureAwait(false);
    }

    /// <summary>Методологія з версією-чернеткою; формули й правила — повз обробники.</summary>
    private async Task<Stand> StandAsync(
        IReadOnlyList<(string Code, string Expression)>? formulas = null,
        IReadOnlyList<(string Code, string Match, int Priority)>? rules = null)
    {
        await using var db = new EcrDbContext(Options());

        var unit = await db.Units.OrderBy(u => u.Id).Select(u => u.Id).FirstAsync().ConfigureAwait(false);

        var methodology = new Methodology(
            EcrCode.Create($"MV{Guid.NewGuid():N}"[..20]),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "V-17/V-18" }));
        db.Methodologies.Add(methodology);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var version = new MethodologyVersion(
            methodology.Id, "1.0", CalculationLevel.Configuration, createdByUserId: 1,
            new DateTime(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc));
        db.MethodologyVersions.Add(version);
        await db.SaveChangesAsync().ConfigureAwait(false);

        db.MethodologyConstants.Add(version.AddNumericConstant(EcrCode.Create("EF_CO2"), 2.5m, unit));

        foreach (var (code, expression) in formulas ?? [])
        {
            db.MethodologyFormulas.Add(
                version.AddFormula(EcrCode.Create(code), expression, FormulaResultType.Number, unit));
        }

        foreach (var (code, match, priority) in rules ?? [])
        {
            db.MethodologyRules.Add(new MethodologyRule(version.Id, EcrCode.Create(code), match, priority));
        }

        await db.SaveChangesAsync().ConfigureAwait(false);

        return new Stand(methodology.Id, version.Id, unit);
    }

    private DbContextOptions<EcrDbContext> Options()
        => new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString)
            .Options;

    private async Task<HttpClient> SignedInAsync(EcrApiFactory app)
    {
        var name = $"methval_{Guid.NewGuid():N}"[..20];

        await using (var db = new EcrDbContext(Options()))
        {
            var user = new User(name, name, AuthProvider.Local);
            user.SetPassword(new PasswordHasher().Hash(Password));

            db.Users.Add(user);
            await db.SaveChangesAsync().ConfigureAwait(false);

            var role = new Role(
                EcrCode.Create($"R{Guid.NewGuid():N}"[..12]),
                new LocalizedText(new Dictionary<string, string> { ["en"] = "Methodology validation test" }));

            db.Roles.Add(role);
            await db.SaveChangesAsync().ConfigureAwait(false);

            foreach (var permission in Permissions)
            {
                db.RolePermissions.Add(new RolePermission(role.Id, permission));
            }

            db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, principalSid: null));
            await db.SaveChangesAsync().ConfigureAwait(false);
        }

        var client = app.CreateClient();

        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative),
            new { userName = name, password = Password }).ConfigureAwait(false);

        Assert.True(login.IsSuccessStatusCode, $"{login.StatusCode}: {app.ErrorsText}");

        return client;
    }

    private sealed record Stand(int MethodologyId, int VersionId, int UnitId);
}
