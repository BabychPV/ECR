// tests/Ecr.Infrastructure.Tests/Persistence/DocumentLockOrderDeadlockTests.cs
using Ecr.Application.Common;
using Ecr.Application.Documents;
using Ecr.Application.Documents.Dto;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Порядок блокувань «спершу <c>wf.ApprovalState</c>, потім <c>doc.Document</c>»
/// у правці шапки (C2) і зміні ключа документа — без дедлоку з тими, хто бере
/// стани аркушів раніше за документ.
/// </summary>
/// <remarks>
/// T2 — обробник під тестом (справжній, на справжніх сховищах). Старий порядок:
/// U на <c>doc.Document</c> (<c>DocumentKeyStore.FindForUpdateAsync</c>), потім
/// <c>UPDLOCK, HOLDLOCK</c> на станах документа
/// (<c>DocumentDeletionStore.LockWorkflowFactsAsync</c>). Суперники — сирі
/// з'єднання з <c>DEADLOCK_PRIORITY HIGH</c>, тож за циклу жертвою детерміновано
/// стає T2, і тест бачить <c>SqlException 1205</c>.
///
/// ⛔ <see cref="Contender.StatesThenDocument"/> — порядок <c>DeleteDocumentHandler</c>:
/// стани під <c>UPDLOCK, HOLDLOCK</c>, потім запис рядка документа. Зі старим
/// порядком T2 це дедлок двох учасників на будь-якій конфігурації бази.
///
/// ⛔ <see cref="Contender.SubmitWithTouch"/> — сценарій «подання + дотик»:
/// T1 (подання) тримає X на рядку стану й потім вставляє <c>wf.ApprovalEvent</c>,
/// перевірка <c>FK_ApprEvent_Doc</c> просить S на документі; T3 («дотик» від
/// правки комірки іншого аркуша) чекає на документ за U від T2. Черга FIFO:
/// S сумісне з U, але стає ЗА ВИНЯТКОВИМ запитом, що вже чекає. ⚠ Виміряно
/// (<c>sys.dm_tran_locks</c>): звичайний <c>UPDATE</c> без оптимізованого
/// блокування чекає в режимі <c>U</c>, і S від перевірки ключа проходить повз
/// нього — циклу немає. Цикл виникає, коли T3 чекає в режимі <c>X</c>: так
/// поводиться <c>UPDATE</c> під оптимізованим блокуванням (LAQ, SQL Server 2025 /
/// Azure SQL — рядок кваліфікується без U і X просить одразу). Тут X-очікувач
/// змодельований явно (<c>XLOCK</c>), щоб сценарій не залежав від конфігурації бази.
///
/// ⚠ Контекст БЕЗ стратегії повторів (<c>UseSqlServer</c> без
/// <c>EnableRetryOnFailure</c>): повтор сховав би 1205 за другою, вдалою спробою.
///
/// ⛔ Мутація, на якій файл зобов'язаний почервоніти: повернути в
/// <c>PatchDocumentHeaderHandler.PersistAsync</c> чи в
/// <c>ChangeDocumentKeyHandler.HandleAsync</c> порядок «документ, потім стани».
/// </remarks>
[Collection("SqlServer")]
public sealed class DocumentLockOrderDeadlockTests(SqlServerFixture sql)
{
    private const int UserId = 1;

    /// <summary>Номер помилки SQL Server «chosen as the deadlock victim».</summary>
    private const int DeadlockVictim = 1205;

    /// <summary>Стеля будь-якого очікування — щоб зламаний тест падав, а не висів.</summary>
    private static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(60);

    /// <summary>Хто бере стани аркушів раніше за документ.</summary>
    public enum Contender
    {
        /// <summary>Подання аркуша + X-очікувач на документі.</summary>
        SubmitWithTouch,

        /// <summary>Порядок видалення документа: стани, потім документ.</summary>
        StatesThenDocument,
    }

