using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// Скрипт методології через HTTP (MS T3.2): створення версії-скрипта, збереження з діагностиками й матеріалізацією,
/// конфлікт за хешем, відмова ручної правки (<c>scriptManaged</c>), права, перегляд на документі й публікація
/// до кроку золотого набору з фейковим модулем.
/// </summary>
/// <remarks>
/// ⚠ Справжній шлях «публікація → перерахунок → значення» перевірить інтеграційний тест після злиття рушія
/// виконання: тут модуль скрипта — фейк, що лише відповідає на <c>CanHandle</c>.
/// </remarks>
[Collection("SqlServer")]
public sealed class MethodologyScriptApiTests(SqlServerFixture sql)
{
    private const string Password = "Api-Script-Tests-2026!";

    private static readonly DateTime Now = new(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc);

    private static readonly string[] Editor = ["Calculation.View", "Calculation.EditFormula"];

    private sealed record Stand(TestDocument Document, string TableCode, string TextColumn, string NumberColumn, int CalculatedColumnId)
    {
        public string SecondTableCode { get; init; } = string.Empty;

        public string SecondNumberColumn { get; init; } = string.Empty;
    }

    private sealed record Created(int MethodologyId, int VersionId, string Code);

    // ---------- стенд ----------

    private async Task<Stand> ArrangeAsync(bool secondTable = false)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(periodKey: 209901, columnCount: 3).ConfigureAwait(false);

        string secondCode = string.Empty;
        string secondColumn = string.Empty;
        if (secondTable)
        {
            var second = await builder.AddTableAsync(doc, TableLayoutKind.PerPeriodInstance, monthColumns: 0, columnCount: 2).ConfigureAwait(false);
            await using var read = builder.CreateContext();
            secondCode = (await read.TableDefs.AsNoTracking().SingleAsync(t => t.Id == second.TableDefId).ConfigureAwait(false)).Code;
            secondColumn = (await read.ColumnDefs.AsNoTracking().Where(c => c.TableDefId == second.TableDefId).OrderBy(c => c.Ordinal).ToListAsync().ConfigureAwait(false))[1].Code;
        }

        await using var db = builder.CreateContext();
        var table = await db.TableDefs.AsNoTracking().SingleAsync(t => t.Id == doc.TableDefId).ConfigureAwait(false);
        var columns = await db.ColumnDefs.AsNoTracking().Where(c => c.TableDefId == doc.TableDefId).OrderBy(c => c.Ordinal).ToListAsync().ConfigureAwait(false);

