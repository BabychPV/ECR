using Ecr.Application.Common;
using Ecr.Application.Security;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Caching;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Security;

/// <summary>
/// ✎ B79: правило залежних затверджених місяців у <c>AccessDecisionService</c> на справжній базі — межі правила, яких
/// не видно через HTTP: колір і очищення, комірки нового рядка, інтеграція й симуляція, копії аркуша, жива формула,
/// тотожність шляхів (пакет, зріз, поштучний).
/// </summary>
/// <remarks>
/// ⛔ Безпека запису. МУТАЦІЇ (локально, не комітити): (1) у <c>Decide</c> прибрати <c>!ignoreColumnFlags</c> — червоніє
/// «Колір_і_очищення…»; (2) прибрати <c>!newRow</c> — червоніє «Комірки_нового_рядка…»; (3) у запиті
/// <c>DependentsAsync</c> прибрати <c>!formula.IsDeleted</c> — червоніє «Видалена_формула…»; (4) у <c>Decide</c> не
/// ставити <c>DependentInput</c> — червоніє «Дозволене_рішення_несе…»; (5) <c>ApprovedMonths</c> рахувати порожнім —
/// червоніють «Пакет_зріз_і_поштучний…», «Стан_копії…».
/// </remarks>
[Collection("SqlServer")]
public sealed class DependentMonthsAccessDecisionTests(SqlServerFixture sql) : IDisposable
{
    private const int Year = 2099;
    private const int View = 209910;
    private const int Jan = 209901;

    private static readonly DateTime Now = new(2026, 10, 15, 10, 0, 0, DateTimeKind.Utc);

    private readonly MemoryCache _memory = new(new MemoryCacheOptions());

    public void Dispose() => _memory.Dispose();

    private sealed record World(TestDocumentBuilder Builder, TestDocument Doc, int UserId, int FormulaId)
    {
        public int Fuel => Doc.ColumnDefIds[1];

        public int Free => Doc.ColumnDefIds[2];

        public CellAddress Cell(int column, int row = 0) => new(Doc.PeriodKey, Doc.RowIds[row], column);
    }

    private AccessDecisionService Service(EcrDbContext db)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Collection:MonthColumnRouting"] = "true" })
            .Build();

