// tests/Ecr.Api.Tests/DocumentVersionMigrationTests.TableRenameKeyedData.cs
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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
/// D-10 (аудит 2026-11-10): перенос версії з мапою перейменування таблиць (<c>X → Y</c>) переносить і дані, що
/// ключуються КОДОМ таблиці, — чат таблиці (<c>doc.TableDiscussion*</c>) і передачі зрізу (<c>doc.EditAssignment</c>).
/// Без цього ексклюзивна передача мовчки переставала збігатися з таблицею, і інші з Write за роллю могли правити.
/// </summary>
/// <remarks>
/// ⚠ Справжнім HTTP на справжньому SQL Server; доступ — реальним PATCH (без підміни фільтрів доступу). Мутаційний доказ
/// (локально, не комітиться): прибрати виклик <c>RenameTableCodesAsync</c> у <c>DocumentVersionMigrationStore</c> —
/// червоніють обидва тести (чужий PATCH у <c>Y</c> дає 200 замість 403, чат <c>Y</c> порожній, передачі лишаються на <c>X</c>).
/// </remarks>
public sealed partial class DocumentVersionMigrationTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "D-10")]
    public async Task Rename_map_carries_table_chat_and_exclusive_assignment()
    {
        // Період — поточний місяць майданчика: інакше він закритий за часом і правку відхилили б права (PeriodClosed).
        var siteToday = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, AtyrauZone));
        var s = await ArrangeAsync(
            Target.TableRenamed, extraPermission: "Document.View",
            periodKey: (siteToday.Year * 100) + siteToday.Month).ConfigureAwait(true);
        await OpenForEditingAsync(s).ConfigureAwait(true);

        // U — виконавець ексклюзивної Write-передачі таблиці X; O — інший із Write за роллю. Делегатор — s.User (Manage).
        var executor = await NewProjectUserAsync(s, "d10u", GrantLevel.Write, "Document.View", "Document.Discuss").ConfigureAwait(true);
        var other = await NewProjectUserAsync(s, "d10o", GrantLevel.Write, "Document.View").ConfigureAwait(true);
        var now = DateTime.UtcNow;
        long assignmentId;
        await using (var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext())
        {
            var assignment = new EditAssignment(
                s.Doc.DocumentId,
                EditAssignmentSlice.Create(s.Doc.SheetCode, s.SourceTableCode, monthNumber: null, periodKey: null),
                isExclusive: true,
                delegatedByUserId: s.UserId,
                handoffId: Guid.NewGuid(),
                createdByUserId: s.UserId,
                utcNow: now,
                level: GrantLevel.Write);
            assignment.AddRecipient(executor.Id, s.UserId, now);
            new EditAssignmentStore(db).Add(assignment);

            db.TableDiscussionMessages.Add(new TableDiscussionMessage(
                s.Doc.DocumentId, s.SourceTableCode, s.Doc.PeriodKey.Value, executor.Id, "D-10 повідомлення", now));
            db.TableDiscussions.Add(new TableDiscussionState(s.Doc.DocumentId, s.SourceTableCode));
            await db.SaveChangesAsync().ConfigureAwait(true);
            assignmentId = assignment.Id;
        }

        using var app = new EcrApiFactory(sql);
        using var otherClient = await SignedInAsync(app, other.Name).ConfigureAwait(true);
        using var executorClient = await SignedInAsync(app, executor.Name).ConfigureAwait(true);

        // Контроль ДО переносу: ексклюзивність X справді закриває таблицю для O.
        await ExpectPatchAsync(app, otherClient, s, "before-other", HttpStatusCode.Forbidden).ConfigureAwait(true);

        using (var migrator = await SignedInAsync(app, s.UserName).ConfigureAwait(true))
        {
            var response = await PostWithRenamesAsync(migrator, s, dryRun: false, Renames(s)).ConfigureAwait(true);
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {body} {app.ErrorsText}");
        }

        await Task.Delay(TimeSpan.FromSeconds(2.5)).ConfigureAwait(true);   // кеш маркерів міграції

        // ⛔ Предмет тесту: ексклюзивність тримається на таблиці з НОВИМ кодом — O дістає 403, виконавець U пише.
        await ExpectPatchAsync(app, otherClient, s, "after-other", HttpStatusCode.Forbidden).ConfigureAwait(true);
        await ExpectPatchAsync(app, executorClient, s, "after-executor", HttpStatusCode.OK).ConfigureAwait(true);

        // Історія чату — під Y.
        using var chat = await executorClient.GetAsync(new Uri(
            $"/api/v1/documents/{s.Doc.DocumentId.ToString(CultureInfo.InvariantCulture)}/tables/{s.TargetTableCode}/messages",
            UriKind.Relative)).ConfigureAwait(true);
        var chatBody = await chat.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(chat.StatusCode == HttpStatusCode.OK, $"чат Y: {chat.StatusCode}: {chatBody} {app.ErrorsText}");
        var message = Assert.Single(JsonDocument.Parse(chatBody).RootElement.GetProperty("messages").EnumerateArray());
        Assert.Equal("D-10 повідомлення", message.GetProperty("body").GetString());

        // Передача тепер на Y: ключ зрізу порахував домен.
        await using var db2 = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        var moved = await db2.EditAssignments.AsNoTracking().SingleAsync(a => a.Id == assignmentId).ConfigureAwait(true);
        Assert.Equal(s.TargetTableCode, moved.TableCode);
        Assert.Contains("|T=" + s.TargetTableCode.ToUpperInvariant() + "|", moved.SliceKey, StringComparison.Ordinal);
        Assert.True(moved.IsExclusive);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "D-10")]
    public async Task Rename_map_from_month_moves_assignments_of_periods_from_X_and_keeps_earlier_ones()
    {
        var s = await ArrangeAsync(Target.TableRenamed, extraPermission: "Document.View").ConfigureAwait(true);   // 202601
        await AddMonthsAsync(s, 202602).ConfigureAwait(true);

        var executor = await NewProjectUserAsync(s, "d10m", GrantLevel.Write, "Document.View").ConfigureAwait(true);
        var now = DateTime.UtcNow;
        var ids = new Dictionary<string, long>(StringComparer.Ordinal);
        await using (var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext())
        {
            var store = new EditAssignmentStore(db);
            var created = new Dictionary<string, EditAssignment>(StringComparer.Ordinal);
            foreach (var (name, period) in new (string, int?)[] { ("jan", 202601), ("feb", 202602), ("any", null) })
            {
                var assignment = new EditAssignment(
                    s.Doc.DocumentId,
                    EditAssignmentSlice.Create(s.Doc.SheetCode, s.SourceTableCode, monthNumber: null, periodKey: period),
                    isExclusive: true, delegatedByUserId: s.UserId, handoffId: Guid.NewGuid(), createdByUserId: s.UserId,
                    utcNow: now, level: GrantLevel.Write);
                assignment.AddRecipient(executor.Id, s.UserId, now);
                store.Add(assignment);
                created[name] = assignment;
            }

            db.TableDiscussionMessages.Add(new TableDiscussionMessage(
                s.Doc.DocumentId, s.SourceTableCode, 202602, executor.Id, "D-10 з місяця", now));
            await db.SaveChangesAsync().ConfigureAwait(true);
            foreach (var (name, assignment) in created)
            {
                ids[name] = assignment.Id;
            }
        }

        using var flag = FromMonthOn();
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);
        var response = await PostRangeAsync(client, s, dryRun: false, "FromMonth", 202602, renames: Renames(s)).ConfigureAwait(true);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {body} {app.ErrorsText}");

        await using var check = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        var codes = await check.EditAssignments.AsNoTracking()
            .Where(a => ids.Values.Contains(a.Id))
            .ToDictionaryAsync(a => a.Id, a => (a.TableCode, a.SliceKey)).ConfigureAwait(true);

        // Січень лишився на старій версії (таблиця X) — і передача з ним; лютий і «будь-який період» — на Y.
        Assert.Equal(s.SourceTableCode, codes[ids["jan"]].TableCode);
        Assert.Equal(s.TargetTableCode, codes[ids["feb"]].TableCode);
        Assert.Equal(s.TargetTableCode, codes[ids["any"]].TableCode);
        Assert.Contains("|T=" + s.TargetTableCode.ToUpperInvariant() + "|", codes[ids["feb"]].SliceKey, StringComparison.Ordinal);

        var chatCodes = await check.TableDiscussionMessages.AsNoTracking()
            .Where(m => m.DocumentId == s.Doc.DocumentId).Select(m => m.TableDefCode).ToListAsync().ConfigureAwait(true);
        Assert.Equal([s.TargetTableCode], chatCodes);
    }

    private sealed record ProjectUser(int Id, string Name);

    /// <summary>Користувач із роллю (дозволи) і грантом рівня <paramref name="level"/> на проєкт документа.</summary>
    private async Task<ProjectUser> NewProjectUserAsync(Scenario s, string prefix, GrantLevel level, params string[] permissions)
    {
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        var name = $"{prefix}_{Guid.NewGuid():N}"[..20];
        var user = new User(name, name, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));
        db.Users.Add(user);

        var role = new Role(
            EcrCode.Create($"D10_{Guid.NewGuid():N}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "D-10 rename" }));
        db.Roles.Add(role);
        await db.SaveChangesAsync().ConfigureAwait(false);

        foreach (var permission in permissions)
        {
            db.RolePermissions.Add(new RolePermission(role.Id, permission));
        }

        db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, null));
        db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, s.Doc.ProjectId, level));
        await db.SaveChangesAsync().ConfigureAwait(false);
        return new ProjectUser(user.Id, name);
    }

    /// <summary>PATCH першої колонки першого рядка таблиці (код колонки — з поточного зрізу).</summary>
    private static async Task ExpectPatchAsync(
        EcrApiFactory app, HttpClient client, Scenario s, string value, HttpStatusCode expected)
    {
        using var slice = await client.GetAsync(new Uri(
            $"/api/v1/documents/{s.Doc.DocumentId.ToString(CultureInfo.InvariantCulture)}/tables/{s.Doc.TableInstanceId.ToString(CultureInfo.InvariantCulture)}",
            UriKind.Relative)).ConfigureAwait(false);
        var sliceBody = await slice.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(slice.StatusCode == HttpStatusCode.OK, $"зріз ({value}): {slice.StatusCode}: {sliceBody} {app.ErrorsText}");

        var root = JsonDocument.Parse(sliceBody).RootElement;
        var columnCode = root.GetProperty("columns").EnumerateArray().First().GetProperty("code").GetString();
        var row = root.GetProperty("rows").EnumerateArray().First();

        using var patched = await client.PatchAsJsonAsync(
            new Uri($"/api/v1/documents/{s.Doc.DocumentId.ToString(CultureInfo.InvariantCulture)}/cells", UriKind.Relative),
            new
            {
                tableInstanceId = s.Doc.TableInstanceId,
                periodKey = s.Doc.PeriodKey.Value,
                origin = "UserEdit",
                rows = new object[]
                {
                    new
                    {
                        rowKey = row.GetProperty("rowKey").GetString(),
                        baseVersion = row.GetProperty("rowVersion").GetString(),
                        cells = new object[] { new { columnCode, value } },
                    },
                },
            }).ConfigureAwait(false);
        var body = await patched.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(patched.StatusCode == expected, $"PATCH ({value}): {(int)patched.StatusCode} замість {(int)expected}\n{body}\n{app.ErrorsText}");
    }
}