        var calculated = new ColumnDef(
            table.Id, EcrCode.Create("OUT_A"), new LocalizedText(new Dictionary<string, string> { ["en"] = "Out A" }), 20, CellDataType.Calculated);
        db.ColumnDefs.Add(calculated);
        var version = await db.TemplateVersions.SingleAsync(v => v.Id == doc.TemplateVersionId).ConfigureAwait(false);
        version.Publish(1, Now);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return new Stand(doc, table.Code, columns[0].Code, columns[1].Code, calculated.Id)
        {
            SecondTableCode = secondCode,
            SecondNumberColumn = secondColumn,
        };
    }

    private async Task<(HttpClient Client, int UserId)> SignInAsync(
        WebApplicationFactory<Program> app, string[] permissions, int? grantProjectId = null)
    {
        var name = $"msc_{Guid.NewGuid():N}"[..20];
        int userId;

        await using (var db = new EcrDbContext(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options))
        {
            var user = new User(name, name, AuthProvider.Local);
            user.SetPassword(new PasswordHasher().Hash(Password));
            db.Users.Add(user);
            await db.SaveChangesAsync().ConfigureAwait(false);
            userId = user.Id;

            if (permissions.Length > 0)
            {
                var role = new Role(
                    EcrCode.Create($"R{Guid.NewGuid():N}"[..12]),
                    new LocalizedText(new Dictionary<string, string> { ["en"] = "Script tests" }));
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

        if (grantProjectId is { } projectId)
        {
            await new TestDocumentBuilder(sql.ConnectionString).GrantProjectAsync(userId, projectId, GrantLevel.Read).ConfigureAwait(false);
        }

        var client = app.CreateClient();
        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative), new { userName = name, password = Password }).ConfigureAwait(false);
        Assert.True(login.IsSuccessStatusCode, $"{login.StatusCode}");
        return (client, userId);
    }

    // ---------- HTTP-помічники ----------

    private static async Task<JsonDocument> JsonAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {body}");
        return JsonDocument.Parse(body);
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string url, object body)
        => client.PostAsJsonAsync(new Uri(url, UriKind.Relative), body);

    private static Task<HttpResponseMessage> PutAsync(HttpClient client, string url, object body)
        => client.PutAsJsonAsync(new Uri(url, UriKind.Relative), body);

    private static Task<HttpResponseMessage> GetAsync(HttpClient client, string url)
        => client.GetAsync(new Uri(url, UriKind.Relative));

    private static string Script(int methodologyId, int versionId, string tail = "")
        => $"/api/v1/methodologies/{methodologyId}/versions/{versionId}/script{tail}";

    private async Task<Created> CreateScriptVersionAsync(HttpClient client, string? engine = "Script", string? tagOverride = null, int kind = 0)
    {
        var tag = tagOverride ?? Guid.NewGuid().ToString("N")[..10];
        var code = $"MS{tag}";
        using var methodology = await JsonAsync(
            await PostAsync(client, "/api/v1/methodologies", new
            {
                code,
                nameL10n = new Dictionary<string, string> { ["en"] = "Script methodology" },
                kind,
                group = (string?)null,
            }).ConfigureAwait(false),
            HttpStatusCode.Created).ConfigureAwait(false);
        var methodologyId = methodology.RootElement.GetProperty("id").GetInt32();

        using var version = await JsonAsync(
            await PostAsync(client, $"/api/v1/methodologies/{methodologyId}/versions", new
            {
                versionNumber = "1.0",
                copyFromVersionId = (int?)null,
                level = "Configuration",
                engine,
            }).ConfigureAwait(false),
            HttpStatusCode.OK).ConfigureAwait(false);
        return new Created(methodologyId, version.RootElement.GetProperty("id").GetInt32(), code);
    }

    private static string GoodText(Stand stand)
        => $$"""
            rows from {{stand.TableCode}} where row.{{stand.TextColumn}} == "x";
            const k1 = 5;
            const EF = { Summer: { NOx: 1.2 }, Winter: { NOx: 1.4 } };
            function twice(a) = a * 2;
            var amount = row.{{stand.NumberColumn}};
            var season = period.Month >= 4 && period.Month <= 9 ? "Summer" : "Winter";
            output OUT_A : t = twice(amount) * k1 * EF[season].NOx;
            """;

    /// <summary>Прибирає справжній модуль скрипта: фейк тесту має відповідати на <c>CanHandle</c> першим.</summary>
    private static void WithoutRealScriptModule(IServiceCollection services)
    {
        // ⚠ Модуль скрипта зареєстровано ФАБРИКОЮ (`p => p.GetRequiredService<ScriptCalculationModule>()`), тож
        // `ImplementationType` у нього порожній: звірка лише за типом реалізації його не знаходила, справжній модуль
        // лишався першим за `CanHandle` і золотий набір ішов у нього (404 `ECR-DOC-0404` «Table instance 0»).
        foreach (var descriptor in services
                     .Where(d => d.ServiceType == typeof(ICalculationModule)
                                 && (d.ImplementationType == typeof(Ecr.Calculations.ScriptCalculationModule)
                                     || (d.ImplementationFactory is not null && d.ImplementationInstance is null)))
                     .ToList())
        {
            services.Remove(descriptor);
        }
    }

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

    // ---------- тести ----------

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "MS-T3.2")]
    public async Task Версія_скрипт_заготовка_помилка_з_позицією_виправлення_структура_конфлікт_за_хешем()
    {
        var stand = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        var (editor, _) = await SignInAsync(app, Editor).ConfigureAwait(true);
        var created = await CreateScriptVersionAsync(editor).ConfigureAwait(true);

        // Заготовка нової порожньої версії-скрипта; лише одна діагностика — «немає rows».
        using (var starter = await JsonAsync(await GetAsync(editor, Script(created.MethodologyId, created.VersionId)).ConfigureAwait(true), HttpStatusCode.OK).ConfigureAwait(true))
        {
            Assert.Equal("Script", starter.RootElement.GetProperty("engine").GetString());
            Assert.True(starter.RootElement.GetProperty("isDraft").GetBoolean());
            Assert.Contains("rows from", starter.RootElement.GetProperty("text").GetString(), StringComparison.Ordinal);
            Assert.Equal(64, starter.RootElement.GetProperty("hash").GetString()!.Length);
            Assert.Equal(
                ["missingRows"],
                starter.RootElement.GetProperty("diagnostics").EnumerateArray().Select(d => d.GetProperty("code").GetString()));
        }

        // PUT з помилкою: 200, діагностика з рядком/колонкою/довжиною і ключем каталогу; нічого не матеріалізовано.
        var broken = $"rows from {stand.TableCode};\nvar a = row.NoSuchColumn;\noutput OUT_A : t = a;";
        using (var withError = await JsonAsync(
                   await PutAsync(editor, Script(created.MethodologyId, created.VersionId), new { text = broken }).ConfigureAwait(true),
                   HttpStatusCode.OK).ConfigureAwait(true))
        {
            var diagnostic = Assert.Single(withError.RootElement.GetProperty("diagnostics").EnumerateArray());
            Assert.Equal("unknownColumn", diagnostic.GetProperty("code").GetString());
            Assert.Equal("script.unknownColumn", diagnostic.GetProperty("messageKey").GetString());
            Assert.Equal(2, diagnostic.GetProperty("line").GetInt32());
            Assert.Equal(9, diagnostic.GetProperty("column").GetInt32());
            Assert.True(diagnostic.GetProperty("length").GetInt32() > 0);
            Assert.Equal("NoSuchColumn", diagnostic.GetProperty("params").GetProperty("column").GetString());
            Assert.False(string.IsNullOrEmpty(diagnostic.GetProperty("message").GetString()));
        }

        await using (var db = Context())
        {
            Assert.Equal(0, await db.MethodologyOutputs.CountAsync(o => o.MethodologyVersionId == created.VersionId).ConfigureAwait(true));
            Assert.Equal(broken, (await db.MethodologyVersions.AsNoTracking().SingleAsync(v => v.Id == created.VersionId).ConfigureAwait(true)).ScriptText);
        }

        // PUT правильного тексту: без діагностик, структура, виходи матеріалізовано.
        var good = GoodText(stand);
        string hash;
        using (var ok = await JsonAsync(
                   await PutAsync(editor, Script(created.MethodologyId, created.VersionId), new { text = good }).ConfigureAwait(true),
                   HttpStatusCode.OK).ConfigureAwait(true))
        {
            Assert.Empty(ok.RootElement.GetProperty("diagnostics").EnumerateArray());
            hash = ok.RootElement.GetProperty("hash").GetString()!;

            var outline = ok.RootElement.GetProperty("outline");
            var table = Assert.Single(outline.GetProperty("tables").EnumerateArray());
            Assert.Equal(stand.TableCode, table.GetProperty("code").GetString());
            Assert.Equal(JsonValueKind.Null, table.GetProperty("alias").ValueKind);
            Assert.Contains(outline.GetProperty("inputs").EnumerateArray(), i =>
                i.GetProperty("name").GetString() == stand.NumberColumn && !i.GetProperty("optional").GetBoolean());
            var output = Assert.Single(outline.GetProperty("outputs").EnumerateArray());
            Assert.Equal("OUT_A", output.GetProperty("code").GetString());
            Assert.Equal("t", output.GetProperty("unit").GetString());
            Assert.False(output.GetProperty("perSubstance").GetBoolean());
            Assert.Equal(
                [("k1", "scalar"), ("EF", "map")],
                outline.GetProperty("constants").EnumerateArray().Select(c => (c.GetProperty("name").GetString()!, c.GetProperty("kind").GetString()!)));
            Assert.Equal(
                ["amount", "season"],
                outline.GetProperty("variables").EnumerateArray().Select(v => v.GetProperty("name").GetString()));
            var function = Assert.Single(outline.GetProperty("functions").EnumerateArray());
            Assert.Equal("twice", function.GetProperty("name").GetString());
            Assert.Equal(["a"], function.GetProperty("parameters").EnumerateArray().Select(p => p.GetString()));
            Assert.True(function.GetProperty("line").GetInt32() > 0);
        }

        await using (var db = Context())
        {
            var output = await db.MethodologyOutputs.AsNoTracking().SingleAsync(o => o.MethodologyVersionId == created.VersionId).ConfigureAwait(true);
            Assert.Equal("OUT_A", output.Code);
            Assert.Equal(1, output.Ordinal);
            Assert.False(output.IsPerSubstance);
        }

        // GET віддає той самий хеш, а validate нічого не зберігає.
        using (var read = await JsonAsync(await GetAsync(editor, Script(created.MethodologyId, created.VersionId)).ConfigureAwait(true), HttpStatusCode.OK).ConfigureAwait(true))
        {
            Assert.Equal(hash, read.RootElement.GetProperty("hash").GetString());
        }

        using (var validated = await JsonAsync(
                   await PostAsync(editor, Script(created.MethodologyId, created.VersionId, "/validate"), new { text = "var = 1;" }).ConfigureAwait(true),
                   HttpStatusCode.OK).ConfigureAwait(true))
        {
            Assert.NotEmpty(validated.RootElement.GetProperty("diagnostics").EnumerateArray());
        }

        await using (var db = Context())
        {
            Assert.Equal(good, (await db.MethodologyVersions.AsNoTracking().SingleAsync(v => v.Id == created.VersionId).ConfigureAwait(true)).ScriptText);
        }

        // Конфлікт за хешем: чужий хеш — 409 scriptChanged, текст не змінено; свій — 200.
        using (var response = await PutAsync(
                   editor, Script(created.MethodologyId, created.VersionId), new { text = good + "\n// ще", expectedHash = new string('0', 64) }).ConfigureAwait(true))
        {
            using var problem = await JsonAsync(response, HttpStatusCode.Conflict).ConfigureAwait(true);
            Assert.Equal("ECR-CALC-0409", problem.RootElement.GetProperty("errorCode").GetString());
            Assert.Equal("err.ECR-CALC-0409.scriptChanged", problem.RootElement.GetProperty("messageKey").GetString());
        }

        using (var accepted = await JsonAsync(
                   await PutAsync(editor, Script(created.MethodologyId, created.VersionId), new { text = good + "\n// ще", expectedHash = hash }).ConfigureAwait(true),
                   HttpStatusCode.OK).ConfigureAwait(true))
        {
            Assert.NotEqual(hash, accepted.RootElement.GetProperty("hash").GetString());
        }

        // Перелік версій несе рушій.
        using var versions = await JsonAsync(
            await GetAsync(editor, $"/api/v1/methodologies/{created.MethodologyId}/versions").ConfigureAwait(true), HttpStatusCode.OK).ConfigureAwait(true);
        Assert.Equal("Script", Assert.Single(versions.RootElement.EnumerateArray()).GetProperty("engine").GetString());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "MS-T3.2")]
    public async Task Ручна_правка_формул_констант_категорій_правил_речовин_виходів_версії_скрипта_409_scriptManaged()
    {
        using var app = new EcrApiFactory(sql);
        var (editor, _) = await SignInAsync(
            app, [.. Editor, "Calculation.EditConstant", "Calculation.EditRule"]).ConfigureAwait(true);
        var created = await CreateScriptVersionAsync(editor).ConfigureAwait(true);
        var root = $"/api/v1/methodologies/{created.MethodologyId}/versions/{created.VersionId}";

        var attempts = new (string Name, HttpMethod Method, string Path, object? Body)[]
        {
            ("PUT formula", HttpMethod.Put, "/formulas/tons", new { expression = "1", resultType = 0, outputUnitId = (int?)null, argumentsCsv = (string?)null }),
            ("DELETE formula", HttpMethod.Delete, "/formulas/tons", null),
            ("PUT constant", HttpMethod.Put, "/constants/k1", new { kind = 0, value = 5m, unitId = (int?)null }),
            ("PUT category", HttpMethod.Put, "/categories/gas", new { description = (string?)null, ordinal = (int?)null, isRetired = (bool?)null }),
            ("DELETE category", HttpMethod.Delete, "/categories/gas", null),
            ("PUT rule", HttpMethod.Put, "/rules/r1", new { matchJson = "{}", priority = 1, isActive = true }),
            ("PUT category-rule", HttpMethod.Put, "/category-rule", new { expression = "'A'" }),
            ("DELETE category-rule", HttpMethod.Delete, "/category-rule", null),
            ("PUT substances", HttpMethod.Put, "/substances", new { substanceEntryIds = Array.Empty<long>() }),
            ("PUT output", HttpMethod.Put, "/outputs/extra", new { unitId = 1, ordinal = 2 }),
        };

        foreach (var (name, method, path, body) in attempts)
        {
            using var request = new HttpRequestMessage(method, new Uri(root + path, UriKind.Relative));
            if (body is not null)
            {
                request.Content = JsonContent.Create(body);
            }

            using var response = await editor.SendAsync(request).ConfigureAwait(true);
            using var problem = await JsonAsync(response, HttpStatusCode.Conflict).ConfigureAwait(true);
            Assert.True(
                problem.RootElement.GetProperty("messageKey").GetString() == "err.ECR-CALC-0409.scriptManaged",
                $"{name}: {problem.RootElement}");
            Assert.Equal("ECR-CALC-0409", problem.RootElement.GetProperty("errorCode").GetString());
        }

        // Тести, обов'язкові колонки, режими лишаються доступними: вони не виводяться зі скрипта.
        using var test = await JsonAsync(
            await PutAsync(editor, root + "/tests/case1", new
            {
                inputJson = """{"periodKey":{"value":209901},"arguments":[]}""",
                expectedJson = """{"OUT_A":5}""",
                tolerance = 0m,
            }).ConfigureAwait(true),
            HttpStatusCode.OK).ConfigureAwait(true);
        Assert.Equal("case1", test.RootElement.GetProperty("code").GetString());

        // Версія з формулами правиться як і було.
        var formulaVersion = await CreateScriptVersionAsync(editor, engine: null).ConfigureAwait(true);
        using var formula = await JsonAsync(
            await PutAsync(editor, $"/api/v1/methodologies/{formulaVersion.MethodologyId}/versions/{formulaVersion.VersionId}/formulas/tons",
                new { expression = "1", resultType = 0, outputUnitId = (int?)null, argumentsCsv = (string?)null }).ConfigureAwait(true),
            HttpStatusCode.OK).ConfigureAwait(true);
        Assert.Equal("tons", formula.RootElement.GetProperty("code").GetString());

        using var formulaScript = await JsonAsync(
            await GetAsync(editor, Script(formulaVersion.MethodologyId, formulaVersion.VersionId)).ConfigureAwait(true), HttpStatusCode.OK).ConfigureAwait(true);
        Assert.Equal("Formulas", formulaScript.RootElement.GetProperty("engine").GetString());
        Assert.Empty(formulaScript.RootElement.GetProperty("diagnostics").EnumerateArray());

        using var notEditable = await PutAsync(editor, Script(formulaVersion.MethodologyId, formulaVersion.VersionId), new { text = "rows from X;" }).ConfigureAwait(true);
        using var problemNotEditable = await JsonAsync(notEditable, HttpStatusCode.Conflict).ConfigureAwait(true);
        Assert.Equal("err.ECR-CALC-0409.scriptNotEditable", problemNotEditable.RootElement.GetProperty("messageKey").GetString());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "MS-T3.2")]
    public async Task Права_без_EditFormula_403_без_View_403_а_View_достатньо_для_читання_й_перевірки()
    {
        using var app = new EcrApiFactory(sql);
        var (editor, _) = await SignInAsync(app, Editor).ConfigureAwait(true);
        var created = await CreateScriptVersionAsync(editor).ConfigureAwait(true);
        var (viewer, _) = await SignInAsync(app, ["Calculation.View"]).ConfigureAwait(true);
        var (nobody, _) = await SignInAsync(app, []).ConfigureAwait(true);

        using var put = await PutAsync(viewer, Script(created.MethodologyId, created.VersionId), new { text = "rows from X;" }).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.Forbidden, put.StatusCode);

        using var create = await PostAsync(
            viewer, $"/api/v1/methodologies/{created.MethodologyId}/versions",
            new { versionNumber = "9.0", copyFromVersionId = (int?)null, level = "Configuration", engine = "Script" }).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);

        using var get = await GetAsync(viewer, Script(created.MethodologyId, created.VersionId)).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        using var validate = await PostAsync(viewer, Script(created.MethodologyId, created.VersionId, "/validate"), new { text = "x" }).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.OK, validate.StatusCode);
        using var format = await PostAsync(viewer, Script(created.MethodologyId, created.VersionId, "/format-const"), new { text = "5" }).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.OK, format.StatusCode);

        foreach (var (name, response) in new (string, HttpResponseMessage)[]
                 {
                     ("GET", await GetAsync(nobody, Script(created.MethodologyId, created.VersionId)).ConfigureAwait(true)),
                     ("validate", await PostAsync(nobody, Script(created.MethodologyId, created.VersionId, "/validate"), new { text = "x" }).ConfigureAwait(true)),
                     ("format", await PostAsync(nobody, Script(created.MethodologyId, created.VersionId, "/format-const"), new { text = "5" }).ConfigureAwait(true)),
                     ("preview", await PostAsync(nobody, Script(created.MethodologyId, created.VersionId, "/preview"), new { documentId = 1, periodKey = 209901 }).ConfigureAwait(true)),
                     ("PUT", await PutAsync(nobody, Script(created.MethodologyId, created.VersionId), new { text = "x" }).ConfigureAwait(true)),
                 })
        {
            using (response)
            {
                Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{name}: {(int)response.StatusCode}");
            }
        }

        // Той самий маршрут із чужою адресою — 404, не 403 (право → належність версії).
        using var foreign = await GetAsync(editor, Script(created.MethodologyId + 100_000, created.VersionId)).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "MS-T3.2")]
    public async Task Format_const_друкує_мапу_канонічно_і_віддає_діагностики_на_зламаному_літералі()
    {
        using var app = new EcrApiFactory(sql);
        var (editor, _) = await SignInAsync(app, Editor).ConfigureAwait(true);
        var created = await CreateScriptVersionAsync(editor).ConfigureAwait(true);

        using var formatted = await JsonAsync(
            await PostAsync(editor, Script(created.MethodologyId, created.VersionId, "/format-const"), new { text = "{A:{x:1,y:2.50},B:{x:3,y:4}}" }).ConfigureAwait(true),
            HttpStatusCode.OK).ConfigureAwait(true);
        Assert.Equal("map", formatted.RootElement.GetProperty("kind").GetString());
        var text = formatted.RootElement.GetProperty("text").GetString()!;
        Assert.Contains("A: { x: 1,", text, StringComparison.Ordinal);
        Assert.Contains("y: 2.5 }", text, StringComparison.Ordinal);
        Assert.Empty(formatted.RootElement.GetProperty("diagnostics").EnumerateArray());

        using var broken = await JsonAsync(
            await PostAsync(editor, Script(created.MethodologyId, created.VersionId, "/format-const"), new { text = "{ A: }" }).ConfigureAwait(true),
            HttpStatusCode.OK).ConfigureAwait(true);
        Assert.Equal(JsonValueKind.Null, broken.RootElement.GetProperty("text").ValueKind);
        var diagnostic = broken.RootElement.GetProperty("diagnostics").EnumerateArray().First();
        Assert.Equal(1, diagnostic.GetProperty("line").GetInt32());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "MS-T3.2")]
    public async Task Копія_версії_лишає_рушій_і_скрипт_джерела()
    {
        var stand = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        var (editor, _) = await SignInAsync(app, Editor).ConfigureAwait(true);
        var created = await CreateScriptVersionAsync(editor).ConfigureAwait(true);
        var good = GoodText(stand);
        using (await JsonAsync(
                   await PutAsync(editor, Script(created.MethodologyId, created.VersionId), new { text = good }).ConfigureAwait(true),
                   HttpStatusCode.OK).ConfigureAwait(true))
        {
        }

        // Рушій у запиті копіювання ігнорується: клон бере його з джерела.
        using var copy = await JsonAsync(
            await PostAsync(editor, $"/api/v1/methodologies/{created.MethodologyId}/versions",
                new { versionNumber = "2.0", copyFromVersionId = created.VersionId, level = "Configuration", engine = "Formulas" }).ConfigureAwait(true),
            HttpStatusCode.OK).ConfigureAwait(true);
        Assert.Equal("Script", copy.RootElement.GetProperty("engine").GetString());

        using var script = await JsonAsync(
            await GetAsync(editor, Script(created.MethodologyId, copy.RootElement.GetProperty("id").GetInt32())).ConfigureAwait(true),
            HttpStatusCode.OK).ConfigureAwait(true);
        Assert.Equal(good, script.RootElement.GetProperty("text").GetString());
        Assert.Empty(script.RootElement.GetProperty("diagnostics").EnumerateArray());
    }

    // ---------- перегляд на документі ----------

    private sealed class FakeRunner : IScriptPreviewRunner
    {
        public ConcurrentQueue<ScriptPreviewRequest> Calls { get; } = new();

        public Task<ScriptPreviewResult> RunAsync(ScriptPreviewRequest request, CancellationToken ct)
        {
            Calls.Enqueue(request);
            return Task.FromResult(new ScriptPreviewResult(
            [
                new ScriptPreviewTable(
                    request.TableInstanceId ?? 1, "T", 3, 2, 1,
                    [
                        new ScriptPreviewRow(
                            "R1", 1, "selected", null, null,
                            [new ScriptPreviewValue("OUT_A", null, "output", "10", null)]),
                        new ScriptPreviewRow("R2", 2, "inactive", null, null, []),
                    ]),
            ]));
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "MS-T3.2")]
    public async Task Перегляд_віддає_відповідь_рушія_нічого_не_пише_і_вимагає_доступу_до_документа()
    {
        var stand = await ArrangeAsync().ConfigureAwait(true);
        var runner = new FakeRunner();
        using var baseApp = new EcrApiFactory(sql);
        using var app = baseApp.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<IScriptPreviewRunner>(runner)));

        var (editor, _) = await SignInAsync(app, Editor).ConfigureAwait(true);
        var created = await CreateScriptVersionAsync(editor).ConfigureAwait(true);
        var good = GoodText(stand);
        using (await JsonAsync(
                   await PutAsync(editor, Script(created.MethodologyId, created.VersionId), new { text = good }).ConfigureAwait(true),
                   HttpStatusCode.OK).ConfigureAwait(true))
        {
        }

        var (reader, _) = await SignInAsync(app, ["Calculation.View"], stand.Document.ProjectId).ConfigureAwait(true);
        var (outsider, _) = await SignInAsync(app, ["Calculation.View"]).ConfigureAwait(true);
        var url = Script(created.MethodologyId, created.VersionId, "/preview");

        await using (var before = Context())
        {
            Assert.Equal(0, await before.CalculationRuns.CountAsync(r => r.ProjectId == stand.Document.ProjectId).ConfigureAwait(true));
        }

        // Збережений текст, стеля рядків 200, типове 50.
        using (var ok = await JsonAsync(
                   await PostAsync(reader, url, new { documentId = stand.Document.DocumentId, periodKey = 209901, maxRows = 5000 }).ConfigureAwait(true),
                   HttpStatusCode.OK).ConfigureAwait(true))
        {
            Assert.Empty(ok.RootElement.GetProperty("diagnostics").EnumerateArray());
            var table = Assert.Single(ok.RootElement.GetProperty("tables").EnumerateArray());
            Assert.Equal(3, table.GetProperty("totalRows").GetInt32());
            Assert.Equal("selected", table.GetProperty("rows")[0].GetProperty("state").GetString());
            Assert.Equal("10", table.GetProperty("rows")[0].GetProperty("values")[0].GetProperty("text").GetString());
        }

        using (await JsonAsync(
                   await PostAsync(reader, url, new { documentId = stand.Document.DocumentId, periodKey = 209901 }).ConfigureAwait(true),
                   HttpStatusCode.OK).ConfigureAwait(true))
        {
        }

        var calls = runner.Calls.ToList();
        Assert.Equal([200, 50], calls.Select(c => c.MaxRows));
        Assert.All(calls, c =>
        {
            Assert.Equal(good, c.Text);
            Assert.Equal(created.VersionId, c.MethodologyVersionId);
            Assert.Equal(stand.Document.DocumentId, c.DocumentId);
        });

        // Текст у запиті перекриває збережений; з діагностиками рушій не викликається.
        var before2 = runner.Calls.Count;
        using (var withErrors = await JsonAsync(
                   await PostAsync(reader, url, new { text = "rows from NO_SUCH_TABLE;", documentId = stand.Document.DocumentId, periodKey = 209901 }).ConfigureAwait(true),
                   HttpStatusCode.OK).ConfigureAwait(true))
        {
            Assert.Empty(withErrors.RootElement.GetProperty("tables").EnumerateArray());
            Assert.NotEmpty(withErrors.RootElement.GetProperty("diagnostics").EnumerateArray());
        }

        Assert.Equal(before2, runner.Calls.Count);

        // Без доступу до документа — 404 ECR-DOC-0404 (існування не розкривається); рушій не викликано.
        using (var denied = await PostAsync(outsider, url, new { documentId = stand.Document.DocumentId, periodKey = 209901 }).ConfigureAwait(true))
        {
            using var problem = await JsonAsync(denied, HttpStatusCode.NotFound).ConfigureAwait(true);
            Assert.Equal("ECR-DOC-0404", problem.RootElement.GetProperty("errorCode").GetString());
        }

        Assert.Equal(before2, runner.Calls.Count);

        // Ключ періоду не місяць — 422.
        using (var badPeriod = await PostAsync(reader, url, new { documentId = stand.Document.DocumentId, periodKey = 209913 }).ConfigureAwait(true))
        {
            using var problem = await JsonAsync(badPeriod, HttpStatusCode.UnprocessableEntity).ConfigureAwait(true);
            Assert.Equal("err.ECR-CALC-0422.simulatePeriodInvalid", problem.RootElement.GetProperty("messageKey").GetString());
        }

        // Нічого не записано: жодного прогону, жодного результату проєкту.
        await using var after = Context();
        Assert.Equal(0, await after.CalculationRuns.CountAsync(r => r.ProjectId == stand.Document.ProjectId).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "MS-T3.2")]
    public async Task Перегляд_без_підключеного_рушія_віддає_названу_відмову()
    {
        var stand = await ArrangeAsync().ConfigureAwait(true);

        // Порт перегляду справді відсутній у цьому хості (у нормальному хості його реєструє AddEcrCalculations).
        using var baseApp = new EcrApiFactory(sql);
        using var app = baseApp.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.RemoveAll<IScriptPreviewRunner>()));
        var (editor, _) = await SignInAsync(app, Editor).ConfigureAwait(true);
        var created = await CreateScriptVersionAsync(editor).ConfigureAwait(true);
        using (await JsonAsync(
                   await PutAsync(editor, Script(created.MethodologyId, created.VersionId), new { text = GoodText(stand) }).ConfigureAwait(true),
                   HttpStatusCode.OK).ConfigureAwait(true))
        {
        }

        var (reader, _) = await SignInAsync(app, ["Calculation.View"], stand.Document.ProjectId).ConfigureAwait(true);
        using var response = await PostAsync(
            reader, Script(created.MethodologyId, created.VersionId, "/preview"),
            new { documentId = stand.Document.DocumentId, periodKey = 209901 }).ConfigureAwait(true);

        using var problem = await JsonAsync(response, HttpStatusCode.UnprocessableEntity).ConfigureAwait(true);
        Assert.Equal("err.ECR-CALC-0422.scriptPreviewUnavailable", problem.RootElement.GetProperty("messageKey").GetString());
    }

    // ---------- публікація до золотого набору ----------

    /// <summary>Фейковий модуль скрипта: відповідає лише на <c>CanHandle</c> і дає одне число, яке чекає тест.</summary>
    private sealed class FakeScriptModule : ICalculationModule
    {
        public string Code => "fake-script";

        public CalculationLevel Level => CalculationLevel.Configuration;

        public ConcurrentQueue<MethodologyDescriptor> Seen { get; } = new();

        public bool CanHandle(MethodologyDescriptor methodology) => methodology.Engine == MethodologyEngine.Script;

        public Task<CalculationBindingContext> PrepareAsync(
            MethodologyDescriptor methodology, long documentId, PeriodKey periodKey, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<CalculationOutput> ExecuteAsync(
            CalculationBindingContext binding, CalculationInput input, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<CalculationOutput> ExecuteAsync(CalculationInput input, CancellationToken ct)
        {
            Seen.Enqueue(input.Methodology);
            return Task.FromResult(new CalculationOutput(
                input.DocumentId, input.SourceRowKey,
                [new CalculationOutputValue(input.Methodology.MethodologyVersionId, null, "OUT_A", 5m, 1)],
                []));
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "MS-T3.2")]
    public async Task Публікація_версії_скрипта_матеріалізує_прив_язує_і_проходить_золотий_набір_через_модуль_за_CanHandle()
    {
        var stand = await ArrangeAsync(secondTable: true).ConfigureAwait(true);
        var module = new FakeScriptModule();
        using var baseApp = new EcrApiFactory(sql);
        using var app = baseApp.WithWebHostBuilder(b => b.ConfigureTestServices(s => { WithoutRealScriptModule(s); s.AddSingleton<ICalculationModule>(module); }));

        var (author, _) = await SignInAsync(app, Editor).ConfigureAwait(true);
        var (publisher, _) = await SignInAsync(app, ["Calculation.View", "Calculation.Publish"]).ConfigureAwait(true);
        var created = await CreateScriptVersionAsync(author).ConfigureAwait(true);

        var text = $$"""
            rows from {{stand.TableCode}};
            table Src = {{stand.SecondTableCode}};
            var total = Sum(Src.{{stand.SecondNumberColumn}});
            var amount = row.{{stand.NumberColumn}};
            output OUT_A : t = amount + total;
            """;
        using (var saved = await JsonAsync(
                   await PutAsync(author, Script(created.MethodologyId, created.VersionId), new { text }).ConfigureAwait(true),
                   HttpStatusCode.OK).ConfigureAwait(true))
        {
            Assert.Empty(saved.RootElement.GetProperty("diagnostics").EnumerateArray());
        }

        var root = $"/api/v1/methodologies/{created.MethodologyId}/versions/{created.VersionId}";

        // Без золотого набору публікація відмовляє — але ДО цього перевірки скрипта вже пройшли.
        using (var noTests = await PostAsync(publisher, root + "/publish", new { changeReason = "MS", effectiveFrom = "2099-01-01" }).ConfigureAwait(true))
        {
            using var problem = await JsonAsync(noTests, HttpStatusCode.UnprocessableEntity).ConfigureAwait(true);
            Assert.Equal("err.ECR-CALC-0422.goldenSetEmpty", problem.RootElement.GetProperty("messageKey").GetString());
        }

        // Відмова відкотила все: прив'язок ще немає, версія — чернетка.
        await using (var db = Context())
        {
            Assert.Equal(0, await db.CalculationBindings.CountAsync(b => b.MethodologyId == created.MethodologyId).ConfigureAwait(true));
            Assert.Equal(
                TemplateVersionStatus.Draft,
                (await db.MethodologyVersions.AsNoTracking().SingleAsync(v => v.Id == created.VersionId).ConfigureAwait(true)).Status);
        }

        using (await JsonAsync(
                   await PutAsync(author, root + "/tests/case1", new
                   {
                       inputJson = $$"""{"periodKey":{"value":209901},"documentId":{{stand.Document.DocumentId}},"arguments":[]}""",
                       expectedJson = """{"OUT_A":5}""",
                       tolerance = 0m,
                   }).ConfigureAwait(true),
                   HttpStatusCode.OK).ConfigureAwait(true))
        {
        }

        using (var published = await JsonAsync(
                   await PostAsync(publisher, root + "/publish", new { changeReason = "MS", effectiveFrom = "2099-01-01" }).ConfigureAwait(true),
                   HttpStatusCode.OK).ConfigureAwait(true))
        {
            Assert.Equal(created.VersionId, published.RootElement.GetProperty("methodologyVersionId").GetInt32());
        }

        // Золотий набір рахував модуль, обраний за CanHandle (дескриптор несе рушій).
        Assert.NotEmpty(module.Seen);
        Assert.All(module.Seen, d => Assert.Equal(MethodologyEngine.Script, d.Engine));

        await using var verify = Context();
        var version = await verify.MethodologyVersions.AsNoTracking().SingleAsync(v => v.Id == created.VersionId).ConfigureAwait(true);
        Assert.Equal(TemplateVersionStatus.Published, version.Status);
        Assert.Equal(new DateOnly(2099, 1, 1), version.EffectiveFrom);

        var output = await verify.MethodologyOutputs.AsNoTracking().SingleAsync(o => o.MethodologyVersionId == created.VersionId).ConfigureAwait(true);
        Assert.Equal("OUT_A", output.Code);

        // Таблиця читання (table Src) матеріалізована, ребра довідників порожні, залежностей від методологій немає.
        Assert.Equal(
            [stand.SecondTableCode],
            await verify.MethodologyReadTables.AsNoTracking().Where(t => t.MethodologyVersionId == created.VersionId).Select(t => t.TableCode).ToListAsync().ConfigureAwait(true));

        // Автоприв'язка: Calculated-колонка з кодом виходу отримала активну прив'язку з предикатом «усе».
        var binding = await verify.CalculationBindings.AsNoTracking().SingleAsync(b => b.MethodologyId == created.MethodologyId).ConfigureAwait(true);
        Assert.Equal(stand.CalculatedColumnId, binding.ColumnDefId);
        Assert.Equal("OUT_A", binding.OutputCode);
        Assert.Equal("{}", binding.MatchJson);
        Assert.True(binding.IsActive);
        Assert.Equal(stand.Document.TableDefId, binding.TableDefId);

        Assert.True(await verify.AuditPublicationEventsAnyAsync(created.VersionId).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-556")]
    public async Task Публікація_скрипта_бібліотеки_проходить_без_золотого_набору_а_методології_без_нього_відмовляє()
    {
        var stand = await ArrangeAsync().ConfigureAwait(true);
        using var baseApp = new EcrApiFactory(sql);
        using var app = baseApp.WithWebHostBuilder(b => b.ConfigureTestServices(s => { WithoutRealScriptModule(s); s.AddSingleton<ICalculationModule>(new FakeScriptModule()); }));
        var (author, _) = await SignInAsync(app, Editor).ConfigureAwait(true);
        var (publisher, _) = await SignInAsync(app, ["Calculation.View", "Calculation.Publish"]).ConfigureAwait(true);

        // Бібліотека (kind = 2): виходів немає, золотого набору немає - публікація проходить.
        var library = await CreateScriptVersionAsync(author, kind: 2).ConfigureAwait(true);
        using (var saved = await JsonAsync(
                   await PutAsync(author, Script(library.MethodologyId, library.VersionId), new { text = "const k = 5; function twice(a) = a * 2;" }).ConfigureAwait(true),
                   HttpStatusCode.OK).ConfigureAwait(true))
        {
            Assert.Empty(saved.RootElement.GetProperty("diagnostics").EnumerateArray());
        }

        using (var published = await JsonAsync(
                   await PostAsync(
                       publisher, $"/api/v1/methodologies/{library.MethodologyId}/versions/{library.VersionId}/publish",
                       new { changeReason = "D-556", effectiveFrom = "2099-01-01" }).ConfigureAwait(true),
                   HttpStatusCode.OK).ConfigureAwait(true))
        {
            Assert.Equal(library.VersionId, published.RootElement.GetProperty("methodologyVersionId").GetInt32());
        }

        await using (var db = Context())
        {
            Assert.Equal(
                TemplateVersionStatus.Published,
                (await db.MethodologyVersions.AsNoTracking().SingleAsync(v => v.Id == library.VersionId).ConfigureAwait(true)).Status);
        }

        // Методологія (kind = 0) без набору - як і було: goldenSetEmpty.
        var regular = await CreateScriptVersionAsync(author).ConfigureAwait(true);
        var text = $"rows from {stand.TableCode}; var amount = row.{stand.NumberColumn}; output OUT_A : t = amount;";
        using (await JsonAsync(
                   await PutAsync(author, Script(regular.MethodologyId, regular.VersionId), new { text }).ConfigureAwait(true),
                   HttpStatusCode.OK).ConfigureAwait(true))
        {
        }

        using var response = await PostAsync(
            publisher, $"/api/v1/methodologies/{regular.MethodologyId}/versions/{regular.VersionId}/publish",
            new { changeReason = "D-556", effectiveFrom = "2099-01-01" }).ConfigureAwait(true);
        using var problem = await JsonAsync(response, HttpStatusCode.UnprocessableEntity).ConfigureAwait(true);
        Assert.Equal("err.ECR-CALC-0422.goldenSetEmpty", problem.RootElement.GetProperty("messageKey").GetString());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "MS-T3.2")]
    public async Task Публікація_версії_скрипта_з_помилкою_оточення_відмовляє_422_з_переліком_і_нічого_не_лишає()
    {
        var stand = await ArrangeAsync().ConfigureAwait(true);
        using var baseApp = new EcrApiFactory(sql);
        using var app = baseApp.WithWebHostBuilder(b => b.ConfigureTestServices(s => { WithoutRealScriptModule(s); s.AddSingleton<ICalculationModule>(new FakeScriptModule()); }));
        var (author, _) = await SignInAsync(app, Editor).ConfigureAwait(true);
        var (publisher, _) = await SignInAsync(app, ["Calculation.View", "Calculation.Publish"]).ConfigureAwait(true);
        var created = await CreateScriptVersionAsync(author).ConfigureAwait(true);

        // Вихід є, а колонки Calculated з таким кодом у таблиці немає: noBoundOutput.
        var text = $"rows from {stand.TableCode};\nvar amount = row.{stand.NumberColumn};\noutput NO_SUCH_COLUMN : t = amount;";
        using (var saved = await JsonAsync(
                   await PutAsync(author, Script(created.MethodologyId, created.VersionId), new { text }).ConfigureAwait(true),
                   HttpStatusCode.OK).ConfigureAwait(true))
        {
            Assert.Empty(saved.RootElement.GetProperty("diagnostics").EnumerateArray());
        }

        using var response = await PostAsync(
            publisher, $"/api/v1/methodologies/{created.MethodologyId}/versions/{created.VersionId}/publish",
            new { changeReason = "MS", effectiveFrom = "2099-01-01" }).ConfigureAwait(true);
        using var problem = await JsonAsync(response, HttpStatusCode.UnprocessableEntity).ConfigureAwait(true);
        Assert.Equal("err.ECR-CALC-0422.publishChecksFailed", problem.RootElement.GetProperty("messageKey").GetString());
        var item = Assert.Single(problem.RootElement.GetProperty("problems").EnumerateArray());
        Assert.Equal("script.noBoundOutput", item.GetProperty("messageKey").GetString());

        await using var db = Context();
        Assert.Equal(0, await db.CalculationBindings.CountAsync(b => b.MethodologyId == created.MethodologyId).ConfigureAwait(true));
        Assert.Equal(
            TemplateVersionStatus.Draft,
            (await db.MethodologyVersions.AsNoTracking().SingleAsync(v => v.Id == created.VersionId).ConfigureAwait(true)).Status);
    }
}

internal static class ScriptApiTestExtensions
{
    /// <summary>Чи є в журналі запис про публікацію версії (<c>PublicationEvent</c>).</summary>
    public static async Task<bool> AuditPublicationEventsAnyAsync(this EcrDbContext db, int versionId)
    {
        var count = await db.Database
            .SqlQuery<int>($"SELECT COUNT(*) AS Value FROM aud.PublicationEvent WHERE EntityType = N'calc.MethodologyVersion' AND EntityId = {versionId}")
            .SingleAsync()
            .ConfigureAwait(false);
        return count > 0;
    }
}
