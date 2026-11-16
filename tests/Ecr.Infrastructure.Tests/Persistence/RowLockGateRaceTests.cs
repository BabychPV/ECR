using Ecr.Application.Common;
using Ecr.Application.Documents;
using Ecr.Application.Documents.Dto;
using Ecr.Application.Documents.RowLocks;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Expressions.Evaluation;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// ✎ B75 (F13 пункт 4): блокування рядка, створене МІЖ рішенням про запис і самим записом, — записувач (PATCH, книга,
/// створення й видалення рядка) перечитує блокування під замком аркуша й відмовляє <c>RowLocked</c>, нічого не
/// записано; а <c>POST …/row-locks</c> бере той самий замок аркуша ВИНЯТКОВО — поки записувач тримає спільний, блокування
/// чекає його коміту.
/// </summary>
/// <remarks>
/// ⛔ Детерміновано, два з'єднання: рішення про запис (заглушка служби доступу дозволяє) → у точці перемикання ІНШЕ
/// з'єднання комітить блокування рядка → записувач іде в транзакцію. Бар'єр — <see cref="TaskCompletionSource"/>, як у
/// <c>EditAssignmentGateRaceTests</c>/<c>PatchCellsRowLimitRaceTests</c>.
/// <para>
/// МУТАЦІЇ (локально): (1) прибрати <c>EnsureRowsNotLockedAsync</c> у <c>PersistCoreAsync</c> — червоніє «PATCH…»;
/// (2) прибрати перевірку в <c>PersistWorkbookAsync</c> — «Книга…»; (3) прибрати <c>EnsureKeyNotLockedAsync</c> —
/// «Створення…»; (4) прибрати перевірку в <c>DeleteRowHandler</c> — «Видалення…»; (5) прибрати
/// <c>EnterSubmitOfAsync</c> у <c>CreateRowLocksHandler</c> — «Блокування_чекає…»; (6) <c>RowLocksSeen</c> завжди
/// <c>false</c> — червоніють усі «між рішенням і записом».
/// </para>
/// </remarks>
[Collection("SqlServer")]
public sealed class RowLockGateRaceTests(SqlServerFixture sql)
{
    private const decimal EditedValue = 513.25m;
    private static readonly TimeSpan HookTimeout = TimeSpan.FromSeconds(60);

    private sealed record Scene(TestDocument Doc, int WriterId, int LockerId, string RowKey, string BaseVersion)
    {
        public string TableCode => $"TB{Doc.TableDefId}";

