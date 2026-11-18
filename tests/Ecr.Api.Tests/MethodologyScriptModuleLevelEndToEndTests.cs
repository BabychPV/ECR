using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// Авторинг версії рівня <c>Module</c> (HSE400, «місяці в колонках») НАСКРІЗЬ через HTTP на справжньому SQL Server і
/// справжній черзі: методологія → версія Module з рушієм Script → збереження тексту (за зразком тексту конвертера) →
/// validate без хибних діагностик → перегляд на документі (зрозуміла відмова 422) → публікація (перевірки, матеріалізація
/// виходів, наявна прив'язка до місячної колонки) → перерахунок документа → числа у місячних колонках.
/// </summary>
/// <remarks>
/// Мутаційні докази: прибрати <c>IsModuleLevel</c> у <c>ScriptSourceCompiler.CompileForVersionAsync</c> (чи у
/// <c>ScriptProgramLoader</c>) — збереження, validate чи публікація дають <c>missingRows</c>, тест червоніє.
/// </remarks>
[Collection("SqlServer")]
public sealed class MethodologyScriptModuleLevelEndToEndTests(SqlServerFixture sql)
{
    private const string Password = "Api-Script-Mod-2026!";

    private static readonly TimeZoneInfo SiteZone = SiteTimeZone.Create("Asia/Atyrau").ToTimeZoneInfo();

    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(90);

    private sealed record Scene(TestDocumentBuilder Builder, TestDocument Document, int Period, string Table, IReadOnlyList<string> MonthCodes);

