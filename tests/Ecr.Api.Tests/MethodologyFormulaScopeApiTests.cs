// tests/Ecr.Api.Tests/MethodologyFormulaScopeApiTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Application.Calculations;
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
/// L2-1: область формули (<c>scope</c>: <c>Row</c> / <c>Substance</c>) задається через
/// <c>PUT …/formulas/{code}</c> і через пакет імпорту AF, читається у відповіді, входить у хеш вмісту
/// пакета, а правило категорії <c>!ECW_Location</c> після цього проходить перевірку публікації.
/// </summary>
/// <remarks>
/// ⛔ Мутації: прибрати <c>formula.SetScope</c> в <c>SaveMethodologyFormulaHandler</c> — червоні тести PUT;
/// прибрати <c>SetScope</c> в <c>ImportMethodologyPackageHandler</c> — червоні тести імпорту й мета-доказ;
/// прибрати область з <c>ImportVersionContent.Keys</c> — червоний
/// <see cref="Повтор_пакета_з_іншою_областю_формули_дає_409_а_не_unchanged"/>;
/// зняти <c>scope.RequireAsync</c> — червоний <see cref="Без_права_EditFormula_область_не_змінюється"/>.
/// </remarks>
[Collection("SqlServer")]
public sealed class MethodologyFormulaScopeApiTests(SqlServerFixture sql)
{
    private const string Password = "Api-Formula-Scope-2026!";

    private static readonly string[] EditorPermissions =
        ["Calculation.View", "Calculation.EditFormula", "Calculation.EditConstant"];

