// tests/Ecr.Infrastructure.Tests/Security/ParticipantGrantUnderSheetLockTests.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Reporting;
using Ecr.Application.Security;
using Ecr.Application.Workflow;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Expressions.Evaluation;
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
/// D-443 debt: рівень учасника <c>Submit</c>/<c>Approve</c> (надання <c>doc.EditAssignment</c>) перевіряється ще раз
/// ПІД блокуванням аркуша, і зняття надання не проходить «між рішенням і комітом» подання чи затвердження.
/// </summary>
/// <remarks>
/// <para>Факти коду до виправлення: подання приймало рішення (<c>CanSubmitAsync</c>) уже під блокуванням аркуша, а
/// затвердження — двічі (швидка відмова до транзакції й ще раз після <c>EnterAsync</c>), але надання в обох читалися
/// знімком RCSI без блокувань. Зняття, зафіксоване ПІСЛЯ рішення під блокуванням і ДО коміту дії, не було видно.</para>
/// <para>Мутаційний доказ (локально, не в кінчику lane):</para>
/// <list type="bullet">
/// <item><c>AccessDecisionService.WithParticipationAsync</c>: <c>var locked = db.Database.CurrentTransaction is not null;</c>
/// → <c>var locked = false;</c> — червоніє <see cref="Зняття_надання_після_рішення_в_транзакції_дії_чекає_її_коміту"/>
/// (обидва рівні): <c>DELETE</c> виконавця проходить одразу, без 1222.</item>
/// <item><c>ApproveSheetHandler</c>: прибрати другий <c>RequireApproveAllowedAsync</c> у транзакції — червоніє
/// <see cref="Затвердження_відкликане_між_швидкою_перевіркою_і_блокуванням_відмовляє_і_нічого_не_пише"/>.</item>
/// <item><c>SubmitSheetHandler.SubmitUnderLockAsync</c>: прибрати перевірку <c>decision.IsAllowed</c> — червоніє
/// <see cref="Подання_відкликане_між_перевіркою_видимості_і_блокуванням_відмовляє_і_нічого_не_пише"/>.</item>
/// </list>
/// </remarks>
[Collection("SqlServer")]
public sealed class ParticipantGrantUnderSheetLockTests(SqlServerFixture sql) : IDisposable
{
    private const int PeriodKeyValue = 202610;

    /// <summary>Номер помилки SQL Server «Lock request time out period exceeded».</summary>
    private const int LockTimeout = 1222;

