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
/// EA-B (P1-N1): повторне читання передач під гейтом БЛОКУЄ переходи станів до коміту запису —
/// детерміновано, без гонки. Транзакція A читає факти й не комітить; з'єднання B із
/// <c>LOCK_TIMEOUT 0</c> не може змінити ні рядок виконавця, ні передачу (SQL 1222); після
/// відкату A — може.
/// </summary>
/// <remarks>
/// ⛔ Мутація: прибрати <c>REPEATABLEREAD</c>/<c>ROWLOCK</c> з <c>LoadLockedAsync</c> (або перейти
/// на звичайне читання) — під RCSI читання не бере блокувань, і обидва UPDATE проходять:
/// червоніє «заблоковано». Окремо: «чужі документи не блокуються» стереже підстановку id з першого
/// statement (P2-N3) — підзапит по документу міг піти через індекс виконавця й заблокувати чужі Fill.
/// </remarks>
[Collection("SqlServer")]
public sealed class EditAssignmentGateLockTests(SqlServerFixture sql)
{
    private const int LockTimeoutExceeded = 1222;
    private static readonly DateTime Now = new(2026, 1, 20, 9, 0, 0, DateTimeKind.Utc);

    private sealed record Seed(
        TestDocument Doc, int Executor, int Delegator, long AssignmentId,
        TestDocument OtherDoc, long OtherAssignmentId);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "EA-B")]
    public async Task Читання_під_гейтом_тримає_спільні_блокування_до_коміту_а_перехід_станів_чекає()
    {
        var seed = await SeedAsync();

        await using var dbA = CreateContext();
        await using var transaction = await dbA.Database.BeginTransactionAsync();

        var gate = await new EditAssignmentStore(dbA)
            .ReadFactsForGateAsync(seed.Doc.DocumentId, seed.Executor, CancellationToken.None);
        Assert.Single(gate.Facts);

        var recipientSql =
            $"UPDATE doc.EditAssignmentRecipient SET StatusChangedAt = SYSUTCDATETIME() "
            + $"WHERE AssignmentId = {seed.AssignmentId} AND UserId = {seed.Executor}";
        var assignmentSql = $"UPDATE doc.EditAssignment SET Note = N'blocked' WHERE Id = {seed.AssignmentId}";

        // Поки запис не завершено, ні «Заповнено» (рядок виконавця), ні валідація (передача) не пройдуть.
        Assert.Equal(LockTimeoutExceeded, await TryUpdateAsync(recipientSql));
        Assert.Equal(LockTimeoutExceeded, await TryUpdateAsync(assignmentSql));

        await transaction.RollbackAsync();

        Assert.Equal(0, await TryUpdateAsync(recipientSql));
        Assert.Equal(0, await TryUpdateAsync(assignmentSql));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "EA-B")]
    public async Task Читання_під_гейтом_не_блокує_чужі_документи_виконавця_і_інших_писарів()
    {
        var seed = await SeedAsync();

        await using var dbA = CreateContext();
        await using var transactionA = await dbA.Database.BeginTransactionAsync();
        await new EditAssignmentStore(dbA)
            .ReadFactsForGateAsync(seed.Doc.DocumentId, seed.Executor, CancellationToken.None);

        // P2-N3: той самий виконавець в ІНШОМУ документі — його Fill не чекає чужого запису.
        var other = $"UPDATE doc.EditAssignmentRecipient SET StatusChangedAt = SYSUTCDATETIME() "
                    + $"WHERE AssignmentId = {seed.OtherAssignmentId} AND UserId = {seed.Executor}";
        Assert.Equal(0, await TryUpdateAsync(other));

        // Два записи по одному документу не серіалізуються (S сумісні між собою).
        await using var dbB = CreateContext();
        await using var transactionB = await dbB.Database.BeginTransactionAsync();
        await dbB.Database.ExecuteSqlRawAsync("SET LOCK_TIMEOUT 0");
        var gateB = await new EditAssignmentStore(dbB)
            .ReadFactsForGateAsync(seed.Doc.DocumentId, seed.Executor, CancellationToken.None);
        Assert.Single(gateB.Facts);

        await transactionB.RollbackAsync();
        await transactionA.RollbackAsync();
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

    private async Task<Seed> SeedAsync()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(rowMode: TableRowMode.Dynamic, ct: CancellationToken.None);
        var otherDoc = await builder.BuildAsync(rowMode: TableRowMode.Dynamic, ct: CancellationToken.None);

        await using var db = builder.CreateContext();
        var delegator = NewUser();
        var executor = NewUser();
        db.Users.AddRange(delegator, executor);
        await db.SaveChangesAsync(CancellationToken.None);

        long Add(TestDocument document)
        {
            var assignment = new EditAssignment(
                document.DocumentId,
                EditAssignmentSlice.Create(null, null, null, document.PeriodKey.Value),
                isExclusive: true, delegatedByUserId: delegator.Id, handoffId: Guid.NewGuid(),
                createdByUserId: delegator.Id, utcNow: Now);
            assignment.AddRecipient(executor.Id, delegator.Id, Now);
            new EditAssignmentStore(db).Add(assignment);
            db.SaveChanges();
            return assignment.Id;
        }

        var first = Add(doc);
        var second = Add(otherDoc);

        return new Seed(doc, executor.Id, delegator.Id, first, otherDoc, second);
    }

    private static User NewUser()
    {
        var user = new User($"lock_{Guid.NewGuid():N}"[..20], "EA-B lock test user", AuthProvider.Local);
        user.SetPassword("not-a-real-hash");
        return user;
    }

    private EcrDbContext CreateContext()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);
}