    private static readonly string[] ViewerPermissions = ["Calculation.View"];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task PUT_з_областю_Row_пишеться_віддається_і_читається_а_без_області_не_скидається()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, EditorPermissions).ConfigureAwait(true);
        var stand = await StandAsync().ConfigureAwait(true);

        // Без поля: нова формула - Substance (типове значення домену не змінилося).
        var created = await PutAsync(client, stand, "F1", new { expression = "1", resultType = "Number" }).ConfigureAwait(true);
        Assert.Equal("Substance", (await JsonAsync(created, HttpStatusCode.OK).ConfigureAwait(true)).GetProperty("scope").GetString());

        var row = await PutAsync(client, stand, "F1", new { expression = "1", resultType = "Number", scope = "Row" })
            .ConfigureAwait(true);
        Assert.Equal("Row", (await JsonAsync(row, HttpStatusCode.OK).ConfigureAwait(true)).GetProperty("scope").GetString());
        Assert.Equal(MethodologyFormulaScope.Row, await ScopeAsync(stand.VersionId, "F1").ConfigureAwait(true));

        // Правка виразу без `scope` область не чіпає.
        var edited = await PutAsync(client, stand, "F1", new { expression = "2", resultType = "Number" }).ConfigureAwait(true);
        Assert.Equal("Row", (await JsonAsync(edited, HttpStatusCode.OK).ConfigureAwait(true)).GetProperty("scope").GetString());
        Assert.Equal(MethodologyFormulaScope.Row, await ScopeAsync(stand.VersionId, "F1").ConfigureAwait(true));

        // Перелік формул віддає область.
        var list = await JsonAsync(
            await client.GetAsync(new Uri($"{FormulasUrl(stand)}", UriKind.Relative)).ConfigureAwait(true),
            HttpStatusCode.OK).ConfigureAwait(true);
        Assert.Equal("Row", list.EnumerateArray().Single(f => f.GetProperty("code").GetString() == "F1").GetProperty("scope").GetString());

        // Явно назад.
        var back = await PutAsync(client, stand, "F1", new { expression = "2", resultType = "Number", scope = "Substance" })
            .ConfigureAwait(true);
        Assert.Equal("Substance", (await JsonAsync(back, HttpStatusCode.OK).ConfigureAwait(true)).GetProperty("scope").GetString());
        Assert.Equal(MethodologyFormulaScope.Substance, await ScopeAsync(stand.VersionId, "F1").ConfigureAwait(true));
    }

    [Theory]
    [InlineData("\"Bogus\"")]
    [InlineData("77")]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Невідома_область_дає_422_і_формула_не_пишеться(string scopeJson)
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, EditorPermissions).ConfigureAwait(true);
        var stand = await StandAsync().ConfigureAwait(true);

        using var content = new StringContent(
            $$"""{"expression":"1","resultType":"Number","scope":{{scopeJson}}}""",
            System.Text.Encoding.UTF8,
            "application/json");
        var response = await client.PutAsync(new Uri($"{FormulasUrl(stand)}/F1", UriKind.Relative), content).ConfigureAwait(true);

        await JsonAsync(response, HttpStatusCode.UnprocessableEntity).ConfigureAwait(true);
        Assert.Null(await ScopeOrNullAsync(stand.VersionId, "F1").ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Область_77_має_ключ_каталогу_formulaScopeInvalid()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, EditorPermissions).ConfigureAwait(true);
        var stand = await StandAsync().ConfigureAwait(true);

        using var content = new StringContent(
            """{"expression":"1","resultType":"Number","scope":77}""", System.Text.Encoding.UTF8, "application/json");
        var response = await client.PutAsync(new Uri($"{FormulasUrl(stand)}/F1", UriKind.Relative), content).ConfigureAwait(true);

        var problem = await JsonAsync(response, HttpStatusCode.UnprocessableEntity).ConfigureAwait(true);
        Assert.Equal("ECR-CALC-0422", problem.GetProperty("errorCode").GetString());
        Assert.Equal("err.ECR-CALC-0422.formulaScopeInvalid", problem.GetProperty("messageKey").GetString());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Без_права_EditFormula_область_не_змінюється()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, ViewerPermissions).ConfigureAwait(true);
        var stand = await StandAsync().ConfigureAwait(true);

        var response = await PutAsync(client, stand, "F1", new { expression = "1", resultType = "Number", scope = "Row" })
            .ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null(await ScopeOrNullAsync(stand.VersionId, "F1").ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Правило_ECW_Location_після_PUT_з_Row_проходить_перевірку_а_без_Row_дає_categoryRuleBadFormula()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, EditorPermissions).ConfigureAwait(true);
        var stand = await StandAsync().ConfigureAwait(true);

        foreach (var (code, text, arguments) in Formulas())
        {
            var response = await PutAsync(
                client, stand, code, new { expression = text, resultType = "Text", argumentsCsv = arguments }).ConfigureAwait(true);
            await JsonAsync(response, HttpStatusCode.OK).ConfigureAwait(true);
        }

        await SetRuleAsync(stand.VersionId, "!ECW_Location").ConfigureAwait(true);

        // До області: усі три - Substance, правило бачить #REF.
        Assert.Contains(
            await RuleProblemsAsync(stand.VersionId).ConfigureAwait(true),
            p => p.MessageKey == "publish.problem.categoryRuleBadFormula");

        foreach (var (code, text, arguments) in Formulas())
        {
            var response = await PutAsync(
                client, stand, code, new { expression = text, resultType = "Text", argumentsCsv = arguments, scope = "Row" })
                .ConfigureAwait(true);
            await JsonAsync(response, HttpStatusCode.OK).ConfigureAwait(true);
        }

        Assert.Empty(await RuleProblemsAsync(stand.VersionId).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Пакет_імпорту_зі_scope_Row_створює_Row_формули_і_правило_ECW_Location_проходить_перевірку()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, EditorPermissions).ConfigureAwait(true);
        var code = $"FS{Guid.NewGuid():N}"[..16];

        var applied = await JsonAsync(await ImportAsync(client, Package(code, "Row")).ConfigureAwait(true), HttpStatusCode.OK)
            .ConfigureAwait(true);
        Assert.True(applied.GetProperty("applied").GetBoolean());
        var versionId = applied.GetProperty("methodologies").EnumerateArray().Single()
            .GetProperty("versions").EnumerateArray().Single().GetProperty("versionId").GetInt32();

        foreach (var (formula, _, _) in Formulas())
        {
            Assert.Equal(MethodologyFormulaScope.Row, await ScopeAsync(versionId, formula).ConfigureAwait(true));
        }

        Assert.Empty(await RuleProblemsAsync(versionId).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Пакет_без_scope_лишає_Substance_а_правило_ECW_Location_дає_categoryRuleBadFormula()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, EditorPermissions).ConfigureAwait(true);
        var code = $"FN{Guid.NewGuid():N}"[..16];

        var applied = await JsonAsync(await ImportAsync(client, Package(code, null)).ConfigureAwait(true), HttpStatusCode.OK)
            .ConfigureAwait(true);
        var versionId = applied.GetProperty("methodologies").EnumerateArray().Single()
            .GetProperty("versions").EnumerateArray().Single().GetProperty("versionId").GetInt32();

        Assert.Equal(MethodologyFormulaScope.Substance, await ScopeAsync(versionId, "ECW_Location").ConfigureAwait(true));
        Assert.Contains(
            await RuleProblemsAsync(versionId).ConfigureAwait(true),
            p => p.MessageKey == "publish.problem.categoryRuleBadFormula");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Повтор_пакета_з_іншою_областю_формули_дає_409_а_не_unchanged()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, EditorPermissions).ConfigureAwait(true);
        var code = $"FD{Guid.NewGuid():N}"[..16];

        await JsonAsync(await ImportAsync(client, Package(code, "Row")).ConfigureAwait(true), HttpStatusCode.OK)
            .ConfigureAwait(true);

        var same = await JsonAsync(await ImportAsync(client, Package(code, "Row")).ConfigureAwait(true), HttpStatusCode.OK)
            .ConfigureAwait(true);
        Assert.Equal("unchanged", same.GetProperty("outcome").GetString());

        var different = await ImportAsync(client, Package(code, null)).ConfigureAwait(true);
        var problem = await JsonAsync(different, HttpStatusCode.Conflict).ConfigureAwait(true);
        Assert.Equal(
            "draftDiffers",
            problem.GetProperty("report").GetProperty("conflicts")[0].GetProperty("kind").GetString());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Пакет_з_невідомою_областю_дає_422_і_нічого_не_пише()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, EditorPermissions).ConfigureAwait(true);
        var code = $"FB{Guid.NewGuid():N}"[..16];

        var response = await ImportAsync(client, Package(code, "Bogus")).ConfigureAwait(true);

        var problem = await JsonAsync(response, HttpStatusCode.UnprocessableEntity).ConfigureAwait(true);
        var blocker = problem.GetProperty("report").GetProperty("blockers").EnumerateArray().First();
        Assert.Equal("invalidFormulaScope", blocker.GetProperty("kind").GetString());

        await using var db = new EcrDbContext(Options());
        Assert.False(await db.Methodologies.AnyAsync(m => m.Code == code).ConfigureAwait(true));
    }

    // ── Опора ─────────────────────────────────────────────────────────────

    private static (string Code, string Text, string Arguments)[] Formulas()
        =>
        [
            ("ECW_RepairStatus", "'Repair'", string.Empty),
            ("ECW_Category", "'Cat1'", string.Empty),
            ("ECW_Location", "!ECW_RepairStatus + '_' + !ECW_Category", "!ECW_RepairStatus;!ECW_Category"),
        ];

    private static object Package(string code, string? scope)
        => new
        {
            format = "ecr-methodology-package",
            version = 1,
            library = "Common",
            methodologies = new object[]
            {
                new
                {
                    name = code,
                    versions = new[]
                    {
                        new
                        {
                            version = "V1",
                            formulas = Formulas()
                                .Select(f => (object)new Dictionary<string, object?>
                                {
                                    ["name"] = f.Code,
                                    ["version"] = "1",
                                    ["arguments"] = f.Arguments,
                                    ["text"] = f.Text,
                                    ["startDate"] = "2023-12-31T19:00:00Z",
                                    ["endDate"] = "9999-02-19T19:00:00Z",
                                    ["isAvailable"] = true,
                                    ["report"] = string.Empty,
                                    ["resultType"] = "Text",
                                    ["scope"] = scope,
                                })
                                .ToArray(),
                            constants = Array.Empty<object>(),
                            categoryRule = new { expression = "!ECW_Location" },
                        },
                    },
                },
            },
            blockers = Array.Empty<string>(),
        };

    private static Task<HttpResponseMessage> ImportAsync(HttpClient client, object package)
        => client.PostAsJsonAsync(new Uri("/api/v1/methodologies/import?dryRun=false", UriKind.Relative), package);

    private static string FormulasUrl(Stand stand)
        => $"/api/v1/methodologies/{stand.MethodologyId}/versions/{stand.VersionId}/formulas";

    private static Task<HttpResponseMessage> PutAsync(HttpClient client, Stand stand, string code, object body)
        => client.PutAsJsonAsync(new Uri($"{FormulasUrl(stand)}/{code}", UriKind.Relative), body);

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(response.StatusCode == expected, $"{response.StatusCode}: {body}");

        return string.IsNullOrEmpty(body) ? default : JsonDocument.Parse(body).RootElement.Clone();
    }

    private async Task<MethodologyFormulaScope> ScopeAsync(int versionId, string code)
        => await ScopeOrNullAsync(versionId, code).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Формули {code} у версії {versionId} немає.");

    private async Task<MethodologyFormulaScope?> ScopeOrNullAsync(int versionId, string code)
    {
        await using var db = new EcrDbContext(Options());
        var found = await db.MethodologyFormulas.AsNoTracking()
            .Where(f => f.MethodologyVersionId == versionId && f.Code == code)
            .Select(f => (MethodologyFormulaScope?)f.Scope)
            .SingleOrDefaultAsync().ConfigureAwait(false);

        return found;
    }

    private async Task SetRuleAsync(int versionId, string expression)
    {
        await using var db = new EcrDbContext(Options());
        var version = await db.MethodologyVersions.SingleAsync(v => v.Id == versionId).ConfigureAwait(false);
        db.MethodologyCategoryRules.Add(version.SetCategoryRule(null, expression, new DateTime(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc)));
        await db.SaveChangesAsync().ConfigureAwait(false);
    }

    /// <summary>Рівно та перевірка правила категорії, яку робить публікація, над формулами з бази.</summary>
    private async Task<IReadOnlyList<PublishProblem>> RuleProblemsAsync(int versionId)
    {
        await using var db = new EcrDbContext(Options());
        var formulas = await db.MethodologyFormulas.AsNoTracking()
            .Where(f => f.MethodologyVersionId == versionId).ToListAsync().ConfigureAwait(false);
        var rule = await db.MethodologyCategoryRules.AsNoTracking()
            .Where(r => r.MethodologyVersionId == versionId).Select(r => r.Expression).SingleAsync().ConfigureAwait(false);

        return MethodologyCategoryRuleChecks.Check(rule, new RealFormulaEngine(), formulas, [], [], null, null).Problems;
    }

    /// <summary>Методологія з порожньою версією-чернеткою.</summary>
    private async Task<Stand> StandAsync()
    {
        await using var db = new EcrDbContext(Options());
        var now = new DateTime(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc);

        var methodology = new Methodology(
            EcrCode.Create($"FS{Guid.NewGuid():N}"[..20]),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "L2-1 formula scope" }));
        db.Methodologies.Add(methodology);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var version = new MethodologyVersion(methodology.Id, "1.0", CalculationLevel.Configuration, createdByUserId: 1, now);
        db.MethodologyVersions.Add(version);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return new Stand(methodology.Id, version.Id);
    }

    private DbContextOptions<EcrDbContext> Options()
        => new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString)
            .Options;

    private async Task<HttpClient> SignedInAsync(EcrApiFactory app, string[] permissions)
    {
        var name = $"fscope_{Guid.NewGuid():N}"[..20];

        await using (var db = new EcrDbContext(Options()))
        {
            var user = new User(name, name, AuthProvider.Local);
            user.SetPassword(new PasswordHasher().Hash(Password));

            db.Users.Add(user);
            await db.SaveChangesAsync().ConfigureAwait(false);

            var role = new Role(
                EcrCode.Create($"R{Guid.NewGuid():N}"[..12]),
                new LocalizedText(new Dictionary<string, string> { ["en"] = "Formula scope test" }));

            db.Roles.Add(role);
            await db.SaveChangesAsync().ConfigureAwait(false);

            foreach (var permission in permissions)
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