    private sealed record Created(int MethodologyId, int VersionId);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "MS-T3.2")]
    public async Task Версія_Module_від_тексту_до_числа_у_місячній_колонці_без_хибного_missingRows()
    {
        var s = await ArrangeAsync(209909).ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        var author = await SignInAsync(app, ["Calculation.View", "Calculation.EditFormula"], s, grant: null).ConfigureAwait(true);
        var publisher = await SignInAsync(app, ["Calculation.View", "Calculation.Publish"], s, grant: null).ConfigureAwait(true);
        var worker = await SignInAsync(app, ["Document.View", "Calculation.View"], s, GrantLevel.Write).ConfigureAwait(true);

        var m = await CreateModuleVersionAsync(author).ConfigureAwait(true);
        var t = s.Table;
        var text = "const Factor = 1.5;\n"
            + "output Common_K : one = Factor * 2;\n"
            + $"output X400_{t}_Sum : one = row.GCV_X400_{t}_Methane + row.GCV_X400_{t}_Nitrogen;\n"
            + $"output X400_{t}_Scaled : one = Round(X400_{t}_Sum * Common_K, 1);\n";

        // validate: чисто (без missingRows).
        var validated = await JsonAsync(
            await author.PostAsJsonAsync(new Uri(Root(m) + "/script/validate", UriKind.Relative), new { text }).ConfigureAwait(true),
            HttpStatusCode.OK).ConfigureAwait(true);
        Assert.True(validated.GetProperty("diagnostics").GetArrayLength() == 0, $"validate: {validated.GetProperty("diagnostics")}");

        // PUT script: текст збережено, діагностик немає, виходи матеріалізовано.
        var saved = await JsonAsync(
            await author.PutAsJsonAsync(new Uri(Root(m) + "/script", UriKind.Relative), new { text }).ConfigureAwait(true),
            HttpStatusCode.OK).ConfigureAwait(true);
        Assert.True(saved.GetProperty("diagnostics").GetArrayLength() == 0, $"save: {saved.GetProperty("diagnostics")}");
        await using (var db = s.Builder.CreateContext())
        {
            var outputs = await db.MethodologyOutputs.AsNoTracking().Where(o => o.MethodologyVersionId == m.VersionId)
                .Select(o => o.Code).ToListAsync().ConfigureAwait(true);
            Assert.Equal(["Common_K", $"X400_{t}_Scaled", $"X400_{t}_Sum"], outputs.Order(StringComparer.Ordinal).ToArray());
        }

        // GET script: ті самі нуль діагностик.
        var got = await JsonAsync(
            await author.GetAsync(new Uri(Root(m) + "/script", UriKind.Relative)).ConfigureAwait(true), HttpStatusCode.OK).ConfigureAwait(true);
        Assert.Equal(0, got.GetProperty("diagnostics").GetArrayLength());

        // Перегляд на документі для Module: зрозуміла відмова, а не порожня відповідь.
        using var preview = await worker.PostAsJsonAsync(
            new Uri(Root(m) + "/script/preview", UriKind.Relative),
            new { text = (string?)null, documentId = s.Document.DocumentId, periodKey = s.Period }).ConfigureAwait(true);
        var previewBody = await preview.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, preview.StatusCode);
        Assert.Contains("err.ECR-CALC-0422.scriptPreviewModuleLevel", previewBody, StringComparison.Ordinal);

        // Прив'язки Module ставить редактор прив'язок (тут — напряму): вихід Sum → колонка першого місяця.
        await using (var db = s.Builder.CreateContext())
        {
            db.CalculationBindings.Add(new CalculationBinding(
                s.Document.TableDefId, s.Document.MonthColumnDefIds[0], m.MethodologyId, $"X400_{t}_Sum", "{}"));
            await db.SaveChangesAsync().ConfigureAwait(true);
        }

        // Золотий тест (публікація без нього відмовляє): Module читає документ сам, результат рядка F_Sum — по місячних колонках.
        var input = JsonSerializer.Serialize(new
        {
            periodKey = new { value = s.Period },
            documentId = s.Document.DocumentId,
            tableInstanceId = s.Document.TableInstanceId,
            sourceRowKey = "F_Sum",
            arguments = new object[]
            {
                new { argumentCode = $"GCV_X400_{t}_Methane", value = (decimal?)10m, valueString = (string?)null, unitId = (int?)null },
                new { argumentCode = $"GCV_X400_{t}_Nitrogen", value = (decimal?)4m, valueString = (string?)null, unitId = (int?)null },
            },
        });
        await JsonAsync(
            await author.PutAsJsonAsync(
                new Uri(Root(m) + "/tests/case1", UriKind.Relative),
                new { inputJson = input, expectedJson = $"{{\"{s.MonthCodes[0]}\":14,\"{s.MonthCodes[1]}\":2.5}}", tolerance = 0m }).ConfigureAwait(true),
            HttpStatusCode.OK).ConfigureAwait(true);

        // Публікація: перевірки проходять (немає missingRows / noBoundOutput).
        using var publish = await publisher.PostAsJsonAsync(
            new Uri(Root(m) + "/publish", UriKind.Relative), new { changeReason = "MS module e2e", effectiveFrom = "2099-01-01" }).ConfigureAwait(true);
        var publishBody = await publish.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(publish.StatusCode == HttpStatusCode.OK, $"publish: {(int)publish.StatusCode}: {publishBody}\n{app.ErrorsText}");

        // Перерахунок → числа: M01 = Methane 10 + Nitrogen 4 = 14, Scaled = 14 * 3 = 42; M02 = 0 + 2.5 = 2.5, Scaled = 7.5.
        await RecalculateAsync(app, worker, s).ConfigureAwait(true);
        Assert.Equal(14m, await CellAsync(worker, s, "F_Sum", s.MonthCodes[0]).ConfigureAwait(true));
        Assert.Equal(42m, await CellAsync(worker, s, "F_Scaled", s.MonthCodes[0]).ConfigureAwait(true));
        Assert.Equal(2.5m, await CellAsync(worker, s, "F_Sum", s.MonthCodes[1]).ConfigureAwait(true));
        Assert.Equal(7.5m, await CellAsync(worker, s, "F_Scaled", s.MonthCodes[1]).ConfigureAwait(true));
    }

    // ───────────────────────────── стенд ─────────────────────────────

    private async Task<Scene> ArrangeAsync(int periodKey)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var document = await builder.BuildAsync(
            periodKey: periodKey, columnCount: 0, rowCount: 0, layoutKind: TableLayoutKind.MonthsInColumns, monthColumns: 12).ConfigureAwait(false);
        var now = DateTime.UtcNow;

        await using var db = builder.CreateContext();
        var table = await db.TableDefs.AsNoTracking().SingleAsync(t => t.Id == document.TableDefId).ConfigureAwait(false);
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
        await ExecuteAsync($"UPDATE doc.Project SET PeriodKind = {(int)PeriodKind.Yearly} WHERE Id = {document.ProjectId};").ConfigureAwait(false);

        string[] keys = ["Methane", "Nitrogen", "F_Sum", "F_Scaled"];
        var first = await new BulkCellLoader(sql.ConnectionString, 1000).ReserveIdsAsync("doc.TableRowSeq", keys.Length, default).ConfigureAwait(false);
        var rowIds = new Dictionary<string, long>(StringComparer.Ordinal);
        for (var i = 0; i < keys.Length; i++)
        {
            db.TableRows.Add(new Ecr.Domain.Entities.Documents.TableRow(
                document.PeriodKey, first + i, document.TableInstanceId, RowKey.Create(keys[i]), i + 1, now));
            rowIds[keys[i]] = first + i;
        }

        await db.SaveChangesAsync().ConfigureAwait(false);

        // M1: 10 + 4; M2: 0 + 2.5.
        var inputs = new (int Month, string Row, decimal Value)[]
        {
            (1, "Methane", 10m), (1, "Nitrogen", 4m),
            (2, "Methane", 0m), (2, "Nitrogen", 2.5m),
        };
        var statements = inputs.Select(v =>
            "INSERT INTO doc.CellValue (PeriodKey, TableRowId, ColumnDefId, TableDefId, ValueNumeric, IsCalculated, IsEmpty) VALUES " +
            $"({periodKey}, {rowIds[v.Row]}, {document.MonthColumnDefIds[v.Month - 1]}, {document.TableDefId}, " +
            $"{v.Value.ToString(CultureInfo.InvariantCulture)}, 0, 0);");
        await ExecuteAsync(string.Join(Environment.NewLine, statements)).ConfigureAwait(false);

        // Код місячної колонки містить лічильник побудови стенда (M1_<n>): не хардкодимо.
        var monthCodes = new List<string>();
        foreach (var id in document.MonthColumnDefIds)
        {
            monthCodes.Add((await db.ColumnDefs.AsNoTracking().SingleAsync(c => c.Id == id).ConfigureAwait(false)).Code);
        }

        return new Scene(builder, document, periodKey, table.Code, monthCodes);
    }

    private async Task ExecuteAsync(string statement)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = statement;
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private async Task<HttpClient> SignInAsync(EcrApiFactory app, string[] permissions, Scene s, GrantLevel? grant)
    {
        var name = $"mod_{Guid.NewGuid():N}"[..20];
        await using (var db = new EcrDbContext(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options))
        {
            var user = new User(name, name, AuthProvider.Local);
            user.SetPassword(new PasswordHasher().Hash(Password));
            db.Users.Add(user);
            var role = new Role(EcrCode.Create($"R{Guid.NewGuid():N}"[..12].ToUpperInvariant()), Name("Script module e2e"));
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

    private static async Task<Created> CreateModuleVersionAsync(HttpClient client)
    {
        var code = $"MOD{Guid.NewGuid():N}"[..14].ToUpperInvariant();
        var methodology = await JsonAsync(
            await client.PostAsJsonAsync(
                new Uri("/api/v1/methodologies", UriKind.Relative),
                new { code, nameL10n = new Dictionary<string, string> { ["en"] = "module e2e" }, kind = 0, group = (string?)null }).ConfigureAwait(false),
            HttpStatusCode.Created).ConfigureAwait(false);
        var id = methodology.GetProperty("id").GetInt32();

        var version = await JsonAsync(
            await client.PostAsJsonAsync(
                new Uri($"/api/v1/methodologies/{id}/versions", UriKind.Relative),
                new { versionNumber = "1.0", copyFromVersionId = (int?)null, level = "Module", engine = "Script" }).ConfigureAwait(false),
            HttpStatusCode.OK).ConfigureAwait(false);
        return new Created(id, version.GetProperty("id").GetInt32());
    }

    private static string Root(Created m) => $"/api/v1/methodologies/{m.MethodologyId}/versions/{m.VersionId}";

    private static async Task RecalculateAsync(EcrApiFactory app, HttpClient client, Scene s)
    {
        var accepted = await JsonAsync(
            await client.PostAsJsonAsync(
                new Uri($"/api/v1/documents/{s.Document.DocumentId}/recalculate", UriKind.Relative),
                new { periodKey = s.Period.ToString(CultureInfo.InvariantCulture) }).ConfigureAwait(false),
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

    private static async Task<decimal?> CellAsync(HttpClient client, Scene s, string rowKey, string column)
    {
        var body = await JsonAsync(
            await client.GetAsync(new Uri(
                $"/api/v1/documents/{s.Document.DocumentId}/tables/{s.Document.TableInstanceId}", UriKind.Relative)).ConfigureAwait(false),
            HttpStatusCode.OK).ConfigureAwait(false);
        var row = body.GetProperty("rows").EnumerateArray().Single(r => r.GetProperty("rowKey").GetString() == rowKey);
        if (!row.GetProperty("cells").TryGetProperty(column, out var value))
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

    private static LocalizedText Name(string value) => new(new Dictionary<string, string> { ["en"] = value });
}
