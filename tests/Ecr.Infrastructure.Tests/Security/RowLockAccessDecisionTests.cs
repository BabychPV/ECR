using Ecr.Application.Common;
using Ecr.Application.Security;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Caching;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Security;

/// <summary>
/// ✎ B75: блокування рядка (<c>doc.RowLock</c>) у <c>AccessDecisionService</c> на справжній базі — усі шляхи запису
/// (пакет комірок PATCH/книги, зріз, поштучний, створення й видалення рядка) бачать його однаково; власник блокування
/// (Q1) і інтеграція (Q2) — ні; зняте, прострочене й ще не почате блокування не діє; версія без погодження ігнорує
/// старий стан аркуша (Q4).
/// </summary>
/// <remarks>
/// ⛔ Безпека запису. МУТАЦІЇ (локально): (1) прибрати <c>RowLockedByOther</c> у <c>Decide</c> — червоніють «…пакет…»,
/// «…зріз…», «…поштучний…»; (2) прибрати його в <c>NewRowDecisions</c> — червоніє «…створення…»; (3) прибрати фільтр
/// <c>OwnerUserId != userId</c> — червоніє «Власник…»; (4) прибрати умову <c>!IsIntegrationWriter</c> — червоніє
/// «Інтеграція…»; (5) прибрати <c>LockFrom &lt;= now</c> — червоніє «…ще_не_почалося…»; (6) прибрати гілку
/// <c>LockedRows</c> у мемо зрізу — червоніє «…сусідній рядок…»; (7) прибрати <c>WorkflowNone → Draft</c> — червоніє
/// «Версія_без_погодження…».
/// </remarks>
[Collection("SqlServer")]
public sealed class RowLockAccessDecisionTests(SqlServerFixture sql) : IDisposable
{
    private const int PeriodKeyValue = 202610;

    private static readonly DateTime Now = new(2026, 10, 15, 10, 0, 0, DateTimeKind.Utc);

    private readonly MemoryCache _memory = new(new MemoryCacheOptions());

    public void Dispose() => _memory.Dispose();

    private sealed record World(TestDocumentBuilder Builder, TestDocument Doc, int LockerUserId, int OtherUserId, string TableCode)
    {
        public CellAddress Cell(int row = 0, int column = 0) => new(Doc.PeriodKey, Doc.RowIds[row], Doc.ColumnDefIds[column]);
    }

    private AccessDecisionService Service(EcrDbContext db)
        => new(
            db,
            new MetadataCache(_memory, db),
            new AccessProfileCache(_memory),
            new TestClock(Now),
            Substitute.For<ICurrentUser>(),
            new WorkflowStore(db));

    private static AccessProfile Writer(World w, int userId)
        => new AccessBuilder { UserId = userId }.Grant(ResourceKind.Project, w.Doc.ProjectId, GrantLevel.Write).Build();

    private async Task<World> ArrangeAsync()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(PeriodKeyValue, columnCount: 2, rowCount: 3, ct: CancellationToken.None);

        await using var db = builder.CreateContext();
        var period = await db.Periods.FirstAsync(
            p => p.ProjectId == doc.ProjectId && p.PeriodKeyValue == PeriodKeyValue, CancellationToken.None);
        period.AdvanceTo(PeriodState.Open, Now);

        var locker = NewUser();
        var other = NewUser();
        db.Users.AddRange(locker, other);
        await db.SaveChangesAsync(CancellationToken.None);

