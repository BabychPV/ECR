using Ecr.Application.Common;
using Ecr.Application.Documents;
using Ecr.Application.Documents.Dto;
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
/// EA-B (P1-R3): рішення про права прийнято, а ПЕРЕД комітом передачу змінено — «Заповнено»,
/// зняття передачі, вимкнення делегатора. Правку мусить відхилити повторна перевірка під
/// блокуванням аркуша (<c>ECR-ACCS-0403</c>, <c>deniedCells</c>), нічого не записано; те саме
/// для книги (<c>HandleWorkbookAsync</c>).
/// </summary>
/// <remarks>
/// ⛔ Рішення — справжній <c>AccessDecisionService</c> (виконавець має лише Read, правку
/// піднімає передача), точка перемикання — одразу ПІСЛЯ рішення. Мутації (локально, не в
/// кінчику lane): прибрати <c>EnsureAssignmentsHoldAsync</c> після гейта — червоніє кожен тест
/// «між рішенням і комітом»; прибрати <c>LiftedColumns.Contains</c> — червоніють «зняття» і
/// «делегатор вимкнений»; прибрати гейт у <c>PersistWorkbookAsync</c> — червоніють «книга…».
/// Контроль «без змін» доводить, що без втручання правка проходить саме завдяки передачі.
/// </remarks>
[Collection("SqlServer")]
public sealed class EditAssignmentGateRaceTests(SqlServerFixture sql) : IDisposable
{
    private const decimal EditedValue = 918273.5m;
    private static readonly DateTime Now = new(2026, 1, 20, 9, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan HookTimeout = TimeSpan.FromSeconds(60);

    private readonly MemoryCache _memory = new(new MemoryCacheOptions());

    public void Dispose() => _memory.Dispose();

    private sealed record Scene(TestDocument Doc, int ExecutorId, int DelegatorId, long AssignmentId, string RowKey, string BaseVersion);

    public enum Interference
    {
        None,
        Fill,
        Remove,
        DeactivateDelegator,
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "EA-B")]
    public async Task Без_втручання_правку_виконавця_приймає_передача()
    {
        var scene = await ArrangeAsync();

        await RunPatchAsync(scene, Interference.None, workbook: false);

        Assert.Equal(EditedValue, await LiveValueAsync(scene));
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "EA-B")]
    [InlineData(Interference.Fill, EditDenyReason.AssignmentFilled)]
    [InlineData(Interference.Remove, EditDenyReason.InsufficientGrantLevel)]
    [InlineData(Interference.DeactivateDelegator, EditDenyReason.InsufficientGrantLevel)]
    public async Task Правка_між_рішенням_і_комітом_відхиляється_після_зміни_передачі(
        Interference interference, EditDenyReason expected)
    {
        var scene = await ArrangeAsync();

        var error = await RunPatchAsync(scene, interference, workbook: false);

        AssertDenied(error, expected);
        Assert.Null(await LiveValueAsync(scene));
        Assert.Equal(0, await AuditCountAsync(scene));
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "EA-B")]
    [InlineData(Interference.Fill, EditDenyReason.AssignmentFilled)]
    [InlineData(Interference.Remove, EditDenyReason.InsufficientGrantLevel)]
    public async Task Книга_між_рішенням_і_комітом_відхиляється_після_зміни_передачі(
        Interference interference, EditDenyReason expected)
    {
        var scene = await ArrangeAsync();

        var error = await RunPatchAsync(scene, interference, workbook: true);

        AssertDenied(error, expected);
        Assert.Null(await LiveValueAsync(scene));
        Assert.Equal(0, await AuditCountAsync(scene));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "EA-B")]
    public async Task Без_сховища_передач_правка_піднята_передачею_відхиляється_а_не_проходить()
    {
        // fail-closed: gate неможливо виконати → не пишемо (у проді сховище вприснуте завжди).
        var scene = await ArrangeAsync();

        var error = await RunPatchAsync(scene, Interference.None, workbook: false, withStore: false);

        Assert.NotNull(error);
        Assert.Null(await LiveValueAsync(scene));
    }

    private static void AssertDenied(Exception? error, EditDenyReason expected)
    {
        var denied = Assert.IsType<AccessDeniedException>(error);
        Assert.Equal("ECR-ACCS-0403", denied.ErrorCode);
        Assert.Equal(expected.ToString(), denied.Details?["reason"]?.ToString());
    }

    private async Task<Exception?> RunPatchAsync(Scene scene, Interference interference, bool workbook, bool withStore = true)
    {
        await using var patchDb = CreateContext();
        await using var serviceDb = CreateContext();

        var real = new AccessDecisionService(
            serviceDb,
            new MetadataCache(_memory, serviceDb),
            new AccessProfileCache(_memory),
            new FixedClock(Now),
            Substitute.For<ICurrentUser>(),
            new WorkflowStore(serviceDb));

        var handler = BuildHandler(patchDb, scene, real, interference, withStore);
        var request = new PatchCellsRequest(
            scene.Doc.TableInstanceId, scene.Doc.PeriodKey.Value, "UserEdit",
            [new PatchRow(scene.RowKey, scene.BaseVersion, [new PatchCell($"C2_{scene.Doc.ColumnDefIds[1]}", EditedValue)])]);

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

    private PatchCellsHandler BuildHandler(
        EcrDbContext db, Scene scene, AccessDecisionService real, Interference interference, bool withStore)
    {
        var doc = scene.Doc;
        var bulk = new BulkCellLoader(db.Database.GetConnectionString()!, 1000);
        var clock = new FixedClock(Now.AddSeconds(1));
        var profile = new AccessBuilder { UserId = scene.ExecutorId }
            .Grant(ResourceKind.Project, doc.ProjectId, GrantLevel.Read)
            .Build();

        var periods = Substitute.For<IPeriodStore>();
        periods.FindPeriodStateAsync(doc.DocumentId, doc.PeriodKey.Value, Arg.Any<CancellationToken>())
               .Returns((PeriodState?)PeriodState.Open);

        async Task Interfere()
        {
            switch (interference)
            {
                case Interference.Fill:
                    await MutateAsync(scene, a => a.FillRecipient(scene.ExecutorId, Now));
                    break;
                case Interference.Remove:
                    await using (var other = CreateContext())
                    {
                        var store = new EditAssignmentStore(other);
                        var found = await store.FindAsync(doc.DocumentId, scene.AssignmentId, CancellationToken.None);
                        store.Remove(found!);
                        await other.SaveChangesAsync(CancellationToken.None);
                    }

                    break;
                case Interference.DeactivateDelegator:
                    await ExecuteAsync("UPDATE sec.[User] SET IsActive = 0 WHERE Id = " + scene.DelegatorId);
                    break;
                default:
                    break;
            }
        }

        var access = Substitute.For<IAccessDecisionService>();
        access.BuildProfileAsync(scene.ExecutorId, Arg.Any<CancellationToken>()).Returns(profile);
        access.CanReadDocumentAsync(Arg.Any<AccessProfile>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
              .Returns(EditDecision.Allow());
        access.CanEditCellsAsync(
                  Arg.Any<AccessProfile>(), Arg.Any<long>(), Arg.Any<PeriodKey>(),
                  Arg.Any<IReadOnlyCollection<CellAddress>>(), Arg.Any<CancellationToken>())
              .Returns(async c =>
              {
                  var decisions = await real.CanEditCellsAsync(
                      c.ArgAt<AccessProfile>(0), c.ArgAt<long>(1), c.ArgAt<PeriodKey>(2),
                      c.ArgAt<IReadOnlyCollection<CellAddress>>(3), CancellationToken.None);
                  await Interfere();
                  return decisions;
              });
        access.CanEditCellsBatchAsync(
                  Arg.Any<AccessProfile>(), Arg.Any<IReadOnlyCollection<CellsAccessRequest>>(), Arg.Any<CancellationToken>())
              .Returns(async c =>
              {
                  var decisions = await real.CanEditCellsBatchAsync(
                      c.ArgAt<AccessProfile>(0), c.ArgAt<IReadOnlyCollection<CellsAccessRequest>>(1), CancellationToken.None);
                  await Interfere();
                  return decisions;
              });
        access.CanCreateRowsAsync(
                  Arg.Any<AccessProfile>(), Arg.Any<long>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
              .Returns(new Dictionary<string, NewRowAccess>());

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
        user.UserId.Returns(scene.ExecutorId);

        return new PatchCellsHandler(
            new NormalizedCellStore(db), new RowStore(db, bulk, clock), new DocumentStore(db), periods,
            Metadata(doc), access,
            new Ecr.Application.Validation.ValidationEngine(new RealFormulaEngine()),
            methodologies, registries, headers, new AuditWriter(db), new AuditReader(db),
            Substitute.For<IBackgroundJobScheduler>(), new UnitOfWork(db), user, clock, new SheetEditGate(db),
            new UnitCatalog(db),
            assignments: withStore ? new EditAssignmentStore(db) : null);
    }

    private async Task<Scene> ArrangeAsync()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(rowMode: TableRowMode.Dynamic, ct: CancellationToken.None);

        int executor;
        int delegator;
        long assignmentId;
        await using (var db = CreateContext())
        {
            var period = await db.Periods.FirstAsync(
                p => p.ProjectId == doc.ProjectId && p.PeriodKeyValue == doc.PeriodKey.Value, CancellationToken.None);
            period.AdvanceTo(PeriodState.Open, Now);

            var delegatorUser = NewUser();
            var executorUser = NewUser();
            db.Users.AddRange(delegatorUser, executorUser);
            await db.SaveChangesAsync(CancellationToken.None);
            delegator = delegatorUser.Id;
            executor = executorUser.Id;

            // Зріз — період документа без місяця: піднімає будь-яку колонку періоду (метадані
            // поштучного хендлера — тестові, без місячних колонок).
            var assignment = new EditAssignment(
                doc.DocumentId,
                EditAssignmentSlice.Create(null, null, null, doc.PeriodKey.Value),
                isExclusive: true, delegatedByUserId: delegator, handoffId: Guid.NewGuid(),
                createdByUserId: delegator, utcNow: Now);
            assignment.AddRecipient(executor, delegator, Now);
            new EditAssignmentStore(db).Add(assignment);
            await db.SaveChangesAsync(CancellationToken.None);
            assignmentId = assignment.Id;
        }

        var rowKey = await ScalarAsync<string>(
            $"SELECT RowKey FROM doc.TableRow WHERE PeriodKey = {doc.PeriodKey.Value} AND Id = {doc.RowIds[0]}");
        var version = await VersionAsync(doc, rowKey);

        return new Scene(doc, executor, delegator, assignmentId, rowKey, version);
    }

    private async Task MutateAsync(Scene scene, Action<EditAssignment> change)
    {
        await using var db = CreateContext();
        var store = new EditAssignmentStore(db);
        var assignment = await store.FindAsync(scene.Doc.DocumentId, scene.AssignmentId, CancellationToken.None);
        change(assignment!);
        await db.SaveChangesAsync(CancellationToken.None);
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

    private async Task<int> AuditCountAsync(Scene scene)
        => await ScalarAsync<int>(
            $"SELECT COUNT(*) FROM aud.CellChange WHERE DocumentId = {scene.Doc.DocumentId} " +
            $"AND TableRowId = {scene.Doc.RowIds[0]} AND ColumnDefId = {scene.Doc.ColumnDefIds[1]}");

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

    private static User NewUser()
    {
        var user = new User($"gate_{Guid.NewGuid():N}"[..20], "EA-B gate test user", AuthProvider.Local);
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
