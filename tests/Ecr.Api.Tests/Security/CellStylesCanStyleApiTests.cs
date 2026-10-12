// tests/Ecr.Api.Tests/Security/CellStylesCanStyleApiTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests.Security;

/// <summary>
/// CELL-STYLE P1A: ознака «можна фарбувати» у зрізі (<c>styleAccess</c>) РІВНА рішенню запису
/// <c>PATCH …/cell-styles</c> — на справжній СУБД, через HTTP. Для кожної комірки зрізу передбачення з
/// <c>styleAccess</c> порівнюється з реальним PATCH (200 / 403).
/// </summary>
/// <remarks>
/// ⚠ Одна перевірка на всі стани: «слайс каже — дозволено» ⇔ «PATCH проходить». Дві копії правил (слайс і запис)
/// розійшлися б на першому ж стані, якого одна з них не знає.
/// </remarks>
[Collection("SqlServer")]
public sealed class CellStylesCanStyleApiTests(SqlServerFixture sql)
{
    private const string Password = "Api-CellStyle-CanStyle-2026!";

    /// <summary>⚠ Не 2026-05…07 (архівує <c>ArchiveJobTests</c>), не 202611/202612 (інші класи цього набору).</summary>
    private const int PeriodKeyValue = 202609;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Писар_з_Write_може_фарбувати_усі_комірки()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        var access = await AssertEqualityAsync(app, s, s.Writer).ConfigureAwait(true);

        Assert.True(access.CanStyle);
        Assert.Null(access.Reason);
        Assert.Empty(access.Rows);
        Assert.Empty(access.Columns);
        Assert.Empty(access.Cells);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Читач_з_Read_не_може_фарбувати_нічого()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        var access = await AssertEqualityAsync(app, s, s.Executor).ConfigureAwait(true);