    private static readonly DateTime Now = new(2026, 10, 15, 10, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan HookTimeout = TimeSpan.FromSeconds(60);

    private readonly MemoryCache _memory = new(new MemoryCacheOptions());

    /// <inheritdoc />
    public void Dispose() => _memory.Dispose();

    private sealed record Scene(TestDocument Doc, int ParticipantId, int DelegatorId, long AssignmentId);

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-443")]
    [InlineData(GrantLevel.Submit)]
    [InlineData(GrantLevel.Approve)]
    public async Task Зняття_надання_після_рішення_в_транзакції_дії_чекає_її_коміту(GrantLevel level)
    {
        var scene = await ArrangeAsync(level, submitted: level == GrantLevel.Approve);

        await using (var db = CreateContext())
        {
            var service = Service(db);
            var profile = await service.BuildProfileAsync(scene.ParticipantId, CancellationToken.None);
            Assert.True(profile.HasDocumentParticipations);

            // Транзакція дії: рішення приймається в ній (як під блокуванням аркуша подання/затвердження).
            await using var transaction = await db.Database.BeginTransactionAsync(CancellationToken.None);
            var decision = await DecideAsync(service, profile, scene, level);
            Assert.True(decision.IsAllowed, $"Контроль: надання {level} дозволяє дію ({decision.Reason}).");

            // ⛔ Зняття, що приходить ПІСЛЯ рішення й ДО коміту дії, чекає коміту — тут до свого LOCK_TIMEOUT.
            var revoke = await Assert.ThrowsAsync<SqlException>(() => RevokeAsync(scene, lockTimeoutMs: 2000));
            Assert.Equal(LockTimeout, revoke.Number);

            await transaction.RollbackAsync(CancellationToken.None);
        }

        // Після завершення дії зняття проходить, і наступне рішення вже відмовляє.
        Assert.Equal(1, await RevokeAsync(scene, lockTimeoutMs: 10_000));
        await using (var db = CreateContext())
        {
            var service = Service(db);
            var profile = await service.BuildProfileAsync(scene.ParticipantId, CancellationToken.None);
            Assert.False((await DecideAsync(service, profile, scene, level)).IsAllowed);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-443")]
    public async Task Затвердження_відкликане_між_швидкою_перевіркою_і_блокуванням_відмовляє_і_нічого_не_пише()
    {
        var scene = await ArrangeAsync(GrantLevel.Approve, submitted: true);

        await using var db = CreateContext();
        var real = Service(db);
        EditDecision? beforeRevoke = null;
        var calls = 0;

        var access = Forwarding(real);
        access.CanApproveAsync(
                  Arg.Any<AccessProfile>(), Arg.Any<long>(), Arg.Any<int>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
              .Returns(async c =>
              {
                  var decision = await real.CanApproveAsync(
                      c.ArgAt<AccessProfile>(0), c.ArgAt<long>(1), c.ArgAt<int>(2), c.ArgAt<PeriodKey>(3),
                      CancellationToken.None);

                  // Точка перемикання: швидка перевірка ДО транзакції пройшла — надання знімають саме тепер.
                  if (Interlocked.Increment(ref calls) == 1)
                  {
                      beforeRevoke = decision;
                      await RevokeAsync(scene, lockTimeoutMs: 10_000);
                  }

                  return decision;
              });

        var bulk = new BulkCellLoader(db.Database.GetConnectionString()!, 1000);
        var clock = new TestClock(Now);
        var documents = new DocumentStore(db);
        var metadata = new MetadataCache(_memory, db);
        var handler = new ApproveSheetHandler(
            new WorkflowStore(db), access, Reports(documents), new UnitOfWork(db), User(scene.ParticipantId), clock,
            new AuditWriter(db), documents,
            new ApprovalSnapshotRefresh(
                new SheetEditGate(db), new NormalizedCellStore(db), new RowStore(db, bulk, clock), documents, metadata,
                Substitute.For<IDocumentHeaderStore>(), new WorkflowStore(db),
                Substitute.For<Ecr.Application.Recalculation.ISubmitRecalculation>()));

        var error = await Assert.ThrowsAsync<AccessDeniedException>(() => handler
            .HandleAsync(scene.Doc.DocumentId, scene.Doc.SheetDefId, PeriodKeyValue, approved: true, reason: null, CancellationToken.None)
            .WaitAsync(HookTimeout));

        Assert.True(beforeRevoke?.IsAllowed, "Контроль: до зняття надання Approve затвердження дозволене.");
        Assert.Equal("err.ECR-ACCS-0403.approveDenied", error.Details?["messageKey"]?.ToString());

        await using var check = CreateContext();
        Assert.Equal(
            DocumentStatus.Submitted,
            await check.ApprovalStates.AsNoTracking()
                .Where(s => s.DocumentId == scene.Doc.DocumentId && s.SheetDefId == scene.Doc.SheetDefId && s.PeriodKey == PeriodKeyValue)
                .Select(s => s.Status)
                .SingleAsync(CancellationToken.None));
        Assert.Equal(0, await check.ApprovalEvents.AsNoTracking().CountAsync(e => e.DocumentId == scene.Doc.DocumentId, CancellationToken.None));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-443")]
    public async Task Подання_відкликане_між_перевіркою_видимості_і_блокуванням_відмовляє_і_нічого_не_пише()
    {
        var scene = await ArrangeAsync(GrantLevel.Submit, submitted: false);

        await using var db = CreateContext();
        var real = Service(db);
        EditDecision? beforeRevoke = null;
        var calls = 0;

        var access = Forwarding(real);

        // Точка перемикання: остання перевірка ДО транзакції подання — межі читання (видимість аркуша).
        access.ReadScopeAsync(Arg.Any<AccessProfile>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
              .Returns(async c =>
              {
                  var scope = await real.ReadScopeAsync(c.ArgAt<AccessProfile>(0), c.ArgAt<long>(1), CancellationToken.None);
                  if (Interlocked.Increment(ref calls) == 1)
                  {
                      beforeRevoke = await real.CanSubmitAsync(
                          c.ArgAt<AccessProfile>(0), scene.Doc.DocumentId, scene.Doc.SheetDefId, new PeriodKey(PeriodKeyValue),
                          CancellationToken.None);
                      await RevokeAsync(scene, lockTimeoutMs: 10_000);
                  }

                  return scope;
              });

        var bulk = new BulkCellLoader(db.Database.GetConnectionString()!, 1000);
        var clock = new TestClock(Now);
        var documents = new DocumentStore(db);
        var headers = Substitute.For<IDocumentHeaderStore>();
        headers.GetExpressionValuesAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
               .Returns(new Dictionary<string, ExpressionValue>());
        headers.GetValuesAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
               .Returns(new Dictionary<int, DocumentHeaderValueData>());

        var handler = new SubmitSheetHandler(
            new NormalizedCellStore(db), new RowStore(db, bulk, clock), new WorkflowStore(db), documents,
            new MetadataCache(_memory, db), access,
            new Ecr.Application.Validation.ValidationEngine(new RealFormulaEngine()),
            headers, Reports(documents), new UnitOfWork(db), User(scene.ParticipantId), clock, new SheetEditGate(db),
            Substitute.For<Ecr.Application.Recalculation.ISubmitRecalculation>(),
            Substitute.For<IMethodologyStore>(), new TemplateVersionStore(db), new RegistryStore(db), new AuditWriter(db));

        var error = await Assert.ThrowsAsync<AccessDeniedException>(() => handler
            .HandleAsync(scene.Doc.DocumentId, scene.Doc.SheetDefId, PeriodKeyValue, CancellationToken.None)
            .WaitAsync(HookTimeout));

        Assert.True(beforeRevoke?.IsAllowed, "Контроль: до зняття надання Submit подання дозволене.");
        Assert.Equal("err.ECR-ACCS-0403.submitDenied", error.Details?["messageKey"]?.ToString());

        await using var check = CreateContext();
        Assert.Equal(0, await check.SubmissionSnapshots.AsNoTracking().CountAsync(s => s.DocumentId == scene.Doc.DocumentId, CancellationToken.None));
        Assert.False(await check.ApprovalStates.AsNoTracking().AnyAsync(
            s => s.DocumentId == scene.Doc.DocumentId && s.Status != DocumentStatus.Draft, CancellationToken.None));
        Assert.Equal(0, await check.ApprovalEvents.AsNoTracking().CountAsync(e => e.DocumentId == scene.Doc.DocumentId, CancellationToken.None));
    }

    private static Task<EditDecision> DecideAsync(
        AccessDecisionService service, AccessProfile profile, Scene scene, GrantLevel level)
        => level == GrantLevel.Approve
            ? service.CanApproveAsync(profile, scene.Doc.DocumentId, scene.Doc.SheetDefId, new PeriodKey(PeriodKeyValue), CancellationToken.None)
            : service.CanSubmitAsync(profile, scene.Doc.DocumentId, scene.Doc.SheetDefId, new PeriodKey(PeriodKeyValue), CancellationToken.None);

    /// <summary>Учасник без ролі з наданням рівня <paramref name="level"/> на весь документ; аркуш — Draft чи поданий.</summary>
    private async Task<Scene> ArrangeAsync(GrantLevel level, bool submitted)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(PeriodKeyValue, columnCount: 2, rowCount: 1, ct: CancellationToken.None);

        await using var db = builder.CreateContext();
        var period = await db.Periods.FirstAsync(
            p => p.ProjectId == doc.ProjectId && p.PeriodKeyValue == PeriodKeyValue, CancellationToken.None);
        period.AdvanceTo(PeriodState.Open, Now);

        var delegator = NewUser();
        var participant = NewUser();
        db.Users.AddRange(delegator, participant);
        await db.SaveChangesAsync(CancellationToken.None);

        var assignment = new EditAssignment(
            doc.DocumentId,
            EditAssignmentSlice.Create(sheetCode: null, tableCode: null, monthNumber: null, periodKey: null),
            isExclusive: false,
            delegatedByUserId: delegator.Id,
            handoffId: Guid.NewGuid(),
            createdByUserId: delegator.Id,
            utcNow: Now,
            level: level);
        assignment.AddRecipient(participant.Id, delegator.Id, Now);
        new EditAssignmentStore(db).Add(assignment);

        if (submitted)
        {
            var state = await new WorkflowStore(db).GetOrCreateAsync(
                doc.DocumentId, doc.SheetDefId, new PeriodKey(PeriodKeyValue), CancellationToken.None);
            state.Submit(delegator.Id, Now);
        }

        await db.SaveChangesAsync(CancellationToken.None);
        return new Scene(doc, participant.Id, delegator.Id, assignment.Id);
    }

    /// <summary>Зняття надання учасника — рядок виконавця, як <c>EditAssignmentStore.Remove</c>; повертає кількість рядків.</summary>
    private async Task<int> RevokeAsync(Scene scene, int lockTimeoutMs)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SET LOCK_TIMEOUT {lockTimeoutMs}; "
            + "DELETE FROM doc.EditAssignmentRecipient WHERE AssignmentId = @a AND UserId = @u;";
        command.Parameters.AddWithValue("@a", scene.AssignmentId);
        command.Parameters.AddWithValue("@u", scene.ParticipantId);
        return await command.ExecuteNonQueryAsync();
    }

    /// <summary>Підміна служби, що віддає справжні рішення (точки перемикання налаштовує тест).</summary>
    private static IAccessDecisionService Forwarding(AccessDecisionService real)
    {
        var access = Substitute.For<IAccessDecisionService>();
        access.BuildProfileAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
              .Returns(c => real.BuildProfileAsync(c.ArgAt<int>(0), CancellationToken.None));
        access.CanReadDocumentAsync(Arg.Any<AccessProfile>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
              .Returns(c => real.CanReadDocumentAsync(c.ArgAt<AccessProfile>(0), c.ArgAt<long>(1), CancellationToken.None));
        access.ReadScopeAsync(Arg.Any<AccessProfile>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
              .Returns(c => real.ReadScopeAsync(c.ArgAt<AccessProfile>(0), c.ArgAt<long>(1), CancellationToken.None));
        access.CanSubmitAsync(
                  Arg.Any<AccessProfile>(), Arg.Any<long>(), Arg.Any<int>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
              .Returns(c => real.CanSubmitAsync(
                  c.ArgAt<AccessProfile>(0), c.ArgAt<long>(1), c.ArgAt<int>(2), c.ArgAt<PeriodKey>(3), CancellationToken.None));
        access.CanApproveAsync(
                  Arg.Any<AccessProfile>(), Arg.Any<long>(), Arg.Any<int>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
              .Returns(c => real.CanApproveAsync(
                  c.ArgAt<AccessProfile>(0), c.ArgAt<long>(1), c.ArgAt<int>(2), c.ArgAt<PeriodKey>(3), CancellationToken.None));
        access.CurrentApprovalStepAsync(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
              .Returns(c => real.CurrentApprovalStepAsync(c.ArgAt<long>(0), c.ArgAt<int>(1), c.ArgAt<PeriodKey>(2), CancellationToken.None));
        return access;
    }

    private AccessDecisionService Service(EcrDbContext db)
        => new(
            db,
            new MetadataCache(_memory, db),
            new AccessProfileCache(_memory),
            new TestClock(Now),
            Substitute.For<ICurrentUser>(),
            new WorkflowStore(db));

    private static ReportSnapshotSync Reports(IDocumentStore documents)
    {
        var snapshots = Substitute.For<IReportSnapshotBuilder>();
        snapshots.ListAsync(
                     Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<IReadOnlyCollection<int>?>(), Arg.Any<CancellationToken>())
                 .Returns<IReadOnlyList<ReportSnapshotSummary>>([]);
        return new ReportSnapshotSync(snapshots, documents);
    }

    private static ICurrentUser User(int userId)
    {
        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(userId);
        return user;
    }

    private static User NewUser()
    {
        var user = new User($"pgl_{Guid.NewGuid():N}"[..20], "D-443 lock test user", AuthProvider.Local);
        user.SetPassword("not-a-real-hash");
        return user;
    }

    private EcrDbContext CreateContext()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);
}
