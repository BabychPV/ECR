using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Domain.Entities.Configuration;
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
/// Методологія-скрипт НАСКРІЗЬ через HTTP на справжньому SQL Server і справжній черзі (MS): шаблон → методологія →
/// версія-скрипт → текст → золотий тест → публікація (автоприв'язка) → перегляд на документі → перерахунок документа →
/// результати й значення в колонці Calculated → правка входу (результат застарів) → перерахунок → нове число.
/// Другий сценарій — агрегат за псевдонімом <c>table</c> і читання результату ІНШОЇ методології: матеріалізація
/// <c>calc.MethodologyReadTable</c> і <c>calc.MethodologyDependency</c> шляхом публікації керує порядком і
/// перерахунком.
/// </summary>
/// <remarks>
/// Мутаційні докази (локально): прибрати <c>ReplaceDependenciesAsync</c> з <c>ScriptMaterializer</c> — у другому
/// сценарії читач (C) після правки входу лишається зі старим числом; не додавати автоприв'язку — перший сценарій
/// червоніє на результатах. Періоди — 2099xx.
/// </remarks>
[Collection("SqlServer")]
public sealed class MethodologyScriptEndToEndTests(SqlServerFixture sql)
{
    private const string Password = "Api-Script-E2E-2026!";

    private static readonly TimeZoneInfo SiteZone = SiteTimeZone.Create("Asia/Atyrau").ToTimeZoneInfo();

    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(90);

    private sealed record Stand(
        TestDocumentBuilder Builder,
        TestDocument Document,
        int PeriodKey,
        string TableCode,
        string KindColumn,
        string ValueColumn,
        string KindColumnId,
        IReadOnlyList<string> RowKeys,
        IReadOnlyList<(int Id, string Code)> Calculated);

    private sealed record Created(int MethodologyId, int VersionId, string Code);

    private sealed record Users(HttpClient Author, HttpClient Publisher, HttpClient Worker);

