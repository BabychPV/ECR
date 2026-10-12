using Ecr.Application.Documents;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// D3-A: м'яке видалення динамічного рядка (<c>DELETE …/tables/{id}/rows/{rowKey}</c>,
/// <see cref="DeleteRowHandler"/>) на справжньому SQL Server.
/// </summary>
/// <remarks>
/// ⛔ Що доводить набір: (1) видалений рядок зникає з УСІХ читачів, зокрема з
/// <c>ArchiveAwareCellReader</c> (там бракувало <c>!IsDeleted</c> — комірки видаленого рядка
/// лишались у зрізі); (2) ключ видаленого рядка зайнятий — відмова <c>ECR-ROW-0409</c>, а не виняток
/// індексу; (3) номер нового рядка = max + 1 і не дублює наявний; (4) «видалено» й «ніколи не було» —
/// одна відповідь; (5) слід у <c>aud.StructureChange</c> зі складом комірок ДО видалення; (6) поданий
/// аркуш і рядок шаблону не видаляються.
/// </remarks>
[Collection("SqlServer")]
public sealed class DeleteRowTests(SqlServerFixture sql)
{
    private const int UserId = 1;
    private const int Rows = 4;

    private static readonly DateTime Now = new(2026, 2, 1, 9, 0, 1, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.6")]
    public async Task Видалений_рядок_зникає_з_читачів_лишає_слід_і_ставить_перерахунок()
    {
        var doc = await ArrangeAsync();
        var key = RowKeyOf(doc, 2);
        var rowId = doc.RowIds[1];
        await InsertCellAsync(doc, rowId, column: 2, value: 777.25m);
        await InsertCellAsync(doc, doc.RowIds[0], column: 2, value: 7);

        var (handler, jobs) = DeleteHandler(doc);
        var result = await handler.HandleAsync(doc.DocumentId, doc.TableInstanceId, key, Profile(), CancellationToken.None);

        // М'яко: рядок і його комірки лишились у базі, але позначені.
        Assert.Equal(1, await ScalarAsync<int>(
            $"SELECT CAST(IsDeleted AS int) FROM doc.TableRow WHERE PeriodKey = {doc.PeriodKey.Value} AND Id = {rowId}"));
        Assert.Equal(1, await ScalarAsync<int>(
            $"SELECT COUNT(*) FROM doc.CellValue WHERE PeriodKey = {doc.PeriodKey.Value} AND TableRowId = {rowId}"));

        // Читачі не бачать рядка.
        await using var db = CreateContext();
        var bulk = new BulkCellLoader(sql.ConnectionString, 1000);
        var rowStore = new RowStore(db, bulk, new FixedClock(Now));
        Assert.DoesNotContain(key, (await rowStore.GetRowIdsAsync(doc.TableInstanceId, doc.PeriodKey, default)).Keys);
        Assert.DoesNotContain(key, (await rowStore.GetRowVersionsAsync(doc.TableInstanceId, doc.PeriodKey, default)).Keys);
        Assert.DoesNotContain(rowId, (await new NormalizedCellStore(db).ReadSliceAsync(doc.TableInstanceId, doc.PeriodKey, default))
            .Select(c => c.Address.TableRowId));

        // ⛔ Саме цей читач не мав фільтра: комірки видаленого рядка лишались у його відповіді.
        var hot = await new ArchiveAwareCellReader(db).ReadAsync(doc.ProjectId, doc.TableInstanceId, doc.PeriodKey, default);
        Assert.DoesNotContain(rowId, hot.Select(c => c.Address.TableRowId));
        Assert.Contains(doc.RowIds[0], hot.Select(c => c.Address.TableRowId));

        // Слід: склад комірок ДО видалення, автор, тип сутності — БЕЗ значень (P2-1).
        Assert.Equal(1, await ScalarAsync<int>(
            "SELECT COUNT(*) FROM aud.StructureChange WHERE EntityType = N'doc.TableRow' AND Operation = N'RowDeleted' " +
            $"AND ChangedByUserId = {UserId} AND OldJson LIKE N'%{key}%' AND OldJson LIKE N'%columnDefId%' " +
            "AND OldJson NOT LIKE N'%777.25%'"));

        // Перерахунок поставлено, насіння — усі колонки видаленого рядка.
        Assert.Equal("job-1", result.RecalculationJobId);
        var call = jobs.ReceivedCalls().Single(c => c.GetMethodInfo().Name == nameof(IBackgroundJobScheduler.EnqueueCoalescedAsync));
        var payload = System.Text.Json.JsonSerializer.Serialize(call.GetArguments()[1]);
        foreach (var column in doc.ColumnDefIds)
        {
            Assert.Contains($"\"RowId\":{rowId},\"ColumnDefId\":{column}", payload, StringComparison.Ordinal);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.6")]
    public async Task Ключ_видаленого_рядка_зайнятий_і_відмова_зрозуміла_а_не_виняток_індексу()
    {
        var doc = await ArrangeAsync();
        var key = RowKeyOf(doc, 3);
        await DeleteHandler(doc).Handler.HandleAsync(doc.DocumentId, doc.TableInstanceId, key, Profile(), default);

        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => CreateHandler(doc).HandleAsync(
            doc.DocumentId, doc.TableInstanceId, RowKey.Create(key), Profile(), default));

        Assert.Equal("ECR-ROW-0409", ex.ErrorCode);
        Assert.Equal("err.ECR-ROW-0409.rowKeyExists", ex.Details!["messageKey"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-3.2")]
    public async Task Номер_нового_рядка_після_видалення_не_дублює_наявний()
    {
        var doc = await ArrangeAsync();
        await DeleteHandler(doc).Handler.HandleAsync(doc.DocumentId, doc.TableInstanceId, RowKeyOf(doc, 2), Profile(), default);

        var created = await CreateHandler(doc).HandleAsync(
            doc.DocumentId, doc.TableInstanceId, requestedKey: null, Profile(), default);

        // Рядки 1..4, видалено №2: «живих + 1» = 4 повторив би номер рядка №4.
        var duplicates = await ScalarAsync<int>(
            "SELECT COUNT(*) FROM (SELECT Ordinal FROM doc.TableRow " +
            $"WHERE PeriodKey = {doc.PeriodKey.Value} AND TableInstanceId = {doc.TableInstanceId} " +
            "GROUP BY Ordinal HAVING COUNT(*) > 1) d");
        var ordinal = await ScalarAsync<int>(
            $"SELECT Ordinal FROM doc.TableRow WHERE PeriodKey = {doc.PeriodKey.Value} " +
            $"AND TableInstanceId = {doc.TableInstanceId} AND RowKey = N'{created.Value}'");

        Assert.Equal(0, duplicates);
        Assert.Equal(Rows + 1, ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.6")]
    public async Task Видалений_і_ніколи_не_існуючий_рядок_відповідають_однаково()
    {
        var doc = await ArrangeAsync();
        var key = RowKeyOf(doc, 1);
        await DeleteHandler(doc).Handler.HandleAsync(doc.DocumentId, doc.TableInstanceId, key, Profile(), default);

        var again = await Assert.ThrowsAsync<NotFoundException>(() => DeleteHandler(doc).Handler.HandleAsync(
            doc.DocumentId, doc.TableInstanceId, key, Profile(), default));
        var never = await Assert.ThrowsAsync<NotFoundException>(() => DeleteHandler(doc).Handler.HandleAsync(
            doc.DocumentId, doc.TableInstanceId, "NEVER_EXISTED", Profile(), default));

        Assert.Equal("ECR-ROW-0404", again.ErrorCode);
        Assert.Equal(never.ErrorCode, again.ErrorCode);
        Assert.Equal(never.Message, again.Message);
        Assert.Equal(
            never.Details!.Select(p => (p.Key, p.Value?.ToString())).OrderBy(p => p.Key),
            again.Details!.Select(p => (p.Key, p.Value?.ToString())).OrderBy(p => p.Key));

        // PATCH бачить саме ці словники: ключа немає ні в «видаленого», ні в «неіснуючого» → однакова відмова.
        await using var db = CreateContext();
        var rowStore = new RowStore(db, new BulkCellLoader(sql.ConnectionString, 1000), new FixedClock(Now));
        var versions = await rowStore.GetRowVersionsAsync(doc.TableInstanceId, doc.PeriodKey, default);
        Assert.False(versions.ContainsKey(key));
        Assert.False(versions.ContainsKey("NEVER_EXISTED"));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-9.4")]
    public async Task Поданий_аркуш_рядок_не_видаляє()
    {
        var doc = await ArrangeAsync();
        await using (var db = CreateContext())
        {
            var state = new ApprovalState(doc.DocumentId, doc.SheetDefId, doc.PeriodKey.Value);
            state.Submit(UserId, Now);
            db.ApprovalStates.Add(state);
            await db.SaveChangesAsync();
        }

        var ex = await Assert.ThrowsAsync<AccessDeniedException>(() => DeleteHandler(doc).Handler.HandleAsync(
            doc.DocumentId, doc.TableInstanceId, RowKeyOf(doc, 2), Profile(), default));

        Assert.Equal("ECR-ACCS-0403", ex.ErrorCode);
        Assert.Equal("err.ECR-ACCS-0403.deleteRowDenied", ex.Details!["messageKey"]);
        Assert.Equal(nameof(EditDenyReason.DocumentSubmitted), ex.Details["reason"]);
        Assert.Equal(0, await ScalarAsync<int>(
            $"SELECT COUNT(*) FROM doc.TableRow WHERE PeriodKey = {doc.PeriodKey.Value} AND TableInstanceId = {doc.TableInstanceId} AND IsDeleted = 1"));
        Assert.Equal(0, await ScalarAsync<int>("SELECT COUNT(*) FROM aud.StructureChange WHERE EntityType = N'doc.TableRow' AND OldJson LIKE N'%" + RowKeyOf(doc, 2) + "%'"));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-3.2")]
    public async Task Рядок_із_опису_шаблону_не_видаляється()
    {
        var doc = await ArrangeAsync();
        var rowId = doc.RowIds[0];
        await ExecuteAsync(
            $"UPDATE doc.TableRow SET RowDefId = {doc.RowDefIds[0]} WHERE PeriodKey = {doc.PeriodKey.Value} AND Id = {rowId}");

        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => DeleteHandler(doc).Handler.HandleAsync(
            doc.DocumentId, doc.TableInstanceId, RowKeyOf(doc, 1), Profile(), default));

        Assert.Equal("ECR-ROW-0409", ex.ErrorCode);
        Assert.Equal("err.ECR-ROW-0409.templateRowNotDeletable", ex.Details!["messageKey"]);
        Assert.Equal(0, await ScalarAsync<int>(
            $"SELECT CAST(IsDeleted AS int) FROM doc.TableRow WHERE PeriodKey = {doc.PeriodKey.Value} AND Id = {rowId}"));
    }

    // ── Побудова ─────────────────────────────────────────────────────────────

    private async Task<TestDocument> ArrangeAsync()
        => await new TestDocumentBuilder(sql.ConnectionString)
            .BuildAsync(columnCount: 3, rowCount: Rows, rowMode: TableRowMode.Dynamic, ct: CancellationToken.None);

    private static string RowKeyOf(TestDocument doc, int ordinal)
        => $"R{ordinal}_{doc.SheetCode["SHEET".Length..]}";

    private (DeleteRowHandler Handler, IBackgroundJobScheduler Jobs) DeleteHandler(TestDocument doc)
    {
        var db = CreateContext();
        var clock = new FixedClock(Now);
        var jobs = Substitute.For<IBackgroundJobScheduler>();
        jobs.EnlistsInCallerTransaction.Returns(false);
        jobs.EnqueueCoalescedAsync<IFormulaRecalculationJob>(
                Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<CancellationToken>(), Arg.Any<int?>())
            .Returns("job-1");

        var handler = new DeleteRowHandler(
            new RowStore(db, new BulkCellLoader(sql.ConnectionString, 1000), clock), new DocumentStore(db),
            Metadata(doc), Access(db, doc), new NormalizedCellStore(db), new AuditWriter(db), jobs,
            new UnitOfWork(db), clock, new SheetEditGate(db));

        return (handler, jobs);
    }

    private CreateRowHandler CreateHandler(TestDocument doc)
    {
        var db = CreateContext();
        var clock = new FixedClock(Now);

        return new CreateRowHandler(
            new RowStore(db, new BulkCellLoader(sql.ConnectionString, 1000), clock), new DocumentStore(db),
            Metadata(doc), Access(db, doc), new UnitOfWork(db), clock, new SheetEditGate(db));
    }

    /// <summary>Служба доступу: документ видимий, рядки дозволені (стан аркуша перевіряє сам обробник під замком).</summary>
    private static IAccessDecisionService Access(EcrDbContext db, TestDocument doc)
    {
        _ = db;
        var access = Substitute.For<IAccessDecisionService>();
        access.CanReadDocumentAsync(Arg.Any<AccessProfile>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
              .Returns(EditDecision.Allow());
        access.CanCreateRowsAsync(
                  Arg.Any<AccessProfile>(), doc.TableInstanceId,
                  Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
              .Returns(call => (IReadOnlyDictionary<string, NewRowAccess>)call.ArgAt<IReadOnlyCollection<string>>(2)
                  .ToDictionary(
                      k => k, _ => new NewRowAccess(EditDecision.Allow(), new Dictionary<int, EditDecision>()),
                      StringComparer.Ordinal));

        // P1-2: рішення на значення комірок рядка — дозволено (колонкові заборони доводить DeleteRowGuardsTests).
        access.CanEditCellsAsync(
                  Arg.Any<AccessProfile>(), doc.TableInstanceId, Arg.Any<PeriodKey>(),
                  Arg.Any<IReadOnlyCollection<CellAddress>>(), Arg.Any<CancellationToken>())
              .Returns(call => (IReadOnlyDictionary<CellAddress, EditDecision>)call
                  .ArgAt<IReadOnlyCollection<CellAddress>>(3).ToDictionary(a => a, _ => EditDecision.Allow()));

        return access;
    }

    private static IMetadataCache Metadata(TestDocument doc)
    {
        var sheet = new SheetDef(doc.TemplateVersionId, EcrCode.Create($"SH{doc.SheetDefId}"), Name("Sheet"), 1);
        SetId(sheet, doc.SheetDefId);
        var table = new TableDef(doc.SheetDefId, EcrCode.Create($"TB{doc.TableDefId}"), Name("Table"), 1,
            TableLayoutKind.PerPeriodInstance, TableRowMode.Dynamic);
        SetId(table, doc.TableDefId);

        for (var i = 0; i < doc.ColumnDefIds.Count; i++)
        {
            var column = new ColumnDef(
                doc.TableDefId, EcrCode.Create($"COL{i + 1}"), Name($"Col {i + 1}"), i + 1,
                i == 0 ? CellDataType.String : CellDataType.Decimal);
            SetId(column, doc.ColumnDefIds[i]);
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

    private static void SetId(object entity, int id) => typeof(Entity<int>).GetProperty("Id")!.SetValue(entity, id);

    private static LocalizedText Name(string value) => new(new Dictionary<string, string> { ["en"] = value });

    private static AccessProfile Profile() => new()
    {
        CacheKey = "p", UserId = UserId, SecurityStamp = "s",
        Permissions = new HashSet<string>(), Grants = new Dictionary<string, GrantLevel>(),
        Denies = new HashSet<string>(), RoleIds = new HashSet<int>(),
    };

    private Task InsertCellAsync(TestDocument doc, long rowId, int column, decimal value)
        => ExecuteAsync(
            "INSERT INTO doc.CellValue (PeriodKey, TableRowId, ColumnDefId, TableDefId, ValueNumeric, IsCalculated, IsEmpty) VALUES " +
            $"({doc.PeriodKey.Value}, {rowId}, {doc.ColumnDefIds[column - 1]}, {doc.TableDefId}, " +
            $"{value.ToString(System.Globalization.CultureInfo.InvariantCulture)}, 0, 0);");

    private EcrDbContext CreateContext()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

    private async Task ExecuteAsync(string command)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = command;
        await cmd.ExecuteNonQueryAsync();
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
