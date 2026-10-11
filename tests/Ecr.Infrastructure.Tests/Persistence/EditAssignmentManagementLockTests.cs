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
/// EA-C: блокування керування передачами — детерміновано, без гонок і таймінгів. Одна транзакція
/// тримає блокування, друге з'єднання з <c>LOCK_TIMEOUT 0</c> одразу бачить 1222 (чекало б) або
/// проходить.
/// </summary>
/// <remarks>
/// ⛔ Мутації: (1) прибрати <c>LockDocumentAsync</c> з upsert / UPDLOCK із нього — «документ під
/// блокуванням» не дає 1222; (2) у <c>LockRecipientsAsync</c> поміняти порядок (виконавці раніше за
/// передачу) — червоніє «порядок N4».
/// </remarks>
[Collection("SqlServer")]
public sealed class EditAssignmentManagementLockTests(SqlServerFixture sql)
{
    private const int LockTimeoutExceeded = 1222;
    private static readonly DateTime Now = new(2026, 1, 20, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "EA-C")]
    public async Task Створення_передачі_чекає_видалення_документа_а_видалений_документ_дає_false()
    {
        var seed = await SeedAsync();

        // «Видалення»: транзакція тримає UPDLOCK на рядку документа (так само бере й DocumentDeletionStore).
        await using (var deleter = CreateContext())
        await using (var transaction = await deleter.Database.BeginTransactionAsync())
        {
            _ = await deleter.Documents
                .FromSql($"SELECT * FROM doc.Document WITH (UPDLOCK, ROWLOCK) WHERE Id = {seed.DocumentId}")
                .Select(d => d.Id).ToListAsync();

            await using var creator = CreateContext();
            await using var creatorTx = await creator.Database.BeginTransactionAsync();
            await creator.Database.ExecuteSqlRawAsync("SET LOCK_TIMEOUT 0");
            var ex = await Assert.ThrowsAnyAsync<Exception>(
                () => new EditAssignmentManagementStore(creator).LockDocumentAsync(seed.DocumentId, CancellationToken.None));
            Assert.Contains(LockTimeoutExceeded, SqlNumbers(ex));

            await transaction.RollbackAsync();
        }

        // Після завершення «видалення» створювач проходить; документа, якого немає, — false (→ 404).
        await using var after = CreateContext();
        await using var afterTx = await after.Database.BeginTransactionAsync();
        var store = new EditAssignmentManagementStore(after);
        Assert.True(await store.LockDocumentAsync(seed.DocumentId, CancellationToken.None));
        Assert.False(await store.LockDocumentAsync(long.MaxValue, CancellationToken.None));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "EA-C")]
    public async Task Перехід_бере_передачу_раніше_за_виконавців_порядок_N4()
    {
        var seed = await SeedAsync();

        // «Запис комірки» (EA-B) тримає S на рядку передачі й ще не дійшов до виконавців.
        await using var writer = CreateContext();
        await using var writerTx = await writer.Database.BeginTransactionAsync();
        _ = await writer.EditAssignments
            .FromSql($"SELECT * FROM doc.EditAssignment WITH (REPEATABLEREAD, ROWLOCK) WHERE Id = {seed.AssignmentId}")
            .AsNoTracking().Select(a => a.Id).ToListAsync();

        // Перехід чекає на X передачі (1222 при LOCK_TIMEOUT 0) — і НЕ встиг узяти нічого на виконавцях.
        await using var transition = CreateContext();
        await using var transitionTx = await transition.Database.BeginTransactionAsync();
        await transition.Database.ExecuteSqlRawAsync("SET LOCK_TIMEOUT 0");
        var ex = await Assert.ThrowsAnyAsync<Exception>(
            () => new EditAssignmentManagementStore(transition).LockRecipientsAsync(seed.AssignmentId, CancellationToken.None));
        Assert.Contains(LockTimeoutExceeded, SqlNumbers(ex));

        // Третє з'єднання змінює виконавця без очікування: перехід нічого на ньому не тримає.
        Assert.Equal(0, await TryUpdateAsync(
            $"UPDATE doc.EditAssignmentRecipient SET StatusChangedAt = SYSUTCDATETIME() "
            + $"WHERE AssignmentId = {seed.AssignmentId} AND UserId = {seed.Executor}"));

        await transitionTx.RollbackAsync();
        await writerTx.RollbackAsync();

        // Без конкуруючого запису перехід бере обидва рівні.
        await using var free = CreateContext();
        await using var freeTx = await free.Database.BeginTransactionAsync();
        await new EditAssignmentManagementStore(free).LockRecipientsAsync(seed.AssignmentId, CancellationToken.None);
        Assert.Equal(LockTimeoutExceeded, await TryUpdateAsync(
            $"UPDATE doc.EditAssignmentRecipient SET StatusChangedAt = SYSUTCDATETIME() "
            + $"WHERE AssignmentId = {seed.AssignmentId} AND UserId = {seed.Executor}"));
    }

    private static IEnumerable<int> SqlNumbers(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is SqlException sqlException)
            {
                yield return sqlException.Number;
            }
        }
    }

    private async Task<int> TryUpdateAsync(string updateSql)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SET LOCK_TIMEOUT 0; " + updateSql;
        try
        {
            await command.ExecuteNonQueryAsync();
            return 0;
        }
        catch (SqlException ex)
        {
            return ex.Number;
        }
    }

    private sealed record Seed(long DocumentId, long AssignmentId, int Executor);

    private async Task<Seed> SeedAsync()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(rowMode: TableRowMode.Dynamic, ct: CancellationToken.None);

        await using var db = builder.CreateContext();
        var delegator = NewUser();
        var executor = NewUser();
        db.Users.AddRange(delegator, executor);
        await db.SaveChangesAsync(CancellationToken.None);

        var assignment = new EditAssignment(
            doc.DocumentId,
            EditAssignmentSlice.Create(null, null, null, doc.PeriodKey.Value),
            isExclusive: true, delegatedByUserId: delegator.Id, handoffId: Guid.NewGuid(),
            createdByUserId: delegator.Id, utcNow: Now);
        assignment.AddRecipient(executor.Id, delegator.Id, Now);
        new EditAssignmentStore(db).Add(assignment);
        await db.SaveChangesAsync(CancellationToken.None);

        return new Seed(doc.DocumentId, assignment.Id, executor.Id);
    }

    private static User NewUser()
    {
        var user = new User($"mgmt_{Guid.NewGuid():N}"[..20], "EA-C lock test user", AuthProvider.Local);
        user.SetPassword("not-a-real-hash");
        return user;
    }

    private EcrDbContext CreateContext()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);
}
