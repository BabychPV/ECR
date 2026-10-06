// tests/Ecr.Api.Tests/ConsistencyIssueWhereTests.cs
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
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
/// <c>GET /api/v1/consistency/issues</c>: поле <c>where</c> (документ / аркуш / таблиця / рядок / колонка)
/// розкладається з <c>EntityId</c> знахідки і віддається лише тому читачеві, що бачить це місце.
/// </summary>
/// <remarks>
/// ⛔ Журнал системний (<c>System.ViewHealth</c>), але <c>where</c> розкриває структуру документів.
/// Мутаційні докази (локально, не комітяться): прибрати перевірку видимості в
/// <c>GetConsistencyIssuesHandler.WithVisibleWhereAsync</c> (віддавати <c>where</c> завжди) —
/// червоніють тести «без прав» і «прихований аркуш»; прибрати розклад — червоніє тест «видимий читач».
/// Лише коди: у відповіді немає значень комірок і назв.
/// </remarks>
[Collection("SqlServer")]
public sealed class ConsistencyIssueWhereTests(SqlServerFixture sql)
{
    private const string Password = "Api-Consistency-Where-2026!";
    private const string ViewHealth = "System.ViewHealth";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Знахідка_по_комірці_і_колонці_шаблону_має_where_з_кодами_а_решта_типів_без()
    {
        var b = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync().ConfigureAwait(true);
        var marker = NewMarker();
        var cellIssue = await InsertAsync("ORPHANED_CELL", "doc.CellValue", b.RowIds[0], marker).ConfigureAwait(true);
        var brokenIssue = await InsertAsync("BROKEN_FK", "doc.TableRow", b.RowIds[1], marker).ConfigureAwait(true);
        var columnIssue = await InsertAsync(
            "UNBOUND_CALCULATED_COLUMN", "cfg.ColumnDef", b.ColumnDefIds[1], marker).ConfigureAwait(true);
        var archiveIssue = await InsertAsync("ARCHIVE_CHECKSUM", "itg.ArchiveRun", 1, marker).ConfigureAwait(true);

        var expected = await ExpectedCodesAsync(b).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(
            app, b.ProjectId, [ViewHealth, "Document.View", "Template.View"], deniedSheetId: null, narrowSheetCode: null)
            .ConfigureAwait(true);

        var items = await IssuesAsync(client, marker).ConfigureAwait(true);

        var cell = items[cellIssue].GetProperty("where");
        Assert.Equal(b.DocumentId, cell.GetProperty("documentId").GetInt64());
        Assert.Equal(expected.DocumentKey, cell.GetProperty("documentBusinessKey").GetString());
        Assert.Equal(expected.SheetCode, cell.GetProperty("sheetCode").GetString());
        Assert.Equal(expected.TableCode, cell.GetProperty("tableCode").GetString());
        Assert.Equal(expected.RowKeys[0], cell.GetProperty("rowKey").GetString());

        // ⚠ EntityId комірки — це id РЯДКА: колонка невизначена, і ми її не вигадуємо.
        Assert.Equal(JsonValueKind.Null, cell.GetProperty("columnCode").ValueKind);

        var column = items[columnIssue].GetProperty("where");
        Assert.Equal(JsonValueKind.Null, column.GetProperty("documentId").ValueKind);
        Assert.Equal(expected.SheetCode, column.GetProperty("sheetCode").GetString());
        Assert.Equal(expected.TableCode, column.GetProperty("tableCode").GetString());
        Assert.Equal(expected.ColumnCodes[1], column.GetProperty("columnCode").GetString());
        Assert.Equal(JsonValueKind.Null, column.GetProperty("rowKey").ValueKind);

        // Екземпляра таблиці в BROKEN_FK уже немає, архів не має місця в структурі документа.
        Assert.Equal(JsonValueKind.Null, items[brokenIssue].GetProperty("where").ValueKind);
        Assert.Equal(JsonValueKind.Null, items[archiveIssue].GetProperty("where").ValueKind);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Без_видимості_документа_і_шаблону_where_немає_а_знахідка_лишається()
    {
        var b = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync().ConfigureAwait(true);
        var marker = NewMarker();
        var cellIssue = await InsertAsync("ORPHANED_CELL", "doc.CellValue", b.RowIds[0], marker).ConfigureAwait(true);
        var columnIssue = await InsertAsync(
            "UNBOUND_CALCULATED_COLUMN", "cfg.ColumnDef", b.ColumnDefIds[1], marker).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);

        // Лише System.ViewHealth: ні документів, ні шаблонів читач не бачить.
        using var client = await SignedInAsync(
            app, projectId: null, [ViewHealth], deniedSheetId: null, narrowSheetCode: null).ConfigureAwait(true);

        var items = await IssuesAsync(client, marker).ConfigureAwait(true);

        // Поведінка списку не змінилась: обидві знахідки на місці, з тим самим текстом і id.
        Assert.Equal(marker, MarkerOf(items[cellIssue]));
        Assert.Equal(b.RowIds[0], items[cellIssue].GetProperty("entityId").GetInt64());
        Assert.Equal(JsonValueKind.Null, items[cellIssue].GetProperty("where").ValueKind);
        Assert.Equal(JsonValueKind.Null, items[columnIssue].GetProperty("where").ValueKind);

        // Проєктний грант без права Document.View — теж не відкриває документ.
        using var grantOnly = await SignedInAsync(
            app, b.ProjectId, [ViewHealth], deniedSheetId: null, narrowSheetCode: null).ConfigureAwait(true);
        var again = await IssuesAsync(grantOnly, marker).ConfigureAwait(true);
        Assert.Equal(JsonValueKind.Null, again[cellIssue].GetProperty("where").ValueKind);
    }

