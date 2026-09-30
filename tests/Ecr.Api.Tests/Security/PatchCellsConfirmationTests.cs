// tests/Ecr.Api.Tests/Security/PatchCellsConfirmationTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests.Security;

/// <summary>
/// <c>ФВ-2.16</c>, <c>AllowWithConfirmation</c>: сервер вимагає підтвердження
/// сам, а не покладається на діалог клієнта. Наскрізно: справжній SQL,
/// справжній вхід, справжнє правило вікна дозволу, HTTP.
/// </summary>
/// <remarks>
/// ⛔ Доти рішення <c>RequiresConfirmation</c> доходило лише до зрізу
/// (<c>cellConfirmations</c>) і до діалогу одиничної правки. <c>PATCH</c> його
/// не читав: вставка, протягування чи прямий запит записували комірку поза
/// вікном дозволу мовчки.
/// </remarks>
[Collection("SqlServer")]
public sealed class PatchCellsConfirmationTests(SqlServerFixture sql)
{
    private const string Password = "Api-Patch-Confirm-2026!";

    /// <summary>
    /// Період зрізу. ⚠ Не 2026-05…07 — ті архівують <c>ArchiveJobTests</c>
    /// (див. <c>PeriodAccessSliceTests</c>).
    /// </summary>
    private const int PeriodKeyValue = 202609;

    private const byte MonthNumber = 9;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-2.16")]
    public async Task Без_прапорця_відмова_і_в_базі_нічого_з_прапорцем_збережено()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        var client = await SignInAsync(app, s.UserName).ConfigureAwait(true);

        // ⚠ Передумова: комірка справді вимагає підтвердження. Без цього
        // «з прапорцем — збережено» проходило б і на зламаному правилі.
        var slice = await SliceAsync(client, s).ConfigureAwait(true);
        Assert.True(
            slice.GetProperty("cellConfirmations").TryGetProperty($"{s.RowKey}:{s.MonthColumnCode}", out _),
            $"Комірка {s.RowKey}:{s.MonthColumnCode} мала б вимагати підтвердження: {slice}");
        var version = RowVersion(slice, s.RowKey);

        // Батч із коміркою, що вимагає підтвердження, І звичайною — без прапорця.
        var refused = await PatchAsync(client, s, version, confirmed: null,
            (s.MonthColumnCode, 11m), (s.PlainColumnCode, 22m)).ConfigureAwait(true);
        var refusedBody = await refused.Content.ReadAsStringAsync().ConfigureAwait(true);

        Assert.True(refused.StatusCode == HttpStatusCode.Forbidden, $"{refused.StatusCode}: {refusedBody}\n{app.ErrorsText}");
        Assert.Contains("ECR-ACCS-0403", refusedBody, StringComparison.Ordinal);
        Assert.Contains("ConfirmationRequired", refusedBody, StringComparison.Ordinal);

        // ⛔ У базі нічого — ні комірки поза вікном, ні звичайної поруч.
        Assert.Null(await StoredAsync(s, s.MonthColumnId).ConfigureAwait(true));
        Assert.Null(await StoredAsync(s, s.PlainColumnId).ConfigureAwait(true));

        // Той самий батч із прапорцем — збережено.
        var accepted = await PatchAsync(client, s, version, confirmed: true,
            (s.MonthColumnCode, 11m), (s.PlainColumnCode, 22m)).ConfigureAwait(true);
        var acceptedBody = await accepted.Content.ReadAsStringAsync().ConfigureAwait(true);

