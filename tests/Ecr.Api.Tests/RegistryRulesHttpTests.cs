// tests/Ecr.Api.Tests/RegistryRulesHttpTests.cs
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Ecr.Application.Registries.Rules;
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
/// Правила довідника під час запису — наскрізь через HTTP і SQL Server (RT-17a, <c>ФВ-8.18</c>,
/// FEATURE-REGISTRY-TABLES §6, §7.1, AC-5; дефект Д-4).
/// </summary>
/// <remarks>
/// Кейс (<c>RC…</c>) — батько, склад (<c>RP…</c>) — його частина (композиція по <c>CASE</c>), правило
/// <c>SUM_100</c> — на кейсі: «Σ мол.% складу = 100 ± 0.5» (шаблон «Сума дочірніх»).
/// Мутаційні докази (§9.2):
/// <list type="bullet">
/// <item>рушій не переоцінює правило батька при зміні дитини →
/// <see cref="Зміна_рядка_складу_перевіряє_Σ_кейсу"/> червоний (upsert проходить 200);</item>
/// <item><c>Warning</c> як <c>Error</c> (<c>RegistryRuleCheck.IsBlocking</c>) →
/// <see cref="Warning_зберігає_і_попереджає"/> червоний; <c>Error</c> як <c>Warning</c> →
/// <see cref="Error_у_пакеті_відхиляє_весь_пакет"/> червоний;</item>
/// <item>пакет пропускає правила → <see cref="Error_у_пакеті_відхиляє_весь_пакет"/> червоний.</item>
/// </list>
/// </remarks>
[Collection("SqlServer")]
public sealed class RegistryRulesHttpTests(SqlServerFixture sql)
{
    private const string Password = "Api-Registry-Rules-2026!";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-8.18")]
    public async Task Зміна_рядка_складу_перевіряє_Σ_кейсу()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app);
        var f = await SeedAsync(ValidationSeverity.Error);

        // Кейс без складу правило не порушує: склад ще не введено (батько — раніше за дітей).
        var (created, caseBody) = await UpsertAsync(client, f.Case, null, $"P{f.Tag}", ("NAME", "1D-2"));
        Assert.True(created == HttpStatusCode.Created, $"{created}: {caseBody}\n{app.ErrorsText}");
        Assert.Empty(caseBody.GetProperty("warnings").EnumerateArray());
        var caseId = caseBody.GetProperty("id").GetInt64();

        var (_, children) = await BatchAsync(client, f.Composition, dryRun: false,
            Row("c1", $"C1{f.Tag}", caseId, "60"),
            Row("c2", $"C2{f.Tag}", caseId, "40"));
        Assert.True(children.GetProperty("applied").GetBoolean(), children.ToString());
        var c2 = Rows(children)[1].GetProperty("entryId").GetInt64();

        // Змінено РЯДОК СКЛАДУ одним upsert: Σ = 60 + 30 = 90 — правило КЕЙСУ відхиляє запис дитини.
        var (status, problem) = await UpsertAsync(client, f.Composition, c2, $"C2{f.Tag}", ("MOL_PCT", "30"));

        Assert.True(status == HttpStatusCode.UnprocessableEntity, $"{status}: {problem}\n{app.ErrorsText}");
        Assert.Equal("ECR-REG-4221", problem.GetProperty("errorCode").GetString());
        Assert.Equal(RegistryRuleEngine.RuleViolatedErrorKey, problem.GetProperty("messageKey").GetString());
        Assert.Equal("SUM_100", problem.GetProperty("rule").GetString());
        Assert.Equal($"P{f.Tag}", problem.GetProperty("entryCode").GetString());
        var violation = Assert.Single(problem.GetProperty("violations").EnumerateArray());
        Assert.Equal(caseId, violation.GetProperty("entryId").GetInt64());
        Assert.Equal("Error", violation.GetProperty("severity").GetString());
        Assert.Equal("90", violation.GetProperty("params").GetProperty("value").GetString());

        // Відкочено все: значення дитини те саме.
        Assert.Equal(40m, await MolPctAsync(c2));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-8.18")]
    public async Task Error_у_пакеті_відхиляє_весь_пакет()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app);
        var f = await SeedAsync(ValidationSeverity.Error);
        var caseId = await CaseAsync(client, f);

        var (_, children) = await BatchAsync(client, f.Composition, dryRun: false,
            Row("c1", $"C1{f.Tag}", caseId, "60"),
            Row("c2", $"C2{f.Tag}", caseId, "40"));
        Assert.True(children.GetProperty("applied").GetBoolean(), children.ToString());
        Assert.Empty(children.GetProperty("rules").EnumerateArray());
        var c1 = Rows(children)[0].GetProperty("entryId").GetInt64();

        // dryRun: правило бачить стан ПІСЛЯ пакета й повертає Error у звіті (200) — сітка показує Σ.
        var (dryStatus, dry) = await BatchAsync(client, f.Composition, dryRun: true,
            Update("u1", c1, "50"),
            Row("n3", $"C3{f.Tag}", caseId, "0.2"));
        Assert.True(dryStatus == HttpStatusCode.OK, $"{dryStatus}: {dry}\n{app.ErrorsText}");
        Assert.False(dry.GetProperty("applied").GetBoolean());
        var reported = Assert.Single(dry.GetProperty("rules").EnumerateArray());
        Assert.Equal("Error", reported.GetProperty("severity").GetString());
        Assert.Equal(caseId, reported.GetProperty("entryId").GetInt64());
        Assert.Equal("90.2", reported.GetProperty("params").GetProperty("value").GetString());

        // Без dryRun той самий пакет — 422 з переліком; не записано нічого, і новий рядок теж.
        var (status, problem) = await BatchAsync(client, f.Composition, dryRun: false,
            Update("u1", c1, "50"),
            Row("n3", $"C3{f.Tag}", caseId, "0.2"));
        Assert.True(status == HttpStatusCode.UnprocessableEntity, $"{status}: {problem}\n{app.ErrorsText}");
        Assert.Equal("ECR-REG-4221", problem.GetProperty("errorCode").GetString());
        Assert.Equal("SUM_100", Assert.Single(problem.GetProperty("violations").EnumerateArray()).GetProperty("rule").GetString());
        Assert.Equal(60m, await MolPctAsync(c1));
        Assert.Equal(2, await CountAsync(f.Composition.Id));

        // Той самий пакет, що дає Σ = 100, записується.
        var (_, fixedBatch) = await BatchAsync(client, f.Composition, dryRun: false,
            Update("u1", c1, "50"),
            Row("n3", $"C3{f.Tag}", caseId, "10"));
        Assert.True(fixedBatch.GetProperty("applied").GetBoolean(), fixedBatch.ToString());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-8.18")]
    public async Task Warning_зберігає_і_попереджає()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app);
        var f = await SeedAsync(ValidationSeverity.Warning);
        var caseId = await CaseAsync(client, f);

        // Σ = 90: пакет записано, порушення — у rules[] звіту.
        var (status, children) = await BatchAsync(client, f.Composition, dryRun: false,
            Row("c1", $"C1{f.Tag}", caseId, "60"),
            Row("c2", $"C2{f.Tag}", caseId, "30"));
        Assert.True(status == HttpStatusCode.OK && children.GetProperty("applied").GetBoolean(), $"{status}: {children}\n{app.ErrorsText}");
        var warned = Assert.Single(children.GetProperty("rules").EnumerateArray());
        Assert.Equal("Warning", warned.GetProperty("severity").GetString());
        Assert.Equal("90", warned.GetProperty("params").GetProperty("value").GetString());
        var c2 = Rows(children)[1].GetProperty("entryId").GetInt64();

        // Upsert дитини: 200 і warnings[] у кінці RegistryEntryIdResponse; значення записано.
        var (upserted, body) = await UpsertAsync(client, f.Composition, c2, $"C2{f.Tag}", ("MOL_PCT", "20"));
        Assert.True(upserted == HttpStatusCode.OK, $"{upserted}: {body}\n{app.ErrorsText}");
        var warning = Assert.Single(body.GetProperty("warnings").EnumerateArray());
        Assert.Equal("SUM_100", warning.GetProperty("rule").GetString());
        Assert.Equal($"P{f.Tag}", warning.GetProperty("entryCode").GetString());
        Assert.Equal("registries.rules.violated", warning.GetProperty("messageKey").GetString());
        Assert.Equal("80", warning.GetProperty("params").GetProperty("value").GetString());
        Assert.Equal(20m, await MolPctAsync(c2));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-8.18")]
    public async Task Імпорт_CSV_проходить_ті_самі_правила()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app);
        var f = await SeedAsync(ValidationSeverity.Error);
        var caseId = await CaseAsync(client, f);

        var (_, children) = await BatchAsync(client, f.Composition, dryRun: false,
            Row("c1", $"C1{f.Tag}", caseId, "60"),
            Row("c2", $"C2{f.Tag}", caseId, "40"));
        Assert.True(children.GetProperty("applied").GetBoolean(), children.ToString());

        // Рядок складу з файлу доводить Σ до 105 — файл відхилено цілком (Lookup — кодом кейсу).
        var (status, problem) = await ImportAsync(client, f.Composition, $"code,CASE,MOL_PCT\r\nC3{f.Tag},P{f.Tag},5\r\n");

        Assert.True(status == HttpStatusCode.UnprocessableEntity, $"{status}: {problem}\n{app.ErrorsText}");
        Assert.Equal("ECR-REG-4221", problem.GetProperty("errorCode").GetString());
        Assert.Equal(2, await CountAsync(f.Composition.Id));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-8.18")]
    public async Task Збереження_опису_компілює_правила()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app);
        var f = await SeedAsync(ValidationSeverity.Error);

        var (typo, typoBody) = await SaveDefinitionAsync(client, f, Rule(null, "TYPO", "Expression", "ROW.T_CX > 0"));
        Assert.True(typo == HttpStatusCode.UnprocessableEntity, $"{typo}: {typoBody}\n{app.ErrorsText}");
        Assert.Equal(RegistryRuleCompiler.ExpressionInvalidKey, typoBody.GetProperty("messageKey").GetString());
        Assert.Equal("TYPO", typoBody.GetProperty("ruleCode").GetString());
        Assert.Equal(
            "expr.registryFieldUnknown",
            Assert.Single(typoBody.GetProperty("diagnostics").EnumerateArray()).GetProperty("messageKey").GetString());

        var (unique, uniqueBody) = await SaveDefinitionAsync(client, f, Rule(null, "UNIQUE", "UniqueWithin", "ROW.NAME"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unique);
        Assert.Equal(RegistryRuleCompiler.UniqueWithinReplacedKey, uniqueBody.GetProperty("messageKey").GetString());

        // Правильне правило зберігається; наявне SUM_100 — без змін.
        var (saved, savedBody) = await SaveDefinitionAsync(client, f, Rule(null, "T_POSITIVE", "Expression", "ROW.T_C > 0"));
        Assert.True(saved == HttpStatusCode.OK, $"{saved}: {savedBody}\n{app.ErrorsText}");
    }

    // ─────────────────────────────────────────────────────────────────────────

    private static object Row(string clientRowId, string code, long caseId, string molPct)
        => new { clientRowId, op = "upsert", code, values = new Dictionary<string, object?> { ["CASE"] = caseId, ["MOL_PCT"] = molPct } };

    private static object Update(string clientRowId, long id, string molPct)
        => new { clientRowId, op = "upsert", id, values = new Dictionary<string, object?> { ["MOL_PCT"] = molPct } };

    private static JsonElement[] Rows(JsonElement body) => [.. body.GetProperty("rows").EnumerateArray()];

    private static async Task<long> CaseAsync(HttpClient client, Fixture f)
    {
        var (status, body) = await UpsertAsync(client, f.Case, null, $"P{f.Tag}", ("NAME", "1D-2"));
        Assert.True(status == HttpStatusCode.Created, $"{status}: {body}");
        return body.GetProperty("id").GetInt64();
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> UpsertAsync(
        HttpClient client, Registry registry, long? id, string code, params (string Field, string Value)[] values)
    {
        var response = await client.PostAsJsonAsync(
            new Uri($"/api/v1/registries/{registry.Code}/entries", UriKind.Relative),
            new
            {
                id,
                registryDefId = registry.Id,
                code,
                display = new { values = new Dictionary<string, string> { ["en"] = code } },
                parentEntryId = (long?)null,
                values = values.ToDictionary(v => v.Field, v => (object?)v.Value),
            });
        return (response.StatusCode, await BodyAsync(response));
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> BatchAsync(
        HttpClient client, Registry registry, bool dryRun, params object[] items)
    {
        var response = await client.PostAsJsonAsync(
            new Uri($"/api/v1/registries/{registry.Code}/entries/batch?dryRun={(dryRun ? "true" : "false")}", UriKind.Relative),
            new { items });
        return (response.StatusCode, await BodyAsync(response));
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> ImportAsync(HttpClient client, Registry registry, string csv)
    {
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(csv));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        content.Add(file, "file", "entries.csv");

        var response = await client.PostAsync(
            new Uri($"/api/v1/registries/{registry.Code}/entries/import?dryRun=false", UriKind.Relative), content);
        return (response.StatusCode, await BodyAsync(response));
    }

    /// <summary>Опис кейсу як є (поля, SUM_100) плюс одне нове правило.</summary>
    private static async Task<(HttpStatusCode Status, JsonElement Body)> SaveDefinitionAsync(HttpClient client, Fixture f, object extraRule)
    {
        var response = await client.PutDefinitionAsync(
            new Uri($"/api/v1/registries/{f.Case.Code}/definition", UriKind.Relative),
            new
            {
                fields = new object[]
                {
                    new { id = f.NameFieldId, code = "NAME", nameL10n = Text("Name"), dataType = "String", ordinal = 1, isRequired = false, isKey = true, lookupRegistryDefId = (int?)null, unitId = (int?)null },
                    new { id = f.TemperatureFieldId, code = "T_C", nameL10n = Text("T"), dataType = "Decimal", ordinal = 2, isRequired = false, isKey = false, lookupRegistryDefId = (int?)null, unitId = (int?)null },
                },
                rules = new[]
                {
                    Rule(f.RuleId, "SUM_100", "Expression", f.RuleExpression, f.RuleParameters),
                    extraRule,
                },
                reason = "RT-17a",
            });
        return (response.StatusCode, await BodyAsync(response));
    }

    private static object Rule(int? id, string code, string kind, string expression, string? parameters = null)
        => new
        {
            id,
            code,
            ruleKind = kind,
            expression,
            severity = "Error",
            messageL10n = Text(code),
            parametersJson = parameters,
            isActive = true,
        };

    private static object Text(string value) => new { values = new Dictionary<string, string> { ["en"] = value } };

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private async Task<decimal?> MolPctAsync(long entryId)
    {
        await using var db = new EcrDbContext(Options());
        var field = await db.RegistryEntries.AsNoTracking().Where(e => e.Id == entryId).Select(e => e.RegistryDefId).SingleAsync();
        var molPct = await db.RegistryFieldDefs.AsNoTracking().Where(x => x.RegistryDefId == field && x.Code == "MOL_PCT").Select(x => x.Id).SingleAsync();
        return await db.RegistryValues.AsNoTracking()
            .Where(v => v.RegistryEntryId == entryId && v.RegistryFieldDefId == molPct)
            .Select(v => v.ValueNumeric)
            .SingleAsync();
    }

    private async Task<int> CountAsync(int registryDefId)
    {
        await using var db = new EcrDbContext(Options());
        return await db.RegistryEntries.CountAsync(e => e.RegistryDefId == registryDefId && !e.IsDeleted);
    }

    /// <summary>
    /// Кейс (NAME — ключове поле, T_C) і склад (CASE — композиція на кейс, MOL_PCT); правило
    /// <c>SUM_100</c> шаблону «Сума дочірніх» на кейсі із заданим рівнем.
    /// </summary>
    private async Task<Fixture> SeedAsync(ValidationSeverity severity)
    {
        var tag = $"{Guid.NewGuid():N}"[..8].ToUpperInvariant();
        await using var db = new EcrDbContext(Options());

        var @case = new RegistryDef(EcrCode.Create($"RC{tag}"), Name($"Case {tag}"), isTemporal: false);
        var composition = new RegistryDef(EcrCode.Create($"RP{tag}"), Name($"Composition {tag}"), isTemporal: false);
        db.RegistryDefs.AddRange(@case, composition);
        await db.SaveChangesAsync();

        var name = new RegistryFieldDef(@case.Id, EcrCode.Create("NAME"), Name("Name"), CellDataType.String, 1);
        name.MarkKey(true);
        var temperature = new RegistryFieldDef(@case.Id, EcrCode.Create("T_C"), Name("T"), CellDataType.Decimal, 2);
        var link = new RegistryFieldDef(composition.Id, EcrCode.Create("CASE"), Name("Case"), CellDataType.Lookup, 1);
        link.Update(Name("Case"), 1, isRequired: true);
        link.PointTo(@case.Id);
        link.ComposeInto(ParentDeletePolicy.Cascade);
        var molPct = new RegistryFieldDef(composition.Id, EcrCode.Create("MOL_PCT"), Name("mol %"), CellDataType.Decimal, 2);
        db.RegistryFieldDefs.AddRange(name, temperature, link, molPct);
        await db.SaveChangesAsync();

        var parameters = string.Create(
            CultureInfo.InvariantCulture,
            $$"""{"template":"childSum","child":"{{composition.Code}}","field":"MOL_PCT","target":100,"tolerance":0.5}""");
        var expression = RegistryRuleTemplates.ChildSumExpression(composition.Code, "CASE", "MOL_PCT", 100m, 0.5m);
        var rule = new RegistryRuleDef(
            @case.Id, EcrCode.Create("SUM_100"), RegistryRuleKind.Expression, expression, severity,
            Name("Composition must add up to 100 %"), parameters);
        db.RegistryRuleDefs.Add(rule);
        await db.SaveChangesAsync();

        return new Fixture(
            new Registry(@case.Id, @case.Code), new Registry(composition.Id, composition.Code), tag,
            name.Id, temperature.Id, rule.Id, expression, parameters);
    }

    private DbContextOptions<EcrDbContext> Options()
        => new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options;

    private static LocalizedText Name(string value)
        => new(new Dictionary<string, string> { ["en"] = value });

    private async Task<HttpClient> SignedInAsync(EcrApiFactory app)
    {
        var name = $"regr_{Guid.NewGuid():N}"[..20];

        await using (var db = new EcrDbContext(Options()))
        {
            var user = new User(name, name, AuthProvider.Local);
            user.SetPassword(new PasswordHasher().Hash(Password));
            db.Users.Add(user);
            await db.SaveChangesAsync();

            var role = new Role(EcrCode.Create($"R{Guid.NewGuid():N}"[..12]), Name("Registry rules test"));
            db.Roles.Add(role);
            await db.SaveChangesAsync();

            foreach (var permission in new[] { "Registry.View", "Registry.EditData", "Registry.EditDefinition", "Registry.Publish" })
            {
                db.RolePermissions.Add(new RolePermission(role.Id, permission));
            }

            db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, principalSid: null));
            await db.SaveChangesAsync();
        }

        var client = app.CreateClient();
        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative),
            new { userName = name, password = Password });
        Assert.True(login.IsSuccessStatusCode, $"{login.StatusCode}: {app.ErrorsText}");
        return client;
    }

    private sealed record Registry(int Id, string Code);

    private sealed record Fixture(
        Registry Case,
        Registry Composition,
        string Tag,
        int NameFieldId,
        int TemperatureFieldId,
        int RuleId,
        string RuleExpression,
        string RuleParameters);
}