    [Theory]
    [InlineData(Contender.SubmitWithTouch)]
    [InlineData(Contender.StatesThenDocument)]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "C2")]
    public async Task Правка_шапки_не_дає_дедлоку_з_тим_хто_бере_стани_раніше_документа(Contender contender)
    {
        var doc = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync(ct: CancellationToken.None);
        var field = await ArrangeHeaderFieldAsync(doc);

        await using var db = CreateContext();
        var (get, patch) = BuildHeaderHandlers(db, doc, field);
        var baseVersion = (await get.HandleAsync(doc.DocumentId, CancellationToken.None)).Version;

        var outcome = await RunScenarioAsync(doc, contender, () => patch.HandleAsync(
            doc.DocumentId,
            new PatchDocumentHeaderRequest([new PatchHeaderField(field.Code, "Kashagan")], baseVersion),
            CancellationToken.None));

        AssertNoDeadlock(outcome);

        var saved = await ScalarAsync<string>(
            $"SELECT ValueString FROM doc.DocumentHeaderValue WHERE DocumentId = {doc.DocumentId} " +
            $"AND HeaderFieldDefId = {field.Id}");
        Assert.Equal("Kashagan", saved);
    }

    [Theory]
    [InlineData(Contender.SubmitWithTouch)]
    [InlineData(Contender.StatesThenDocument)]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-3.9")]
    public async Task Зміна_ключа_не_дає_дедлоку_з_тим_хто_бере_стани_раніше_документа(Contender contender)
    {
        var doc = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync(ct: CancellationToken.None);
        var oldKey = await ScalarAsync<string>($"SELECT BusinessKey FROM doc.Document WHERE Id = {doc.DocumentId}");
        var newKey = $"{oldKey}-RK";

        await using var db = CreateContext();
        var handler = BuildChangeKeyHandler(db, doc.ProjectId);

        var outcome = await RunScenarioAsync(doc, contender, () => handler.HandleAsync(
            doc.DocumentId, newKey, oldKey, "Перейменування під час подання", CancellationToken.None));

        AssertNoDeadlock(outcome);

        Assert.Equal(
            newKey,
            await ScalarAsync<string>($"SELECT BusinessKey FROM doc.Document WHERE Id = {doc.DocumentId}"));
    }

    /// <summary>Учасники в детермінованому порядку; повертає, чим скінчився T2 (<c>null</c> — штатно).</summary>
    private async Task<ScenarioOutcome> RunScenarioAsync(
        TestDocument doc, Contender contender, Func<Task> operationUnderTest)
    {
        // Рядок стану аркуша вже є (аркуш колись торкали) — подання його ОНОВЛЮЄ.
        await using (var seed = CreateContext())
        {
            seed.ApprovalStates.Add(new ApprovalState(doc.DocumentId, doc.SheetDefId, doc.PeriodKey.Value));
            await seed.SaveChangesAsync();
        }

        await using var monitor = await OpenAsync();

        // T1 — власний контекст і транзакція: стани бере СПРАВЖНІЙ
        // `DocumentDeletionStore` (той самий запит, той самий план, що в T2), а
        // не саморобний SELECT, який міг би взяти інший індекс і не перетнутися.
        await using var firstDb = CreateContext();
        await firstDb.Database.OpenConnectionAsync();
        await using var efTx = await firstDb.Database.BeginTransactionAsync();
        var first = (SqlConnection)firstDb.Database.GetDbConnection();
        var tx = (SqlTransaction)efTx.GetDbTransaction();
        var firstSpid = await SpidAsync(first, tx);

        await ExecAsync(first, tx, "SET DEADLOCK_PRIORITY HIGH;");
        if (contender == Contender.SubmitWithTouch)
        {
            await ExecAsync(first, tx,
                $"UPDATE wf.ApprovalState SET SubmittedAt = SYSUTCDATETIME() WHERE DocumentId = {doc.DocumentId} " +
                $"AND SheetDefId = {doc.SheetDefId} AND PeriodKey = {doc.PeriodKey.Value};");
        }
        else
        {
            _ = await new DocumentDeletionStore(firstDb).LockWorkflowFactsAsync(doc.DocumentId, CancellationToken.None);
        }

        // T2: обробник у фоні — доходить до блокувань і стає за T1.
        var operation = Task.Run(operationUnderTest);
        var operationSpid = await WaitForAsync(() => BlockedByAsync(monitor, firstSpid), () => operation.IsCompleted);

        Assert.False(
            operation.IsCompleted,
            $"Обробник завершився, не ставши за T1: {operation.Exception?.GetBaseException().Message}");

        var touchWaited = false;
        if (contender == Contender.SubmitWithTouch)
        {
            // T3: X-очікувач на рядку документа (так чекає UPDATE під LAQ).
            await using var touch = await OpenAsync();
            var touchSpid = await SpidAsync(touch);
            await ExecAsync(touch, null, "SET DEADLOCK_PRIORITY HIGH;");
            var touchTask = ExecAsync(touch, null,
                $"BEGIN TRAN; SELECT Id FROM doc.Document WITH (XLOCK, ROWLOCK) WHERE Id = {doc.DocumentId}; " +
                $"UPDATE doc.Document SET ModifiedAt = SYSUTCDATETIME() WHERE Id = {doc.DocumentId}; COMMIT;");
            touchWaited = await WaitForAsync(
                async () => await IsBlockedByAsync(monitor, touchSpid, operationSpid!.Value) ? touchSpid : null,
                () => touchTask.IsCompleted) is not null;

            // T1: журнал переходу — зовнішній ключ на документ просить S.
            await ExecAsync(first, tx,
                $"INSERT INTO wf.ApprovalEvent (DocumentId, SheetDefId, PeriodKey, FromStatus, ToStatus, Action, " +
                $"ByUserId, At, Reason, StepOrdinal) VALUES ({doc.DocumentId}, {doc.SheetDefId}, " +
                $"{doc.PeriodKey.Value}, 0, 0, 0, NULL, SYSUTCDATETIME(), NULL, NULL);");
            await efTx.CommitAsync();
            await touchTask.WaitAsync(Ceiling);
        }
        else
        {
            // T1: запис рядка документа після станів — як DELETE у DeleteDocumentHandler.
            await ExecAsync(first, tx,
                $"UPDATE doc.Document SET ModifiedAt = SYSUTCDATETIME() WHERE Id = {doc.DocumentId};");
            await efTx.CommitAsync();
        }

        Exception? error = null;
        try
        {
            await operation.WaitAsync(Ceiling);
        }
        catch (TimeoutException)
        {
            throw;
        }
        catch (Exception ex)
        {
            error = ex;
        }

        // ⚠ Оптимізоване блокування змінює, ЯКІ замки тримаються до коміту; у
        // повідомленні — щоб червоний прогін у CI читався без здогадок.
        var optimizedLocking = await ScalarAsync<object>(
            "SELECT CAST(DATABASEPROPERTYEX(DB_NAME(), 'IsOptimizedLockingOn') AS nvarchar(10))");

        return new ScenarioOutcome(error, touchWaited, optimizedLocking?.ToString() ?? "NULL");
    }

    private static void AssertNoDeadlock(ScenarioOutcome outcome)
    {
        var deadlock = Flatten(outcome.Error).OfType<SqlException>().FirstOrDefault(e => e.Number == DeadlockVictim);
        Assert.True(
            deadlock is null,
            $"Обробник обрано жертвою дедлоку (1205; X-очікувач стояв за обробником: " +
            $"{outcome.TouchWaitedOnOperation}; IsOptimizedLockingOn = {outcome.OptimizedLocking}): " +
            deadlock?.Message);
        Assert.Null(outcome.Error);
    }

    private static IEnumerable<Exception> Flatten(Exception? error)
    {
        for (var e = error; e is not null; e = e.InnerException)
        {
            yield return e;
        }
    }

    private static (GetDocumentHeaderHandler Get, PatchDocumentHeaderHandler Patch) BuildHeaderHandlers(
        EcrDbContext db, TestDocument doc, HeaderFieldDef field)
    {
        var metadata = Substitute.For<IMetadataCache>();
        metadata.GetAsync(doc.TemplateVersionId, Arg.Any<CancellationToken>()).Returns(new TemplateVersionSnapshot(
            doc.TemplateVersionId, 0, [], new Dictionary<int, ColumnDef>(), new Dictionary<(int, string), RowDef>())
        {
            HeaderFields = [field],
        });

        var access = Access(doc.ProjectId);

        // Проєкт без періодів: перерахунку після коміту ставити нікуди, а стан
        // «не закритий рік» — саме те, що потрібно для дозволу правки.
        var project = new Project(
            EcrCode.Create($"PRJ{doc.ProjectId}"), new LocalizedText(new Dictionary<string, string> { ["en"] = "P" }),
            new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31),
            doc.TemplateVersionId, PeriodKind.Monthly, 1, "Asia/Atyrau");
        typeof(Entity<int>).GetProperty("Id")!.SetValue(project, doc.ProjectId);
        var periods = Substitute.For<IPeriodStore>();
        periods.FindProjectAsync(doc.ProjectId, Arg.Any<CancellationToken>()).Returns(project);

        var documents = new DocumentStore(db);
        var headers = new DocumentHeaderStore(db);
        var user = User();

        return (
            new GetDocumentHeaderHandler(documents, metadata, headers, access, user),
            new PatchDocumentHeaderHandler(
                documents, metadata, headers, access, user, periods,
                new DocumentKeyStore(db), new DocumentDeletionStore(db), new UnitOfWork(db), new AuditWriter(db),
                Substitute.For<IBackgroundJobScheduler>(), new FixedClock()));
    }

    private static ChangeDocumentKeyHandler BuildChangeKeyHandler(EcrDbContext db, int projectId)
        => new(
            new DocumentKeyStore(db), new DocumentDeletionStore(db), Access(projectId),
            new UnitOfWork(db), new AuditWriter(db), User(), new FixedClock());

    private static IAccessDecisionService Access(int projectId)
    {
        var access = Substitute.For<IAccessDecisionService>();
        access.BuildProfileAsync(UserId, Arg.Any<CancellationToken>()).Returns(
            new AccessBuilder { UserId = UserId }
                .Permission("Document.View")
                .Permission(ChangeDocumentKeyHandler.Permission)
                .Grant(ResourceKind.Project, projectId, GrantLevel.Write)
                .Build());
        access.CanReadDocumentAsync(Arg.Any<AccessProfile>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(EditDecision.Allow());
        return access;
    }

    private async Task<HeaderFieldDef> ArrangeHeaderFieldAsync(TestDocument doc)
    {
        await using var db = CreateContext();
        var field = new HeaderFieldDef(
            doc.TemplateVersionId, EcrCode.Create($"Area{doc.TemplateVersionId}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Area" }), 0, CellDataType.String);
        db.HeaderFieldDefs.Add(field);
        await db.SaveChangesAsync();
        return field;
    }

    /// <summary>Чекає, доки <paramref name="probe"/> дасть сесію, або доки <paramref name="done"/>.</summary>
    private static async Task<short?> WaitForAsync(Func<Task<short?>> probe, Func<bool> done)
    {
        var deadline = DateTime.UtcNow + Ceiling;
        while (DateTime.UtcNow < deadline)
        {
            if (await probe() is { } spid)
            {
                return spid;
            }

            if (done())
            {
                return null;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("Учасник сценарію не дійшов до очікуваного блокування.");
    }

    private static async Task<short?> BlockedByAsync(SqlConnection monitor, short blocker)
    {
        await using var command = monitor.CreateCommand();
        command.CommandText =
            "SELECT TOP (1) session_id FROM sys.dm_os_waiting_tasks " +
            "WHERE blocking_session_id = @b AND session_id <> @b";
        command.Parameters.AddWithValue("@b", blocker);
        return await command.ExecuteScalarAsync() is short spid ? spid : null;
    }

    private static async Task<bool> IsBlockedByAsync(SqlConnection monitor, short waiter, short blocker)
    {
        await using var command = monitor.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM sys.dm_os_waiting_tasks WHERE session_id = @w AND blocking_session_id = @b";
        command.Parameters.AddWithValue("@w", waiter);
        command.Parameters.AddWithValue("@b", blocker);
        return (int)(await command.ExecuteScalarAsync())! > 0;
    }

    private static async Task<short> SpidAsync(SqlConnection connection, SqlTransaction? tx = null)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "SELECT @@SPID";
        return (short)(await command.ExecuteScalarAsync())!;
    }

    private static async Task ExecAsync(SqlConnection connection, SqlTransaction? tx, string text)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = text;
        command.CommandTimeout = (int)Ceiling.TotalSeconds;
        await command.ExecuteNonQueryAsync();
    }

    private async Task<SqlConnection> OpenAsync()
    {
        var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    private async Task<T> ScalarAsync<T>(string query)
    {
        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = query;
        return (T)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>⚠ Без <c>EnableRetryOnFailure</c> — повтор сховав би 1205.</summary>
    private EcrDbContext CreateContext()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

    private static ICurrentUser User()
    {
        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(UserId);
        user.CorrelationId.Returns("lock-order");
        return user;
    }

    private sealed record ScenarioOutcome(Exception? Error, bool TouchWaitedOnOperation, string OptimizedLocking);

    private sealed class FixedClock : IClock
    {
        public DateTime UtcNow { get; } = new(2026, 2, 1, 9, 0, 0, DateTimeKind.Utc);
    }
}