        Assert.False(access.CanStyle);
        Assert.False(string.IsNullOrEmpty(access.Reason));
    }

    [Theory]
    [InlineData(DocumentStatus.Submitted, "DocumentSubmitted")]
    [InlineData(DocumentStatus.Approved, "DocumentApproved")]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Поданий_чи_затверджений_аркуш_не_можна_фарбувати(DocumentStatus status, string reason)
    {
        var s = await ArrangeAsync(sheetStatus: status).ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        var access = await AssertEqualityAsync(app, s, s.Writer).ConfigureAwait(true);

        Assert.False(access.CanStyle);
        Assert.Equal(reason, access.Reason);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Аркуш_після_Reopen_можна_фарбувати()
    {
        var s = await ArrangeAsync(sheetStatus: DocumentStatus.Approved, reopen: true).ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        var access = await AssertEqualityAsync(app, s, s.Writer).ConfigureAwait(true);

        Assert.True(access.CanStyle);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Закритий_період_не_можна_фарбувати()
    {
        var s = await ArrangeAsync(closePeriod: true).ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        var access = await AssertEqualityAsync(app, s, s.Writer).ConfigureAwait(true);

        Assert.False(access.CanStyle);
        Assert.Equal("PeriodClosed", access.Reason);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Архівований_проєкт_не_можна_фарбувати()
    {
        var s = await ArrangeAsync(archive: true).ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        var access = await AssertEqualityAsync(app, s, s.Manager).ConfigureAwait(true);

        Assert.False(access.CanStyle);
        Assert.Equal("ProjectArchived", access.Reason);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Колонки_лише_читання_фарбувати_можна_бо_колір_не_дані()
    {
        var s = await ArrangeAsync(readOnlyColumns: true).ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignInAsync(app, s.Writer).ConfigureAwait(true);

        var access = await AssertEqualityAsync(client, s).ConfigureAwait(true);
        Assert.True(access.CanStyle);
        Assert.Empty(access.Columns);

        // Контраст: ЗНАЧЕННЯ в цих колонках заборонене (CellPermissions), а колір — ні.
        var slice = await SliceAsync(client, s).ConfigureAwait(true);
        Assert.True(slice.GetProperty("cellPermissions").EnumerateObject().Any());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Рядок_лише_читання_віддається_у_StyleDeniedRowKeys_а_решта_фарбується()
    {
        var s = await ArrangeAsync(readOnlyRow: true).ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        var access = await AssertEqualityAsync(app, s, s.Writer).ConfigureAwait(true);

        Assert.True(access.CanStyle);
        Assert.Contains(s.RowKeys[0], access.Rows);
        Assert.DoesNotContain(s.RowKeys[1], access.Rows);
    }

    [Theory]
    [InlineData(GrantLevel.None, true)]
    [InlineData(GrantLevel.Read, false)]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Грант_на_колонку_слайс_і_запис_збігаються_а_схованої_колонки_немає_в_переліках(
        GrantLevel level, bool isDeny)
    {
        var s = await ArrangeAsync(columnGrant: (level, isDeny)).ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignInAsync(app, s.Writer).ConfigureAwait(true);

        var access = await AssertEqualityAsync(client, s).ConfigureAwait(true);
        Assert.True(access.CanStyle);

        if (isDeny)
        {
            // ⛔ S6: колонка під забороною для читача не існує — її коду немає в жодному переліку styleAccess.
            var slice = await SliceAsync(client, s).ConfigureAwait(true);
            var raw = slice.GetProperty("styleAccess").GetRawText();
            Assert.DoesNotContain(s.ColumnCodes[1], raw, StringComparison.Ordinal);
            Assert.DoesNotContain(
                slice.GetProperty("columns").EnumerateArray(),
                c => c.GetProperty("code").GetString() == s.ColumnCodes[1]);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Виконавець_передачі_Filled_Validated_і_замок_власника_слайс_збігається_із_записом()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var owner = await SignInAsync(app, s.Manager).ConfigureAwait(true);
        using var exec = await SignInAsync(app, s.Executor).ConfigureAwait(true);
        using var writer = await SignInAsync(app, s.Writer).ConfigureAwait(true);

        // Без передачі виконавець (Read) фарбувати не може.
        var none = await AssertEqualityAsync(exec, s).ConfigureAwait(true);
        Assert.False(none.CanStyle);

        var created = await owner.PostAsJsonAsync(
            ListUri(s), new { slice = new { sheetCode = s.SheetCode }, recipientUserIds = new[] { s.ExecutorId }, isExclusive = true })
            .ConfigureAwait(true);
        var id = (await ReadAsync(created, HttpStatusCode.OK, app).ConfigureAwait(true)).GetProperty("id").GetInt64();

        // Підйом передачею: Read → Write у своєму зрізі.
        var lifted = await AssertEqualityAsync(exec, s).ConfigureAwait(true);
        Assert.True(lifted.CanStyle);

        // Ексклюзивна передача на ВЕСЬ аркуш замикає решту (писаря) — LockedByOwner.
        var locked = await AssertEqualityAsync(writer, s).ConfigureAwait(true);
        Assert.False(locked.CanStyle);
        Assert.Equal("LockedByOwner", locked.Reason);

        // Заповнено → виконавець більше не пише.
        var filled = await ReadAsync(
            await exec.PostAsync(ActionUri(s, id, "fill"), null).ConfigureAwait(true), HttpStatusCode.OK, app).ConfigureAwait(true);
        var afterFill = await AssertEqualityAsync(exec, s).ConfigureAwait(true);
        Assert.False(afterFill.CanStyle);
        Assert.Equal("AssignmentFilled", afterFill.Reason);

        // Валідовано власником.
        await ReadAsync(
            await owner.PostAsJsonAsync(ActionUri(s, id, "validate"), new { baseVersion = filled.GetProperty("version").GetString() })
                .ConfigureAwait(true),
            HttpStatusCode.OK, app).ConfigureAwait(true);
        var afterValidate = await AssertEqualityAsync(exec, s).ConfigureAwait(true);
        Assert.False(afterValidate.CanStyle);
        Assert.Equal("AssignmentValidated", afterValidate.Reason);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Чужа_комірка_виконавця_поза_його_місяцем_не_піднімається()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        await using (var connection = new Microsoft.Data.SqlClient.SqlConnection(sql.ConnectionString))
        {
            await connection.OpenAsync().ConfigureAwait(true);
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE cfg.ColumnDef SET IsMonthColumn = 1, MonthNumber = 5 WHERE Id = @id";
            command.Parameters.AddWithValue("@id", s.Document.ColumnDefIds[1]);
            await command.ExecuteNonQueryAsync().ConfigureAwait(true);
        }

        using var app = new EcrApiFactory(sql);
        using var owner = await SignInAsync(app, s.Manager).ConfigureAwait(true);
        using var exec = await SignInAsync(app, s.Executor).ConfigureAwait(true);

        // Передача на травень: виконавець може фарбувати лише колонку-травень; перша колонка для нього «чужа».
        var created = await owner.PostAsJsonAsync(
            ListUri(s),
            new { slice = new { sheetCode = s.SheetCode, monthNumber = 5 }, recipientUserIds = new[] { s.ExecutorId }, isExclusive = false })
            .ConfigureAwait(true);
        await ReadAsync(created, HttpStatusCode.OK, app).ConfigureAwait(true);

        var access = await AssertEqualityAsync(exec, s).ConfigureAwait(true);
        Assert.True(access.CanStyle);
        Assert.Contains(s.ColumnCodes[0], access.Columns);
        Assert.DoesNotContain(s.ColumnCodes[1], access.Columns);
    }

    private async Task<Access> AssertEqualityAsync(EcrApiFactory app, Scenario s, string userName)
    {
        using var client = await SignInAsync(app, userName).ConfigureAwait(true);
        return await AssertEqualityAsync(client, s).ConfigureAwait(true);
    }

    /// <summary>Для КОЖНОЇ комірки: передбачення зі <c>styleAccess</c> ⇔ реальний PATCH (200 / 403).</summary>
    private async Task<Access> AssertEqualityAsync(HttpClient client, Scenario s)
    {
        var slice = await SliceAsync(client, s).ConfigureAwait(true);
        var node = slice.GetProperty("styleAccess");

        var access = new Access(
            node.GetProperty("canStyle").GetBoolean(),
            node.GetProperty("deniedReason").ValueKind == JsonValueKind.Null
                ? null
                : node.GetProperty("deniedReason").GetString(),
            [.. node.GetProperty("styleDeniedRowKeys").EnumerateArray().Select(e => e.GetString()!)],
            [.. node.GetProperty("styleDeniedColumnCodes").EnumerateArray().Select(e => e.GetString()!)],
            [.. node.GetProperty("styleDeniedCells").EnumerateObject().Select(p => p.Name)],
            node.GetProperty("truncated").GetBoolean());

        Assert.False(access.Truncated);
        Assert.Equal(access.CanStyle, access.Reason is null);

        var visibleCodes = slice.GetProperty("columns").EnumerateArray()
            .Select(c => c.GetProperty("code").GetString()!).ToList();
        var rowKeys = slice.GetProperty("rows").EnumerateArray()
            .Select(r => r.GetProperty("rowKey").GetString()!).ToList();

        foreach (var rowKey in rowKeys)
        {
            foreach (var code in visibleCodes)
            {
                var predicted = access.CanStyle
                                && !access.Rows.Contains(rowKey)
                                && !access.Columns.Contains(code)
                                && !access.Cells.Contains($"{rowKey}:{code}");

                var response = await client.PatchAsJsonAsync(
                    StylesUri(s),
                    new
                    {
                        periodKey = PeriodKeyValue,
                        items = new[] { new { rowKey, columnCode = code, backgroundHex = "#112233" } },
                        reset = false,
                    }).ConfigureAwait(true);
                var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);

                Assert.True(
                    response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Forbidden,
                    $"{rowKey}:{code}: {response.StatusCode}: {body}");
                Assert.True(
                    predicted == (response.StatusCode == HttpStatusCode.OK),
                    $"{rowKey}:{code}: слайс каже canStyle={predicted}, PATCH дав {response.StatusCode}: {body}");

                await ClearStylesAsync(s).ConfigureAwait(true);
            }
        }

        return access;
    }

    private static Uri StylesUri(Scenario s)
        => new($"/api/v1/documents/{s.Document.DocumentId}/tables/{s.Document.TableInstanceId}/cell-styles", UriKind.Relative);

    private static Uri ListUri(Scenario s)
        => new($"/api/v1/documents/{s.Document.DocumentId}/edit-assignments", UriKind.Relative);

    private static Uri ActionUri(Scenario s, long id, string action)
        => new($"/api/v1/documents/{s.Document.DocumentId}/edit-assignments/{id}/{action}", UriKind.Relative);

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

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response, HttpStatusCode status, EcrApiFactory app)
    {
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(response.StatusCode == status, $"{response.StatusCode}: {body}\n{app.ErrorsText}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static async Task<HttpClient> SignInAsync(EcrApiFactory app, string userName)
    {
        var client = app.CreateClient();
        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative), new { userName, password = Password }).ConfigureAwait(false);
        Assert.True(login.IsSuccessStatusCode, $"Вхід {userName}: {login.StatusCode}: {app.ErrorsText}");
        return client;
    }

    private async Task ClearStylesAsync(Scenario s)
    {
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        var rowIds = s.Document.RowIds.ToList();
        await db.CellStyles.Where(c => c.PeriodKeyValue == PeriodKeyValue && rowIds.Contains(c.TableRowId))
            .ExecuteDeleteAsync().ConfigureAwait(false);
    }

    private async Task<Scenario> ArrangeAsync(
        DocumentStatus? sheetStatus = null,
        bool reopen = false,
        bool archive = false,
        bool closePeriod = false,
        bool readOnlyColumns = false,
        bool readOnlyRow = false,
        (GrantLevel Level, bool IsDeny)? columnGrant = null)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(PeriodKeyValue, columnCount: 2, rowCount: 2).ConfigureAwait(false);
        await using var db = builder.CreateContext();
        var at = new DateTime(2026, 9, 5, 9, 0, 0, DateTimeKind.Utc);

        async Task<(int Id, string UserName, int RoleId)> NewUserAsync(string prefix, GrantLevel level)
        {
            var userName = $"{prefix}_{Guid.NewGuid():N}"[..20];
            var user = new User(userName, userName, AuthProvider.Local);
            user.SetPassword(new PasswordHasher().Hash(Password));
            db.Users.Add(user);
            var role = new Role(
                EcrCode.Create($"CSX_{Guid.NewGuid():N}"[..24]),
                new LocalizedText(new Dictionary<string, string> { ["en"] = "CSX" }));
            db.Roles.Add(role);
            await db.SaveChangesAsync().ConfigureAwait(false);

            db.RolePermissions.Add(new RolePermission(role.Id, "Document.View"));
            db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, null));
            db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, doc.ProjectId, level));
            await db.SaveChangesAsync().ConfigureAwait(false);
            return (user.Id, userName, role.Id);
        }

        var manager = await NewUserAsync("csm", GrantLevel.Manage).ConfigureAwait(false);
        var writer = await NewUserAsync("csw", GrantLevel.Write).ConfigureAwait(false);
        var executor = await NewUserAsync("cse", GrantLevel.Read).ConfigureAwait(false);

        var period = await db.Periods
            .FirstAsync(p => p.ProjectId == doc.ProjectId && p.PeriodKeyValue == PeriodKeyValue).ConfigureAwait(false);
        period.AdvanceTo(PeriodState.Open, at);
        if (closePeriod)
        {
            period.AdvanceTo(PeriodState.Closed, at);
        }

        if (sheetStatus is { } status)
        {
            var state = new ApprovalState(doc.DocumentId, doc.SheetDefId, PeriodKeyValue);
            state.Submit(manager.Id, at);
            if (status == DocumentStatus.Approved)
            {
                state.Approve(manager.Id, at.AddMinutes(1));
            }

            if (reopen)
            {
                state.Reopen(manager.Id, "Fix", at.AddMinutes(2));
            }

            db.ApprovalStates.Add(state);
        }

        if (archive)
        {
            var project = await db.Projects.Include(p => p.Periods)
                .FirstAsync(p => p.Id == doc.ProjectId).ConfigureAwait(false);
            project.Activate(at);
            foreach (var p in project.Periods)
            {
                p.AdvanceTo(PeriodState.Closed, at);
            }

            project.Archive(at);
        }

        if (columnGrant is { } grant)
        {
            db.ResourceGrants.Add(new ResourceGrant(
                writer.RoleId, ResourceKind.Column, doc.ColumnDefIds[1], grant.Level, isDeny: grant.IsDeny));
        }

        await db.SaveChangesAsync().ConfigureAwait(false);

        // SQL ДО створення фабрики: знімок метаданих прогрівається при старті.
        if (readOnlyColumns || readOnlyRow)
        {
            await using var connection = new Microsoft.Data.SqlClient.SqlConnection(sql.ConnectionString);
            await connection.OpenAsync().ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = readOnlyColumns
                ? "UPDATE cfg.ColumnDef SET IsReadOnly = 1 WHERE Id IN (@c0, @c1)"
                : "UPDATE cfg.RowDef SET IsReadOnly = 1 WHERE Id = @r0";
            if (readOnlyColumns)
            {
                command.Parameters.AddWithValue("@c0", doc.ColumnDefIds[0]);
                command.Parameters.AddWithValue("@c1", doc.ColumnDefIds[1]);
            }
            else
            {
                command.Parameters.AddWithValue("@r0", doc.RowDefIds[0]);
            }

            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        var rowKeys = new List<string>();
        foreach (var rowId in doc.RowIds)
        {
            rowKeys.Add(await db.TableRows.AsNoTracking()
                .Where(r => r.Id == rowId).Select(r => r.RowKeyValue).SingleAsync().ConfigureAwait(false));
        }

        var codes = new List<string>();
        foreach (var columnId in doc.ColumnDefIds)
        {
            codes.Add(await db.ColumnDefs.AsNoTracking()
                .Where(c => c.Id == columnId).Select(c => c.Code).SingleAsync().ConfigureAwait(false));
        }

        return new Scenario(
            doc, manager.UserName, writer.UserName, executor.UserName, executor.Id, doc.SheetCode, rowKeys, codes);
    }

    private sealed record Access(
        bool CanStyle, string? Reason, HashSet<string> Rows, HashSet<string> Columns, HashSet<string> Cells, bool Truncated);

    private sealed record Scenario(
        TestDocument Document, string Manager, string Writer, string Executor, int ExecutorId, string SheetCode,
        IReadOnlyList<string> RowKeys, IReadOnlyList<string> ColumnCodes);
}
