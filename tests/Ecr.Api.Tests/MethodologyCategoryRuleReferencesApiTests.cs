// tests/Ecr.Api.Tests/MethodologyCategoryRuleReferencesApiTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
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
/// RC16-2 (P3): <c>PUT …/versions/{vid}/category-rule</c> відмовляє вже при ЗБЕРЕЖЕННІ, а не лише при публікації,
/// якщо правило посилається на аргумент, якого немає в прив'язаній таблиці (<c>@Nope</c>), на формулу, що не є
/// Row-формулою версії (<c>!X</c>), або на константу, якої немає (<c>CST.X</c>).
/// </summary>
/// <remarks>
/// ⛔ Мутація: прибрати виклик <c>MethodologyCategoryRuleChecks.Check</c> / <c>CheckArgumentTableColumns</c> у
/// <c>SaveMethodologyCategoryRuleHandler</c> — усі 422-випадки дають 200 і рядок у <c>calc.CategoryRule</c>.
/// </remarks>
[Collection("SqlServer")]
public sealed class MethodologyCategoryRuleReferencesApiTests(SqlServerFixture sql)
{
    private const string Password = "Api-Category-Rule-Refs-2026!";

    private static readonly DateTime Now = new(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Аргумент_якого_немає_в_прив_язаній_таблиці_дає_422_0438_і_правило_не_пишеться()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app).ConfigureAwait(true);
        var stand = await StandAsync(bind: true).ConfigureAwait(true);

        var response = await PutAsync(client, stand, "@Nope").ConfigureAwait(true);

        var problem = await ProblemAsync(response, HttpStatusCode.UnprocessableEntity).ConfigureAwait(true);
        Assert.Equal("err.ECR-CALC-0438.missingColumns", problem.GetProperty("messageKey").GetString());
        Assert.Contains("@Nope", problem.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, await RuleCountAsync(stand.VersionId).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Аргумент_що_є_колонкою_прив_язаної_таблиці_зберігається()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app).ConfigureAwait(true);
        var stand = await StandAsync(bind: true).ConfigureAwait(true);

        var response = await PutAsync(client, stand, "@Fuel").ConfigureAwait(true);

        await ProblemAsync(response, HttpStatusCode.OK).ConfigureAwait(true);
        Assert.Equal(1, await RuleCountAsync(stand.VersionId).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Невідома_константа_дає_422_з_іменем_і_правило_не_пишеться()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app).ConfigureAwait(true);
        var stand = await StandAsync(bind: false).ConfigureAwait(true);

        var response = await PutAsync(client, stand, "CST.NOPE_CONST").ConfigureAwait(true);