        public string SheetCode => $"SH{Doc.SheetDefId}";
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "B75")]
    public async Task Контроль_без_блокування_PATCH_пише()
    {
        var scene = await ArrangeAsync();

        Assert.Null(await RunPatchAsync(scene, lockBetween: false, workbook: false));
        Assert.Equal(EditedValue, await LiveValueAsync(scene));
    }

    [Theory]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "B75")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PATCH_і_книга_між_рішенням_і_записом_блокування_рядка_відхиляє_RowLocked(bool workbook)
    {
        var scene = await ArrangeAsync();

        var error = await RunPatchAsync(scene, lockBetween: true, workbook);

        var denied = Assert.IsType<AccessDeniedException>(error);
        Assert.Equal(nameof(EditDenyReason.RowLocked), denied.Details?["reason"]?.ToString());
        Assert.Null(await LiveValueAsync(scene));
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "B75-Q1")]
    public async Task Власне_блокування_між_рішенням_і_записом_не_заважає()
    {
        var scene = await ArrangeAsync();

        Assert.Null(await RunPatchAsync(scene, lockBetween: true, workbook: false, lockOwner: scene.WriterId));
        Assert.Equal(EditedValue, await LiveValueAsync(scene));
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "B75")]
    public async Task Створення_й_видалення_рядка_між_рішенням_і_записом_відхиляються_RowLocked()
    {
        var scene = await ArrangeAsync();
        const string newKey = "B75LOCKED";

        var created = await RunRowAsync(scene, newKey, delete: false, lockBetween: true);
        var addDenied = Assert.IsType<AccessDeniedException>(created);
        Assert.Equal(nameof(EditDenyReason.RowLocked), addDenied.Details?["reason"]?.ToString());
        Assert.Equal(0, await RowCountAsync(scene, newKey));

        // Видалення: рядок створено без блокування, блокування — між рішенням про видалення і записом.
        var fresh = await ArrangeAsync();
        Assert.Null(await RunRowAsync(fresh, newKey, delete: false, lockBetween: false));
        var deleted = await RunRowAsync(fresh, newKey, delete: true, lockBetween: true);
        var deleteDenied = Assert.IsType<AccessDeniedException>(deleted);
        Assert.Equal(nameof(EditDenyReason.RowLocked), deleteDenied.Details?["reason"]?.ToString());
        Assert.Equal(1, await RowCountAsync(fresh, newKey));
    }

    /// <remarks>✎ F13: власник блокування видаляє свій заблокований рядок — блокування знімається тією ж транзакцією.
    /// Мутація: прибрати зняття в <c>DeleteRowHandler</c> — червоніє.</remarks>
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "B75")]
    public async Task Видалення_рядка_власником_блокування_знімає_його_блокування()
    {
        var scene = await ArrangeAsync();
        const string key = "B75OWNDEL";
        Assert.Null(await RunRowAsync(scene, key, delete: false, lockBetween: false));
        await InsertLockAsync(scene, key, scene.WriterId);

        Assert.Null(await RunRowAsync(scene, key, delete: true, lockBetween: false));

        Assert.Equal(0, await RowCountAsync(scene, key));
        Assert.Equal(0, await ScalarAsync<int>(
            $"SELECT COUNT(*) FROM doc.RowLock WHERE DocumentId = {scene.Doc.DocumentId} AND RowKey = N'{key}' AND ReleasedAt IS NULL"));
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "B75")]
    public async Task Блокування_чекає_коміту_записувача_що_тримає_замок_аркуша()
    {
        var scene = await ArrangeAsync();

        // З'єднання 1: записувач у транзакції тримає спільні замки структури й аркуша × періоду.
        await using var writerDb = CreateContext();
        var writerUow = new UnitOfWork(writerDb);
        var holding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writer = Task.Run(() => writerUow.ExecuteInTransactionAsync(async ct =>
        {
            var gate = new SheetEditGate(writerDb);
            await gate.EnterStructureForPeriodAsync(scene.Doc.DocumentId, exclusive: false, scene.Doc.PeriodKey.Value, ct);
            await gate.EnterEditAsync(scene.Doc.DocumentId, scene.Doc.SheetDefId, scene.Doc.PeriodKey, ct);
            holding.TrySetResult();
            await release.Task.WaitAsync(HookTimeout, ct);
        }, CancellationToken.None));

        await holding.Task.WaitAsync(HookTimeout);

        // З'єднання 2: блокування рядка — мусить чекати на виняткове замка аркуша.
        await using var lockDb = CreateContext();
        var lockTask = Task.Run(() => LockHandler(lockDb, scene).HandleAsync(scene.Doc.DocumentId, LockRequest(scene), CancellationToken.None));

        var early = await Task.WhenAny(lockTask, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.NotSame(lockTask, early);
        Assert.Equal(0, await LockCountAsync(scene));

        release.TrySetResult();
        await writer.WaitAsync(HookTimeout);
        var created = await lockTask.WaitAsync(HookTimeout);

        Assert.Single(created);
        Assert.Equal(1, await LockCountAsync(scene));
    }

    // ───────────────────────────── допоміжне ─────────────────────────────

    private async Task<Exception?> RunPatchAsync(Scene scene, bool lockBetween, bool workbook, int? lockOwner = null)
    {
        await using var db = CreateContext();
        var doc = scene.Doc;
        var clock = new FixedClock(DateTime.UtcNow);
        var bulk = new BulkCellLoader(db.Database.GetConnectionString()!, 1000);
        var profile = Writer(scene);

        async Task<T> AfterDecision<T>(T decisions)
        {
            if (lockBetween)
            {
                await InsertLockAsync(scene, scene.RowKey, lockOwner ?? scene.LockerId);
            }

            return decisions;
        }

        var access = Substitute.For<IAccessDecisionService>();
        access.BuildProfileAsync(scene.WriterId, Arg.Any<CancellationToken>()).Returns(profile);
        access.CanReadDocumentAsync(Arg.Any<AccessProfile>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
              .Returns(EditDecision.Allow());
        access.CanEditCellsAsync(
                  Arg.Any<AccessProfile>(), Arg.Any<long>(), Arg.Any<PeriodKey>(),
                  Arg.Any<IReadOnlyCollection<CellAddress>>(), Arg.Any<CancellationToken>())
              .Returns(c => AfterDecision<IReadOnlyDictionary<CellAddress, EditDecision>>(
                  c.ArgAt<IReadOnlyCollection<CellAddress>>(3).ToDictionary(a => a, _ => EditDecision.Allow())));
        access.CanEditCellsBatchAsync(
                  Arg.Any<AccessProfile>(), Arg.Any<IReadOnlyCollection<CellsAccessRequest>>(), Arg.Any<CancellationToken>())
              .Returns(c => AfterDecision<IReadOnlyDictionary<long, IReadOnlyDictionary<CellAddress, EditDecision>>>(
                  c.ArgAt<IReadOnlyCollection<CellsAccessRequest>>(1).ToDictionary(
                      r => r.TableInstanceId,
                      r => (IReadOnlyDictionary<CellAddress, EditDecision>)r.Addresses.ToDictionary(a => a, _ => EditDecision.Allow()))));
        access.CanCreateRowsAsync(
                  Arg.Any<AccessProfile>(), Arg.Any<long>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
              .Returns(new Dictionary<string, NewRowAccess>());

        var periods = Substitute.For<IPeriodStore>();
        periods.FindPeriodStateAsync(doc.DocumentId, doc.PeriodKey.Value, Arg.Any<CancellationToken>())
               .Returns((PeriodState?)PeriodState.Open);

        var methodologies = Substitute.For<IMethodologyStore>();
        methodologies.GetMethodologyIdsBoundToTableAsync(doc.TableDefId, Arg.Any<CancellationToken>())
                     .Returns(Task.FromResult<IReadOnlyList<int>>([]));
        methodologies.GetMethodologyIdsBoundToTablesAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
                     .Returns(Task.FromResult<IReadOnlyList<int>>([]));

        var registries = Substitute.For<IRegistryStore>();
        registries.FindExistingEntryIdsAsync(Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
                  .Returns(call => call.ArgAt<IReadOnlyCollection<long>>(0).ToHashSet());

        var headers = Substitute.For<IDocumentHeaderStore>();
        headers.GetExpressionValuesAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
               .Returns(new Dictionary<string, ExpressionValue>());

        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(scene.WriterId);

        var handler = new PatchCellsHandler(
            new NormalizedCellStore(db), new RowStore(db, bulk, clock), new DocumentStore(db), periods,
            Metadata(doc), access,
            new Ecr.Application.Validation.ValidationEngine(new RealFormulaEngine()),
            methodologies, registries, headers, new AuditWriter(db), new AuditReader(db),
            Substitute.For<IBackgroundJobScheduler>(), new UnitOfWork(db), user, clock, new SheetEditGate(db),
            new UnitCatalog(db),
            assignments: new EditAssignmentStore(db),
            rowLocks: new RowLockStore(db));

        var request = new PatchCellsRequest(
            doc.TableInstanceId, doc.PeriodKey.Value, "UserEdit",
            [new PatchRow(scene.RowKey, scene.BaseVersion, [new PatchCell($"C2_{doc.ColumnDefIds[1]}", EditedValue)])]);

        try
        {
            if (workbook)
            {
                await handler.HandleWorkbookAsync(
                        [request], new List<RecalculationSeed>(), new List<RowWindowChange>(), CancellationToken.None)
                    .WaitAsync(HookTimeout);
            }
            else
            {
                await handler.HandleAsync(request, CancellationToken.None).WaitAsync(HookTimeout);
            }

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

    private async Task<Exception?> RunRowAsync(Scene scene, string rowKey, bool delete, bool lockBetween)
    {
        await using var db = CreateContext();
        var doc = scene.Doc;
        var clock = new FixedClock(DateTime.UtcNow);
        var bulk = new BulkCellLoader(db.Database.GetConnectionString()!, 1000);
        var profile = Writer(scene);

        var access = Substitute.For<IAccessDecisionService>();
        access.CanReadDocumentAsync(Arg.Any<AccessProfile>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
              .Returns(EditDecision.Allow());
        access.CanCreateRowsAsync(
                  Arg.Any<AccessProfile>(), Arg.Any<long>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
              .Returns(async c =>
              {
                  if (lockBetween)
                  {
                      await InsertLockAsync(scene, rowKey, scene.LockerId);
                  }

                  return (IReadOnlyDictionary<string, NewRowAccess>)c.ArgAt<IReadOnlyCollection<string>>(2).ToDictionary(
                      k => k,
                      _ => new NewRowAccess(EditDecision.Allow(), doc.ColumnDefIds.ToDictionary(id => id, _ => EditDecision.Allow())),
                      StringComparer.Ordinal);
              });

        try
        {
            if (delete)
            {
                await new DeleteRowHandler(
                        new RowStore(db, bulk, clock), new DocumentStore(db), Metadata(doc), access,
                        new NormalizedCellStore(db), new AuditWriter(db), Substitute.For<IBackgroundJobScheduler>(),
                        new UnitOfWork(db), clock, new SheetEditGate(db), new EditAssignmentStore(db),
                        rowLocks: new RowLockStore(db))
                    .HandleAsync(doc.DocumentId, doc.TableInstanceId, rowKey, profile, CancellationToken.None)
                    .WaitAsync(HookTimeout);
            }
            else
            {
                await new CreateRowHandler(
                        new RowStore(db, bulk, clock), new DocumentStore(db), Metadata(doc), access,
                        new UnitOfWork(db), clock, new SheetEditGate(db), new EditAssignmentStore(db),
                        rowLocks: new RowLockStore(db))
                    .HandleAsync(doc.DocumentId, doc.TableInstanceId, RowKey.Create(rowKey), profile, CancellationToken.None)
                    .WaitAsync(HookTimeout);
            }

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

    private CreateRowLocksHandler LockHandler(EcrDbContext db, Scene scene)
    {
        var profile = new AccessBuilder { UserId = scene.LockerId }
            .Grant(ResourceKind.Project, scene.Doc.ProjectId, GrantLevel.Read)
            .Permission(RowLockRules.Permission)
            .Build();

        var access = Substitute.For<IAccessDecisionService>();
        access.BuildProfileAsync(scene.LockerId, Arg.Any<CancellationToken>()).Returns(profile);
        access.CanReadDocumentAsync(Arg.Any<AccessProfile>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
              .Returns(EditDecision.Allow());

        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(scene.LockerId);

        return new CreateRowLocksHandler(
            new RowLockStore(db), new DocumentStore(db), Metadata(scene.Doc), access, new UnitOfWork(db),
            new AuditWriter(db), user, new FixedClock(DateTime.UtcNow), new SheetEditGate(db));
    }

    private static CreateRowLocksRequest LockRequest(Scene scene)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        return new CreateRowLocksRequest(
            scene.Doc.PeriodKey.Value, null, scene.SheetCode, scene.TableCode, [scene.RowKey], today, today.AddDays(3),
            "race");
    }

    private static AccessProfile Writer(Scene scene)
        => new AccessBuilder { UserId = scene.WriterId }
            .Grant(ResourceKind.Project, scene.Doc.ProjectId, GrantLevel.Write)
            .Build();

    private async Task<Scene> ArrangeAsync()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(rowMode: TableRowMode.Mixed, ct: CancellationToken.None);

        int writer;
        int locker;
        await using (var db = CreateContext())
        {
            var period = await db.Periods.FirstAsync(
                p => p.ProjectId == doc.ProjectId && p.PeriodKeyValue == doc.PeriodKey.Value, CancellationToken.None);
            period.AdvanceTo(PeriodState.Open, DateTime.UtcNow);

            var writerUser = NewUser();
            var lockerUser = NewUser();
            db.Users.AddRange(writerUser, lockerUser);
            await db.SaveChangesAsync(CancellationToken.None);
            writer = writerUser.Id;
            locker = lockerUser.Id;
        }

        var rowKey = await ScalarAsync<string>(
            $"SELECT RowKey FROM doc.TableRow WHERE PeriodKey = {doc.PeriodKey.Value} AND Id = {doc.RowIds[0]}");
        var version = await VersionAsync(doc, rowKey);

        return new Scene(doc, writer, locker, rowKey, version);
    }

    /// <summary>Інше з'єднання комітить діюче блокування рядка (точка перемикання гонки).</summary>
    private async Task InsertLockAsync(Scene scene, string rowKey, int owner)
    {
        await using var other = CreateContext();
        var now = DateTime.UtcNow;
        other.RowLocks.Add(RowLock.Create(
            scene.Doc.DocumentId, scene.Doc.PeriodKey.Value, null, scene.SheetCode, scene.TableCode, rowKey,
            now.AddMinutes(-1), now.AddDays(1), owner, "race", now));
        await other.SaveChangesAsync(CancellationToken.None);
    }

    private static User NewUser()
    {
        var user = new User($"rlg_{Guid.NewGuid():N}"[..20], "B75 race user", AuthProvider.Local);
        user.SetPassword("not-a-real-hash");
        return user;
    }

    private async Task<decimal?> LiveValueAsync(Scene scene)
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

    private Task<int> RowCountAsync(Scene scene, string rowKey)
        => ScalarAsync<int>(
            $"SELECT COUNT(*) FROM doc.TableRow WHERE PeriodKey = {scene.Doc.PeriodKey.Value} "
            + $"AND TableInstanceId = {scene.Doc.TableInstanceId} AND RowKey = N'{rowKey}' AND IsDeleted = 0");

    private Task<int> LockCountAsync(Scene scene)
        => ScalarAsync<int>($"SELECT COUNT(*) FROM doc.RowLock WHERE DocumentId = {scene.Doc.DocumentId}");

    private async Task<string> VersionAsync(TestDocument doc, string rowKey)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT RowVersion FROM doc.TableRow WHERE TableInstanceId = {doc.TableInstanceId} AND RowKey = N'{rowKey}'";
        return Convert.ToBase64String((byte[])(await command.ExecuteScalarAsync())!);
    }

    private static IMetadataCache Metadata(TestDocument doc)
    {
        var column1 = ColumnDefFor(doc, 1, CellDataType.String);
        var column2 = ColumnDefFor(doc, 2, CellDataType.Decimal);

        var sheet = new SheetDef(doc.TemplateVersionId, EcrCode.Create($"SH{doc.SheetDefId}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Sheet" }), 1);
        typeof(Entity<int>).GetProperty("Id")!.SetValue(sheet, doc.SheetDefId);
        var table = new TableDef(doc.SheetDefId, EcrCode.Create($"TB{doc.TableDefId}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Table" }), 1,
            TableLayoutKind.PerPeriodInstance, TableRowMode.Dynamic);
        typeof(Entity<int>).GetProperty("Id")!.SetValue(table, doc.TableDefId);
        table.AddColumn(column1);
        table.AddColumn(column2);
        sheet.AddTable(table);

        var snapshot = new TemplateVersionSnapshot(
            TemplateVersionId: doc.TemplateVersionId, PresentationRevision: 0, Sheets: [sheet],
            ColumnsById: new Dictionary<int, ColumnDef>
            {
                [doc.ColumnDefIds[0]] = column1,
                [doc.ColumnDefIds[1]] = column2,
            },
            RowsByKey: new Dictionary<(int, string), RowDef>());

        var metadata = Substitute.For<IMetadataCache>();
        metadata.GetAsync(doc.TemplateVersionId, Arg.Any<CancellationToken>()).Returns(snapshot);
        return metadata;
    }

    private static ColumnDef ColumnDefFor(TestDocument doc, int ordinal, CellDataType type)
    {
        var id = doc.ColumnDefIds[ordinal - 1];
        var column = new ColumnDef(doc.TableDefId, EcrCode.Create($"C{ordinal}_{id}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = $"Col{ordinal}" }), ordinal, type);
        typeof(Entity<int>).GetProperty("Id")!.SetValue(column, id);
        return column;
    }

    private EcrDbContext CreateContext()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

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
