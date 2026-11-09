// tests/Ecr.Infrastructure.Tests/Persistence/SheetNameRecalculationTests.cs
using System.Text.Json;
using Ecr.Application.Calculations;
using Ecr.Application.Common;
using Ecr.Application.Documents;
using Ecr.Application.Ports;
using Ecr.Application.Recalculation;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Expressions.Evaluation;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// <c>SHEETNAME()</c> на <b>реальному</b> SQL Server: копія аркуша рахує «&lt;Назва&gt; 2», оригінал «&lt;Назва&gt; 1»,
/// а перейменування групи ставить перерахунок формул аркуша (задача <c>IRecalculationJob</c>, той самий шлях, що правка
/// шапки), після якого значення — нова назва.
/// </summary>
/// <remarks>
/// Мутаційні докази (локально, не комітяться): прибрати постановку з <c>DocumentSheetCopyHandler.RenameGroupAsync</c> —
/// червоніє <see cref="Перейменування_групи_ставить_перерахунок_і_формула_дає_нову_назву"/> (задачі немає); не
/// передавати назви документа в контекст (<c>RecalculationService</c>, <c>sheetNames</c>) — червоніють обидва тести
/// (копія й оригінал дають назву шаблону без номера).
/// </remarks>
[Collection("SqlServer")]
public sealed class SheetNameRecalculationTests(SqlServerFixture sql)
{
    private const int UserId = 1;
    private const int FormulaId = 930_001;
    private static readonly DateTime Now = new(2026, 3, 20, 6, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "SHEETNAME-1109")]
    public async Task Копія_рахує_свою_назву_з_номером_а_оригінал_номер_1()
    {
        var world = await ArrangeAsync();
        var copy = await world.Run(h => h.AddAsync(world.Doc.DocumentId, world.Doc.SheetDefId, default));
        var copyRow = await AddCopyInstanceAsync(world.Doc, copy.SheetCopyId);

        await world.RecalculateSheetAsync();

        Assert.Equal("Vessel 1", await TextAsync(world.Doc, world.Doc.RowIds[0]));
        Assert.Equal("Vessel 2", await TextAsync(world.Doc, copyRow));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "SHEETNAME-1109")]
    public async Task Перейменування_групи_ставить_перерахунок_і_формула_дає_нову_назву()
    {
        var world = await ArrangeAsync();
        var copy = await world.Run(h => h.AddAsync(world.Doc.DocumentId, world.Doc.SheetDefId, default));
        var copyRow = await AddCopyInstanceAsync(world.Doc, copy.SheetCopyId);
        await world.RecalculateSheetAsync();
        world.Jobs.ClearReceivedCalls();

        await world.Run(h => h.RenameGroupAsync(
            world.Doc.DocumentId, world.Doc.SheetDefId, new Dictionary<string, string> { ["en"] = "Contract" }, default));

        // Постановка — та сама задача й ціль, що в правки шапки, звужена до аркуша.
        var call = Assert.Single(world.Jobs.ReceivedCalls(), c => c.GetMethodInfo().Name == "EnqueueCoalescedAsync");
        Assert.Equal(typeof(IRecalculationJob), call.GetMethodInfo().GetGenericArguments()[0]);
        Assert.Equal(RecalculateDocumentHandler.TargetOf(world.Doc.DocumentId, world.Doc.PeriodKey), call.GetArguments()[0]);
        using var payload = JsonDocument.Parse(JsonSerializer.Serialize(call.GetArguments()[1]));
        Assert.Equal(world.Doc.SheetDefId, payload.RootElement.GetProperty("SheetDefId").GetInt32());
        Assert.Equal(world.Doc.PeriodKey.Value, payload.RootElement.GetProperty("PeriodKey").GetInt32());

        // Що зробить задача: повний прогін аркуша (RecalculationJob → RecalculateAllAsync(sheetDefId)).
        await world.RecalculateSheetAsync();

        Assert.Equal("Contract 1", await TextAsync(world.Doc, world.Doc.RowIds[0]));
        Assert.Equal("Contract 2", await TextAsync(world.Doc, copyRow));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "SHEETNAME-1109")]
    public async Task Аркуш_без_SHEETNAME_не_ставить_перерахунку_при_перейменуванні()
    {
        var world = await ArrangeAsync(expression: "'fixed'");
        await world.Run(h => h.AddAsync(world.Doc.DocumentId, world.Doc.SheetDefId, default));
        world.Jobs.ClearReceivedCalls();

        await world.Run(h => h.RenameGroupAsync(
            world.Doc.DocumentId, world.Doc.SheetDefId, new Dictionary<string, string> { ["en"] = "Contract" }, default));

        Assert.DoesNotContain(world.Jobs.ReceivedCalls(), c => c.GetMethodInfo().Name.StartsWith("Enqueue", StringComparison.Ordinal));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "SHEETNAME-1109")]
    public async Task Нова_копія_при_першому_відкритті_ставить_перерахунок_і_дає_назву_з_номером()
    {
        var world = await ArrangeAsync();
        var copy = await world.Run(h => h.AddAsync(world.Doc.DocumentId, world.Doc.SheetDefId, default));
        world.Jobs.ClearReceivedCalls();

        // Перше відкриття періоду після додавання копії — екземпляр копії створює саме воно (без ручного «Перерахувати»).
        await world.OpenTablesAsync();

        var call = Assert.Single(world.Jobs.ReceivedCalls(), c => c.GetMethodInfo().Name == "EnqueueCoalescedAsync");
        Assert.Equal(RecalculateDocumentHandler.TargetOf(world.Doc.DocumentId, world.Doc.PeriodKey), call.GetArguments()[0]);

        // Друге відкриття нічого не створює — і нічого не ставить.
        world.Jobs.ClearReceivedCalls();
        await world.OpenTablesAsync();
        Assert.DoesNotContain(world.Jobs.ReceivedCalls(), c => c.GetMethodInfo().Name.StartsWith("Enqueue", StringComparison.Ordinal));

        await world.RecalculateSheetAsync();
        Assert.Equal("Vessel 2", await TextAsync(world.Doc, await CopyRowAsync(world.Doc, copy.SheetCopyId)));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "SHEETNAME-1109")]
    public async Task Перейменування_й_нова_копія_ставлять_перерахунок_лише_в_періоди_з_екземплярами_аркуша()
    {
        var world = await ArrangeAsync();

        // Другий відкритий період проєкту, у якому документ ще не відкривали (екземплярів немає).
        await using (var db = new EcrDbContext(
                         new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options))
        {
            var april = new Period(
                world.Doc.ProjectId, new PeriodKey(202604), 4, new DateOnly(2026, 4, 1), new DateOnly(2026, 4, 30));
            april.AdvanceTo(PeriodState.Open, Now);
            db.Periods.Add(april);
            await db.SaveChangesAsync();
        }

        await world.Run(h => h.AddAsync(world.Doc.DocumentId, world.Doc.SheetDefId, default));
        await world.Run(h => h.RenameGroupAsync(
            world.Doc.DocumentId, world.Doc.SheetDefId, new Dictionary<string, string> { ["en"] = "Contract" }, default));

        var targets = world.Jobs.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == "EnqueueCoalescedAsync")
            .Select(c => (string)c.GetArguments()[0]!)
            .ToList();

        // Додавання + перейменування — по одній задачі, і лише за березень (там є екземпляр); квітня немає.
        var march = RecalculateDocumentHandler.TargetOf(world.Doc.DocumentId, world.Doc.PeriodKey);
        Assert.Equal([march, march], targets);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "F13-p218")]
    public async Task Затверджений_оригінал_перейменування_задача_проходить_копія_Barge_2_оригінал_незмінний()
    {
        // F13 p218 (P2): затверджений оригінал + перейменування -> задача падала ECR-CALC-4221 цілим періодом, копія-чернетка
        // лишалась «Fleet 2» під вкладкою «Barge 2». Правило людини 07.11: пропускати лише затверджені аркуші.
        var world = await ArrangeAsync();
        var copy = await world.Run(h => h.AddAsync(world.Doc.DocumentId, world.Doc.SheetDefId, default));
        var copyRow = await AddCopyInstanceAsync(world.Doc, copy.SheetCopyId);
        await world.Run(h => h.RenameGroupAsync(
            world.Doc.DocumentId, world.Doc.SheetDefId, new Dictionary<string, string> { ["en"] = "Fleet" }, default));
        await world.RecalculateSheetAsync();
        Assert.Equal("Fleet 1", await TextAsync(world.Doc, world.Doc.RowIds[0]));
        await ApproveOriginalAsync(world.Doc);
        world.Jobs.ClearReceivedCalls();

        await world.Run(h => h.RenameGroupAsync(
            world.Doc.DocumentId, world.Doc.SheetDefId, new Dictionary<string, string> { ["en"] = "Barge" }, default));
        var payload = Assert.Single(world.Jobs.ReceivedCalls(), c => c.GetMethodInfo().Name == "EnqueueCoalescedAsync")
            .GetArguments()[1];

        // Та сама задача, що поставило перейменування, справжнім RecalculationJob: без винятку.
        await world.RunRecalculationJobAsync(payload);

        Assert.Equal("Barge 2", await TextAsync(world.Doc, copyRow));
        Assert.Equal("Fleet 1", await TextAsync(world.Doc, world.Doc.RowIds[0]));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "F13-p218")]
    public async Task Ручний_перерахунок_документа_з_затвердженим_оригіналом_рахує_копію_а_не_відмовляє_періодом()
    {
        // Загальний шлях: «Перерахувати» документа (без області аркуша) — єдиний аркуш затверджений, його копія — чернетка.
        // Доти період вважався «поданим цілком» за оригіналом і відмовляв 4221.
        var world = await ArrangeAsync();
        var copy = await world.Run(h => h.AddAsync(world.Doc.DocumentId, world.Doc.SheetDefId, default));
        var copyRow = await AddCopyInstanceAsync(world.Doc, copy.SheetCopyId);
        await ApproveOriginalAsync(world.Doc);

        await world.RunRecalculationJobAsync(new RecalculationRequest(
            world.Doc.ProjectId, world.Doc.DocumentId, world.Doc.PeriodKey.Value, TriggeredByUserId: UserId));

        Assert.Equal("Vessel 2", await TextAsync(world.Doc, copyRow));
        Assert.Null(await TextAsync(world.Doc, world.Doc.RowIds[0]));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "F13-p218-788")]
    public async Task Подана_копія_перераховується_з_позначкою_і_записом_журналу()
    {
        // F13 p218 #788 (правило людини 07.11): подана копія — перераховується З позначкою «перераховано після подання»
        // і записом журналу, як оригінал. Доти журнал і позначка бачили лише оригінали («marked: 0»).
        var world = await ArrangeAsync();
        var copy = await world.Run(h => h.AddAsync(world.Doc.DocumentId, world.Doc.SheetDefId, default));
        var copyRow = await AddCopyInstanceAsync(world.Doc, copy.SheetCopyId);
        await using (var db = new EcrDbContext(
                         new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options))
        {
            var state = new ApprovalState(world.Doc.DocumentId, world.Doc.SheetDefId, world.Doc.PeriodKey.Value, copy.SheetCopyId);
            state.Submit(UserId, Now.AddMinutes(-5));
            db.ApprovalStates.Add(state);
            await db.SaveChangesAsync();
        }

        var messages = await world.RunRecalculationJobAsync(new RecalculationRequest(
            world.Doc.ProjectId, world.Doc.DocumentId, world.Doc.PeriodKey.Value, TriggeredByUserId: UserId));

        Assert.Equal("Vessel 2", await TextAsync(world.Doc, copyRow));

        // Запис журналу — з ключем копії.
        var correlation = RecalculationSkips.AuditCorrelation(
            world.Doc.DocumentId, world.Doc.SheetDefId, world.Doc.PeriodKey.Value, copy.SheetCopyId);
        await using (var connection = new SqlConnection(sql.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT COUNT(*) FROM aud.StructureChange WHERE EntityType = @t AND Operation = @o AND CorrelationId = @c";
            command.Parameters.AddWithValue("@t", RecalculationSkips.AuditEntityType);
            command.Parameters.AddWithValue("@o", RecalculationSkips.AuditRecalculatedAfterSubmission);
            command.Parameters.AddWithValue("@c", correlation);
            Assert.Equal(1, (int)(await command.ExecuteScalarAsync())!);
        }

        // Підсумок задачі: «перераховано поданих» ≥ 1.
        var marked = messages
            .Select(m => { try { return JsonDocument.Parse(m); } catch (JsonException) { return null; } })
            .Where(d => d is not null && d.RootElement.ValueKind == JsonValueKind.Object
                        && d.RootElement.TryGetProperty("k", out var k) && k.GetString() == "jobs.recalcSheetLog")
            .Select(d => int.Parse(d!.RootElement.GetProperty("p").GetProperty("recalculatedSubmitted").GetString()!,
                System.Globalization.CultureInfo.InvariantCulture))
            .ToList();
        Assert.True(marked.Count == 1 && marked[0] >= 1, $"повідомлення задачі: {string.Join(" | ", messages)}");

        // Картка документа: позначка на стані КОПІЇ («КОД#sheetCopyId»).
        await using (var db = new EcrDbContext(
                         new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options))
        {
            var summary = await new DocumentStore(db).FindAsync(
                world.Doc.DocumentId, new PeriodKeyFilter(world.Doc.PeriodKey.Value), CancellationToken.None);
            var copyState = Assert.Single(summary!.Sheets!, s => s.Code == SheetCopyNaming.StateKey(world.Doc.SheetCode, copy.SheetCopyId));
            Assert.True(copyState.RecalculatedAfterSubmission);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "F13-p218-788")]
    public async Task Методології_копій_не_рахуються_за_побудовою_тож_затверджений_оригінал_копію_не_позбавляє()
    {
        // Ризик «IsActive по SheetDefId позбавляє методологій копію-чернетку затвердженого оригіналу» не застосовний:
        // методології прив'язані до таблиць ОРИГІНАЛУ (RS 03.11, release notes «методології — лише оригінали»), і план
        // прив'язок копій не містить ні до, ні після затвердження оригіналу. Тест фіксує це твердження.
        var world = await ArrangeAsync();
        var copy = await world.Run(h => h.AddAsync(world.Doc.DocumentId, world.Doc.SheetDefId, default));
        await AddCopyInstanceAsync(world.Doc, copy.SheetCopyId);
        await AddBindingAsync(world.Doc);
        var copyInstances = await CopyInstanceIdsAsync(world.Doc, copy.SheetCopyId);

        var draft = new RecordingRunner();
        await world.RunRecalculationJobAsync(
            new RecalculationRequest(world.Doc.ProjectId, world.Doc.DocumentId, world.Doc.PeriodKey.Value, TriggeredByUserId: UserId),
            draft);

        // Чернетка: методологія рахується на оригіналі, на копії — ніколи.
        Assert.Contains(draft.Instances, id => id == world.Doc.TableInstanceId);
        Assert.DoesNotContain(draft.Instances, copyInstances.Contains);

        await ApproveOriginalAsync(world.Doc);
        var approved = new RecordingRunner();
        await world.RunRecalculationJobAsync(
            new RecalculationRequest(world.Doc.ProjectId, world.Doc.DocumentId, world.Doc.PeriodKey.Value, TriggeredByUserId: UserId),
            approved);

        // Затверджений оригінал пропущено; копія, як і доти, без методологій — нічого не втратила.
        Assert.Empty(approved.Instances);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "F13-p218-788")]
    public async Task Перерахунок_проєкту_не_вважає_все_затвердженим_коли_копія_чернетка()
    {
        // WorkflowStore.AllSheetsApprovedAsync (перерахунок проєкту «рахувати нічого») бачив лише оригінали.
        var world = await ArrangeAsync();
        var copy = await world.Run(h => h.AddAsync(world.Doc.DocumentId, world.Doc.SheetDefId, default));
        await ApproveOriginalAsync(world.Doc);

        await using var db = new EcrDbContext(
            new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);
        var store = new WorkflowStore(db);
        Assert.False(await store.AllSheetsApprovedAsync(world.Doc.ProjectId, world.Doc.PeriodKey.Value, CancellationToken.None));

        var state = new ApprovalState(world.Doc.DocumentId, world.Doc.SheetDefId, world.Doc.PeriodKey.Value, copy.SheetCopyId);
        state.Submit(UserId, Now);
        state.Approve(UserId, Now);
        db.ApprovalStates.Add(state);
        await db.SaveChangesAsync();

        Assert.True(await store.AllSheetsApprovedAsync(world.Doc.ProjectId, world.Doc.PeriodKey.Value, CancellationToken.None));
    }

    private async Task AddBindingAsync(TestDocument doc)
    {
        await using var db = new EcrDbContext(
            new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);
        var methodology = new Ecr.Domain.Entities.Calculations.Methodology(
            EcrCode.Create($"SNM{Guid.NewGuid():N}"[..16]), Name("m"));
        db.Methodologies.Add(methodology);
        await db.SaveChangesAsync();
        db.CalculationBindings.Add(new CalculationBinding(
            doc.TableDefId, doc.ColumnDefIds[0], methodology.Id, "out", "{}"));
        db.MethodologyVersions.Add(new Ecr.Domain.Entities.Calculations.MethodologyVersion(
            methodology.Id, "1.0", CalculationLevel.Configuration, 1, Now));
        await db.SaveChangesAsync();
    }

    private async Task<List<long>> CopyInstanceIdsAsync(TestDocument doc, long sheetCopyId)
    {
        await using var db = new EcrDbContext(
            new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);
        return await db.TableInstances.AsNoTracking()
            .Where(i => i.DocumentId == doc.DocumentId && i.SheetCopyId == sheetCopyId)
            .Select(i => i.Id)
            .ToListAsync();
    }

    private async Task ApproveOriginalAsync(TestDocument doc)
    {
        await using var db = new EcrDbContext(
            new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);
        var state = new ApprovalState(doc.DocumentId, doc.SheetDefId, doc.PeriodKey.Value);
        state.Submit(UserId, Now);
        state.Approve(UserId, Now);
        db.ApprovalStates.Add(state);
        await db.SaveChangesAsync();
    }

    private async Task<long> CopyRowAsync(TestDocument doc, long sheetCopyId)
    {
        await using var db = new EcrDbContext(
            new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

        return await db.TableRows.AsNoTracking()
            .Where(r => r.PeriodKeyValue == doc.PeriodKey.Value
                        && db.TableInstances.Any(i => i.Id == r.TableInstanceId
                                                      && i.PeriodKeyValue == doc.PeriodKey.Value
                                                      && i.SheetCopyId == sheetCopyId))
            .Select(r => r.Id)
            .SingleAsync();
    }

    private async Task<World> ArrangeAsync(string expression = "SHEETNAME()")
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(periodKey: 202603, columnCount: 1, rowCount: 1, ct: CancellationToken.None);

        await using (var db = builder.CreateContext())
        {
            db.DocumentSheets.Add(new DocumentSheet(doc.DocumentId, doc.SheetDefId));
            var period = await db.Periods.SingleAsync(p => p.ProjectId == doc.ProjectId);
            period.AdvanceTo(PeriodState.Open, Now);
            await db.SaveChangesAsync();
        }

        var snapshot = Snapshot(doc, expression);
        var metadata = Substitute.For<IMetadataCache>();
        metadata.GetAsync(doc.TemplateVersionId, Arg.Any<CancellationToken>()).Returns(snapshot);

        var access = Substitute.For<IAccessDecisionService>();
        var profile = new AccessBuilder { UserId = UserId }
            .Permission(DocumentSheetCopyHandler.Permission)
            .Permission(GetDocumentTablesHandler.Permission)
            .Grant(ResourceKind.Project, doc.ProjectId, GrantLevel.Write)
            .Build();
        access.BuildProfileAsync(UserId, Arg.Any<CancellationToken>()).Returns(profile);
        access.CanReadDocumentAsync(Arg.Any<AccessProfile>(), doc.DocumentId, Arg.Any<CancellationToken>())
            .Returns(EditDecision.Allow());
        access.ReadScopeAsync(Arg.Any<AccessProfile>(), doc.DocumentId, Arg.Any<CancellationToken>())
            .Returns(_ => DocumentReadScope.For(profile, doc.ProjectId, snapshot));

        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(UserId);
        user.CorrelationId.Returns("sheet-name");

        // Quartz-подібна черга: постановка ПІСЛЯ коміту (EnlistsInCallerTransaction = false).
        var jobs = Substitute.For<IBackgroundJobScheduler>();
        jobs.EnlistsInCallerTransaction.Returns(false);

        return new World(builder, doc, metadata, access, user, jobs);
    }

    /// <summary>Екземпляр копії аркуша за період з тим самим Fixed-рядком (як при першому відкритті копії).</summary>
    private async Task<long> AddCopyInstanceAsync(TestDocument doc, long sheetCopyId)
    {
        var loader = new BulkCellLoader(sql.ConnectionString, 1000);
        var instanceId = await loader.ReserveIdsAsync("doc.TableInstanceSeq", 1, CancellationToken.None);
        var rowId = await loader.ReserveIdsAsync("doc.TableRowSeq", 1, CancellationToken.None);

        await using var db = new EcrDbContext(
            new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);
        var rowKey = await db.TableRows.AsNoTracking()
            .Where(r => r.Id == doc.RowIds[0] && r.PeriodKeyValue == doc.PeriodKey.Value)
            .Select(r => r.RowKeyValue)
            .SingleAsync();

        db.TableInstances.Add(new TableInstance(doc.PeriodKey, instanceId, doc.DocumentId, doc.TableDefId, Now, sheetCopyId));
        db.TableRows.Add(new TableRow(doc.PeriodKey, rowId, instanceId, RowKey.Create(rowKey), 1, Now));
        await db.SaveChangesAsync();

        return rowId;
    }

    private static TemplateVersionSnapshot Snapshot(TestDocument doc, string expression)
    {
        var tag = doc.SheetCode["SHEET".Length..];

        var sheet = new SheetDef(doc.TemplateVersionId, EcrCode.Create(doc.SheetCode), Name("Vessel"), 1);
        SetId(sheet, doc.SheetDefId);
        sheet.SetCopyable(true);

        var table = new TableDef(doc.SheetDefId, EcrCode.Create($"TBL{tag}"), Name("Table"), 1,
            TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed);
        SetId(table, doc.TableDefId);

        var column = new ColumnDef(doc.TableDefId, EcrCode.Create($"C1_{tag}"), Name("Name"), 1, CellDataType.String);
        SetId(column, doc.ColumnDefIds[0]);
        table.AddColumn(column);

        var row = new RowDef(doc.TableDefId, RowKey.Create($"R1_{tag}"), 1, Name("Row 1"), RowKind.Item);
        SetId(row, doc.RowDefIds[0]);
        table.AddRow(row);

        var formula = new FormulaDef(table.Id, FormulaScope.Column, expression, ExpressionDialect.Template);
        SetId(formula, FormulaId);
        formula.AssignColumn(column.Id);
        formula.SetEvaluationOrder(1);
        table.AddFormula(formula);

        sheet.AddTable(table);

        return new TemplateVersionSnapshot(
            doc.TemplateVersionId, 0, [sheet], new Dictionary<int, ColumnDef> { [column.Id] = column },
            new Dictionary<(int, string), RowDef> { [(table.Id, row.RowKeyValue)] = row });
    }

    private async Task<string?> TextAsync(TestDocument doc, long rowId)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT ValueString FROM doc.CellValue WHERE PeriodKey = {doc.PeriodKey.Value} " +
            $"AND TableRowId = {rowId} AND ColumnDefId = {doc.ColumnDefIds[0]}";
        var result = await command.ExecuteScalarAsync();
        return result is null or DBNull ? null : (string)result;
    }

    private static void SetId(object entity, int id)
        => typeof(Entity<int>).GetProperty("Id")!.SetValue(entity, id);

    private static LocalizedText Name(string value)
        => new(new Dictionary<string, string> { ["en"] = value });

    private sealed class World(
        TestDocumentBuilder builder, TestDocument doc, IMetadataCache metadata, IAccessDecisionService access,
        ICurrentUser user, IBackgroundJobScheduler jobs)
    {
        public TestDocument Doc { get; } = doc;

        public IBackgroundJobScheduler Jobs { get; } = jobs;

        /// <summary>Новий контекст і обробник на КОЖЕН виклик — як на кожен HTTP-запит.</summary>
        public async Task<T> Run<T>(Func<DocumentSheetCopyHandler, Task<T>> action)
        {
            await using var db = builder.CreateContext();
            return await action(Handler(db)).ConfigureAwait(false);
        }

        public async Task Run(Func<DocumentSheetCopyHandler, Task> action)
        {
            await using var db = builder.CreateContext();
            await action(Handler(db)).ConfigureAwait(false);
        }

        /// <summary>Відкриття документа за період (<c>GET …/tables</c>): ліниве створення екземплярів.</summary>
        public async Task OpenTablesAsync()
        {
            await using var db = builder.CreateContext();
            var clock = new FixedClock();
            var handler = new GetDocumentTablesHandler(
                new RowStore(db, new BulkCellLoader(db.Database.GetConnectionString()!, 1000), clock),
                Substitute.For<IPermitBindingStore>(), metadata, access, user, new DocumentSheetCopyStore(db), Jobs);
            await handler.HandleAsync(Doc.DocumentId, Doc.PeriodKey.Value, CancellationToken.None);
        }

        /// <summary>Те, що робить <c>RecalculationJob</c> з payload постановки: повний прогін аркуша за період.</summary>
        public async Task RecalculateSheetAsync()
        {
            await using var db = builder.CreateContext();
            await Service(db).RecalculateAllAsync(Doc.DocumentId, Doc.PeriodKey, CancellationToken.None, Doc.SheetDefId);
        }

        /// <summary>Справжній <c>RecalculationJob</c> над цим документом (методологій немає — оркестратор порожній).</summary>
        public async Task<List<string>> RunRecalculationJobAsync(object? payload, ICalculationRunner? calculationRunner = null)
        {
            await using var db = builder.CreateContext();
            var clock = new FixedClock();
            var runner = calculationRunner ?? new EmptyRunner();
            var runs = new RunCalculationHandler(
                Substitute.For<IPeriodStore>(), Substitute.For<IWorkflowStore>(), new CalculationResultStore(db, clock),
                Substitute.For<IBackgroundJobScheduler>(), new UnitOfWork(db), Substitute.For<IAccessDecisionService>(),
                Substitute.For<ICurrentUser>(), clock, Substitute.For<IAuditWriter>());

            var messages = new List<string>();
            var progress = Substitute.For<IJobProgress>();
            progress.ReportAsync(Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    messages.Add(call.ArgAt<string?>(1) ?? string.Empty);
                    return Task.CompletedTask;
                });

            var job = new RecalculationJob(
                db, runner, runs, Service(db), clock, sheetGate: new SheetEditGate(db), audit: new AuditWriter(db));
            await job.ExecuteAsync(payload, progress, CancellationToken.None);
            return messages;
        }

        private DocumentSheetCopyHandler Handler(EcrDbContext db)
            => new(
                new DocumentStore(db), new DocumentSheetCopyStore(db), metadata, new SheetEditGate(db), access,
                new UnitOfWork(db), new AuditWriter(db), user, new FixedClock(), Jobs, new PeriodStore(db));

        private RecalculationService Service(EcrDbContext db)
        {
            var clock = new FixedClock();
            var periods = Substitute.For<IPeriodStore>();
            periods.FindPeriodBoundsAsync(Doc.DocumentId, Doc.PeriodKey.Value, Arg.Any<CancellationToken>())
                   .Returns(new PeriodBounds(new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31)));
            periods.FindPeriodStateAsync(Doc.DocumentId, Doc.PeriodKey.Value, Arg.Any<CancellationToken>())
                   .Returns((PeriodState?)PeriodState.Open);

            var versions = Substitute.For<ITemplateVersionStore>();
            versions.ListFormulaDependenciesAsync(Doc.TemplateVersionId, Arg.Any<CancellationToken>()).Returns([]);

            var units = Substitute.For<IUnitCatalog>();
            units.GetAsync(Arg.Any<CancellationToken>()).Returns(UnitCatalogSnapshot.Empty);

            var headers = Substitute.For<IDocumentHeaderStore>();
            headers.GetExpressionValuesAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
                   .Returns(new Dictionary<string, ExpressionValue>());

            return new RecalculationService(
                new NormalizedCellStore(db), new RowStore(db, new BulkCellLoader(db.Database.GetConnectionString()!, 1000), clock),
                periods, metadata, versions, new RealFormulaEngine(), units, Substitute.For<IRegistryStore>(), headers,
                new AuditWriter(db), clock, new UnitOfWork(db), new SheetEditGate(db),
                sheetCopies: new DocumentSheetCopyStore(db));
        }
    }

    /// <summary>Оркестратор, що лише запам'ятовує екземпляри, на яких рахувались прив'язки.</summary>
    private sealed class RecordingRunner : ICalculationRunner
    {
        public List<long> Instances { get; } = [];

        public Task<ModuleProfile> RunAsync(
            long calculationRunId, long documentId, PeriodKey periodKey,
            IReadOnlyList<CalculationBindingRef> bindings, IJobProgress progress, CancellationToken ct)
        {
            Instances.AddRange(bindings.Select(b => b.TableInstanceId));
            return Task.FromResult(new ModuleProfile());
        }
    }

    /// <summary>Оркестратор методологій без прив'язок: прогін методологій порожній.</summary>
    private sealed class EmptyRunner : ICalculationRunner
    {
        public Task<ModuleProfile> RunAsync(
            long calculationRunId, long documentId, PeriodKey periodKey,
            IReadOnlyList<CalculationBindingRef> bindings, IJobProgress progress, CancellationToken ct)
            => Task.FromResult(new ModuleProfile());
    }

    private sealed class FixedClock : IClock
    {
        public DateTime UtcNow { get; } = Now;
    }
}
