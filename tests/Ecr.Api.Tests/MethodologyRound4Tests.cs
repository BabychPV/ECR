// tests/Ecr.Api.Tests/MethodologyRound4Tests.cs
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
/// Відмови авторингу й публікації методології через HTTP — четвертий раунд UX,
/// лінія B1 (B-01, F-14, F-15/B-12).
/// </summary>
/// <remarks>
/// ⛔ Кожен випадок відтворено аналітиком на стенді <c>EcrUx</c>: неіснуючий
/// <c>unitId</c> — <c>500</c> на FK; автор, що публікує свою версію, бачив
/// «Period not found»; «{count} values diverged…» без числа. Тут — справжній
/// конвеєр (<c>ExceptionHandlingMiddleware</c> підставляє параметри лише
/// рядками), бо саме там і губилося число.
/// </remarks>
[Collection("SqlServer")]
public sealed class MethodologyRound4Tests(SqlServerFixture sql)
{
    private const string Password = "Api-Methodology-R4-2026!";

    private static readonly string[] Permissions =
    [
        "Calculation.View", "Calculation.EditConstant", "Calculation.EditFormula", "Calculation.EditRule",
        "Calculation.Publish",
    ];

    // ── B-01 ──────────────────────────────────────────────────────────────

    /// <remarks>
    /// Мутація: прибрати виклик <c>MethodologyUnitChecks.RequireKnownAsync</c> з
    /// відповідного обробника — рядок стає <c>500</c> (<c>FK_MC_Unit</c> /
    /// <c>FK_MO_Unit</c> / <c>FK_MF_Unit</c>).
    /// </remarks>
    [Theory]
    [InlineData("constants")]
    [InlineData("outputs")]
    [InlineData("formulas")]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Неіснуюча_одиниця_дає_422_з_ключем_а_не_500(string kind)
    {
        using var app = new EcrApiFactory(sql);
        using var client = (await SignedInAsync(app).ConfigureAwait(true)).Client;
        var stand = await StandAsync(authorUserId: 1).ConfigureAwait(true);

        object body = kind switch
        {
            "constants" => new
            {
                kind = "Numeric", value = 1.5m, unitId = 999_999, textValue = (string?)null,
                validFrom = (string?)null, validTo = (string?)null, category = (string?)null,
                substanceEntryId = (long?)null, source = (string?)null,
            },
            "outputs" => new { unitId = 999_999, ordinal = 1 },
            _ => new { expression = "CST.EF_CO2 * 2", resultType = "Number", outputUnitId = 999_999, argumentsCsv = (string?)null },
        };

        var response = await client.PutAsJsonAsync(
            new Uri($"{VersionUrl(stand)}/{kind}/R4M_X", UriKind.Relative), body).ConfigureAwait(true);

        var problem = await ProblemAsync(response, HttpStatusCode.UnprocessableEntity).ConfigureAwait(true);

        Assert.Equal("err.ECR-CALC-0422.unknownUnit", problem.GetProperty("messageKey").GetString());
        Assert.Equal(
            "R4M_X: unit 999999 does not exist in the unit catalog.",
            problem.GetProperty("detail").GetString());
    }

    // ── F-14 ──────────────────────────────────────────────────────────────

    /// <remarks>
    /// Мутація: прибрати <c>RejectAuthor</c> з <c>PublishMethodologyHandler</c> —
    /// відповідь про порожній золотий набір (<c>goldenSetEmpty</c>), а не про
    /// «чотири ока».
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Автор_що_публікує_свою_версію_отримує_пояснення_про_чотири_ока()
    {
        using var app = new EcrApiFactory(sql);
        var signedIn = await SignedInAsync(app).ConfigureAwait(true);
        using var client = signedIn.Client;
        var stand = await StandAsync(authorUserId: signedIn.UserId).ConfigureAwait(true);

        var problem = await PublishExpecting(client, stand, HttpStatusCode.Conflict).ConfigureAwait(true);

        Assert.Equal("ECR-CALC-0409", problem.GetProperty("errorCode").GetString());
        Assert.Equal("err.ECR-CALC-0409.authorCannotPublish", problem.GetProperty("messageKey").GetString());
    }

    // ── F-15 / B-12 ───────────────────────────────────────────────────────

