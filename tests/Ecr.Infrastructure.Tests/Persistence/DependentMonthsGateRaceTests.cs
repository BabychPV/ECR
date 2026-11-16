using Ecr.Application.Common;
using Ecr.Application.Documents;
using Ecr.Application.Documents.Dto;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Entities.Workflow;
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
/// ✎ B79: місяць, ЗАТВЕРДЖЕНИЙ між рішенням про запис і самим записом, — записувач (PATCH, книга) під замками аркушів
/// року перечитує стани й відмовляє <c>DependentMonthsLocked</c> з переліком місяців; нічого не записано. Поданий між
/// рішенням і записом місяць не заважає, а стовпець без залежних і далі судить річний агрегат.
/// </summary>
/// <remarks>
/// ⛔ Детерміновано, два з'єднання (як <c>RowLockGateRaceTests</c>): рішення про запис (заглушка служби доступу
/// дозволяє річну комірку) → у точці перемикання ІНШЕ з'єднання комітить стан аркуша січня → записувач іде в транзакцію.
/// <para>
/// МУТАЦІЇ (локально): (1) у <c>PatchCellsHandler.CheckLockedColumnPeriods</c> прибрати виклик
/// <c>DependentMonthsRule.ThrowIfLocked</c> — червоніє «Затвердження_між…» (обидва рядки); (2) там само прибрати
/// <c>if (!annualAggregateGoverns) return;</c> — червоніє «Усі_місяці_подані…»; (3) у <c>NoteAnnualText</c> не
/// ставити <c>DependentInputWritten</c> — червоніє «Затвердження_між…».
/// </para>
/// </remarks>
[Collection("SqlServer")]
public sealed class DependentMonthsGateRaceTests(SqlServerFixture sql)
{
    private const decimal EditedValue = 791.5m;
    private const int View = 209910;
    private const int Jan = 209901;
    private static readonly TimeSpan HookTimeout = TimeSpan.FromSeconds(60);

    private sealed record Scene(TestDocument Doc, int WriterId, string RowKey, string BaseVersion);

    /// <summary>Payload задач каскаду, поставлених останнім <see cref="RunPatchAsync"/> (JSON).</summary>
    private readonly List<string> _payloads = [];