    // ───────────────────────────── сценарій 1 ─────────────────────────────

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "MS-T3.2")]
    public async Task Скрипт_від_тексту_до_числа_в_колонці_Calculated_правка_входу_застарює_результат_і_перерахунок_оновлює_його()
    {
        var s = await ArrangeAsync(209911, ["OUT_A"], [("gas", 10m), ("gas", 5m), ("oil", 7m)]).ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        var u = await SignInAllAsync(app, s).ConfigureAwait(true);

        var a = await CreateScriptVersionAsync(u.Author, "A").ConfigureAwait(true);
        var text = $"rows from {s.TableCode} where row.{s.KindColumn} == \"gas\";\noutput OUT_A : t = row.{s.ValueColumn} * 2;";
        await SaveTextAsync(u.Author, a, text).ConfigureAwait(true);

        // Золотий тест через наявний ендпоінт: перший рядок (gas, 10) → OUT_A = 20.
        await AddGoldenAsync(u.Author, a, s, rowIndex: 0, value: 10m, extra: [], expected: """{"OUT_A":20}""").ConfigureAwait(true);

        // Перегляд ДО публікації: збережений текст і текст-перекриття (незбережений).
        var preview = await PreviewAsync(u.Worker, a, s, text: null).ConfigureAwait(true);
        var table = Assert.Single(preview.GetProperty("tables").EnumerateArray());
        Assert.Equal(3, table.GetProperty("totalRows").GetInt32());
        Assert.Equal(2, table.GetProperty("selectedRows").GetInt32());
        var first = table.GetProperty("rows")[0];
        Assert.Equal("selected", first.GetProperty("state").GetString());
        Assert.Equal("20", ValueOf(first, "OUT_A"));
        Assert.Equal("notSelected", table.GetProperty("rows")[2].GetProperty("state").GetString());

        // Входи вибраного рядка — для золотого тесту з перегляду: лише поля, які читає скрипт.
        var inputs = first.GetProperty("inputs").EnumerateArray().ToDictionary(i => i.GetProperty("name").GetString()!, i => i);
        // `where` читає колонку виду, але це умова вибору, а не поле рядка скрипта: у входах її немає.
        Assert.Equal([s.ValueColumn], inputs.Keys);
        Assert.Equal(10m, Num(inputs[s.ValueColumn].GetProperty("value")));
        Assert.Empty(table.GetProperty("rows")[2].GetProperty("inputs").EnumerateArray());

        var tripled = text.Replace("* 2", "* 3", StringComparison.Ordinal);
        var overridden = await PreviewAsync(u.Worker, a, s, tripled).ConfigureAwait(true);
        Assert.Equal("30", ValueOf(Assert.Single(overridden.GetProperty("tables").EnumerateArray()).GetProperty("rows")[0], "OUT_A"));

        // Публікація: автоприв'язка створює прив'язку (колонка Calculated з кодом виходу).
        await PublishAsync(u.Publisher, a).ConfigureAwait(true);
        await using (var db = s.Builder.CreateContext())
        {
            var binding = await db.CalculationBindings.AsNoTracking().SingleAsync(b => b.MethodologyId == a.MethodologyId).ConfigureAwait(true);
            Assert.Equal(s.Calculated[0].Id, binding.ColumnDefId);
            Assert.Equal("OUT_A", binding.OutputCode);
            Assert.True(binding.IsActive);
        }

        // Перерахунок документа → результати й зріз таблиці.
        await RecalculateAsync(app, u.Worker, s).ConfigureAwait(true);
        var results = await ResultsAsync(u.Worker, s).ConfigureAwait(true);
        Assert.Equal(["OUT_A|20", "OUT_A|10"], results.Select(r => $"{r.Output}|{r.Value:0.####}").ToArray());
        Assert.All(results, r => Assert.False(r.Stale));
        Assert.Equal(20m, await SliceValueAsync(u.Worker, s, 0, "OUT_A").ConfigureAwait(true));
        Assert.Equal(10m, await SliceValueAsync(u.Worker, s, 1, "OUT_A").ConfigureAwait(true));
        Assert.Null(await SliceValueAsync(u.Worker, s, 2, "OUT_A").ConfigureAwait(true));

        // Правка входу (10 → 30) звичайним PATCH: результат застарів.
        await PatchCellAsync(app, u.Worker, s, rowIndex: 0, 30m).ConfigureAwait(true);
        Assert.All(await ResultsAsync(u.Worker, s).ConfigureAwait(true), r => Assert.True(r.Stale));

        // Перерахунок → нове число, застарілості немає.
        await RecalculateAsync(app, u.Worker, s).ConfigureAwait(true);
        var again = await ResultsAsync(u.Worker, s).ConfigureAwait(true);
        Assert.Equal(["OUT_A|60", "OUT_A|10"], again.Select(r => $"{r.Output}|{r.Value:0.####}").ToArray());
        Assert.All(again, r => Assert.False(r.Stale));
        Assert.Equal(60m, await SliceValueAsync(u.Worker, s, 0, "OUT_A").ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "MS-T3.2")]
    public async Task Живі_таблиці_за_кодами_для_підказок_редактора()
    {
        var s = await ArrangeAsync(209909, ["OUT_A"], [("gas", 10m)]).ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        var u = await SignInAllAsync(app, s).ConfigureAwait(true);
        var url = $"/api/v1/methodologies/script/tables?codes={s.TableCode},NO_SUCH_TABLE";

        var found = await JsonAsync(await u.Author.GetAsync(new Uri(url, UriKind.Relative)).ConfigureAwait(true), HttpStatusCode.OK).ConfigureAwait(true);
        var table = Assert.Single(found.EnumerateArray());
        Assert.Equal(s.TableCode, table.GetProperty("requested").GetString());
        Assert.Equal(s.TableCode, table.GetProperty("tableCode").GetString());
        var columns = table.GetProperty("columns").EnumerateArray().ToDictionary(c => c.GetProperty("code").GetString()!, c => c);
        Assert.Equal("Calculated", columns["OUT_A"].GetProperty("dataType").GetString());
        Assert.Equal("Decimal", columns[s.ValueColumn].GetProperty("dataType").GetString());
        Assert.False(columns[s.ValueColumn].GetProperty("isMonthColumn").GetBoolean());
        Assert.Empty(table.GetProperty("monthAliases").EnumerateArray());

        // Шаблон.Таблиця — з кодом шаблону.
        var templateCode = table.GetProperty("templateCode").GetString();
        var qualified = await JsonAsync(
            await u.Author.GetAsync(new Uri($"/api/v1/methodologies/script/tables?codes={templateCode}.{s.TableCode}", UriKind.Relative)).ConfigureAwait(true),
            HttpStatusCode.OK).ConfigureAwait(true);
        Assert.Single(qualified.EnumerateArray());

        using (var none = await u.Author.GetAsync(new Uri("/api/v1/methodologies/script/tables?codes=", UriKind.Relative)).ConfigureAwait(true))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, none.StatusCode);
        }

        var nobody = await SignInAsync(app, [], s, grant: null).ConfigureAwait(true);
        using var forbidden = await nobody.GetAsync(new Uri(url, UriKind.Relative)).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
    }

    // ───────────────────────────── сценарій 2 ─────────────────────────────

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "MS-T3.2")]
    public async Task Агрегат_table_і_читання_результату_іншої_методології_публікація_матеріалізує_таблиці_читання_й_залежності()
    {
        var s = await ArrangeAsync(209912, ["OUT_A", "OUT_B", "OUT_C"], [("gas", 10m), ("gas", 5m), ("oil", 7m)]).ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        var u = await SignInAllAsync(app, s).ConfigureAwait(true);

        // A: власний результат рядка.
        var a = await CreateScriptVersionAsync(u.Author, "A").ConfigureAwait(true);
        await SaveTextAsync(u.Author, a, $"rows from {s.TableCode};\noutput OUT_A : t = row.{s.ValueColumn} * 2;").ConfigureAwait(true);
        await AddGoldenAsync(u.Author, a, s, 0, 10m, [], """{"OUT_A":20}""").ConfigureAwait(true);
        await PublishAsync(u.Publisher, a).ConfigureAwait(true);

        // B: агрегат за псевдонімом таблиці — сума значень того самого виду.
        var b = await CreateScriptVersionAsync(u.Author, "B").ConfigureAwait(true);
        await SaveTextAsync(
            u.Author, b,
            $"rows from {s.TableCode};\ntable Src = {s.TableCode};\n"
            + $"output OUT_B : t = Sum(Src.{s.ValueColumn} where Src.{s.KindColumn} == row.{s.KindColumn});").ConfigureAwait(true);
        await AddGoldenAsync(u.Author, b, s, 0, 10m, [], """{"OUT_B":15}""").ConfigureAwait(true);
        await PublishAsync(u.Publisher, b).ConfigureAwait(true);

        // C: читає результат A як звичайну колонку Calculated.
        var c = await CreateScriptVersionAsync(u.Author, "C").ConfigureAwait(true);
        await SaveTextAsync(u.Author, c, $"rows from {s.TableCode};\noutput OUT_C : t = row.OUT_A + 1;").ConfigureAwait(true);
        await AddGoldenAsync(u.Author, c, s, 0, 10m, [("OUT_A", 20m)], """{"OUT_C":21}""").ConfigureAwait(true);
        await PublishAsync(u.Publisher, c).ConfigureAwait(true);

        // Матеріалізація шляхом публікації: таблиця читання B і залежність C → A.
        await using (var db = s.Builder.CreateContext())
        {
            Assert.Equal(
                [s.TableCode],
                await db.MethodologyReadTables.AsNoTracking().Where(t => t.MethodologyVersionId == b.VersionId).Select(t => t.TableCode).ToListAsync().ConfigureAwait(true));
            var edges = await db.MethodologyDependencies.AsNoTracking()
                .Where(d => d.FromMethodologyId == c.MethodologyId)
                .Select(d => d.ToMethodologyId)
                .ToListAsync().ConfigureAwait(true);
            Assert.Equal([a.MethodologyId], edges);
        }

        // Перший перерахунок: A у ранішій пачці, ніж C, — C бачить результат A з ТОГО САМОГО прогону.
        await RecalculateAsync(app, u.Worker, s).ConfigureAwait(true);
        Assert.Equal(
            ["OUT_A|0|20", "OUT_A|1|10", "OUT_A|2|14", "OUT_B|0|15", "OUT_B|1|15", "OUT_B|2|7", "OUT_C|0|21", "OUT_C|1|11", "OUT_C|2|15"],
            await ShapeAsync(u.Worker, s).ConfigureAwait(true));
        Assert.Equal(21m, await SliceValueAsync(u.Worker, s, 0, "OUT_C").ConfigureAwait(true));
        Assert.Equal(15m, await SliceValueAsync(u.Worker, s, 1, "OUT_B").ConfigureAwait(true));

        // Правка входу першого рядка (10 → 30): A = 60, сума gas = 35, C = 61 — усе в одному прогоні.
        await PatchCellAsync(app, u.Worker, s, 0, 30m).ConfigureAwait(true);
        await RecalculateAsync(app, u.Worker, s).ConfigureAwait(true);
        Assert.Equal(
            ["OUT_A|0|60", "OUT_A|1|10", "OUT_A|2|14", "OUT_B|0|35", "OUT_B|1|35", "OUT_B|2|7", "OUT_C|0|61", "OUT_C|1|11", "OUT_C|2|15"],
            await ShapeAsync(u.Worker, s).ConfigureAwait(true));
        Assert.Equal(61m, await SliceValueAsync(u.Worker, s, 0, "OUT_C").ConfigureAwait(true));
    }

    /// <summary>
    /// D-4: тест золотого набору без екземпляра таблиці (<c>tableInstanceId = 0</c>, як у методологів на річній розкладці)
    /// публікується: скрипт рахує на порожньому власному екземплярі, а не падає <c>404 ECR-DOC-0404 tableInstance</c>.
    /// </summary>
    /// <remarks>Мутація: у <c>ScriptRuntime.InstanceAsync</c> умову <c>tableInstanceId &gt; 0</c> замінити на <c>true</c> — червоніє (404 на публікації).</remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-558")]
    public async Task Золотий_набір_скрипта_без_екземпляра_таблиці_публікується()
    {
        var s = await ArrangeAsync(209912, ["OUT_A"], [("gas", 10m), ("gas", 5m)]).ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        var u = await SignInAllAsync(app, s).ConfigureAwait(true);

        var a = await CreateScriptVersionAsync(u.Author, "A").ConfigureAwait(true);
        await SaveTextAsync(u.Author, a, $"rows from {s.TableCode};\noutput OUT_A : t = row.{s.ValueColumn} * 2;").ConfigureAwait(true);
        await AddGoldenAsync(u.Author, a, s, 0, 10m, [], """{"OUT_A":20}""", tableInstanceId: 0).ConfigureAwait(true);
        await PublishAsync(u.Publisher, a).ConfigureAwait(true);
    }

    // ───────────────────────────── стенд ─────────────────────────────

    private async Task<Stand> ArrangeAsync(int periodKey, string[] calculated, (string Kind, decimal Value)[] rows)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var document = await builder.BuildAsync(periodKey, columnCount: 3, rowCount: rows.Length, rowMode: TableRowMode.Fixed).ConfigureAwait(false);
        var now = DateTime.UtcNow;

        await using var db = builder.CreateContext();
        var table = await db.TableDefs.SingleAsync(t => t.Id == document.TableDefId).ConfigureAwait(false);
        var columns = await db.ColumnDefs.AsNoTracking()
            .Where(c => document.ColumnDefIds.Contains(c.Id))
            .OrderBy(c => c.Ordinal)
            .ToListAsync().ConfigureAwait(false);
        var rowKeys = await db.RowDefs.AsNoTracking()
            .Where(r => r.TableDefId == document.TableDefId)
            .OrderBy(r => r.Ordinal)
            .Select(r => r.RowKeyValue)
            .ToListAsync().ConfigureAwait(false);

        var added = new List<ColumnDef>();
        var ordinal = 20;
        foreach (var code in calculated)
        {
            var column = new ColumnDef(table.Id, EcrCode.Create(code), Name(code), ordinal++, CellDataType.Calculated);
            db.ColumnDefs.Add(column);
            added.Add(column);
        }

        var version = await db.TemplateVersions.SingleAsync(v => v.Id == document.TemplateVersionId).ConfigureAwait(false);
        version.Publish(1, now);

        var project = await db.Projects.FirstAsync(p => p.Id == document.ProjectId).ConfigureAwait(false);
        project.Activate(now);
        var policy = await db.PeriodPolicies.FirstAsync(p => p.Id == project.PeriodPolicyId).ConfigureAwait(false);
        var period = await db.Periods
            .FirstAsync(p => p.ProjectId == document.ProjectId && p.PeriodKeyValue == periodKey).ConfigureAwait(false);
        period.RecomputeBoundaries(policy, SiteZone);
        period.AdvanceTo(PeriodState.Open, now);
        await db.SaveChangesAsync().ConfigureAwait(false);

        for (var i = 0; i < rows.Length; i++)
        {
            db.CellValues.Add(new CellValue(
                new CellAddress(document.PeriodKey, document.RowIds[i], columns[0].Id),
                document.TableDefId, new CellValueData { ValueString = rows[i].Kind }));
            db.CellValues.Add(new CellValue(
                new CellAddress(document.PeriodKey, document.RowIds[i], columns[1].Id),
                document.TableDefId, new CellValueData { ValueNumeric = rows[i].Value }));
        }

        await db.SaveChangesAsync().ConfigureAwait(false);

        return new Stand(
            builder, document, periodKey, table.Code, columns[0].Code, columns[1].Code, columns[0].Code, rowKeys,
            [.. added.Select(c => (c.Id, c.Code))]);
    }

    private async Task<Users> SignInAllAsync(EcrApiFactory app, Stand s)
    {
        var author = await SignInAsync(app, ["Calculation.View", "Calculation.EditFormula"], s, grant: null).ConfigureAwait(false);
        var publisher = await SignInAsync(app, ["Calculation.View", "Calculation.Publish"], s, grant: null).ConfigureAwait(false);
        var worker = await SignInAsync(app, ["Document.View", "Calculation.View"], s, GrantLevel.Write).ConfigureAwait(false);
        return new Users(author, publisher, worker);
    }

    private async Task<HttpClient> SignInAsync(EcrApiFactory app, string[] permissions, Stand s, GrantLevel? grant)
    {
        var name = $"e2e_{Guid.NewGuid():N}"[..20];
        await using (var db = new EcrDbContext(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options))
        {
            var user = new User(name, name, AuthProvider.Local);
            user.SetPassword(new PasswordHasher().Hash(Password));
            db.Users.Add(user);
            var role = new Role(EcrCode.Create($"R{Guid.NewGuid():N}"[..12].ToUpperInvariant()), Name("Script e2e"));
            db.Roles.Add(role);
            await db.SaveChangesAsync().ConfigureAwait(false);
            foreach (var permission in permissions)
            {
                db.RolePermissions.Add(new RolePermission(role.Id, permission));
            }

            db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, null));
            if (grant is { } level)
            {
                db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, s.Document.ProjectId, level));
            }

            await db.SaveChangesAsync().ConfigureAwait(false);
        }

        var client = app.CreateClient();
        using var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative), new { userName = name, password = Password }).ConfigureAwait(false);
        Assert.True(login.IsSuccessStatusCode, $"Вхід {name}: {login.StatusCode}: {app.ErrorsText}");
        return client;
    }

    // ───────────────────────────── HTTP ─────────────────────────────

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private async Task<Created> CreateScriptVersionAsync(HttpClient client, string key)
    {
        var code = $"E2E{key}{Guid.NewGuid():N}"[..14].ToUpperInvariant();
        var methodology = await JsonAsync(
            await client.PostAsJsonAsync(
                new Uri("/api/v1/methodologies", UriKind.Relative),
                new { code, nameL10n = new Dictionary<string, string> { ["en"] = "e2e" }, kind = 0, group = (string?)null }).ConfigureAwait(false),
            HttpStatusCode.Created).ConfigureAwait(false);
        var id = methodology.GetProperty("id").GetInt32();

        var version = await JsonAsync(
            await client.PostAsJsonAsync(
                new Uri($"/api/v1/methodologies/{id}/versions", UriKind.Relative),
                new { versionNumber = "1.0", copyFromVersionId = (int?)null, level = "Configuration", engine = "Script" }).ConfigureAwait(false),
            HttpStatusCode.OK).ConfigureAwait(false);
        return new Created(id, version.GetProperty("id").GetInt32(), code);
    }

    private static string Root(Created m) => $"/api/v1/methodologies/{m.MethodologyId}/versions/{m.VersionId}";

    private static async Task SaveTextAsync(HttpClient client, Created m, string text)
    {
        var saved = await JsonAsync(
            await client.PutAsJsonAsync(new Uri(Root(m) + "/script", UriKind.Relative), new { text }).ConfigureAwait(false),
            HttpStatusCode.OK).ConfigureAwait(false);
        Assert.True(
            saved.GetProperty("diagnostics").GetArrayLength() == 0,
            $"Діагностики збереження: {saved.GetProperty("diagnostics")}");
    }

    private static async Task AddGoldenAsync(
        HttpClient client, Created m, Stand s, int rowIndex, decimal value, (string Code, decimal Value)[] extra, string expected,
        long? tableInstanceId = null)
    {
        var arguments = new List<object>
        {
            new { argumentCode = s.KindColumn, value = (decimal?)null, valueString = "gas", unitId = (int?)null },
            new { argumentCode = s.ValueColumn, value = (decimal?)value, valueString = (string?)null, unitId = (int?)null },
        };
        arguments.AddRange(extra.Select(e => (object)new { argumentCode = e.Code, value = (decimal?)e.Value, valueString = (string?)null, unitId = (int?)null }));

        var input = JsonSerializer.Serialize(new
        {
            periodKey = new { value = s.PeriodKey },
            documentId = s.Document.DocumentId,
            tableInstanceId = tableInstanceId ?? s.Document.TableInstanceId,
            sourceRowKey = s.RowKeys[rowIndex],
            arguments,
        });
        await JsonAsync(
            await client.PutAsJsonAsync(
                new Uri(Root(m) + "/tests/case1", UriKind.Relative),
                new { inputJson = input, expectedJson = expected, tolerance = 0m }).ConfigureAwait(false),
            HttpStatusCode.OK).ConfigureAwait(false);
    }

    private static async Task PublishAsync(HttpClient client, Created m)
        => (await JsonAsync(
            await client.PostAsJsonAsync(
                new Uri(Root(m) + "/publish", UriKind.Relative), new { changeReason = "MS e2e", effectiveFrom = "2099-01-01" }).ConfigureAwait(false),
            HttpStatusCode.OK).ConfigureAwait(false)).GetProperty("methodologyVersionId").GetInt32();

    private static async Task<JsonElement> PreviewAsync(HttpClient client, Created m, Stand s, string? text)
        => await JsonAsync(
            await client.PostAsJsonAsync(
                new Uri(Root(m) + "/script/preview", UriKind.Relative),
                new { text, documentId = s.Document.DocumentId, periodKey = s.PeriodKey }).ConfigureAwait(false),
            HttpStatusCode.OK).ConfigureAwait(false);

    private static string? ValueOf(JsonElement row, string name)
        => row.GetProperty("values").EnumerateArray()
            .First(v => v.GetProperty("name").GetString() == name).GetProperty("text").GetString();

    private async Task RecalculateAsync(EcrApiFactory app, HttpClient client, Stand s)
    {
        var accepted = await JsonAsync(
            await client.PostAsJsonAsync(
                new Uri($"/api/v1/documents/{s.Document.DocumentId}/recalculate", UriKind.Relative),
                new { periodKey = s.PeriodKey.ToString(CultureInfo.InvariantCulture) }).ConfigureAwait(false),
            HttpStatusCode.Accepted).ConfigureAwait(false);
        var jobId = accepted.GetProperty("jobId").GetString()!;

        var deadline = DateTime.UtcNow + Deadline;
        while (true)
        {
            var job = await JsonAsync(
                await client.GetAsync(new Uri($"/api/v1/jobs/{Uri.EscapeDataString(jobId)}", UriKind.Relative)).ConfigureAwait(false),
                HttpStatusCode.OK).ConfigureAwait(false);
            var state = job.GetProperty("state").GetString();
            if (state is not ("Queued" or "Running"))
            {
                Assert.True(state == "Succeeded", $"Задача {jobId}: {job}\n{app.ErrorsText}");
                return;
            }

            Assert.True(DateTime.UtcNow < deadline, $"Задача {jobId} не завершилась: {job}\n{app.ErrorsText}");
            await Task.Delay(150).ConfigureAwait(false);
        }
    }

    private sealed record Result(string Output, decimal Value, bool Stale, string? RowKey);

    private static async Task<List<Result>> ResultsAsync(HttpClient client, Stand s)
    {
        var body = await JsonAsync(
            await client.GetAsync(new Uri(
                $"/api/v1/documents/{s.Document.DocumentId}/calculation-results?periodKey={s.PeriodKey}", UriKind.Relative)).ConfigureAwait(false),
            HttpStatusCode.OK).ConfigureAwait(false);
        return
        [
            .. body.EnumerateArray()
                .Select(r => new Result(
                    r.GetProperty("outputCode").GetString()!,
                    Num(r.GetProperty("value")),
                    r.GetProperty("isStale").GetBoolean(),
                    r.GetProperty("sourceRowKey").GetString()))
                .OrderBy(r => r.Output, StringComparer.Ordinal)
                .ThenBy(r => r.RowKey, StringComparer.Ordinal),
        ];
    }

    /// <summary>Форма результатів: <c>вихід|номер рядка|значення</c>.</summary>
    private static async Task<List<string>> ShapeAsync(HttpClient client, Stand s)
        => [.. (await ResultsAsync(client, s).ConfigureAwait(false))
            .Select(r => $"{r.Output}|{s.RowKeys.ToList().IndexOf(r.RowKey!)}|{r.Value:0.####}")
            .Order(StringComparer.Ordinal)];

    private static async Task<decimal?> SliceValueAsync(HttpClient client, Stand s, int rowIndex, string code)
    {
        var body = await JsonAsync(
            await client.GetAsync(new Uri(
                $"/api/v1/documents/{s.Document.DocumentId}/tables/{s.Document.TableInstanceId}", UriKind.Relative)).ConfigureAwait(false),
            HttpStatusCode.OK).ConfigureAwait(false);
        var row = body.GetProperty("rows").EnumerateArray().Single(r => r.GetProperty("rowKey").GetString() == s.RowKeys[rowIndex]);
        if (!row.GetProperty("cells").TryGetProperty(code, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetDecimal(),
            JsonValueKind.String => decimal.Parse(value.GetString()!, CultureInfo.InvariantCulture),
            _ => null,
        };
    }

    private static async Task PatchCellAsync(EcrApiFactory app, HttpClient client, Stand s, int rowIndex, decimal value)
    {
        var table = await JsonAsync(
            await client.GetAsync(new Uri(
                $"/api/v1/documents/{s.Document.DocumentId}/tables/{s.Document.TableInstanceId}", UriKind.Relative)).ConfigureAwait(false),
            HttpStatusCode.OK).ConfigureAwait(false);
        var key = s.RowKeys[rowIndex];
        var version = table.GetProperty("rows").EnumerateArray()
            .Single(r => r.GetProperty("rowKey").GetString() == key).GetProperty("rowVersion").GetString();

        using var response = await client.PatchAsJsonAsync(
            new Uri($"/api/v1/documents/{s.Document.DocumentId}/cells", UriKind.Relative),
            new
            {
                tableInstanceId = s.Document.TableInstanceId,
                periodKey = s.PeriodKey,
                origin = "UserEdit",
                rows = new[]
                {
                    new
                    {
                        rowKey = key,
                        baseVersion = version,
                        cells = new object[] { new { columnCode = s.ValueColumn, value = value.ToString(CultureInfo.InvariantCulture) } },
                    },
                },
            }).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"PATCH: {(int)response.StatusCode}: {text}\n{app.ErrorsText}");

        // Пауза: «застарілість» міряється від початку прогону, правка мусить бути строго пізніше.
        await Task.Delay(50).ConfigureAwait(false);
    }

    private static decimal Num(JsonElement value)
        => value.ValueKind == JsonValueKind.String
            ? decimal.Parse(value.GetString()!, CultureInfo.InvariantCulture)
            : value.GetDecimal();

    private static LocalizedText Name(string value) => new(new Dictionary<string, string> { ["en"] = value });
}