    /// <remarks>
    /// Мутація: повернути <c>["count"] = problems.Count</c> (int) у
    /// <c>PublishMethodologyHandler.Reject</c> — у <c>detail</c> лишається сире
    /// <c>{count}</c>; прибрати <c>["problems"]</c> — переліку немає.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Відмова_перевірок_публікації_підставляє_число_й_віддає_перелік_ключами()
    {
        using var app = new EcrApiFactory(sql);
        using var client = (await SignedInAsync(app).ConfigureAwait(true)).Client;

        // Бібліотека з правилом прив'язки — рівно одна проблема
        // (`publish.problem.libraryHasRules`), без формул, тож перелік
        // відхиляє публікацію раніше за порожній золотий набір.
        var stand = await StandAsync(authorUserId: 1, library: true).ConfigureAwait(true);

        var problem = await PublishExpecting(client, stand, HttpStatusCode.UnprocessableEntity).ConfigureAwait(true);

        Assert.Equal("err.ECR-CALC-0422.publishChecksFailed", problem.GetProperty("messageKey").GetString());
        Assert.Equal("The version failed pre-publication checks (1 problems).", problem.GetProperty("detail").GetString());

        var items = problem.GetProperty("problems").EnumerateArray().ToList();
        var item = Assert.Single(items);
        Assert.Equal("publish.problem.libraryHasRules", item.GetProperty("messageKey").GetString());
        Assert.Equal("1", item.GetProperty("args").GetProperty("count").GetString());
    }

    // ── Опора ─────────────────────────────────────────────────────────────

    private static string VersionUrl(Stand stand)
        => $"/api/v1/methodologies/{stand.MethodologyId}/versions/{stand.VersionId}";

    private static async Task<JsonElement> PublishExpecting(HttpClient client, Stand stand, HttpStatusCode expected)
    {
        var response = await client.PostAsJsonAsync(
            new Uri($"{VersionUrl(stand)}/publish", UriKind.Relative),
            new { changeReason = "R4 B1", effectiveFrom = "2026-01-01" }).ConfigureAwait(false);

        return await ProblemAsync(response, expected).ConfigureAwait(false);
    }

    private static async Task<JsonElement> ProblemAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(response.StatusCode == expected, $"{response.StatusCode}: {body}");

        return JsonDocument.Parse(body).RootElement.Clone();
    }

    /// <summary>Методологія з версією-чернеткою; вміст — повз обробники.</summary>
    private async Task<Stand> StandAsync(int authorUserId, bool library = false)
    {
        await using var db = new EcrDbContext(Options());

        var unit = await db.Units.OrderBy(u => u.Id).Select(u => u.Id).FirstAsync().ConfigureAwait(false);

        var methodology = new Methodology(
            EcrCode.Create($"R4M{Guid.NewGuid():N}"[..20]),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "R4 B1" }));
        if (library)
        {
            methodology.SetKind(MethodologyKind.Library);
        }

        db.Methodologies.Add(methodology);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var version = new MethodologyVersion(
            methodology.Id, "1.0", CalculationLevel.Configuration, createdByUserId: authorUserId,
            new DateTime(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc));
        db.MethodologyVersions.Add(version);
        await db.SaveChangesAsync().ConfigureAwait(false);

        db.MethodologyConstants.Add(version.AddNumericConstant(EcrCode.Create("EF_CO2"), 2.5m, unit));

        if (library)
        {
            db.MethodologyRules.Add(new MethodologyRule(version.Id, EcrCode.Create("R_ALL"), "{}", 100));
        }

        await db.SaveChangesAsync().ConfigureAwait(false);

        return new Stand(methodology.Id, version.Id);
    }

    private DbContextOptions<EcrDbContext> Options()
        => new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString)
            .Options;

    private async Task<(HttpClient Client, int UserId)> SignedInAsync(EcrApiFactory app)
    {
        var name = $"r4m_{Guid.NewGuid():N}"[..20];
        int userId;

        await using (var db = new EcrDbContext(Options()))
        {
            var user = new User(name, name, AuthProvider.Local);
            user.SetPassword(new PasswordHasher().Hash(Password));

            db.Users.Add(user);
            await db.SaveChangesAsync().ConfigureAwait(false);
            userId = user.Id;

            var role = new Role(
                EcrCode.Create($"R{Guid.NewGuid():N}"[..12]),
                new LocalizedText(new Dictionary<string, string> { ["en"] = "R4 B1 methodology test" }));

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

        return (client, userId);
    }

    private sealed record Stand(int MethodologyId, int VersionId);
}
