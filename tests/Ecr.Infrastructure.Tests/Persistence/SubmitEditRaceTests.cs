using Ecr.Application.Common;
using Ecr.Application.Documents;
using Ecr.Application.Documents.Dto;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Reporting;
using Ecr.Application.Security;
using Ecr.Application.Workflow;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
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
/// Правка комірки, що перетинається в часі з поданням того самого аркуша
/// (<c>docs/build/UX-PASS-2026-09-23.md</c>, «правка під час подання»).
/// </summary>
/// <remarks>
/// ⛔ Інваріант, який тримає файл: <b>або правку відхилено, або вона є у
/// <c>calc.SubmissionSnapshot</c> цього подання</b>. Стан «правку прийнято й
/// зажурналізовано, але в зрізі її немає» — дефект: подана форма й жива комірка
/// розходяться, і ніхто про це не знає.
///
/// ⚠ Гонка відтворюється ДЕТЕРМІНОВАНО, двома точками перемикання — по одній на
/// кожен порядок, у якому вікно відкривається:
/// <list type="number">
/// <item>подання вже прочитало зріз, але ще не зафіксоване, — і в цей момент
/// приходить правка (точка — <c>IAccessDecisionService.CurrentApprovalStepAsync</c>,
/// яку подання кличе ВСЕРЕДИНІ своєї транзакції, після читання комірок);</item>
/// <item>правка вже пройшла перевірку прав (аркуш ще <c>Draft</c>), а запис
/// іще не почала, — і в цей момент подання проходить повністю (точка —
/// <c>IDocumentHeaderStore.GetExpressionValuesAsync</c>, яку правка кличе ПІСЛЯ
/// <c>EnsureAccessAsync</c> і ДО транзакції запису).</item>
/// </list>
///
/// ⚠ Перевірка прав правки змодельована так само, як її робить справжній
/// <c>AccessDecisionService.BuildContextAsync</c>: стан аркуша читається з
/// <c>wf.ApprovalState</c> тим самим контекстом, <c>AsNoTracking</c>, окремим
/// запитом, і <c>Submitted</c>/<c>Approved</c> дає <c>DocumentSubmitted</c>.
/// Решта рішення про доступ (гранти, період) — не предмет цього файлу.
///
/// ⚠ Подання й правка мають ВЛАСНІ <c>EcrDbContext</c> (отже, власні з'єднання
/// й транзакції) — інакше гонки не було б.
/// </remarks>
[Collection("SqlServer")]
public sealed class SubmitEditRaceTests(SqlServerFixture sql)
{
    private const int UserId = 1;

    /// <summary>Значення, яке правка пише в комірку; у базі до того її немає.</summary>
    private const decimal EditedValue = 918273.5m;

    /// <summary>Скільки чекати, поки правка сама дійде до кінця або стане в чергу за поданням.</summary>
    private static readonly TimeSpan OverlapWindow = TimeSpan.FromSeconds(2);

