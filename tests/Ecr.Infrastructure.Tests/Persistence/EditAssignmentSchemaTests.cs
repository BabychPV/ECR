using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// EA-1: таблиці <c>doc.EditAssignment*</c>/<c>doc.EditorSet*</c> створює міграція <c>EA1EditAssignment</c>,
/// а CHECK/UQ тримає САМА база (у таблицю пишуть не лише через домен), цикл станів переживає запис і
/// читання, видалення документа-чернетки прибирає передачі (FK без каскаду).
/// </summary>
/// <remarks>
/// Мутації: прибрати <c>CK_EditAssignment_Validation</c> - червоніє тест «база відхиляє»; прибрати
/// <c>UQ_EditAssignment</c> - червоніє тест дубля зрізу; прибрати видалення передач у
/// <c>DocumentDeletionStore</c> - червоніє тест видалення (FK 547).
/// </remarks>
[Collection("SqlServer")]
public sealed class EditAssignmentSchemaTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 10, 11, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Міграція_створила_таблиці_і_індекси_передачі()
    {
        var tables = await QueryAsync("""
            SELECT t.name FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id
            WHERE s.name = N'doc' AND t.name IN (N'EditAssignment', N'EditAssignmentRecipient', N'EditorSet', N'EditorSetMember')
            ORDER BY t.name
            """);
        Assert.Equal(["EditAssignment", "EditAssignmentRecipient", "EditorSet", "EditorSetMember"], tables);

        var unique = await QueryAsync("""
            SELECT i.name FROM sys.indexes i
            WHERE i.is_unique = 1 AND i.name IN (N'UQ_EditAssignment', N'UQ_EditorSet_Owner')
            ORDER BY i.name
            """);
        Assert.Equal(["UQ_EditAssignment", "UQ_EditorSet_Owner"], unique);

        // «Мої задачі»: ключ (UserId, Status) + INCLUDE (AssignmentId).
        var shape = (await QueryAsync("""
            SELECT STUFF((SELECT N',' + c.name FROM sys.index_columns ic
                          JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                          WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 0
                          ORDER BY ic.key_ordinal FOR XML PATH('')), 1, 1, N'')
                   + N'|' + STUFF((SELECT N',' + c.name FROM sys.index_columns ic
                          JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                          WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 1
                          ORDER BY c.name FOR XML PATH('')), 1, 1, N'')
            FROM sys.indexes i WHERE i.object_id = OBJECT_ID(N'doc.EditAssignmentRecipient') AND i.name = N'IX_EditAssignmentRecipient_User'
            """)).Single();
        Assert.Equal("UserId,Status|AssignmentId", shape);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task База_відхиляє_порушені_стани_зрізу_і_дубль_зрізу()
    {
        var (doc, owner, _) = await SeedAsync();
        var key = "S=" + Guid.NewGuid().ToString("N")[..8] + "|T=*|M=*|P=*";

        // Валідний зріз проходить, дубль (DocumentId, SliceKey) - ні.
        await InsertAssignmentAsync(doc, owner, key);
        await AssertConstraintAsync("UQ_EditAssignment", () => InsertAssignmentAsync(doc, owner, key));

        await AssertConstraintAsync("CK_EditAssignment_Table",
            () => InsertAssignmentAsync(doc, owner, Unique(), "TableCode = N'T1'"));
        await AssertConstraintAsync("CK_EditAssignment_Month",
            () => InsertAssignmentAsync(doc, owner, Unique(), "MonthNumber = 13"));
        await AssertConstraintAsync("CK_EditAssignment_Period",
            () => InsertAssignmentAsync(doc, owner, Unique(), "PeriodKey = 202600"));
        await AssertConstraintAsync("CK_EditAssignment_Validation",
            () => InsertAssignmentAsync(doc, owner, Unique(), "ValidationStatus = 1"));

        // Валідовано з часом і автором - проходить.
        await InsertAssignmentAsync(
            doc, owner, Unique(), $"ValidationStatus = 1, ValidatedAt = '2026-10-11', ValidatedByUserId = {owner}");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task База_відхиляє_суперечливі_стани_виконавця()
    {
        var (doc, owner, executor) = await SeedAsync();
        var assignmentId = await InsertAssignmentAsync(doc, owner, Unique());

        // Assigned з відміткою «заповнено» - суперечність.
        await AssertConstraintAsync("CK_EditAssignmentRecipient_State",
            () => InsertRecipientAsync(assignmentId, executor, owner, "Status = 0, FilledAt = '2026-10-11'"));
        // Filled без часу.
        await AssertConstraintAsync("CK_EditAssignmentRecipient_State",
            () => InsertRecipientAsync(assignmentId, executor, owner, "Status = 1"));
        // Returned без причини.
        await AssertConstraintAsync("CK_EditAssignmentRecipient_State",
            () => InsertRecipientAsync(
                assignmentId, executor, owner,
                $"Status = 2, ReturnedAt = '2026-10-11', ReturnedByUserId = {owner}, ReturnCount = 1"));

        await InsertRecipientAsync(assignmentId, executor, owner, "Status = 0");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Цикл_станів_переживає_запис_а_сховище_не_віддає_чужу_передачу()
    {
        var (doc, owner, executor) = await SeedAsync();
        var builder = new TestDocumentBuilder(sql.ConnectionString);

        long assignmentId;
        await using (var db = builder.CreateContext())
        {
            var assignment = new EditAssignment(
                doc.DocumentId, EditAssignmentSlice.Create(doc.SheetCode, null, 3, doc.PeriodKey.Value),
                isExclusive: true, delegatedByUserId: owner, handoffId: Guid.NewGuid(),
                createdByUserId: owner, utcNow: Now, note: "Заповніть березень");
            assignment.AddRecipient(executor, owner, Now);
            new EditAssignmentStore(db).Add(assignment);
            await db.SaveChangesAsync();
            assignmentId = assignment.Id;
        }

        await using (var db = builder.CreateContext())
        {
            var store = new EditAssignmentStore(db);
            var loaded = await store.FindAsync(doc.DocumentId, assignmentId, CancellationToken.None);
            Assert.NotNull(loaded);
            Assert.Single(loaded.Recipients);
            Assert.NotEmpty(loaded.RowVersion);
            Assert.Equal((byte?)3, loaded.MonthNumber);

            // IDOR: та сама передача під чужим документом - «не знайдено».
            Assert.Null(await store.FindAsync(doc.DocumentId + 1_000_000, assignmentId, CancellationToken.None));

            loaded.FillRecipient(executor, Now.AddHours(1));
            await db.SaveChangesAsync();
        }

        await using (var db = builder.CreateContext())
        {
            var store = new EditAssignmentStore(db);
            var tasks = await store.ListTasksAsync(
                executor, [EditAssignmentRecipientStatus.Filled], doc.DocumentId, CancellationToken.None);
            var task = Assert.Single(tasks);
            Assert.Equal(assignmentId, task.AssignmentId);
            Assert.Equal((byte?)3, task.Slice.MonthNumber);

            Assert.Empty(await store.ListTasksAsync(
                executor, [EditAssignmentRecipientStatus.Assigned], doc.DocumentId, CancellationToken.None));

            var assignment = await store.FindBySliceKeyAsync(
                doc.DocumentId,
                EditAssignmentSlice.Create(doc.SheetCode, null, 3, doc.PeriodKey.Value).Key,
                CancellationToken.None);
            Assert.NotNull(assignment);
            Assert.True(assignment.Validate(owner, Now.AddHours(2)));
            await db.SaveChangesAsync();
        }

        await using var check = builder.CreateContext();
        var final = await check.EditAssignments.AsNoTracking().SingleAsync(a => a.Id == assignmentId);
        Assert.Equal(EditAssignmentValidationStatus.Validated, final.ValidationStatus);
        Assert.Equal(owner, final.ValidatedByUserId);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Видалення_документа_прибирає_передачі_а_набір_знімає_посилання()
    {
        var (doc, owner, executor) = await SeedAsync();
        var builder = new TestDocumentBuilder(sql.ConnectionString);

        long assignmentId;
        int setId;
        await using (var db = builder.CreateContext())
        {
            var set = new EditorSet(owner, "Набір " + Guid.NewGuid().ToString("N")[..8], Now);
            set.AddMember(executor);
            db.EditorSets.Add(set);
            await db.SaveChangesAsync();
            setId = set.Id;

            var assignment = new EditAssignment(
                doc.DocumentId, EditAssignmentSlice.Create(null, null, 1, null),
                true, owner, Guid.NewGuid(), owner, Now);
            assignment.AddRecipient(executor, owner, Now);
            assignment.MarkAppliedSet(set.Id, set.Name);
            db.EditAssignments.Add(assignment);
            await db.SaveChangesAsync();
            assignmentId = assignment.Id;
        }

        // Видалення набору обнуляє AppliedSetId (ON DELETE SET NULL вимкнено конвенцією).
        await using (var db = builder.CreateContext())
        {
            var sets = new EditorSetStore(db);
            var set = await sets.FindAsync(owner, setId, CancellationToken.None);
            Assert.NotNull(set);
            Assert.Null(await sets.FindAsync(owner + 1_000_000, setId, CancellationToken.None)); // чужий набір
            await sets.RemoveAsync(set, CancellationToken.None);
            await db.SaveChangesAsync();
        }

        await using (var check = builder.CreateContext())
        {
            Assert.Null((await check.EditAssignments.AsNoTracking().SingleAsync(a => a.Id == assignmentId)).AppliedSetId);
            Assert.False(await check.EditorSets.AnyAsync(s => s.Id == setId));
        }

        await using (var db = builder.CreateContext())
        {
            await new DocumentDeletionStore(db).DeleteAsync(doc.DocumentId, CancellationToken.None);
        }

        await using var final = builder.CreateContext();
        Assert.False(await final.EditAssignments.AnyAsync(a => a.DocumentId == doc.DocumentId));
        Assert.False(await final.EditAssignmentRecipients.AnyAsync(r => r.AssignmentId == assignmentId));
        Assert.False(await final.Documents.AnyAsync(d => d.Id == doc.DocumentId));
    }

    private static string Unique() => "S=" + Guid.NewGuid().ToString("N")[..12] + "|T=*|M=*|P=*";

    /// <summary>Документ плюс двоє користувачів (власник, виконавець).</summary>
    private async Task<(TestDocument Doc, int Owner, int Executor)> SeedAsync()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(ct: CancellationToken.None);

        await using var db = builder.CreateContext();
        var owner = NewUser();
        var executor = NewUser();
        db.Users.AddRange(owner, executor);
        await db.SaveChangesAsync();
        return (doc, owner.Id, executor.Id);
    }

    private static User NewUser()
    {
        var user = new User($"ea_{Guid.NewGuid():N}"[..20], "EA test user", AuthProvider.Local);
        user.SetPassword("not-a-real-hash"); // CK_User_Provider: локальному - хеш.
        return user;
    }

    private async Task<long> InsertAssignmentAsync(TestDocument doc, int user, string sliceKey, string? extra = null)
    {
        var columns = "DocumentId, SliceKey, DelegatedByUserId, HandoffId, CreatedAt, CreatedByUserId, ModifiedAt, ModifiedByUserId";
        var values = $"{doc.DocumentId}, N'{sliceKey}', {user}, NEWID(), '2026-10-11', {user}, '2026-10-11', {user}";
        if (extra is not null)
        {
            // «Колонка = значення, колонка = значення» → окремі списки колонок і значень.
            var parts = extra.Split(',', StringSplitOptions.TrimEntries);
            columns += ", " + string.Join(", ", parts.Select(p => p.Split('=', 2)[0].Trim()));
            values += ", " + string.Join(", ", parts.Select(p => p.Split('=', 2)[1].Trim()));
        }

        return await ScalarAsync<long>(
            $"SET NOCOUNT ON; INSERT INTO doc.EditAssignment ({columns}) VALUES ({values}); SELECT CAST(SCOPE_IDENTITY() AS bigint);");
    }

    private Task<long> InsertRecipientAsync(long assignmentId, int userId, int assignedBy, string extra)
    {
        var parts = extra.Split(',', StringSplitOptions.TrimEntries);
        var columns = "AssignmentId, UserId, AssignedAt, AssignedByUserId, StatusChangedAt, "
            + string.Join(", ", parts.Select(p => p.Split('=', 2)[0].Trim()));
        var values = $"{assignmentId}, {userId}, '2026-10-11', {assignedBy}, '2026-10-11', "
            + string.Join(", ", parts.Select(p => p.Split('=', 2)[1].Trim()));

        return ScalarAsync<long>($"SET NOCOUNT ON; INSERT INTO doc.EditAssignmentRecipient ({columns}) VALUES ({values}); SELECT CAST(1 AS bigint);");
    }

    private static async Task AssertConstraintAsync(string constraint, Func<Task> action)
    {
        var error = await Assert.ThrowsAsync<SqlException>(action);
        Assert.Contains(constraint, error.Message, StringComparison.Ordinal);
    }

    private async Task<T> ScalarAsync<T>(string query)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = query;
        return (T)Convert.ChangeType((await command.ExecuteScalarAsync())!, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<List<string>> QueryAsync(string query)
    {
        var rows = new List<string>();
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = query;
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(reader.GetString(0));
        }

        return rows;
    }
}
