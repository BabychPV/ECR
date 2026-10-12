using Ecr.Application.Common;
using Ecr.Application.Documents;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
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

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// D3-A, зауваження аудиту: права на ЗНАЧЕННЯ колонок рядка (P1-2), машинне походження (P1-3) і передача зрізу
/// між рішенням і комітом (P1-4) — справжня <c>AccessDecisionService</c> на справжній базі.
/// </summary>
/// <remarks>
/// ⛔ Мутації (локально, не в кінчику): прибрати блок «CanEditCellsAsync» у <c>DeleteRowHandler</c> — червоніють
/// «Deny/Read на колонку»; прибрати <c>HasMachineOriginAsync</c> — червоніє «Integration…»; прибрати
/// <c>EnsureAssignmentsHoldAsync</c> — червоніють «між рішенням і комітом». Точка перемикання детермінована:
/// втручання виконується одразу ПІСЛЯ рішення служби доступу, без таймерів.
/// </remarks>
[Collection("SqlServer")]
public sealed class DeleteRowGuardsTests(SqlServerFixture sql) : IDisposable
{
    private static readonly DateTime Now = new(2026, 1, 20, 9, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan HookTimeout = TimeSpan.FromSeconds(60);

    private readonly MemoryCache _memory = new(new MemoryCacheOptions());

    public void Dispose() => _memory.Dispose();

    private sealed record Scene(TestDocument Doc, int ExecutorId, int DelegatorId, int StrangerId, long AssignmentId);

    public enum Interference
    {
        None,
        Fill,
        Validate,
        Expire,
        MakeExclusive,
    }

    // ── P1-2: значення колонок ──────────────────────────────────────────────

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D3-A")]
    public async Task З_Write_на_таблицю_без_заборон_рядок_видаляється()
    {
        var scene = await ArrangeAsync(withAssignment: false);
        var profile = new AccessBuilder { UserId = scene.StrangerId }
            .Grant(ResourceKind.Project, scene.Doc.ProjectId, GrantLevel.Write).Build();

        var error = await RunAsync(scene, profile, Interference.None);

        Assert.Null(error);
        Assert.Equal(1, await IsDeletedAsync(scene));
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D3-A")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Колонка_із_забороною_або_лише_Read_блокує_видалення_а_значення_на_місці(bool explicitDeny)
    {
        var scene = await ArrangeAsync(withAssignment: false);
        var column = scene.Doc.ColumnDefIds[1];
        var builder = new AccessBuilder { UserId = scene.StrangerId }
            .Grant(ResourceKind.Project, scene.Doc.ProjectId, GrantLevel.Write);
        var profile = explicitDeny
            ? builder.Deny(ResourceKind.Column, column).Build()
            : builder.Grant(ResourceKind.Column, column, GrantLevel.Read).Build();

        var error = await RunAsync(scene, profile, Interference.None);

        Assert.True(error is AccessDeniedException, error?.ToString());
        var denied = (AccessDeniedException)error!;
        Assert.Equal("ECR-ACCS-0403", denied.ErrorCode);
        Assert.Equal("err.ECR-ACCS-0403.deleteRowDenied", denied.Details!["messageKey"]);
        // S-4: reason фіксований — прихована колонка не відрізняється від відкритої.
        Assert.Equal(nameof(EditDenyReason.BusinessRule), denied.Details["reason"]?.ToString());

        // Нерозкриття: ні колонки, ні її значення у відповіді.
        Assert.DoesNotContain(column.ToString(System.Globalization.CultureInfo.InvariantCulture), denied.Message, StringComparison.Ordinal);
        Assert.Equal(0, await IsDeletedAsync(scene));
        Assert.Equal(777.25m, await ValueAsync(scene));
        Assert.Equal(0, await ScalarAsync<int>(
            "SELECT COUNT(*) FROM aud.StructureChange WHERE EntityType = N'doc.TableRow' "
            + $"AND OldJson LIKE N'%\"rowId\":{scene.Doc.RowIds[0]},%'"));
    }

    // ── P1-3: машинне походження ────────────────────────────────────────────

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D3-A")]
    [InlineData("Integration", null, true)]
    [InlineData("Integration", "Recalculation", true)]
    [InlineData("Integration", "UserEdit", false)]
    [InlineData("Import", null, false)]
    [InlineData("ImportOverwrite", null, false)]
    [InlineData("UserEdit", null, false)]    public async Task Рядок_з_останнім_значенням_від_машини_не_видаляється_а_після_правки_людини_видаляється(
        string first, string? second, bool blocked)
    {
        var scene = await ArrangeAsync(withAssignment: false);
        var profile = new AccessBuilder { UserId = scene.StrangerId }
            .Grant(ResourceKind.Project, scene.Doc.ProjectId, GrantLevel.Write).Build();

        await InsertCellChangeAsync(scene, first, Now.AddMinutes(-10));
        if (second is not null)
        {
            await InsertCellChangeAsync(scene, second, Now.AddMinutes(-5));
        }

        var error = await RunAsync(scene, profile, Interference.None);

        if (blocked)
        {
            var rule = Assert.IsType<BusinessRuleException>(error);
            Assert.Equal("ECR-ROW-0409", rule.ErrorCode);
            Assert.Equal("err.ECR-ROW-0409.sourceRowNotDeletable", rule.Details!["messageKey"]);
            Assert.Equal(0, await IsDeletedAsync(scene));
        }
        else
        {
            Assert.Null(error);
            Assert.Equal(1, await IsDeletedAsync(scene));
        }
    }

    // ── S-2: колонка «лише читання» з явним Deny ────────────────────────────

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D3-A S-2")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Колонка_лише_для_читання_з_явним_Deny_блокує_а_без_Deny_не_заважає(bool deny)
    {
        var scene = await ArrangeAsync(withAssignment: false);
        var column = scene.Doc.ColumnDefIds[1];
        await ExecuteAsync($"UPDATE cfg.ColumnDef SET IsReadOnly = 1 WHERE Id = {column}");

        var builder = new AccessBuilder { UserId = scene.StrangerId }
            .Grant(ResourceKind.Project, scene.Doc.ProjectId, GrantLevel.Write);
        var profile = (deny ? builder.Deny(ResourceKind.Column, column) : builder).Build();

        var error = await RunAsync(scene, profile, Interference.None);

        if (deny)
        {
            var denied = Assert.IsType<AccessDeniedException>(error);
            Assert.Equal("ECR-ACCS-0403", denied.ErrorCode);
            Assert.Equal(0, await IsDeletedAsync(scene));
            Assert.Equal(777.25m, await ValueAsync(scene));
        }
        else
        {
            Assert.Null(error);
            Assert.Equal(1, await IsDeletedAsync(scene));
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D3-A P1-B")]
    public async Task Архівний_журнал_теж_враховується_у_машинному_походженні()
    {
        var scene = await ArrangeAsync(withAssignment: false);
        var profile = new AccessBuilder { UserId = scene.StrangerId }
            .Grant(ResourceKind.Project, scene.Doc.ProjectId, GrantLevel.Write).Build();
        var doc = scene.Doc;
        var rowKey = await ScalarAsync<string>(
            $"SELECT RowKey FROM doc.TableRow WHERE PeriodKey = {doc.PeriodKey.Value} AND Id = {doc.RowIds[0]}");
        await ExecuteAsync(
            "INSERT INTO arc.AuditCellChange (ChangedAt, PeriodKey, DocumentId, TableRowId, RowKey, ColumnDefId, OldValue, "
            + "NewValue, ChangedByUserId, Origin, IsLateEdit, IsOutOfWindow) VALUES "
            + $"('2026-01-10 09:00:00', {doc.PeriodKey.Value}, {doc.DocumentId}, {doc.RowIds[0]}, N'{rowKey}', "
            + $"{doc.ColumnDefIds[1]}, NULL, N'777.25', {scene.StrangerId}, N'Integration', 0, 0);");

        var error = await RunAsync(scene, profile, Interference.None);

        var rule = Assert.IsType<BusinessRuleException>(error);
        Assert.Equal("err.ECR-ROW-0409.sourceRowNotDeletable", rule.Details!["messageKey"]);
        Assert.Equal(0, await IsDeletedAsync(scene));
    }
    // ── P1-4: передача зрізу ────────────────────────────────────────────────

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "EA-B")]
    [InlineData(Interference.Fill, EditDenyReason.AssignmentFilled)]
    [InlineData(Interference.Validate, EditDenyReason.AssignmentValidated)]
    [InlineData(Interference.Expire, EditDenyReason.AssignmentExpired)]
    public async Task Виконавець_між_рішенням_і_комітом_втрачає_передачу_рядок_не_видалено(
        Interference interference, EditDenyReason expected)
    {
        var scene = await ArrangeAsync(withAssignment: true);
        var profile = new AccessBuilder { UserId = scene.ExecutorId }
            .Grant(ResourceKind.Project, scene.Doc.ProjectId, GrantLevel.Write).Build();

        var error = await RunAsync(scene, profile, interference);

        AssertDenied(error, expected);
        Assert.Equal(0, await IsDeletedAsync(scene));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "EA-B")]
    public async Task Третя_особа_між_рішенням_і_комітом_стикається_з_ексклюзивною_передачею()
    {
        var scene = await ArrangeAsync(withAssignment: true, exclusive: false);
        var profile = new AccessBuilder { UserId = scene.StrangerId }
            .Grant(ResourceKind.Project, scene.Doc.ProjectId, GrantLevel.Write).Build();

        var error = await RunAsync(scene, profile, Interference.MakeExclusive);

        AssertDenied(error, EditDenyReason.LockedByOwner);
        Assert.Equal(0, await IsDeletedAsync(scene));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "EA-B")]
    public async Task Уже_діюча_ексклюзивна_передача_відхиляє_третю_особу()
    {
        var scene = await ArrangeAsync(withAssignment: true, exclusive: true);
        var profile = new AccessBuilder { UserId = scene.StrangerId }
            .Grant(ResourceKind.Project, scene.Doc.ProjectId, GrantLevel.Write).Build();

        var error = await RunAsync(scene, profile, Interference.None);

        AssertDenied(error, EditDenyReason.LockedByOwner);
        Assert.Equal(0, await IsDeletedAsync(scene));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "EA-B")]
    public async Task Без_сховища_передач_документ_із_передачами_не_видаляє_рядок()
    {
        var scene = await ArrangeAsync(withAssignment: true, exclusive: false);
        var profile = new AccessBuilder { UserId = scene.StrangerId }
            .Grant(ResourceKind.Project, scene.Doc.ProjectId, GrantLevel.Write).Build();

        var error = await RunAsync(scene, profile, Interference.None, withStore: false);

        Assert.NotNull(error);
        Assert.Equal(0, await IsDeletedAsync(scene));
    }

    // ── P2-3: конкурентний PATCH/DELETE (детермінований бар'єр: чужа транзакція тримає блокування аркуша) ──

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D3-A")]
    public async Task Видалення_поки_аркуш_зберігається_відмовляє_409_зайнято_і_рядок_цілий()
    {
        var scene = await ArrangeAsync(withAssignment: false);
        var profile = new AccessBuilder { UserId = scene.StrangerId }
            .Grant(ResourceKind.Project, scene.Doc.ProjectId, GrantLevel.Write).Build();

        // Правка (спільне блокування аркуша) тримається в окремій транзакції до кінця перевірки.
        await using var holder = CreateContext();
        await using var save = await holder.Database.BeginTransactionAsync();
        await new SheetEditGate(holder).EnterEditAsync(
            scene.Doc.DocumentId, scene.Doc.SheetDefId, scene.Doc.PeriodKey, CancellationToken.None);

        var error = await RunAsync(
            scene, profile, Interference.None, policy: new SheetEditGatePolicy(TimeSpan.FromMilliseconds(300)));
        await save.RollbackAsync();

        var busy = Assert.IsType<ConcurrencyConflictException>(error);
        Assert.Equal("ECR-DOC-4091", busy.ErrorCode);
        Assert.Equal("err.ECR-DOC-4091.lockTimeout", busy.Details!["messageKey"]);
        Assert.Equal(0, await IsDeletedAsync(scene));
        Assert.Equal(777.25m, await ValueAsync(scene));
    }
    private static void AssertDenied(Exception? error, EditDenyReason expected)
    {
        var denied = Assert.IsType<AccessDeniedException>(error);
        Assert.Equal("ECR-ACCS-0403", denied.ErrorCode);
        Assert.Equal("err.ECR-ACCS-0403.deleteRowDenied", denied.Details?["messageKey"]?.ToString());
        Assert.Equal(expected.ToString(), denied.Details?["reason"]?.ToString());
    }

    // ── Побудова ────────────────────────────────────────────────────────────

    private async Task<Exception?> RunAsync(
        Scene scene, AccessProfile profile, Interference interference, bool withStore = true,
        SheetEditGatePolicy? policy = null)
    {
        await using var db = CreateContext();
        await using var serviceDb = CreateContext();

        var real = new AccessDecisionService(
            serviceDb,
            new MetadataCache(_memory, serviceDb),
            new AccessProfileCache(_memory),
            new FixedClock(Now),
            Substitute.For<ICurrentUser>(),
            new WorkflowStore(serviceDb));

        var doc = scene.Doc;
        var clock = new FixedClock(Now.AddSeconds(1));

        var access = Substitute.For<IAccessDecisionService>();
        access.CanReadDocumentAsync(Arg.Any<AccessProfile>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
              .Returns(EditDecision.Allow());
        access.CanCreateRowsAsync(
                  Arg.Any<AccessProfile>(), Arg.Any<long>(), Arg.Any<IReadOnlyCollection<string>>(),
                  Arg.Any<CancellationToken>())
              .Returns(async c =>
              {
                  var decisions = await real.CanCreateRowsAsync(
                      c.ArgAt<AccessProfile>(0), c.ArgAt<long>(1), c.ArgAt<IReadOnlyCollection<string>>(2),
                      CancellationToken.None);
                  await InterfereAsync(scene, interference);
                  return decisions;
              });
        // Профіль із обмеженнями нижче проєкту (колонковий Deny/Read) проходить через RequireTableVisibleAsync.
        access.ReadScopeAsync(Arg.Any<AccessProfile>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
              .Returns(c => real.ReadScopeAsync(c.ArgAt<AccessProfile>(0), c.ArgAt<long>(1), CancellationToken.None));
        access.CanClearCellsAsync(
                  Arg.Any<AccessProfile>(), Arg.Any<long>(), Arg.Any<PeriodKey>(),
                  Arg.Any<IReadOnlyCollection<CellAddress>>(), Arg.Any<CancellationToken>())
              .Returns(c => real.CanClearCellsAsync(
                  c.ArgAt<AccessProfile>(0), c.ArgAt<long>(1), c.ArgAt<PeriodKey>(2),
                  c.ArgAt<IReadOnlyCollection<CellAddress>>(3), CancellationToken.None));

        var jobs = Substitute.For<IBackgroundJobScheduler>();
        jobs.EnlistsInCallerTransaction.Returns(false);
        jobs.EnqueueCoalescedAsync<IFormulaRecalculationJob>(
                Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<CancellationToken>(), Arg.Any<int?>())
            .Returns("job-1");

        var handler = new DeleteRowHandler(
            new RowStore(db, new BulkCellLoader(sql.ConnectionString, 1000), clock), new DocumentStore(db),
            Metadata(doc), access, new NormalizedCellStore(db), new AuditWriter(db), jobs,
            new UnitOfWork(db), clock, new SheetEditGate(db, policy),
            withStore ? new EditAssignmentStore(db) : null);

        var rowKey = await ScalarAsync<string>(
            $"SELECT RowKey FROM doc.TableRow WHERE PeriodKey = {doc.PeriodKey.Value} AND Id = {doc.RowIds[0]}");

        try
        {
            await handler.HandleAsync(doc.DocumentId, doc.TableInstanceId, rowKey, profile, CancellationToken.None)
                .WaitAsync(HookTimeout);

            return null;
        }
        catch (TimeoutException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private async Task InterfereAsync(Scene scene, Interference interference)
    {
        switch (interference)
        {
            case Interference.Fill:
                await MutateAsync(scene, a => a.FillRecipient(scene.ExecutorId, Now));
                break;
            case Interference.Validate:
                await MutateAsync(scene, a =>
                {
                    a.FillRecipient(scene.ExecutorId, Now);
                    a.Validate(scene.DelegatorId, Now);
                });
                break;
            case Interference.Expire:
                await ExecuteAsync($"UPDATE doc.EditAssignment SET ExpiresAt = '2026-01-01' WHERE Id = {scene.AssignmentId}");
                break;
            case Interference.MakeExclusive:
                await ExecuteAsync($"UPDATE doc.EditAssignment SET IsExclusive = 1 WHERE Id = {scene.AssignmentId}");
                break;
            default:
                break;
        }
    }

    private async Task<Scene> ArrangeAsync(bool withAssignment, bool exclusive = true)
    {
        // Mixed, а не Dynamic: шаблон із рядками в Dynamic-таблиці TableDef.AddRow відхиляє (знімок не збирається).
        var doc = await new TestDocumentBuilder(sql.ConnectionString)
            .BuildAsync(rowMode: TableRowMode.Mixed, ct: CancellationToken.None);

        int executor;
        int delegator;
        int stranger;
        long assignmentId = 0;
        await using (var db = CreateContext())
        {
            var period = await db.Periods.FirstAsync(
                p => p.ProjectId == doc.ProjectId && p.PeriodKeyValue == doc.PeriodKey.Value, CancellationToken.None);
            period.AdvanceTo(PeriodState.Open, Now);

            var delegatorUser = NewUser();
            var executorUser = NewUser();
            var strangerUser = NewUser();
            db.Users.AddRange(delegatorUser, executorUser, strangerUser);
            await db.SaveChangesAsync(CancellationToken.None);
            delegator = delegatorUser.Id;
            executor = executorUser.Id;
            stranger = strangerUser.Id;

            if (withAssignment)
            {
                var assignment = new EditAssignment(
                    doc.DocumentId,
                    EditAssignmentSlice.Create(null, null, null, doc.PeriodKey.Value),
                    isExclusive: exclusive, delegatedByUserId: delegator, handoffId: Guid.NewGuid(),
                    createdByUserId: delegator, utcNow: Now);
                assignment.AddRecipient(executor, delegator, Now);
                new EditAssignmentStore(db).Add(assignment);
                await db.SaveChangesAsync(CancellationToken.None);
                assignmentId = assignment.Id;
            }
            else
            {
                await db.SaveChangesAsync(CancellationToken.None);
            }
        }

        // Значення, яке видалення стерло б.
        await ExecuteAsync(
            "INSERT INTO doc.CellValue (PeriodKey, TableRowId, ColumnDefId, TableDefId, ValueNumeric, IsCalculated, IsEmpty) " +
            $"VALUES ({doc.PeriodKey.Value}, {doc.RowIds[0]}, {doc.ColumnDefIds[1]}, {doc.TableDefId}, 777.25, 0, 0);");

        return new Scene(doc, executor, delegator, stranger, assignmentId);
    }

    private async Task InsertCellChangeAsync(Scene scene, string origin, DateTime at)
    {
        var doc = scene.Doc;
        var rowKey = await ScalarAsync<string>(
            $"SELECT RowKey FROM doc.TableRow WHERE PeriodKey = {doc.PeriodKey.Value} AND Id = {doc.RowIds[0]}");
        await ExecuteAsync(
            "INSERT INTO aud.CellChange (ChangedAt, PeriodKey, DocumentId, TableRowId, RowKey, ColumnDefId, OldValue, NewValue, "
            + "ChangedByUserId, Origin, IsLateEdit, IsOutOfWindow) VALUES "
            + $"('{at:yyyy-MM-dd HH:mm:ss}', {doc.PeriodKey.Value}, {doc.DocumentId}, {doc.RowIds[0]}, N'{rowKey}', "
            + $"{doc.ColumnDefIds[1]}, NULL, N'777.25', {scene.StrangerId}, N'{origin}', 0, 0);");
    }

    private async Task MutateAsync(Scene scene, Action<EditAssignment> change)
    {
        await using var db = CreateContext();
        var store = new EditAssignmentStore(db);
        var assignment = await store.FindAsync(scene.Doc.DocumentId, scene.AssignmentId, CancellationToken.None);
        change(assignment!);
        await db.SaveChangesAsync(CancellationToken.None);
    }

    private Task<int> IsDeletedAsync(Scene scene)
        => ScalarAsync<int>(
            $"SELECT CAST(IsDeleted AS int) FROM doc.TableRow WHERE PeriodKey = {scene.Doc.PeriodKey.Value} "
            + $"AND Id = {scene.Doc.RowIds[0]}");

    private async Task<decimal?> ValueAsync(Scene scene)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT ValueNumeric FROM doc.CellValue WHERE PeriodKey = {scene.Doc.PeriodKey.Value} " +
            $"AND TableRowId = {scene.Doc.RowIds[0]} AND ColumnDefId = {scene.Doc.ColumnDefIds[1]}";
        var result = await command.ExecuteScalarAsync();

        return result is null or DBNull ? null : (decimal)result;
    }

    private static IMetadataCache Metadata(TestDocument doc)
    {
        var sheet = new SheetDef(doc.TemplateVersionId, EcrCode.Create($"SH{doc.SheetDefId}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Sheet" }), 1);
        typeof(Entity<int>).GetProperty("Id")!.SetValue(sheet, doc.SheetDefId);
        var table = new TableDef(doc.SheetDefId, EcrCode.Create($"TB{doc.TableDefId}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Table" }), 1,
            TableLayoutKind.PerPeriodInstance, TableRowMode.Dynamic);
        typeof(Entity<int>).GetProperty("Id")!.SetValue(table, doc.TableDefId);

        for (var ordinal = 1; ordinal <= 2; ordinal++)
        {
            var id = doc.ColumnDefIds[ordinal - 1];
            var column = new ColumnDef(doc.TableDefId, EcrCode.Create($"C{ordinal}_{id}"),
                new LocalizedText(new Dictionary<string, string> { ["en"] = $"Col{ordinal}" }), ordinal,
                ordinal == 1 ? CellDataType.String : CellDataType.Decimal);
            typeof(Entity<int>).GetProperty("Id")!.SetValue(column, id);
            table.AddColumn(column);
        }

        sheet.AddTable(table);

        var snapshot = new TemplateVersionSnapshot(
            TemplateVersionId: doc.TemplateVersionId, PresentationRevision: 0, Sheets: [sheet],
            ColumnsById: new Dictionary<int, ColumnDef>(),
            RowsByKey: new Dictionary<(int, string), RowDef>());

        var metadata = Substitute.For<IMetadataCache>();
        metadata.GetAsync(doc.TemplateVersionId, Arg.Any<CancellationToken>()).Returns(snapshot);

        return metadata;
    }

    private static User NewUser()
    {
        var user = new User($"del_{Guid.NewGuid():N}"[..20], "D3 delete-row guards test user", AuthProvider.Local);
        user.SetPassword("not-a-real-hash");

        return user;
    }

    private EcrDbContext CreateContext()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

    private async Task ExecuteAsync(string text)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = text;
        await command.ExecuteNonQueryAsync();
    }

    private async Task<T> ScalarAsync<T>(string query)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = query;

        return (T)(await command.ExecuteScalarAsync())!;
    }

    private sealed class FixedClock(DateTime utcNow) : IClock
    {
        public DateTime UtcNow { get; } = utcNow;
    }
}
