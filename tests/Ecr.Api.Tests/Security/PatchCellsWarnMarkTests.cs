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
/// <c>ФВ-2.16</c>, <c>Warn</c> («дозволити з позначкою», <c>D-239</c>): правка
/// поза вікном доступу проходить БЕЗ підтвердження, а аудит позначає її
/// <c>IsOutOfWindow = 1</c>. Наскрізно: справжній SQL, вхід, правило, HTTP.
/// </summary>
[Collection("SqlServer")]
public sealed class PatchCellsWarnMarkTests(SqlServerFixture sql)
{
    private const string Password = "Api-Patch-Warn-2026!";

    /// <summary>Період зрізу; не 2026-05…07 (їх архівує <c>ArchiveJobTests</c>).</summary>
    private const int PeriodKeyValue = 202609;

    private const byte MonthNumber = 9;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-2.16")]
    public async Task Warn_поза_вікном_зберігає_без_підтвердження_і_позначає_аудит_а_звичайна_комірка_ні()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        var client = await SignInAsync(app, s.UserName).ConfigureAwait(true);
        var version = RowVersion(await SliceAsync(client, s).ConfigureAwait(true), s.RowKey);

        // ⚠ Без прапорця confirmed: Warn не вимагає підтвердження.
        var response = await PatchAsync(client, s, version, confirmed: null,
            (s.MonthColumnCode, 11m), (s.PlainColumnCode, 22m)).ConfigureAwait(true);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);

        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {body}\n{app.ErrorsText}");
        Assert.Equal(11m, await StoredAsync(s, s.MonthColumnId).ConfigureAwait(true));
        Assert.Equal(22m, await StoredAsync(s, s.PlainColumnId).ConfigureAwait(true));

        // Відповідь називає рівно комірку поза вікном (клієнту - для значка).
        using var json = JsonDocument.Parse(body);
        var marked = json.RootElement.GetProperty("outOfWindow").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Equal(new[] { $"{s.RowKey}:{s.MonthColumnCode}" }, marked);

        // Позначка лише на комірці поза вікном.
        var monthMarks = await MarksAsync(s, s.MonthColumnId).ConfigureAwait(true);
        Assert.True(monthMarks.SequenceEqual(new[] { true }), string.Join(',', monthMarks));
        var plainMarks = await MarksAsync(s, s.PlainColumnId).ConfigureAwait(true);
        Assert.True(plainMarks.SequenceEqual(new[] { false }), string.Join(',', plainMarks));
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

    private async Task<bool[]> MarksAsync(Scenario s, int columnDefId)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT IsOutOfWindow FROM aud.CellChange WHERE DocumentId = @doc AND TableRowId = @row AND ColumnDefId = @col ORDER BY Id";
        command.Parameters.AddWithValue("@doc", s.Document.DocumentId);
        command.Parameters.AddWithValue("@row", s.RowId);
        command.Parameters.AddWithValue("@col", columnDefId);
        var marks = new List<bool>();
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            marks.Add(reader.GetBoolean(0));
        }

        return [.. marks];
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
    /// неї діє правило вікна дозволу з <c>Warn</c>: дозвіл
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

        var userName = $"pcw_{Guid.NewGuid():N}"[..20];
        var user = new User(userName, userName, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));
        db.Users.Add(user);

        var writer = new Role(
            EcrCode.Create($"PCW_W_{Guid.NewGuid():N}"[..24]),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "PCW writer" }));
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
            .ForSourceWindow(doc.TemplateVersionId, doc.ColumnDefIds[0], OutOfWindowBehavior.Warn)
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
