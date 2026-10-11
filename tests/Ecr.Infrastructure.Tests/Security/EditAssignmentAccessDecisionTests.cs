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
/// EA-B: «передача місяця» у <c>AccessDecisionService</c> на справжній базі — усі шляхи
/// (пакетний запис, зріз, поштучний, створення рядків) бачать передачу однаково.
/// </summary>
/// <remarks>
/// ⛔ Безпека запису. Мутаційні докази (локально, у detached-worktree): (1) прибрати
/// <c>EditAssignmentGuard.Evaluate</c> у <c>Decide</c> — червоніє кожен тест «передача діє»;
/// (2) у <c>CanEditCellAsync</c> повернути <c>BuildContextAsync + EditRules.CanEdit</c> —
/// червоніє «поштучний шлях бачить передачу»; (3) не передавати <c>profile.UserId</c> у
/// читання передач — червоніє «чужа передача не піднімає».
/// </remarks>
[Collection("SqlServer")]
public sealed class EditAssignmentAccessDecisionTests(SqlServerFixture sql) : IDisposable
{
    private const int PeriodKeyValue = 202610;
    private const byte OwnMonth = 10;
    private const byte OtherMonth = 11;

    private static readonly DateTime Now = new(2026, 10, 15, 10, 0, 0, DateTimeKind.Utc);

    private readonly MemoryCache _memory = new(new MemoryCacheOptions());

    public void Dispose() => _memory.Dispose();

    private sealed record World(
        TestDocumentBuilder Builder,
        TestDocument Doc,
        int OwnerUserId,
        int DelegatorUserId,
        int ExecutorUserId,
        int StrangerUserId,
        long AssignmentId)
    {
        public int OwnMonthColumn => Doc.ColumnDefIds[0];

        public int OtherMonthColumn => Doc.ColumnDefIds[1];

        public int PlainColumn => Doc.ColumnDefIds[2];

        public CellAddress Cell(int columnDefId, int row = 0)
            => new(Doc.PeriodKey, Doc.RowIds[row], columnDefId);
    }

    private AccessDecisionService Service(EcrDbContext db)
        => new(
            db,
            new MetadataCache(_memory, db),
            new AccessProfileCache(_memory),
            new TestClock(Now),
            Substitute.For<ICurrentUser>(),
            new WorkflowStore(db));

    private static AccessProfile Profile(World w, int userId, GrantLevel level)
        => new AccessBuilder { UserId = userId }
            .Grant(ResourceKind.Project, w.Doc.ProjectId, level)
            .Build();

    /// <summary>Документ із двома місячними колонками, власником, делегатором, виконавцем і сторонньою людиною.</summary>
    private async Task<World> ArrangeAsync(
        bool exclusive = true, DateTime? expiresAt = null, Action<EditAssignment>? shape = null,
        byte? month = OwnMonth)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(PeriodKeyValue, columnCount: 3, rowCount: 2, ct: CancellationToken.None);

        await Execute(
            "UPDATE cfg.ColumnDef SET IsMonthColumn = 1, MonthNumber = @m WHERE Id = @id",
            ("@id", doc.ColumnDefIds[0]), ("@m", OwnMonth));
        await Execute(
            "UPDATE cfg.ColumnDef SET IsMonthColumn = 1, MonthNumber = @m WHERE Id = @id",
            ("@id", doc.ColumnDefIds[1]), ("@m", OtherMonth));

        await using var db = builder.CreateContext();

        var period = await db.Periods.FirstAsync(
            p => p.ProjectId == doc.ProjectId && p.PeriodKeyValue == PeriodKeyValue, CancellationToken.None);
        period.AdvanceTo(PeriodState.Open, Now);

        var delegator = NewUser();
        var executor = NewUser();
        var stranger = NewUser();
        db.Users.AddRange(delegator, executor, stranger);
        await db.SaveChangesAsync(CancellationToken.None);

        var ownerId = await db.Documents
            .Where(d => d.Id == doc.DocumentId)
            .Select(d => d.CreatedByUserId)
            .SingleAsync(CancellationToken.None);