    [Theory]
    [InlineData("deny")]
    [InlineData("scope")]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Прихований_аркуш_не_відкриває_where_а_видимий_відкриває(string how)
    {
        var b = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync().ConfigureAwait(true);
        var hidden = await AddHiddenSheetRowAsync(b).ConfigureAwait(true);
        var marker = NewMarker();

        var visibleIssue = await InsertAsync("ORPHANED_CELL", "doc.CellValue", b.RowIds[0], marker).ConfigureAwait(true);
        var hiddenIssue = await InsertAsync("ORPHANED_CELL", "doc.CellValue", hidden.RowId, marker).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(
            app,
            b.ProjectId,
            [ViewHealth, "Document.View"],
            deniedSheetId: how == "deny" ? hidden.SheetDefId : null,
            narrowSheetCode: how == "scope" ? b.SheetCode : null).ConfigureAwait(true);

        var items = await IssuesAsync(client, marker).ConfigureAwait(true);

        Assert.Equal(b.SheetCode, items[visibleIssue].GetProperty("where").GetProperty("sheetCode").GetString());

        // ⛔ Код прихованого аркуша й таблиці ніде у відповіді не з'являється як where.
        Assert.Equal(JsonValueKind.Null, items[hiddenIssue].GetProperty("where").ValueKind);
        Assert.DoesNotContain(hidden.SheetCode, items[hiddenIssue].GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Розклад_місць_іде_сталим_числом_запитів_незалежно_від_розміру_сторінки()
    {
        var small = await MeasureAsync(rows: 3).ConfigureAwait(true);
        var large = await MeasureAsync(rows: 40).ConfigureAwait(true);

        Assert.Equal(3 + 3, small.Resolved);
        Assert.Equal(40 + 3, large.Resolved);

        // Два типи сутностей — два запити (рядки+документи в одному join, колонки в одному).
        Assert.True(
            small.Queries == 2 && large.Queries == 2,
            $"3 рядки дали {small.Queries} запитів, 40 — {large.Queries} (очікувалось по 2)");
    }

    private async Task<(int Resolved, int Queries)> MeasureAsync(int rows)
    {
        var b = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync(rowCount: rows).ConfigureAwait(true);

        var counter = new DbCommandCounter();
        await using var db = new EcrDbContext(
            new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).AddInterceptors(counter).Options);
        var reader = new ConsistencyIssueReader(db);

        var keys = b.RowIds.Select(id => ("doc.CellValue", id))
            .Concat(b.ColumnDefIds.Select(id => ("cfg.ColumnDef", (long)id)))
            .ToList();

        counter.Tally.Reset();
        var resolved = await reader.ResolveLocationsAsync(keys, CancellationToken.None).ConfigureAwait(true);

        return (resolved.Count, counter.Tally.Snapshot().Total);
    }

    private async Task<(string DocumentKey, string SheetCode, string TableCode, List<string> RowKeys, List<string> ColumnCodes)>
        ExpectedCodesAsync(TestDocument b)
    {
        await using var db = new EcrDbContext(
            new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

        var key = await db.Documents.Where(d => d.Id == b.DocumentId).Select(d => d.BusinessKey).SingleAsync().ConfigureAwait(false);
        var table = await db.TableDefs.Where(t => t.Id == b.TableDefId).Select(t => t.Code).SingleAsync().ConfigureAwait(false);
        var rowKeys = await db.TableRows.Where(r => b.RowIds.Contains(r.Id)).OrderBy(r => r.Ordinal)
            .Select(r => r.RowKeyValue).ToListAsync().ConfigureAwait(false);
        var columns = await db.ColumnDefs.Where(c => b.ColumnDefIds.Contains(c.Id)).OrderBy(c => c.Id)
            .Select(c => c.Code).ToListAsync().ConfigureAwait(false);

        return (key, b.SheetCode, table, rowKeys, columns);
    }

    private sealed record Hidden(int SheetDefId, string SheetCode, long RowId);

    /// <summary>Другий аркуш документа з таблицею, екземпляром і одним рядком.</summary>
    private async Task<Hidden> AddHiddenSheetRowAsync(TestDocument b)
    {
        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var now = new DateTime(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc);

        await using var db = new EcrDbContext(
            new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

        db.DocumentSheets.Add(new DocumentSheet(b.DocumentId, b.SheetDefId));
        var sheet = new SheetDef(b.TemplateVersionId, EcrCode.Create($"HSH{tag}"), Name($"HiddenSheet{tag}"), 2);
        db.SheetDefs.Add(sheet);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var table = new TableDef(
            sheet.Id, EcrCode.Create($"HTB{tag}"), Name($"HiddenTable{tag}"), 1,
            TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed);
        db.TableDefs.Add(table);
        db.DocumentSheets.Add(new DocumentSheet(b.DocumentId, sheet.Id));
        await db.SaveChangesAsync().ConfigureAwait(false);

        var loader = new BulkCellLoader(sql.ConnectionString, 1000);
        var instanceId = await loader.ReserveIdsAsync("doc.TableInstanceSeq", 1, CancellationToken.None).ConfigureAwait(false);
        var rowId = await loader.ReserveIdsAsync("doc.TableRowSeq", 1, CancellationToken.None).ConfigureAwait(false);

        db.TableInstances.Add(new TableInstance(b.PeriodKey, instanceId, b.DocumentId, table.Id, now));
        db.TableRows.Add(new TableRow(b.PeriodKey, rowId, instanceId, RowKey.Create($"HR{tag}"), 1, now));
        await db.SaveChangesAsync().ConfigureAwait(false);

        return new Hidden(sheet.Id, sheet.Code, rowId);
    }

    private static string NewMarker() => $"WHR{Guid.NewGuid():N}"[..16];

    private static string? MarkerOf(JsonElement item)
        => item.GetProperty("message").GetString() is { } m ? m[..16] : null;

    /// <summary>Знахідки з маркером, за id знахідки.</summary>
    private static async Task<Dictionary<long, JsonElement>> IssuesAsync(HttpClient client, string marker)
    {
        var response = await client
            .GetAsync(new Uri($"/api/v1/consistency/issues?q={marker}&limit=50", UriKind.Relative))
            .ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {text}");

        return JsonDocument.Parse(text).RootElement.GetProperty("items").EnumerateArray()
            .ToDictionary(i => i.GetProperty("id").GetInt64(), i => i.Clone());
    }

    /// <summary>Вставляє знахідку напряму; повертає її <c>Id</c>. Текст починається з маркера.</summary>
    private async Task<long> InsertAsync(string rule, string entityType, long entityId, string marker)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync(CancellationToken.None).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT aud.ConsistencyIssue (DetectedAt, Severity, RuleCode, EntityType, EntityId, Message)
            VALUES (SYSUTCDATETIME(), 2, @rule, @type, @id, @message);
            SELECT CAST(SCOPE_IDENTITY() AS bigint);
            """;
        command.Parameters.AddWithValue("@rule", rule);
        command.Parameters.AddWithValue("@type", entityType);
        command.Parameters.AddWithValue("@id", entityId);
        command.Parameters.AddWithValue("@message", $"{marker} знахідка {entityType} {entityId}");

        return (long)(await command.ExecuteScalarAsync(CancellationToken.None).ConfigureAwait(false))!;
    }

    private async Task<HttpClient> SignedInAsync(
        EcrApiFactory app, int? projectId, string[] permissions, int? deniedSheetId, string? narrowSheetCode)
    {
        var name = $"cwh_{Guid.NewGuid():N}"[..20];

        await using (var db = new EcrDbContext(
            new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options))
        {
            var user = new User(name, name, AuthProvider.Local);
            user.SetPassword(new PasswordHasher().Hash(Password));
            db.Users.Add(user);
            var role = new Role(EcrCode.Create($"R{Guid.NewGuid():N}"[..12]), Name("Consistency where test"));
            db.Roles.Add(role);
            await db.SaveChangesAsync().ConfigureAwait(false);

            // ⚠ Роль, звужена аркушами, не дає глобального System.ViewHealth: журнал читає окрема
            // незвужена роль, а документи відкриває звужена.
            var narrowed = narrowSheetCode is not null;
            var rolePermissions = narrowed ? permissions.Where(p => p == ViewHealth) : permissions;
            foreach (var permission in rolePermissions)
            {
                db.RolePermissions.Add(new RolePermission(role.Id, permission));
            }

            if (projectId is { } project && !narrowed)
            {
                db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, project, GrantLevel.Read));
            }

            if (deniedSheetId is { } sheetId)
            {
                db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Sheet, sheetId, GrantLevel.Read, isDeny: true));
            }

            db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, principalSid: null));

            if (narrowed && projectId is { } scopedProject)
            {
                var docRole = new Role(EcrCode.Create($"D{Guid.NewGuid():N}"[..12]), Name("Consistency where narrowed"));
                db.Roles.Add(docRole);
                await db.SaveChangesAsync().ConfigureAwait(false);

                foreach (var permission in permissions.Where(p => p != ViewHealth))
                {
                    db.RolePermissions.Add(new RolePermission(docRole.Id, permission));
                }

                db.ResourceGrants.Add(new ResourceGrant(docRole.Id, ResourceKind.Project, scopedProject, GrantLevel.Read));
                var narrowAssignment = new RoleAssignment(docRole.Id, user.Id, principalSid: null);
                db.RoleAssignments.Add(narrowAssignment);
                await db.SaveChangesAsync().ConfigureAwait(false);

                var scope = RoleAssignmentScope.Create([scopedProject], [narrowSheetCode!], null, null).ToJson();
                await db.Database.ExecuteSqlInterpolatedAsync(
                        $"UPDATE sec.RoleAssignment SET ScopeJson = {scope} WHERE Id = {narrowAssignment.Id}")
                    .ConfigureAwait(false);
            }
            else
            {
                await db.SaveChangesAsync().ConfigureAwait(false);
            }
        }
        var client = app.CreateClient();
        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative),
            new { userName = name, password = Password }).ConfigureAwait(false);
        Assert.True(login.IsSuccessStatusCode, $"{login.StatusCode}: {app.ErrorsText}");

        return client;
    }

    private static LocalizedText Name(string value)
        => new(new Dictionary<string, string> { ["en"] = value });
}