    /// <summary>Стеля на будь-яке очікування точки перемикання — щоб зламаний тест падав, а не висів.</summary>
    private static readonly TimeSpan HookTimeout = TimeSpan.FromSeconds(60);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-9.4")]
    public async Task Правка_поки_транзакція_подання_відкрита_або_відхилена_або_в_зрізі()
    {
        var doc = await new TestDocumentBuilder(sql.ConnectionString)
            .BuildAsync(rowMode: TableRowMode.Dynamic, ct: CancellationToken.None);
        var rowKey = await FirstRowKeyAsync(doc);
        var baseVersion = await RowVersionBase64Async(doc, rowKey);

        await using var submitDb = CreateContext();
        await using var patchDb = CreateContext();

        var submitInside = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSubmit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var submit = BuildSubmit(submitDb, doc, async () =>
        {
            submitInside.TrySetResult();
            await releaseSubmit.Task.WaitAsync(HookTimeout).ConfigureAwait(false);
        });
        var patch = BuildPatch(patchDb, doc, afterAccessCheck: null);

        var submitTask = Task.Run(() => submit.HandleAsync(
            doc.DocumentId, doc.SheetDefId, doc.PeriodKey.Value, CancellationToken.None));
        await submitInside.Task.WaitAsync(HookTimeout);

        // Подання стоїть усередині своєї транзакції: зріз прочитано, коміту ще
        // немає. Правка стартує саме тепер і має час або пройти до кінця, або
        // стати в чергу за поданням.
        var patchTask = Task.Run(() => patch.HandleAsync(
            Request(doc, rowKey, baseVersion), CancellationToken.None));
        await Task.WhenAny(patchTask, Task.Delay(OverlapWindow));

        releaseSubmit.TrySetResult();
        await submitTask.WaitAsync(HookTimeout);

        var patchError = await OutcomeAsync(patchTask);

        await AssertEditRejectedOrInSnapshotAsync(doc, patchError);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-9.4")]
    public async Task Правка_що_перевірила_права_до_подання_а_пише_після_або_відхилена_або_в_зрізі()
    {
        var doc = await new TestDocumentBuilder(sql.ConnectionString)
            .BuildAsync(rowMode: TableRowMode.Dynamic, ct: CancellationToken.None);
        var rowKey = await FirstRowKeyAsync(doc);
        var baseVersion = await RowVersionBase64Async(doc, rowKey);

        await using var submitDb = CreateContext();
        await using var patchDb = CreateContext();

        var patchChecked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePatch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var submit = BuildSubmit(submitDb, doc, insideTransaction: null);
        var patch = BuildPatch(patchDb, doc, async () =>
        {
            patchChecked.TrySetResult();
            await releasePatch.Task.WaitAsync(HookTimeout).ConfigureAwait(false);
        });

        var patchTask = Task.Run(() => patch.HandleAsync(
            Request(doc, rowKey, baseVersion), CancellationToken.None));
        await patchChecked.Task.WaitAsync(HookTimeout);

        // Правка побачила `Draft` і пройшла перевірку прав; подання тепер
        // проходить повністю, до коміту, і лише потім правка пише.
        await submit.HandleAsync(doc.DocumentId, doc.SheetDefId, doc.PeriodKey.Value, CancellationToken.None)
                    .WaitAsync(HookTimeout);

        releasePatch.TrySetResult();
        var patchError = await OutcomeAsync(patchTask);

        await AssertEditRejectedOrInSnapshotAsync(doc, patchError);
    }

    /// <summary>Сам інваріант — спільний для обох порядків.</summary>
    private async Task AssertEditRejectedOrInSnapshotAsync(TestDocument doc, Exception? patchError)
    {
        var column = doc.ColumnDefIds[1];
        var row = doc.RowIds[0];

        var payload = await ScalarAsync<string>(
            $"SELECT TOP (1) PayloadJson FROM calc.SubmissionSnapshot WHERE DocumentId = {doc.DocumentId} " +
            $"AND SheetDefId = {doc.SheetDefId} AND PeriodKey = {doc.PeriodKey.Value} ORDER BY Id DESC");
        var inSnapshot = SubmissionPayload.Read(payload)
            .Any(c => c.Row == row && c.Column == column
                      && c.Value is { } v
                      && decimal.Parse(v, System.Globalization.CultureInfo.InvariantCulture) == EditedValue);

        var live = await ScalarOrNullAsync<decimal?>(
            $"SELECT ValueNumeric FROM doc.CellValue WHERE PeriodKey = {doc.PeriodKey.Value} " +
            $"AND TableRowId = {row} AND ColumnDefId = {column}");
        var audited = await ScalarAsync<int>(
            $"SELECT COUNT(*) FROM aud.CellChange WHERE DocumentId = {doc.DocumentId} " +
            $"AND TableRowId = {row} AND ColumnDefId = {column}");

        if (patchError is null)
        {
            // ⛔ Правку прийнято — отже, вона ЗОБОВ'ЯЗАНА бути в зрізі. Інакше
            // подана форма каже одне, а жива комірка й журнал — інше.
            Assert.True(
                inSnapshot,
                $"Правку прийнято (жива комірка = {live}, записів журналу = {audited}), " +
                "але в зрізі подання її немає: подана форма й дані розійшлися.");
            return;
        }

        var denied = Assert.IsType<AccessDeniedException>(patchError);
        Assert.Equal("ECR-ACCS-0403", denied.ErrorCode);
        Assert.Equal(nameof(EditDenyReason.DocumentSubmitted), denied.Details?["reason"]?.ToString());

        // Відмова — повна: ні значення, ні рядка журналу.
        Assert.Null(live);
        Assert.Equal(0, audited);
    }

    private static async Task<Exception?> OutcomeAsync(Task patchTask)
    {
        try
        {
            await patchTask.WaitAsync(HookTimeout).ConfigureAwait(false);
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

    private static PatchCellsRequest Request(TestDocument doc, string rowKey, string baseVersion)
        => new(doc.TableInstanceId, doc.PeriodKey.Value, "UserEdit",
            [new PatchRow(rowKey, baseVersion, [new PatchCell(CodeOf(doc, 2), EditedValue)])]);

    /// <summary>Подання на реальних сховищах; <paramref name="insideTransaction"/> — точка перемикання.</summary>
    private static SubmitSheetHandler BuildSubmit(EcrDbContext db, TestDocument doc, Func<Task>? insideTransaction)
    {
        var bulk = new BulkCellLoader(db.Database.GetConnectionString()!, 1000);
        var clock = new FixedClock(new DateTime(2026, 2, 1, 9, 0, 0, DateTimeKind.Utc));

        var documents = Substitute.For<IDocumentStore>();
        documents.HasSheetAsync(doc.DocumentId, doc.SheetDefId, Arg.Any<CancellationToken>()).Returns(true);
        documents.FindProjectIdAsync(doc.DocumentId, Arg.Any<CancellationToken>()).Returns(doc.ProjectId);

        var access = Substitute.For<IAccessDecisionService>();
        access.BuildProfileAsync(UserId, Arg.Any<CancellationToken>()).Returns(Profile());
        access.CanReadDocumentAsync(Arg.Any<AccessProfile>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(EditDecision.Allow());
        access.CanSubmitAsync(
                  Arg.Any<AccessProfile>(), doc.DocumentId, doc.SheetDefId, Arg.Any<PeriodKey>(),
                  Arg.Any<CancellationToken>())
              .Returns(EditDecision.Allow());

        // ⚠ Точка перемикання: подання кличе це ВСЕРЕДИНІ транзакції, ПІСЛЯ
        // того, як прочитало комірки в зріз, і ДО коміту.
        access.CurrentApprovalStepAsync(doc.DocumentId, doc.SheetDefId, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
              .Returns(async _ =>
              {
                  if (insideTransaction is not null)
                  {
                      await insideTransaction().ConfigureAwait(false);
                  }

                  return (ApprovalStepView?)null;
              });

        var snapshots = Substitute.For<IReportSnapshotBuilder>();
        snapshots.ListAsync(
                     Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<IReadOnlyCollection<int>?>(),
                     Arg.Any<CancellationToken>())
                 .Returns<IReadOnlyList<ReportSnapshotSummary>>([]);

        var headers = Substitute.For<IDocumentHeaderStore>();
        headers.GetExpressionValuesAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
               .Returns(new Dictionary<string, ExpressionValue>());
        headers.GetValuesAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
               .Returns(new Dictionary<int, DocumentHeaderValueData>());

        return new SubmitSheetHandler(
            new NormalizedCellStore(db), new RowStore(db, bulk, clock), new WorkflowStore(db), documents,
            Metadata(doc), access,
            new Ecr.Application.Validation.ValidationEngine(new RealFormulaEngine()),
            headers,
            new ReportSnapshotSync(snapshots, documents),
            new UnitOfWork(db), User(), clock, new SheetEditGate(db), NSubstitute.Substitute.For<Ecr.Application.Recalculation.ISubmitRecalculation>());
    }

    /// <summary>Правка на реальних сховищах; <paramref name="afterAccessCheck"/> — точка перемикання.</summary>
    private static PatchCellsHandler BuildPatch(EcrDbContext db, TestDocument doc, Func<Task>? afterAccessCheck)
    {
        var bulk = new BulkCellLoader(db.Database.GetConnectionString()!, 1000);
        var clock = new FixedClock(new DateTime(2026, 2, 1, 9, 0, 1, DateTimeKind.Utc));

        var periods = Substitute.For<IPeriodStore>();
        periods.FindPeriodStateAsync(doc.DocumentId, doc.PeriodKey.Value, Arg.Any<CancellationToken>())
               .Returns((PeriodState?)PeriodState.Open);

        var access = Substitute.For<IAccessDecisionService>();
        access.BuildProfileAsync(UserId, Arg.Any<CancellationToken>()).Returns(Profile());
        access.CanReadDocumentAsync(Arg.Any<AccessProfile>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(EditDecision.Allow());

        // ⚠ Стан аркуша — так, як його читає `AccessDecisionService.BuildContextAsync`:
        // тим самим контекстом, `AsNoTracking`, окремим запитом поза транзакцією запису.
        access.CanEditSliceAsync(Arg.Any<AccessProfile>(), doc.TableInstanceId, Arg.Any<CancellationToken>())
              .Returns(async _ =>
              {
                  var status = await db.ApprovalStates
                      .AsNoTracking()
                      .Where(a => a.DocumentId == doc.DocumentId
                                  && a.SheetDefId == doc.SheetDefId
                                  && a.PeriodKey == doc.PeriodKey.Value)
                      .Select(a => (DocumentStatus?)a.Status)
                      .FirstOrDefaultAsync()
                      .ConfigureAwait(false);

                  var decision = status is DocumentStatus.Submitted or DocumentStatus.Approved
                      ? EditDecision.Deny(EditDenyReason.DocumentSubmitted)
                      : EditDecision.Allow();

                  return (IReadOnlyDictionary<CellAddress, EditDecision>)doc.RowIds
                      .SelectMany(rowId => doc.ColumnDefIds
                          .Select(columnId => new CellAddress(doc.PeriodKey, rowId, columnId)))
                      .ToDictionary(address => address, _ => decision);
              });
        access.CanCreateRowsAsync(
                  Arg.Any<AccessProfile>(), doc.TableInstanceId,
                  Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
              .Returns(new Dictionary<string, NewRowAccess>());

        var methodologies = Substitute.For<IMethodologyStore>();
        methodologies.GetMethodologyIdsBoundToTableAsync(doc.TableDefId, Arg.Any<CancellationToken>())
                     .Returns(Task.FromResult<IReadOnlyList<int>>([]));

        var registries = Substitute.For<IRegistryStore>();
        registries.FindExistingEntryIdsAsync(Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
                  .Returns(call => call.ArgAt<IReadOnlyCollection<long>>(0).ToHashSet());

        // ⚠ Точка перемикання: обробник кличе це ПІСЛЯ `EnsureAccessAsync` і ДО
        // транзакції запису.
        var headers = Substitute.For<IDocumentHeaderStore>();
        headers.GetExpressionValuesAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
               .Returns(async _ =>
               {
                   if (afterAccessCheck is not null)
                   {
                       await afterAccessCheck().ConfigureAwait(false);
                   }

                   return (IReadOnlyDictionary<string, ExpressionValue>)new Dictionary<string, ExpressionValue>();
               });

        return new PatchCellsHandler(
            new NormalizedCellStore(db), new RowStore(db, bulk, clock), new DocumentStore(db), periods,
            Metadata(doc), access,
            new Ecr.Application.Validation.ValidationEngine(new RealFormulaEngine()),
            methodologies, registries, headers, new AuditWriter(db), new AuditReader(db),
            Substitute.For<IBackgroundJobScheduler>(), new UnitOfWork(db), User(), clock, new SheetEditGate(db));
    }

    /// <summary>
    /// Знімок структури з РЕАЛЬНИМИ ідентифікаторами аркуша й таблиці: і подання,
    /// і правка мають говорити про той самий аркуш.
    /// </summary>
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

    private static AccessProfile Profile() => new()
    {
        CacheKey = "p", UserId = UserId, SecurityStamp = "s",
        Permissions = new HashSet<string>(), Grants = new Dictionary<string, GrantLevel>(),
        Denies = new HashSet<string>(), RoleIds = new HashSet<int>(),
    };

    private static ICurrentUser User()
    {
        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(UserId);
        return user;
    }

    private static ColumnDef ColumnDefFor(TestDocument doc, int ordinal, CellDataType type)
    {
        var id = doc.ColumnDefIds[ordinal - 1];
        var column = new ColumnDef(doc.TableDefId, EcrCode.Create($"C{ordinal}_{id}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = $"Col{ordinal}" }), ordinal, type);
        typeof(Entity<int>).GetProperty("Id")!.SetValue(column, id);
        return column;
    }

    private static string CodeOf(TestDocument doc, int ordinal) => $"C{ordinal}_{doc.ColumnDefIds[ordinal - 1]}";

    private async Task<string> FirstRowKeyAsync(TestDocument doc)
        => await ScalarAsync<string>(
            $"SELECT RowKey FROM doc.TableRow WHERE PeriodKey = {doc.PeriodKey.Value} AND Id = {doc.RowIds[0]}");

    private async Task<string> RowVersionBase64Async(TestDocument doc, string rowKey)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT RowVersion FROM doc.TableRow WHERE TableInstanceId = {doc.TableInstanceId} " +
            $"AND RowKey = N'{rowKey}'";
        var bytes = (byte[])(await command.ExecuteScalarAsync())!;
        return Convert.ToBase64String(bytes);
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

    private async Task<T?> ScalarOrNullAsync<T>(string query)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = query;
        var result = await command.ExecuteScalarAsync();
        return result is null or DBNull ? default : (T)result;
    }

    private sealed class FixedClock(DateTime utcNow) : IClock
    {
        public DateTime UtcNow { get; } = utcNow;
    }
}