        var assignment = new EditAssignment(
            doc.DocumentId,
            EditAssignmentSlice.Create(sheetCode: null, tableCode: null, monthNumber: month, periodKey: null),
            isExclusive: exclusive,
            delegatedByUserId: delegator.Id,
            handoffId: Guid.NewGuid(),
            createdByUserId: delegator.Id,
            utcNow: Now,
            expiresAt: expiresAt);
        assignment.AddRecipient(executor.Id, delegator.Id, Now);
        shape?.Invoke(assignment);

        new EditAssignmentStore(db).Add(assignment);
        await db.SaveChangesAsync(CancellationToken.None);

        return new World(builder, doc, ownerId, delegator.Id, executor.Id, stranger.Id, assignment.Id);
    }

    private async Task<IReadOnlyDictionary<CellAddress, EditDecision>> BatchAsync(
        World w, AccessProfile profile, params CellAddress[] addresses)
    {
        await using var db = w.Builder.CreateContext();
        return await Service(db).CanEditCellsAsync(
            profile, w.Doc.TableInstanceId, w.Doc.PeriodKey, addresses, CancellationToken.None);
    }

    private async Task MutateAsync(World w, Action<EditAssignment> change)
    {
        await using var db = w.Builder.CreateContext();
        var store = new EditAssignmentStore(db);
        var assignment = await store.FindAsync(w.Doc.DocumentId, w.AssignmentId, CancellationToken.None);
        change(assignment!);
        await db.SaveChangesAsync(CancellationToken.None);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "EA-B")]
    public async Task Виконавець_із_Read_пише_лише_місячну_колонку_переданого_місяця()
    {
        var w = await ArrangeAsync();
        var profile = Profile(w, w.ExecutorUserId, GrantLevel.Read);

        var decisions = await BatchAsync(
            w, profile, w.Cell(w.OwnMonthColumn), w.Cell(w.OtherMonthColumn), w.Cell(w.PlainColumn));

        Assert.True(decisions[w.Cell(w.OwnMonthColumn)].IsAllowed);
        Assert.True(decisions[w.Cell(w.OwnMonthColumn)].ViaAssignment);
        Assert.True(decisions[w.Cell(w.OwnMonthColumn)].AssignmentsPresent);
        Assert.Equal(EditDenyReason.InsufficientGrantLevel, decisions[w.Cell(w.OtherMonthColumn)].Reason);
        Assert.Equal(EditDenyReason.InsufficientGrantLevel, decisions[w.Cell(w.PlainColumn)].Reason);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "EA-B")]
    public async Task Без_гранта_і_з_явною_забороною_передача_нічого_не_піднімає()
    {
        var w = await ArrangeAsync();
        var none = new AccessBuilder { UserId = w.ExecutorUserId }.Build();
        var denied = new AccessBuilder { UserId = w.ExecutorUserId }
            .Grant(ResourceKind.Project, w.Doc.ProjectId, GrantLevel.Read)
            .Deny(ResourceKind.Column, w.OwnMonthColumn)
            .Build();

        var a = await BatchAsync(w, none, w.Cell(w.OwnMonthColumn));
        var b = await BatchAsync(w, denied, w.Cell(w.OwnMonthColumn));

        Assert.Equal(EditDenyReason.NoGrant, a[w.Cell(w.OwnMonthColumn)].Reason);
        Assert.Equal(EditDenyReason.NoGrant, b[w.Cell(w.OwnMonthColumn)].Reason);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "EA-B")]
    public async Task Цикл_Заповнено_Повернуто_Валідовано_змінює_рішення_виконавця()
    {
        var w = await ArrangeAsync();
        var profile = Profile(w, w.ExecutorUserId, GrantLevel.Read);
        var cell = w.Cell(w.OwnMonthColumn);

        Assert.True((await BatchAsync(w, profile, cell))[cell].IsAllowed);

        await MutateAsync(w, a => a.FillRecipient(w.ExecutorUserId, Now));
        Assert.Equal(EditDenyReason.AssignmentFilled, (await BatchAsync(w, profile, cell))[cell].Reason);

        await MutateAsync(w, a => a.ReturnRecipient(w.ExecutorUserId, w.DelegatorUserId, "Уточніть", Now));
        Assert.True((await BatchAsync(w, profile, cell))[cell].IsAllowed);

        await MutateAsync(w, a => a.FillRecipient(w.ExecutorUserId, Now));
        await MutateAsync(w, a => a.Validate(w.DelegatorUserId, Now));
        Assert.Equal(EditDenyReason.AssignmentValidated, (await BatchAsync(w, profile, cell))[cell].Reason);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "EA-B")]
    public async Task Ексклюзивна_передача_закриває_місяць_іншим_але_не_власнику_і_не_делегатору()
    {
        var w = await ArrangeAsync(exclusive: true);
        var cell = w.Cell(w.OwnMonthColumn);
        var other = w.Cell(w.OtherMonthColumn);

        var stranger = await BatchAsync(w, Profile(w, w.StrangerUserId, GrantLevel.Write), cell, other);
        var owner = await BatchAsync(w, Profile(w, w.OwnerUserId, GrantLevel.Write), cell);
        var delegator = await BatchAsync(w, Profile(w, w.DelegatorUserId, GrantLevel.Write), cell);

        Assert.Equal(EditDenyReason.LockedByOwner, stranger[cell].Reason);
        Assert.True(stranger[other].IsAllowed);
        Assert.True(owner[cell].IsAllowed);
        Assert.True(delegator[cell].IsAllowed);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "EA-B")]
    public async Task Неексклюзивна_передача_не_закриває_місяць_іншим()
    {
        var w = await ArrangeAsync(exclusive: false);
        var cell = w.Cell(w.OwnMonthColumn);

        var stranger = await BatchAsync(w, Profile(w, w.StrangerUserId, GrantLevel.Write), cell);

        Assert.True(stranger[cell].IsAllowed);
        Assert.False(stranger[cell].ViaAssignment);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "EA-B")]
    public async Task Строк_минув_і_делегатор_вимкнений_передача_не_діє()
    {
        var expired = await ArrangeAsync(expiresAt: Now.AddDays(-1));
        var cell = expired.Cell(expired.OwnMonthColumn);
        var decision = (await BatchAsync(
            expired, Profile(expired, expired.ExecutorUserId, GrantLevel.Read), cell))[cell];
        Assert.Equal(EditDenyReason.AssignmentExpired, decision.Reason);

        var inactive = await ArrangeAsync();
        await Execute("UPDATE sec.[User] SET IsActive = 0 WHERE Id = @id", ("@id", inactive.DelegatorUserId));
        var inactiveCell = inactive.Cell(inactive.OwnMonthColumn);
        var after = (await BatchAsync(
            inactive, Profile(inactive, inactive.ExecutorUserId, GrantLevel.Read), inactiveCell))[inactiveCell];
        Assert.Equal(EditDenyReason.InsufficientGrantLevel, after.Reason);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "EA-B")]
    public async Task Поштучний_зрізовий_і_створення_рядків_бачать_передачу_так_само()
    {
        var w = await ArrangeAsync();
        var profile = Profile(w, w.ExecutorUserId, GrantLevel.Read);
        var own = w.Cell(w.OwnMonthColumn);
        var other = w.Cell(w.OtherMonthColumn);

        await using var db = w.Builder.CreateContext();
        var service = Service(db);

        // Поштучний шлях — той самий пакетний (F-A), а не обхід.
        var single = await service.CanEditCellAsync(profile, w.Doc.DocumentId, own, CancellationToken.None);
        var singleOther = await service.CanEditCellAsync(profile, w.Doc.DocumentId, other, CancellationToken.None);
        Assert.True(single.IsAllowed);
        Assert.Equal(EditDenyReason.InsufficientGrantLevel, singleOther.Reason);

        // Чужий документ чи неіснуючий рядок — відмова без розрізнення.
        var wrongDocument = await service.CanEditCellAsync(profile, w.Doc.DocumentId + 1_000_000, own, CancellationToken.None);
        Assert.False(wrongDocument.IsAllowed);

        var slice = await service.CanEditSliceAsync(profile, w.Doc.TableInstanceId, CancellationToken.None);
        Assert.True(slice[own].IsAllowed);
        Assert.False(slice[other].IsAllowed);

        // D5: виконавець рядків не створює — рівень рядка підйому не має.
        var rows = await service.CanCreateRowsAsync(
            profile, w.Doc.TableInstanceId, ["new-row"], CancellationToken.None);
        Assert.False(rows["new-row"].Row.IsAllowed);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "EA-B")]
    public async Task Створення_рядка_закривається_передачею_без_місяця_а_виконавцю_не_піднімається()
    {
        // Передача на весь документ (без місяця): на рівні рядка діють лише заборони (D5).
        var w = await ArrangeAsync(month: null);
        const string newRow = "new-row";

        async Task<EditDecision> RowAsync(AccessProfile profile)
        {
            await using var db = w.Builder.CreateContext();
            var rows = await Service(db).CanCreateRowsAsync(
                profile, w.Doc.TableInstanceId, [newRow], CancellationToken.None);
            return rows[newRow].Row;
        }

        var stranger = await RowAsync(Profile(w, w.StrangerUserId, GrantLevel.Write));
        Assert.Equal(EditDenyReason.LockedByOwner, stranger.Reason);

        Assert.True((await RowAsync(Profile(w, w.OwnerUserId, GrantLevel.Write))).IsAllowed);

        // Виконавець із Read: підйому на рівні рядка немає.
        var reader = await RowAsync(Profile(w, w.ExecutorUserId, GrantLevel.Read));
        Assert.Equal(EditDenyReason.InsufficientGrantLevel, reader.Reason);

        // Виконавець із Write: поки Assigned — можна; після «Заповнено» і «Валідовано» — ні.
        var writer = Profile(w, w.ExecutorUserId, GrantLevel.Write);
        Assert.True((await RowAsync(writer)).IsAllowed);

        await MutateAsync(w, a => a.FillRecipient(w.ExecutorUserId, Now));
        Assert.Equal(EditDenyReason.AssignmentFilled, (await RowAsync(writer)).Reason);

        await MutateAsync(w, a => a.Validate(w.DelegatorUserId, Now));
        Assert.Equal(EditDenyReason.AssignmentValidated, (await RowAsync(writer)).Reason);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "EA-B")]
    public async Task Документ_без_передач_не_читає_їх_і_не_позначається_ознакою()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(PeriodKeyValue, columnCount: 2, rowCount: 1, ct: CancellationToken.None);
        await using var db = builder.CreateContext();
        var period = await db.Periods.FirstAsync(
            p => p.ProjectId == doc.ProjectId && p.PeriodKeyValue == PeriodKeyValue, CancellationToken.None);
        period.AdvanceTo(PeriodState.Open, Now);
        await db.SaveChangesAsync(CancellationToken.None);

        var profile = new AccessBuilder { UserId = 4242 }
            .Grant(ResourceKind.Project, doc.ProjectId, GrantLevel.Write)
            .Build();
        var address = new CellAddress(doc.PeriodKey, doc.RowIds[0], doc.ColumnDefIds[0]);

        var decisions = await Service(db).CanEditCellsAsync(
            profile, doc.TableInstanceId, doc.PeriodKey, [address], CancellationToken.None);

        Assert.True(decisions[address].IsAllowed);
        Assert.False(decisions[address].AssignmentsPresent);
        Assert.False(decisions[address].ViaAssignment);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "EA-B")]
    public async Task Симуляція_і_системний_шлях_передачею_не_змінюються()
    {
        var w = await ArrangeAsync();
        var cell = w.Cell(w.OwnMonthColumn);

        var simulation = new AccessBuilder { UserId = w.ExecutorUserId }
            .Grant(ResourceKind.Project, w.Doc.ProjectId, GrantLevel.Read)
            .Build(simulation: true, simulatedFor: w.ExecutorUserId);
        var integration = new AccessProfile
        {
            CacheKey = "integration-ea-b",
            UserId = w.StrangerUserId,
            SecurityStamp = "s",
            Permissions = new HashSet<string>(),
            Grants = new Dictionary<string, GrantLevel>(),
            Denies = new HashSet<string>(),
            RoleIds = new HashSet<int>(),
            IsIntegrationWriter = true,
        };

        var simulated = await BatchAsync(w, simulation, cell);
        var system = await BatchAsync(w, integration, cell);

        Assert.Equal(EditDenyReason.SimulationReadOnly, simulated[cell].Reason);
        Assert.True(system[cell].IsAllowed);
    }

    private static User NewUser()
    {
        var user = new User($"eab_{Guid.NewGuid():N}"[..20], "EA-B test user", AuthProvider.Local);
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