    /// <remarks>
    /// Контроль: без зміни стану між рішенням і записом PATCH пише, і каскад формул ставиться — і для стовпця з
    /// залежними (це не «текст якоря», перерахунок не пропускається), і для стовпця без них. Мутація: у
    /// <c>NoteAnnualText</c> для <c>DependentInput</c> додати колонку в <c>AnnualTextColumns</c> — червоніє рядок (true).
    /// </remarks>
    [Theory]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "B79")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Контроль_без_зміни_стану_PATCH_пише_і_ставить_каскад_формул(bool dependent)
    {
        var scene = await ArrangeAsync();

        Assert.Null(await RunPatchAsync(scene, between: null, dependent, workbook: false));
        Assert.Equal(EditedValue, await LiveValueAsync(scene));

        var payload = Assert.Single(_payloads);
        Assert.Contains($"\"ColumnDefId\":{scene.Doc.ColumnDefIds[1]}", payload, StringComparison.Ordinal);
    }

    [Theory]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "B79")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Затвердження_між_рішенням_і_записом_відхиляє_DependentMonthsLocked_з_місяцем(bool workbook)
    {
        var scene = await ArrangeAsync();

        var error = await RunPatchAsync(scene, DocumentStatus.Approved, dependent: true, workbook);

        var denied = Assert.IsType<AccessDeniedException>(error);
        Assert.Equal("ECR-ACCS-0403", denied.ErrorCode);
        Assert.Equal(nameof(EditDenyReason.DependentMonthsLocked), denied.Details?["reason"]?.ToString());
        Assert.Equal(new[] { Jan }, Assert.IsType<int[]>(denied.Details?[DependentMonthsRule.MonthsDetail]));
        Assert.Null(await LiveValueAsync(scene));
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "B79")]
    public async Task Подання_між_рішенням_і_записом_не_заважає()
    {
        var scene = await ArrangeAsync();

        Assert.Null(await RunPatchAsync(scene, DocumentStatus.Submitted, dependent: true, workbook: false));
        Assert.Equal(EditedValue, await LiveValueAsync(scene));
    }

    /// <remarks>Обидва місяці року подані: стовпець із залежними пишеться, а без залежних — <c>DocumentSubmitted</c>.</remarks>
    [Theory]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "B79")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Усі_місяці_подані_під_замком_із_залежними_пишеться_без_залежних_DocumentSubmitted(bool dependent)
    {
        var scene = await ArrangeAsync();
        await InsertStateAsync(scene, View, DocumentStatus.Submitted);

        var error = await RunPatchAsync(scene, DocumentStatus.Submitted, dependent, workbook: false);

        if (dependent)
        {
            Assert.Null(error);
            Assert.Equal(EditedValue, await LiveValueAsync(scene));
            return;
        }

        var denied = Assert.IsType<AccessDeniedException>(error);
        Assert.Equal(nameof(EditDenyReason.DocumentSubmitted), denied.Details?["reason"]?.ToString());
        Assert.Null(await LiveValueAsync(scene));
    }

    /// <remarks>Диференційний контроль: те саме затвердження між рішенням і записом, але стовпець без залежних —
    /// річний агрегат (січень Approved, жовтень Draft → Draft) запис пропускає, як і до B79.</remarks>
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "B79")]
    public async Task Стовпець_без_залежних_затвердження_одного_місяця_між_рішенням_і_записом_не_відхиляє()
    {
        var scene = await ArrangeAsync();

        Assert.Null(await RunPatchAsync(scene, DocumentStatus.Approved, dependent: false, workbook: false));
        Assert.Equal(EditedValue, await LiveValueAsync(scene));
    }

    // ───────────────────────────── допоміжне ─────────────────────────────

    /// <remarks>Текст якоря (AN-142) — теж за річним агрегатом: рік, затверджений цілком, його замикає й під замком.
    /// Мутація: у <c>NoteAnnualText</c> не ставити <c>AnnualTextWritten</c> — червоніє.</remarks>
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "B79")]
    public async Task Текст_якоря_під_замком_рік_затверджено_цілком_DocumentApproved()
    {
        var scene = await ArrangeAsync();
        await InsertStateAsync(scene, View, DocumentStatus.Approved);

        var error = await RunPatchAsync(scene, DocumentStatus.Approved, dependent: false, workbook: false, text: true);

        var denied = Assert.IsType<AccessDeniedException>(error);
        Assert.Equal(nameof(EditDenyReason.DocumentApproved), denied.Details?["reason"]?.ToString());
    }

    private async Task<Exception?> RunPatchAsync(
        Scene scene, DocumentStatus? between, bool dependent, bool workbook, bool text = false)
    {
        await using var db = CreateContext();
        var doc = scene.Doc;
        var clock = new FixedClock(DateTime.UtcNow);
        var bulk = new BulkCellLoader(db.Database.GetConnectionString()!, 1000);
        var profile = new AccessBuilder { UserId = scene.WriterId }
            .Grant(ResourceKind.Project, doc.ProjectId, GrantLevel.Write)
            .Build();

        // Рішення поза транзакцією: річна комірка (січень і жовтень 2099), усі місяці ще чернетки.
        IReadOnlyList<int> annual = [Jan, View];
        var allowed = EditDecision.Allow() with { AnnualPeriods = annual, DependentInput = dependent, AnnualTextOnly = text };

        async Task<T> AfterDecision<T>(T decisions)
        {
            if (between is { } status)
            {
                await InsertStateAsync(scene, Jan, status);
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
                  c.ArgAt<IReadOnlyCollection<CellAddress>>(3).ToDictionary(a => a, _ => allowed)));
        access.CanEditCellsBatchAsync(
                  Arg.Any<AccessProfile>(), Arg.Any<IReadOnlyCollection<CellsAccessRequest>>(), Arg.Any<CancellationToken>())
              .Returns(c => AfterDecision<IReadOnlyDictionary<long, IReadOnlyDictionary<CellAddress, EditDecision>>>(
                  c.ArgAt<IReadOnlyCollection<CellsAccessRequest>>(1).ToDictionary(
                      r => r.TableInstanceId,
                      r => (IReadOnlyDictionary<CellAddress, EditDecision>)r.Addresses.ToDictionary(a => a, _ => allowed))));
        access.CanCreateRowsAsync(
                  Arg.Any<AccessProfile>(), Arg.Any<long>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
              .Returns(new Dictionary<string, NewRowAccess>());

        var periods = Substitute.For<IPeriodStore>();
        periods.FindPeriodStateAsync(doc.DocumentId, Arg.Any<int>(), Arg.Any<CancellationToken>())
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

        // Payload задачі каскаду формул — щоб побачити, що правка ставить перерахунок.
        _payloads.Clear();
        var jobs = Substitute.For<IBackgroundJobScheduler>();
        jobs.EnqueueCoalescedAsync<IFormulaRecalculationJob>(
                Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<CancellationToken>(), Arg.Any<int?>())
            .Returns(call =>
            {
                _payloads.Add(System.Text.Json.JsonSerializer.Serialize(call.ArgAt<object?>(1)));
                return "job";
            });

        var handler = new PatchCellsHandler(
            new NormalizedCellStore(db), new RowStore(db, bulk, clock), new DocumentStore(db), periods,
            Metadata(doc), access,
            new Ecr.Application.Validation.ValidationEngine(new RealFormulaEngine()),
            methodologies, registries, headers, new AuditWriter(db), new AuditReader(db),
            jobs, new UnitOfWork(db), user, clock, new SheetEditGate(db),
            new UnitCatalog(db),
            assignments: new EditAssignmentStore(db),
            rowLocks: new RowLockStore(db));

        var request = new PatchCellsRequest(
            doc.TableInstanceId, doc.PeriodKey.Value, "UserEdit",
            [new PatchRow(scene.RowKey, scene.BaseVersion,
                [text ? new PatchCell($"C1_{doc.ColumnDefIds[0]}", "b79") : new PatchCell($"C2_{doc.ColumnDefIds[1]}", EditedValue)])]);

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

    private async Task<Scene> ArrangeAsync()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(View, rowMode: TableRowMode.Mixed, ct: CancellationToken.None);

        int writer;
        await using (var db = CreateContext())
        {
            var period = await db.Periods.FirstAsync(
                p => p.ProjectId == doc.ProjectId && p.PeriodKeyValue == View, CancellationToken.None);
            period.AdvanceTo(PeriodState.Open, DateTime.UtcNow);

            var writerUser = new User($"dmg_{Guid.NewGuid():N}"[..20], "B79 race user", AuthProvider.Local);
            writerUser.SetPassword("not-a-real-hash");
            db.Users.Add(writerUser);
            await db.SaveChangesAsync(CancellationToken.None);
            writer = writerUser.Id;
        }

        var rowKey = await ScalarAsync<string>(
            $"SELECT RowKey FROM doc.TableRow WHERE PeriodKey = {View} AND Id = {doc.RowIds[0]}");
        var version = Convert.ToBase64String(await ScalarAsync<byte[]>(
            $"SELECT RowVersion FROM doc.TableRow WHERE TableInstanceId = {doc.TableInstanceId} AND RowKey = N'{rowKey}'"));

        return new Scene(doc, writer, rowKey, version);
    }

    /// <summary>Інше з'єднання комітить стан аркуша місяця (точка перемикання гонки).</summary>
    private async Task InsertStateAsync(Scene scene, int periodKey, DocumentStatus status)
    {
        await using var other = CreateContext();
        var now = DateTime.UtcNow;
        var state = new ApprovalState(scene.Doc.DocumentId, scene.Doc.SheetDefId, periodKey);
        state.Submit(scene.WriterId, now);
        if (status == DocumentStatus.Approved)
        {
            state.Approve(scene.WriterId, now);
        }

        other.ApprovalStates.Add(state);
        await other.SaveChangesAsync(CancellationToken.None);
    }

    private async Task<decimal?> LiveValueAsync(Scene scene)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT ValueNumeric FROM doc.CellValue WHERE PeriodKey = {View} " +
            $"AND TableRowId = {scene.Doc.RowIds[0]} AND ColumnDefId = {scene.Doc.ColumnDefIds[1]}";
        var result = await command.ExecuteScalarAsync();
        return result is null or DBNull ? null : (decimal)result;
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