        var problem = await ProblemAsync(response, HttpStatusCode.UnprocessableEntity).ConfigureAwait(true);
        Assert.Equal("err.ECR-CALC-0422.categoryRuleUnknownConstant", problem.GetProperty("messageKey").GetString());
        Assert.Contains("NOPE_CONST", problem.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, await RuleCountAsync(stand.VersionId).ConfigureAwait(true));
    }

    [Theory]
    [InlineData("!NOPE_FORMULA")]
    [InlineData("!SUBST_FORMULA")]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Формула_якої_немає_або_що_не_Row_дає_422_і_правило_не_пишеться(string expression)
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app).ConfigureAwait(true);
        var stand = await StandAsync(bind: false).ConfigureAwait(true);

        var response = await PutAsync(client, stand, expression).ConfigureAwait(true);

        var problem = await ProblemAsync(response, HttpStatusCode.UnprocessableEntity).ConfigureAwait(true);
        Assert.Equal("err.ECR-CALC-0422.categoryRuleBadFormula", problem.GetProperty("messageKey").GetString());
        Assert.Contains(expression[1..], problem.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, await RuleCountAsync(stand.VersionId).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Row_формула_і_наявна_константа_зберігаються()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app).ConfigureAwait(true);
        var stand = await StandAsync(bind: false).ConfigureAwait(true);

        await ProblemAsync(await PutAsync(client, stand, "!ROW_FORMULA").ConfigureAwait(true), HttpStatusCode.OK)
            .ConfigureAwait(true);
        await ProblemAsync(await PutAsync(client, stand, "if(CST.K1 > 0, 'A', 'B')").ConfigureAwait(true), HttpStatusCode.OK)
            .ConfigureAwait(true);
        Assert.Equal(1, await RuleCountAsync(stand.VersionId).ConfigureAwait(true));
    }

    // ── Опора ─────────────────────────────────────────────────────────────

    private static Uri Url(Stand stand)
        => new($"/api/v1/methodologies/{stand.MethodologyId}/versions/{stand.VersionId}/category-rule", UriKind.Relative);

    private static Task<HttpResponseMessage> PutAsync(HttpClient client, Stand stand, string expression)
        => client.PutAsJsonAsync(Url(stand), new { expression });

    private static async Task<JsonElement> ProblemAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(response.StatusCode == expected, $"{response.StatusCode}: {body}");

        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private async Task<int> RuleCountAsync(int versionId)
    {
        await using var db = new EcrDbContext(Options());
        return await db.MethodologyCategoryRules.CountAsync(r => r.MethodologyVersionId == versionId).ConfigureAwait(false);
    }

    /// <summary>Чернетка з Row-формулою, формулою речовини, константою K1; <paramref name="bind"/> — з прив'язкою до таблиці з колонкою Fuel.</summary>
    private async Task<Stand> StandAsync(bool bind)
    {
        await using var db = new EcrDbContext(Options());

        var methodology = new Methodology(
            EcrCode.Create($"CRR{Guid.NewGuid():N}"[..20]),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "RC16-2 category rule refs" }));
        db.Methodologies.Add(methodology);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var version = new MethodologyVersion(methodology.Id, "1.0", CalculationLevel.Configuration, createdByUserId: 1, Now);
        db.MethodologyVersions.Add(version);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var unit = await db.Units.OrderBy(u => u.Id).Select(u => u.Id).FirstAsync().ConfigureAwait(false);
        db.MethodologyConstants.Add(version.AddNumericConstant(EcrCode.Create("K1"), 1m, unit));
        var row = version.AddFormula(EcrCode.Create("ROW_FORMULA"), "'Diesel'", FormulaResultType.Text, null);
        row.SetScope(MethodologyFormulaScope.Row);
        db.MethodologyFormulas.Add(row);
        db.MethodologyFormulas.Add(
            version.AddFormula(EcrCode.Create("SUBST_FORMULA"), "CST.K1 * 2", FormulaResultType.Number, unit));
        await db.SaveChangesAsync().ConfigureAwait(false);

        if (bind)
        {
            var tag = Guid.NewGuid().ToString("N")[..8];
            var template = new Template(EcrCode.Create($"CR{tag}"), Name("CR"), 1, Now);
            db.Templates.Add(template);
            await db.SaveChangesAsync().ConfigureAwait(false);

            var templateVersion = new TemplateVersion(template.Id, "1.0.0.0", 1, Now);
            db.TemplateVersions.Add(templateVersion);
            await db.SaveChangesAsync().ConfigureAwait(false);

            var sheet = new SheetDef(templateVersion.Id, EcrCode.Create($"S{tag}"), Name("Sheet"), 1);
            db.SheetDefs.Add(sheet);
            await db.SaveChangesAsync().ConfigureAwait(false);

            var table = new TableDef(
                sheet.Id, EcrCode.Create($"T{tag}"), Name("Table"), 1,
                TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed);
            db.TableDefs.Add(table);
            await db.SaveChangesAsync().ConfigureAwait(false);

            var fuel = new ColumnDef(table.Id, EcrCode.Create("Fuel"), Name("Fuel"), 1, CellDataType.Decimal);
            var result = new ColumnDef(table.Id, EcrCode.Create("Result"), Name("Result"), 2, CellDataType.Decimal);
            db.ColumnDefs.AddRange(fuel, result);
            await db.SaveChangesAsync().ConfigureAwait(false);

            db.CalculationBindings.Add(new CalculationBinding(table.Id, result.Id, methodology.Id, "TONS", "{}"));
            await db.SaveChangesAsync().ConfigureAwait(false);
        }

        return new Stand(methodology.Id, version.Id);
    }

    private static LocalizedText Name(string value) => new(new Dictionary<string, string> { ["en"] = value });

    private DbContextOptions<EcrDbContext> Options()
        => new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options;

    private async Task<HttpClient> SignedInAsync(EcrApiFactory app)
    {
        var name = $"catref_{Guid.NewGuid():N}"[..20];

        await using (var db = new EcrDbContext(Options()))
        {
            var user = new User(name, name, AuthProvider.Local);
            user.SetPassword(new PasswordHasher().Hash(Password));
            db.Users.Add(user);
            await db.SaveChangesAsync().ConfigureAwait(false);

            var role = new Role(EcrCode.Create($"R{Guid.NewGuid():N}"[..12]), Name("Category rule refs test"));
            db.Roles.Add(role);
            await db.SaveChangesAsync().ConfigureAwait(false);

            foreach (var permission in new[] { "Calculation.View", "Calculation.EditRule" })
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

    private sealed record Stand(int MethodologyId, int VersionId);
}