        return new AccessDecisionService(
            db,
            new MetadataCache(_memory, db),
            new AccessProfileCache(_memory),
            new TestClock(Now),
            Substitute.For<ICurrentUser>(),
            new WorkflowStore(db),
            new MonthColumnRouter(db, configuration));
    }

    private static AccessProfile Writer(World w)
        => new AccessBuilder { UserId = w.UserId }.Grant(ResourceKind.Project, w.Doc.ProjectId, GrantLevel.Write).Build();

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
    [Trait("Requirement", "B79")]
    public async Task Пакет_зріз_і_поштучний_шлях_однаково_відхиляють_стовпець_із_залежними_а_сусідній_ні()
    {
        var w = await ArrangeAsync();
        await StateAsync(w, Jan, DocumentStatus.Approved);
        var writer = Writer(w);

        var batch = await BatchAsync(w, writer, w.Cell(w.Fuel), w.Cell(w.Free), w.Cell(w.Fuel, row: 1));
        await using var db = w.Builder.CreateContext();
        var service = Service(db);
        var slice = await service.CanEditSliceAsync(writer, w.Doc.TableInstanceId, CancellationToken.None);
        var single = await service.CanEditCellAsync(writer, w.Doc.DocumentId, w.Cell(w.Fuel), CancellationToken.None);

        foreach (var decision in new[] { batch[w.Cell(w.Fuel)], batch[w.Cell(w.Fuel, row: 1)], slice[w.Cell(w.Fuel)], slice[w.Cell(w.Fuel, row: 1)], single })
        {
            Assert.Equal(EditDenyReason.DependentMonthsLocked, decision.Reason);
            Assert.Equal(new[] { Jan }, decision.BlockingMonths);
        }

        Assert.True(batch[w.Cell(w.Free)].IsAllowed);
        Assert.True(slice[w.Cell(w.Free)].IsAllowed);
        Assert.True(slice[w.Cell(w.Doc.MonthColumnDefIds[9])].IsAllowed);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "B79")]
    public async Task Дозволене_рішення_несе_ознаку_стовпця_із_залежними_для_повторної_перевірки_під_замком()
    {
        var w = await ArrangeAsync();
        await StateAsync(w, Jan, DocumentStatus.Submitted);

        var decisions = await BatchAsync(w, Writer(w), w.Cell(w.Fuel), w.Cell(w.Free));

        Assert.True(decisions[w.Cell(w.Fuel)].IsAllowed);
        Assert.True(decisions[w.Cell(w.Fuel)].DependentInput);
        Assert.Contains(Jan, decisions[w.Cell(w.Fuel)].AnnualPeriods!);
        Assert.Null(decisions[w.Cell(w.Fuel)].BlockingMonths);
        Assert.True(decisions[w.Cell(w.Free)].IsAllowed);
        Assert.False(decisions[w.Cell(w.Free)].DependentInput);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "B79")]
    public async Task Колір_і_очищення_комірки_правило_залежних_місяців_не_чіпає()
    {
        var w = await ArrangeAsync();
        await StateAsync(w, Jan, DocumentStatus.Approved);
        var writer = Writer(w);

        await using var db = w.Builder.CreateContext();
        var service = Service(db);
        var edit = await service.CanEditCellsAsync(
            writer, w.Doc.TableInstanceId, w.Doc.PeriodKey, [w.Cell(w.Fuel)], CancellationToken.None);
        var clear = await service.CanClearCellsAsync(
            writer, w.Doc.TableInstanceId, w.Doc.PeriodKey, [w.Cell(w.Fuel)], CancellationToken.None);
        var style = await service.CanStyleSliceAsync(writer, w.Doc.TableInstanceId, CancellationToken.None);

        Assert.Equal(EditDenyReason.DependentMonthsLocked, edit[w.Cell(w.Fuel)].Reason);
        Assert.True(clear[w.Cell(w.Fuel)].IsAllowed);
        Assert.False(clear[w.Cell(w.Fuel)].DependentInput);
        Assert.True(style[w.Cell(w.Fuel)].IsAllowed);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "B79")]
    public async Task Комірки_нового_рядка_лишаються_на_річному_агрегаті()
    {
        var w = await ArrangeAsync();
        await StateAsync(w, Jan, DocumentStatus.Approved);

        await using var db = w.Builder.CreateContext();
        var created = await Service(db).CanCreateRowsAsync(
            Writer(w), w.Doc.TableInstanceId, ["B79_NEW"], CancellationToken.None);

        Assert.True(created["B79_NEW"].Row.IsAllowed);
        Assert.True(created["B79_NEW"].Columns[w.Fuel].IsAllowed);
        Assert.False(created["B79_NEW"].Columns[w.Fuel].DependentInput);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "B79")]
    public async Task Інтеграція_пише_а_симуляція_бачить_SimulationReadOnly_а_не_місяці()
    {
        var w = await ArrangeAsync();
        await StateAsync(w, Jan, DocumentStatus.Approved);
        var own = Writer(w);
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
        var simulation = new AccessBuilder { UserId = w.UserId }
            .Grant(ResourceKind.Project, w.Doc.ProjectId, GrantLevel.Write)
            .Build(simulation: true, simulatedFor: 42);

        var byIntegration = await BatchAsync(w, integration, w.Cell(w.Fuel));
        var bySimulation = await BatchAsync(w, simulation, w.Cell(w.Fuel));
        var byUser = await BatchAsync(w, own, w.Cell(w.Fuel));

        Assert.True(byIntegration[w.Cell(w.Fuel)].IsAllowed);
        Assert.Equal(EditDenyReason.SimulationReadOnly, bySimulation[w.Cell(w.Fuel)].Reason);
        Assert.Null(bySimulation[w.Cell(w.Fuel)].BlockingMonths);
        Assert.Equal(EditDenyReason.DependentMonthsLocked, byUser[w.Cell(w.Fuel)].Reason);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "B79")]
    public async Task Стан_копії_аркуша_оригінал_не_блокує_а_стан_оригіналу_блокує()
    {
        var w = await ArrangeAsync();
        long copyId;
        await using (var db = w.Builder.CreateContext())
        {
            copyId = (await new DocumentSheetCopyStore(db).AddCopyAsync(w.Doc.DocumentId, w.Doc.SheetDefId, default)).SheetCopyId;
            await db.SaveChangesAsync();
        }

        await StateAsync(w, Jan, DocumentStatus.Approved, copyId);
        var copyOnly = await BatchAsync(w, Writer(w), w.Cell(w.Fuel));
        Assert.True(copyOnly[w.Cell(w.Fuel)].IsAllowed);

        await StateAsync(w, Jan, DocumentStatus.Approved);
        var original = await BatchAsync(w, Writer(w), w.Cell(w.Fuel));
        Assert.Equal(EditDenyReason.DependentMonthsLocked, original[w.Cell(w.Fuel)].Reason);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "B79")]
    public async Task Видалена_формула_залежних_не_дає()
    {
        var w = await ArrangeAsync();
        await StateAsync(w, Jan, DocumentStatus.Approved);

        var live = await BatchAsync(w, Writer(w), w.Cell(w.Fuel));
        Assert.Equal(EditDenyReason.DependentMonthsLocked, live[w.Cell(w.Fuel)].Reason);

        await using (var connection = new SqlConnection(sql.ConnectionString))
        {
            await connection.OpenAsync(CancellationToken.None);
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE cfg.FormulaDef SET IsDeleted = 1 WHERE Id = @id";
            command.Parameters.AddWithValue("@id", w.FormulaId);
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }

        _memory.Clear();

        var deleted = await BatchAsync(w, Writer(w), w.Cell(w.Fuel));
        Assert.True(deleted[w.Cell(w.Fuel)].IsAllowed);
        Assert.False(deleted[w.Cell(w.Fuel)].DependentInput);
    }

    /// <remarks>
    /// ✎ F13 AU (правило 437): стовпець FUEL читає лише формула ІНШОГО аркуша (у ньому нічого не затверджено), а весь рік
    /// аркуша правки (оригіналу або КОПІЇ) затверджено. Рішення для всіх шляхів запису — PATCH
    /// (<c>CanEditCellsAsync</c>), книга (<c>CanEditCellsBatchAsync</c>), зріз (<c>CanEditSliceAsync</c>) — однаково
    /// <c>DocumentApproved</c>: затверджені значення не змінюються. Контроль: інший бік (копія, коли затверджено
    /// оригінал, і навпаки) пишеться. МУТАЦІЯ (AU): у <c>Decide</c> зводити <c>Approved</c> до <c>Draft</c> без
    /// <c>OwnSheetReads</c> — червоніють обидва рядки.
    /// </remarks>
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "B79")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Читач_лише_інший_аркуш_затверджений_рік_аркуша_правки_DocumentApproved_на_всіх_шляхах_і_для_копії(
        bool onCopy)
    {
        var w = await ArrangeAsync(readerOnOtherSheet: true);
        var (copyId, copyInstance, copyRow) = await AddCopyInstanceAsync(w);
        for (var m = 1; m <= 12; m++)
        {
            await StateAsync(w, (Year * 100) + m, DocumentStatus.Approved, onCopy ? copyId : null);
        }

        var writer = Writer(w);
        var originalCell = w.Cell(w.Fuel);
        var copyCell = new CellAddress(w.Doc.PeriodKey, copyRow, w.Fuel);
        var (instance, cell, otherInstance, otherCell) = onCopy
            ? (copyInstance, copyCell, w.Doc.TableInstanceId, originalCell)
            : (w.Doc.TableInstanceId, originalCell, copyInstance, copyCell);

        await using var db = w.Builder.CreateContext();
        var service = Service(db);
        var patch = await service.CanEditCellsAsync(writer, instance, w.Doc.PeriodKey, [cell], CancellationToken.None);
        var workbook = await service.CanEditCellsBatchAsync(
            writer, [new CellsAccessRequest(instance, w.Doc.PeriodKey, [cell])], CancellationToken.None);
        var slice = await service.CanEditSliceAsync(writer, instance, CancellationToken.None);
        var other = await service.CanEditCellsAsync(
            writer, otherInstance, w.Doc.PeriodKey, [otherCell], CancellationToken.None);

        foreach (var decision in new[] { patch[cell], workbook[instance][cell], slice[cell] })
        {
            Assert.Equal(EditDenyReason.DocumentApproved, decision.Reason);
            Assert.Null(decision.BlockingMonths);
        }

        Assert.True(other[otherCell].IsAllowed);
        Assert.True(other[otherCell].DependentOnlyOtherSheets);
    }

    // ───────────────────────────── допоміжне ─────────────────────────────

    /// <summary>Копія аркуша таблиці з екземпляром перегляду і першим рядком (той самий ключ рядка).</summary>
    private async Task<(long CopyId, long Instance, long Row)> AddCopyInstanceAsync(World w)
    {
        await using var db = w.Builder.CreateContext();
        var copyId = (await new DocumentSheetCopyStore(db).AddCopyAsync(w.Doc.DocumentId, w.Doc.SheetDefId, default)).SheetCopyId;
        await db.SaveChangesAsync();

        var rowKey = await db.TableRows.AsNoTracking()
            .Where(r => r.PeriodKeyValue == View && r.Id == w.Doc.RowIds[0]).Select(r => r.RowKeyValue).SingleAsync();
        var loader = new BulkCellLoader(sql.ConnectionString, 1000);
        var instanceId = await loader.ReserveIdsAsync("doc.TableInstanceSeq", 1, CancellationToken.None);
        var rowId = await loader.ReserveIdsAsync("doc.TableRowSeq", 1, CancellationToken.None);
        db.TableInstances.Add(new TableInstance(w.Doc.PeriodKey, instanceId, w.Doc.DocumentId, w.Doc.TableDefId, Now, copyId));
        db.TableRows.Add(new TableRow(w.Doc.PeriodKey, rowId, instanceId, RowKey.Create(rowKey), 1, Now));
        await db.SaveChangesAsync();
        return (copyId, instanceId, rowId);
    }

    private static async Task StateAsync(World w, int periodKey, DocumentStatus status, long? sheetCopyId = null)
    {
        await using var db = w.Builder.CreateContext();
        var state = new ApprovalState(w.Doc.DocumentId, w.Doc.SheetDefId, periodKey, sheetCopyId);
        state.Submit(w.UserId, Now.AddDays(-2));
        if (status == DocumentStatus.Approved)
        {
            state.Approve(w.UserId, Now.AddDays(-1));
        }

        db.ApprovalStates.Add(state);
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Документ 2099 року з таблицею «місяці в колонках» (екземпляри 01 і 10), усі місяці календаря відкриті; формула
    /// M12 читає другий стовпець (FUEL), третій (FREE) не читає ніхто.
    /// </summary>
    /// <param name="readerOnOtherSheet">✎ F13 AU: FUEL читає не M12 аркуша, а формула аркуша B тієї ж версії.</param>
    private async Task<World> ArrangeAsync(bool readerOnOtherSheet = false)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(
            View, columnCount: 3, rowCount: 2, rowMode: TableRowMode.Mixed,
            layoutKind: TableLayoutKind.MonthsInColumns, monthColumns: 12, ct: CancellationToken.None);

        int userId;
        int formulaId;
        await using (var db = builder.CreateContext())
        {
            db.DocumentSheets.Add(new DocumentSheet(doc.DocumentId, doc.SheetDefId));
            for (var m = 1; m <= 12; m++)
            {
                var key = (Year * 100) + m;
                var period = key == View
                    ? await db.Periods.SingleAsync(p => p.ProjectId == doc.ProjectId && p.PeriodKeyValue == View)
                    : db.Periods.Add(new Period(
                        doc.ProjectId, new PeriodKey(key), (byte)m,
                        new DateOnly(Year, m, 1), new DateOnly(Year, m, DateTime.DaysInMonth(Year, m)))).Entity;
                period.AdvanceTo(PeriodState.Open, Now);
            }

            var user = new User($"dm_{Guid.NewGuid():N}"[..20], "B79 user", AuthProvider.Local);
            user.SetPassword("not-a-real-hash");
            db.Users.Add(user);

            var fuelCode = await db.ColumnDefs.Where(c => c.Id == doc.ColumnDefIds[1]).Select(c => c.Code).SingleAsync();
            FormulaDef formula;
            if (readerOnOtherSheet)
            {
                var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
                var sheetB = new SheetDef(
                    doc.TemplateVersionId, EcrCode.Create($"SHB{tag}"),
                    new LocalizedText(new Dictionary<string, string> { ["en"] = "Reader sheet" }), 2);
                db.SheetDefs.Add(sheetB);
                await db.SaveChangesAsync();
                var tableB = new TableDef(
                    sheetB.Id, EcrCode.Create($"TBB{tag}"),
                    new LocalizedText(new Dictionary<string, string> { ["en"] = "Reader table" }), 1,
                    TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed);
                db.TableDefs.Add(tableB);
                await db.SaveChangesAsync();
                var readerColumn = new ColumnDef(
                    tableB.Id, EcrCode.Create($"RB{tag}"),
                    new LocalizedText(new Dictionary<string, string> { ["en"] = "Reader" }), 1, CellDataType.Formula);
                db.ColumnDefs.Add(readerColumn);
                db.DocumentSheets.Add(new DocumentSheet(doc.DocumentId, sheetB.Id));
                await db.SaveChangesAsync();
                formula = new FormulaDef(tableB.Id, FormulaScope.Column, "0", ExpressionDialect.Template);
                formula.AssignColumn(readerColumn.Id);
            }
            else
            {
                formula = new FormulaDef(doc.TableDefId, FormulaScope.Column, $"[{fuelCode}] * 2", ExpressionDialect.Template);
                formula.AssignColumn(doc.MonthColumnDefIds[11]);
            }

            db.FormulaDefs.Add(formula);
            await db.SaveChangesAsync();

            db.FormulaDependencies.Add(FormulaDependency.ForFormula(
                formula.Id, dependsOnKind: 0, doc.TableDefId, rowKey: null, doc.ColumnDefIds[1],
                filterJson: null, periodOffset: null, sortOrder: 0));
            await db.SaveChangesAsync();
            userId = user.Id;
            formulaId = formula.Id;
        }

        await builder.AddPeriodInstanceAsync(doc, new PeriodKey(Jan));
        return new World(builder, doc, userId, formulaId);
    }
}