        var tableCode = await db.TableDefs.Where(t => t.Id == doc.TableDefId).Select(t => t.Code).SingleAsync();
        return new World(builder, doc, locker.Id, other.Id, tableCode);
    }

    private async Task<RowLock> LockAsync(
        World w, int row = 0, DateTime? from = null, DateTime? until = null, int? owner = null)
    {
        await using var db = w.Builder.CreateContext();
        var rowKey = await db.TableRows
            .Where(r => r.PeriodKeyValue == PeriodKeyValue && r.Id == w.Doc.RowIds[row])
            .Select(r => r.RowKeyValue)
            .SingleAsync();

        // Створення — «давніше» (домен не приймає кінця в минулому), рішення — за годинником Now.
        var created = (from ?? Now.AddDays(-1)).AddDays(-1);
        var item = RowLock.Create(
            w.Doc.DocumentId, PeriodKeyValue, sheetCopyId: null, w.TableCode, rowKey,
            from ?? Now.AddDays(-1), until ?? Now.AddDays(5), owner ?? w.LockerUserId, note: "test", created);
        db.RowLocks.Add(item);
        await db.SaveChangesAsync();
        return item;
    }

    private async Task<IReadOnlyDictionary<CellAddress, EditDecision>> BatchAsync(
        World w, AccessProfile profile, params CellAddress[] addresses)
    {
        await using var db = w.Builder.CreateContext();
        return await Service(db).CanEditCellsAsync(
            profile, w.Doc.TableInstanceId, w.Doc.PeriodKey, addresses, CancellationToken.None);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "B75")]
    public async Task Чужий_заблокований_рядок_відхиляє_пакет_комірок_RowLocked_а_сусідній_рядок_пишеться()
    {
        var w = await ArrangeAsync();
        await LockAsync(w);

        var decisions = await BatchAsync(w, Writer(w, w.OtherUserId), w.Cell(0, 0), w.Cell(0, 1), w.Cell(1, 0));

        Assert.Equal(EditDenyReason.RowLocked, decisions[w.Cell(0, 0)].Reason);
        Assert.Equal(EditDenyReason.RowLocked, decisions[w.Cell(0, 1)].Reason);
        Assert.True(decisions[w.Cell(1, 0)].IsAllowed);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "B75")]
    public async Task Зріз_і_поштучний_шлях_бачать_замок_а_мемо_класу_не_переносить_його_на_сусідній_рядок()
    {
        var w = await ArrangeAsync();
        await LockAsync(w, row: 1);
        var other = Writer(w, w.OtherUserId);

        await using var db = w.Builder.CreateContext();
        var service = Service(db);
        var slice = await service.CanEditSliceAsync(other, w.Doc.TableInstanceId, CancellationToken.None);
        var single = await service.CanEditCellAsync(other, w.Doc.DocumentId, w.Cell(1, 0), CancellationToken.None);

        Assert.True(slice[w.Cell(0, 0)].IsAllowed);
        Assert.Equal(EditDenyReason.RowLocked, slice[w.Cell(1, 0)].Reason);
        Assert.True(slice[w.Cell(2, 0)].IsAllowed);
        Assert.Equal(EditDenyReason.RowLocked, single.Reason);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "B75")]
    public async Task Створення_й_видалення_заблокованого_рядка_відхиляються_RowLocked()
    {
        var w = await ArrangeAsync();
        var item = await LockAsync(w);

        await using var db = w.Builder.CreateContext();
        var decisions = await Service(db).CanCreateRowsAsync(
            Writer(w, w.OtherUserId), w.Doc.TableInstanceId, [item.RowKey, "FREE_KEY"], CancellationToken.None);

        Assert.Equal(EditDenyReason.RowLocked, decisions[item.RowKey].Row.Reason);
        Assert.True(decisions["FREE_KEY"].Row.IsAllowed);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "B75-Q1")]
    public async Task Власник_блокування_пише_свій_заблокований_рядок()
    {
        var w = await ArrangeAsync();
        await LockAsync(w);

        var decisions = await BatchAsync(w, Writer(w, w.LockerUserId), w.Cell(0, 0));

        Assert.True(decisions[w.Cell(0, 0)].IsAllowed);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "B75-Q2")]
    public async Task Інтеграція_пише_в_заблокований_рядок()
    {
        var w = await ArrangeAsync();
        await LockAsync(w);
        var own = Writer(w, w.OtherUserId);
        var integration = new AccessProfile
        {
            CacheKey = own.CacheKey + "|integration-job",
            UserId = own.UserId,
            SecurityStamp = own.SecurityStamp,
            Permissions = own.Permissions,
            Grants = own.Grants,
            Denies = own.Denies,
            RoleIds = own.RoleIds,
            IsIntegrationWriter = true,
        };

        var decisions = await BatchAsync(w, integration, w.Cell(0, 0));

        Assert.True(decisions[w.Cell(0, 0)].IsAllowed);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "B75")]
    public async Task Зняте_прострочене_й_ще_не_почате_блокування_не_діє()
    {
        var w = await ArrangeAsync();
        var released = await LockAsync(w, row: 0);
        await LockAsync(w, row: 1, from: Now.AddDays(-10), until: Now.AddMinutes(-1));
        await LockAsync(w, row: 2, from: Now.AddDays(1), until: Now.AddDays(3));

        await using (var db = w.Builder.CreateContext())
        {
            var tracked = await db.RowLocks.SingleAsync(x => x.Id == released.Id);
            tracked.Release(w.LockerUserId, Now);
            await db.SaveChangesAsync();
        }

        var decisions = await BatchAsync(w, Writer(w, w.OtherUserId), w.Cell(0, 0), w.Cell(1, 0), w.Cell(2, 0));

        Assert.True(decisions[w.Cell(0, 0)].IsAllowed);
        Assert.True(decisions[w.Cell(1, 0)].IsAllowed);
        Assert.True(decisions[w.Cell(2, 0)].IsAllowed);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "B75-Q4")]
    public async Task Версія_без_погодження_ігнорує_старий_стан_аркуша_а_звичайна_ні()
    {
        var w = await ArrangeAsync();
        await using (var setup = w.Builder.CreateContext())
        {
            var state = new Ecr.Domain.Entities.Workflow.ApprovalState(w.Doc.DocumentId, w.Doc.SheetDefId, PeriodKeyValue);
            state.Submit(w.LockerUserId, Now.AddDays(-2));
            state.Approve(w.LockerUserId, Now.AddDays(-1));
            setup.ApprovalStates.Add(state);
            await setup.SaveChangesAsync();
        }

        var before = await BatchAsync(w, Writer(w, w.OtherUserId), w.Cell(0, 0));
        Assert.Equal(EditDenyReason.DocumentApproved, before[w.Cell(0, 0)].Reason);

        await Execute("UPDATE cfg.TemplateVersion SET WorkflowMode = 1 WHERE Id = @v", ("@v", w.Doc.TemplateVersionId));
        _memory.Clear();

        var after = await BatchAsync(w, Writer(w, w.OtherUserId), w.Cell(0, 0));
        Assert.True(after[w.Cell(0, 0)].IsAllowed);
    }

    private static User NewUser()
    {
        var user = new User($"rl_{Guid.NewGuid():N}"[..20], "B75 row lock user", AuthProvider.Local);
        user.SetPassword("not-a-real-hash");
        return user;
    }

    private async Task Execute(string text, params (string Name, object Value)[] parameters)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = text;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }
}