        Assert.True(accepted.StatusCode == HttpStatusCode.OK, $"{accepted.StatusCode}: {acceptedBody}\n{app.ErrorsText}");
        Assert.Equal(11m, await StoredAsync(s, s.MonthColumnId).ConfigureAwait(true));
        Assert.Equal(22m, await StoredAsync(s, s.PlainColumnId).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-2.16")]
    public async Task Звичайна_комірка_без_прапорця_зберігається()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        var client = await SignInAsync(app, s.UserName).ConfigureAwait(true);

        var version = RowVersion(await SliceAsync(client, s).ConfigureAwait(true), s.RowKey);

        // ⚠ Регрес: прапорець потрібен лише там, де рішення його вимагає.
        var response = await PatchAsync(client, s, version, confirmed: null, (s.PlainColumnCode, 33m))
            .ConfigureAwait(true);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);

        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {body}\n{app.ErrorsText}");
        Assert.Equal(33m, await StoredAsync(s, s.PlainColumnId).ConfigureAwait(true));
    }

    private static async Task<JsonElement> SliceAsync(HttpClient client, Scenario s)
    {
        var response = await client
            .GetAsync(new Uri(
                $"/api/v1/documents/{s.Document.DocumentId}/tables/{s.Document.TableInstanceId}", UriKind.Relative))
            .ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(response.IsSuccessStatusCode, $"Зріз: {response.StatusCode}: {body}");

        using var json = JsonDocument.Parse(body);
        return json.RootElement.Clone();
    }

    private static string RowVersion(JsonElement slice, string rowKey)
        => slice.GetProperty("rows").EnumerateArray()
            .Single(r => r.GetProperty("rowKey").GetString() == rowKey)
            .GetProperty("rowVersion").GetString()!;

    private static Task<HttpResponseMessage> PatchAsync(
        HttpClient client, Scenario s, string baseVersion, bool? confirmed,
        params (string Column, decimal Value)[] cells)
        => client.PatchAsJsonAsync(
            new Uri($"/api/v1/documents/{s.Document.DocumentId}/cells", UriKind.Relative),
            new
            {
                tableInstanceId = s.Document.TableInstanceId,
                periodKey = PeriodKeyValue,
                origin = "UserEdit",
                confirmed,
                rows = new object[]
                {
                    new
                    {
                        rowKey = s.RowKey,
                        baseVersion,
                        cells = cells.Select(c => new { columnCode = c.Column, value = (object)c.Value }).ToArray(),
                    },
                },
            });

    private static async Task<HttpClient> SignInAsync(EcrApiFactory app, string userName)
    {
        var client = app.CreateClient();
        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative),
            new { userName, password = Password }).ConfigureAwait(false);
        Assert.True(login.IsSuccessStatusCode, $"Вхід: {login.StatusCode}: {app.ErrorsText}");
        return client;
    }

    private async Task<decimal?> StoredAsync(Scenario s, int columnDefId)
    {
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();

        return await db.CellValues.AsNoTracking()
            .Where(c => c.PeriodKeyValue == PeriodKeyValue
                        && c.TableRowId == s.RowId
                        && c.ColumnDefId == columnDefId)
            .Select(c => c.ValueNumeric)
            .SingleOrDefaultAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Документ у відкритому періоді; третя колонка — місячна (вересень), і на
    /// неї діє правило вікна дозволу з <c>AllowWithConfirmation</c>: дозвіл
    /// першого рядка закінчився 31 серпня.
    /// </summary>
    private async Task<Scenario> ArrangeAsync()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(PeriodKeyValue, columnCount: 3, rowCount: 2).ConfigureAwait(false);

        await using var db = builder.CreateContext();

        var period = await db.Periods
            .FirstAsync(p => p.ProjectId == doc.ProjectId && p.PeriodKeyValue == PeriodKeyValue)
            .ConfigureAwait(false);
        period.AdvanceTo(PeriodState.Open, DateTime.UtcNow);

        var userName = $"pcc_{Guid.NewGuid():N}"[..20];
        var user = new User(userName, userName, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));
        db.Users.Add(user);

        var writer = new Role(
            EcrCode.Create($"PCC_W_{Guid.NewGuid():N}"[..24]),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "PCC writer" }));
        db.Roles.Add(writer);

        var registry = new RegistryDef(
            EcrCode.Create($"PERMIT_{doc.TemplateVersionId}"), Text("Permits"), isTemporal: true);
        db.RegistryDefs.Add(registry);
        await db.SaveChangesAsync().ConfigureAwait(false);

        db.RolePermissions.Add(new RolePermission(writer.Id, "Document.View"));
        db.RoleAssignments.Add(new RoleAssignment(writer.Id, user.Id, null));
        db.ResourceGrants.Add(new ResourceGrant(writer.Id, ResourceKind.Project, doc.ProjectId, GrantLevel.Write));

        var entry = new RegistryEntry(registry.Id, EcrCode.Create("P1"), Text("Permit 1"));
        entry.SetValidity(new DateOnly(2026, 1, 1), new DateOnly(2026, 8, 31));
        db.RegistryEntries.Add(entry);

        // Правило вказує на ПЕРШУ колонку — ту, де рядок обирає дозвіл.
        db.PeriodAccessRules.Add(PeriodAccessRuleDef
            .ForSourceWindow(doc.TemplateVersionId, doc.ColumnDefIds[0], OutOfWindowBehavior.AllowWithConfirmation)
            .ForTable(doc.TableDefId));
        await db.SaveChangesAsync().ConfigureAwait(false);

        db.CellValues.Add(new CellValue(
            new CellAddress(new PeriodKey(PeriodKeyValue), doc.RowIds[0], doc.ColumnDefIds[0]),
            doc.TableDefId,
            new CellValueData { ValueRegistryEntryId = entry.Id }));
        await db.SaveChangesAsync().ConfigureAwait(false);

        // Третя колонка стає місячною: правило `SourceWindow` — про місяці.
        await using (var connection = new SqlConnection(sql.ConnectionString))
        {
            await connection.OpenAsync().ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE cfg.ColumnDef SET IsMonthColumn = 1, MonthNumber = @month WHERE Id = @id";
            command.Parameters.AddWithValue("@id", doc.ColumnDefIds[2]);
            command.Parameters.AddWithValue("@month", MonthNumber);
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        var codes = await db.ColumnDefs.AsNoTracking()
            .Where(c => c.Id == doc.ColumnDefIds[1] || c.Id == doc.ColumnDefIds[2])
            .ToDictionaryAsync(c => c.Id, c => c.Code)
            .ConfigureAwait(false);

        var rowKey = await db.TableRows.AsNoTracking()
            .Where(r => r.Id == doc.RowIds[0])
            .Select(r => r.RowKeyValue)
            .SingleAsync().ConfigureAwait(false);

        return new Scenario(
            doc, userName, rowKey, doc.RowIds[0],
            doc.ColumnDefIds[2], codes[doc.ColumnDefIds[2]],
            doc.ColumnDefIds[1], codes[doc.ColumnDefIds[1]]);
    }

    private static LocalizedText Text(string value)
        => new(new Dictionary<string, string> { ["en"] = value });

    private sealed record Scenario(
        TestDocument Document,
        string UserName,
        string RowKey,
        long RowId,
        int MonthColumnId,
        string MonthColumnCode,
        int PlainColumnId,
        string PlainColumnCode);
}
