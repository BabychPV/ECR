using Ecr.Application.Common;
using Ecr.Application.Documents;
using Ecr.Application.Documents.Dto;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
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
/// Q-243 (критичний, цілісність аудиту). Доказ мутацією: тест проганяється
/// ДО фікса (RED, доводить дефект) і ПІСЛЯ (GREEN, доводить атомарність) —
/// обидва прогони описані в <c>docs/build/questions/Q-243.md</c>.
/// </summary>
/// <remarks>
/// Реальний SQL Server, реальні <see cref="NormalizedCellStore"/>,
/// <see cref="RowStore"/>, <see cref="DocumentStore"/>, <see cref="AuditWriter"/>,
/// <see cref="UnitOfWork"/> — усе, що бере участь у транзакції
/// <c>PatchCellsHandler.PersistChangesAsync</c>. Метадані/права/період —
/// підмінені (той самий прийом, що вже <c>PatchCellsTests</c> в
/// Ecr.Application.Tests): вони НЕ пишуть у транзакцію, що перевіряється,
/// тож підміна не впливає на доказ атомарності.
///
/// Збій імітується декоратором <see cref="ThrowingCellStore"/> — кидає ОДРАЗУ
/// ПІСЛЯ реального <c>cellStore.ApplyAsync(...)</c>, точно як пропонує
/// директива аудиту: «insert a throw immediately after cellStore.ApplyAsync».
/// </remarks>
[Collection("SqlServer")]
public sealed class PatchCellsAtomicityTests(SqlServerFixture sql)
{
    private const string FaultMarker = "Q243_FAULT_INJECTION";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Збій_після_ApplyAsync_не_лишає_комірку_зміненою_без_аудиту()
    {
        var doc = await new TestDocumentBuilder(sql.ConnectionString)
            .BuildAsync(rowMode: TableRowMode.Dynamic, ct: CancellationToken.None);
        var realRowKey = await FirstRowKeyAsync(doc);
        var baseVersion = await RowVersionBase64Async(doc, realRowKey);

        await using var db = CreateContext();
        var clock = new FixedClock(new DateTime(2026, 2, 1, 9, 0, 0, DateTimeKind.Utc));
        var bulk = new BulkCellLoader(sql.ConnectionString, 1000);

        var realCellStore = new NormalizedCellStore(db);
        var rowStore = new RowStore(db, bulk, clock);
        var documentStore = new DocumentStore(db);
        var auditWriter = new AuditWriter(db);
        var uow = new UnitOfWork(db);

        var handler = BuildHandler(
            new ThrowingCellStore(realCellStore), rowStore, documentStore, auditWriter, uow, clock, doc);

        var request = new PatchCellsRequest(doc.TableInstanceId, doc.PeriodKey.Value, "UserEdit",
            [new PatchRow(realRowKey, baseVersion, [new PatchCell(CodeOf(doc, 2), 777m)])]);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => handler.HandleAsync(request, CancellationToken.None));
        Assert.Contains(FaultMarker, thrown.Message, StringComparison.Ordinal);

        // ⛔ Доказ атомарності: комірка НЕ повинна лишитись зміненою (rollback
        // усього блоку), і аудиту цієї зміни НЕ повинно бути — обидва або
        // разом, або жоден. До фіксу (стара `NormalizedCellStore.ApplyAsync`,
        // яка сама відкриває й комітить ВЛАСНУ транзакцію) перше твердження
        // падає: комірка змінюється НАСПРАВДІ до того, як декоратор кидає
        // виняток, бо коміт уже стався всередині ApplyAsync.
        var cellValue = await ScalarOrNullAsync<decimal?>(
            $"SELECT ValueNumeric FROM doc.CellValue WHERE PeriodKey = {doc.PeriodKey.Value} " +
            $"AND TableRowId = (SELECT TOP 1 Id FROM doc.TableRow WHERE TableInstanceId = {doc.TableInstanceId} " +
            $"AND RowKey = '{realRowKey}') AND ColumnDefId = {doc.ColumnDefIds[1]}");
        Assert.True(
            cellValue is null or not 777m,
            "Дефект Q-243: комірка змінилась (закомічена), хоча весь батч мав відкотитися повністю.");

        var auditCount = await ScalarAsync<int>(
            $"SELECT COUNT(*) FROM aud.CellChange WHERE DocumentId = {doc.DocumentId} " +
            $"AND ColumnDefId = {doc.ColumnDefIds[1]} AND NewValue = '777'");
        Assert.Equal(0, auditCount);
    }

    private PatchCellsHandler BuildHandler(
        ICellStore cells, IRowStore rows, IDocumentStore documents, IAuditWriter audit, IUnitOfWork uow,
        IClock clock, TestDocument doc)
    {
        var column1 = ColumnDefFor(doc, 1, CellDataType.String);
        var column2 = ColumnDefFor(doc, 2, CellDataType.Decimal);

        var sheet = new SheetDef(doc.TemplateVersionId, EcrCode.Create($"SH{doc.SheetDefId}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Sheet" }), 1);
        var table = new TableDef(sheet.Id, EcrCode.Create($"TB{doc.TableDefId}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Table" }), 1,
            TableLayoutKind.PerPeriodInstance, TableRowMode.Dynamic);
        typeof(Ecr.Domain.Abstractions.Entity<int>).GetProperty("Id")!.SetValue(table, doc.TableDefId);
        table.AddColumn(column1);
        table.AddColumn(column2);
        sheet.AddTable(table);

        var snapshot = new Domain.Entities.Configuration.TemplateVersionSnapshot(
            TemplateVersionId: doc.TemplateVersionId, PresentationRevision: 0, Sheets: [sheet],
            ColumnsById: new Dictionary<int, ColumnDef> { [doc.ColumnDefIds[0]] = column1, [doc.ColumnDefIds[1]] = column2 },
            RowsByKey: new Dictionary<(int, string), RowDef>());

        var metadata = Substitute.For<IMetadataCache>();
        metadata.GetAsync(doc.TemplateVersionId, Arg.Any<CancellationToken>()).Returns(snapshot);

        var periods = Substitute.For<IPeriodStore>();
        periods.FindPeriodStateAsync(doc.DocumentId, doc.PeriodKey.Value, Arg.Any<CancellationToken>())
               .Returns((PeriodState?)PeriodState.Open);

        var access = Substitute.For<IAccessDecisionService>();
        var profile = new AccessProfile
        {
            CacheKey = "p", UserId = 1, SecurityStamp = "s",
            Permissions = new HashSet<string>(), Grants = new Dictionary<string, GrantLevel>(),
            Denies = new HashSet<string>(), RoleIds = new HashSet<int>(),
        };
        access.BuildProfileAsync(1, Arg.Any<CancellationToken>()).Returns(profile);
        access.CanEditSliceAsync(Arg.Any<AccessProfile>(), doc.TableInstanceId, Arg.Any<CancellationToken>())
              .Returns(new Dictionary<CellAddress, EditDecision>());
        access.CanCreateRowsAsync(
                  Arg.Any<AccessProfile>(), doc.TableInstanceId, Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
              .Returns(new Dictionary<string, NewRowAccess>());

        var jobs = Substitute.For<IBackgroundJobScheduler>();
        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(1);

        return new PatchCellsHandler(
            cells, rows, documents, periods, metadata, access,
            new Ecr.Application.Validation.ValidationEngine(new RealFormulaEngine()),
            audit, jobs, uow, user, clock);
    }

    private static ColumnDef ColumnDefFor(TestDocument doc, int ordinal, CellDataType type)
    {
        var id = doc.ColumnDefIds[ordinal - 1];
        var column = new ColumnDef(doc.TableDefId, EcrCode.Create($"C{ordinal}_{id}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = $"Col{ordinal}" }), ordinal, type);
        typeof(Ecr.Domain.Abstractions.Entity<int>).GetProperty("Id")!.SetValue(column, id);
        return column;
    }

    private static string CodeOf(TestDocument doc, int ordinal) => $"C{ordinal}_{doc.ColumnDefIds[ordinal - 1]}";

    private async Task<string> FirstRowKeyAsync(TestDocument doc)
        => await ScalarAsync<string>(
            $"SELECT TOP 1 RowKey FROM doc.TableRow WHERE TableInstanceId = {doc.TableInstanceId} ORDER BY Id");

    private async Task<string> RowVersionBase64Async(TestDocument doc, string rowKey)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT RowVersion FROM doc.TableRow WHERE TableInstanceId = {doc.TableInstanceId} " +
            $"AND RowKey = '{rowKey}'";
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

    /// <summary>Годинник із фіксованим часом — детермінований <c>ModifiedAt</c>.</summary>
    private sealed class FixedClock(DateTime utcNow) : IClock
    {
        public DateTime UtcNow { get; } = utcNow;
    }

    /// <summary>
    /// Декоратор, що кидає ОДРАЗУ після реального <c>ApplyAsync</c> — та сама
    /// точка збою, яку пропонує директива аудиту Q-243, щоб довести дефект і
    /// фікс без потреби рвати справжнє з'єднання з базою.
    /// </summary>
    private sealed class ThrowingCellStore(ICellStore inner) : ICellStore
    {
        public Task<IReadOnlyList<CellRecord>> ReadSliceAsync(long tableInstanceId, CancellationToken ct)
            => inner.ReadSliceAsync(tableInstanceId, ct);

        public Task<IReadOnlyDictionary<long, IReadOnlyList<CellRecord>>> ReadSlicesAsync(
            IReadOnlyList<long> tableInstanceIds, CancellationToken ct)
            => inner.ReadSlicesAsync(tableInstanceIds, ct);

        public Task<IReadOnlyDictionary<CellAddress, CellValueData>> ReadCellsAsync(
            IReadOnlyCollection<CellAddress> addresses, CancellationToken ct)
            => inner.ReadCellsAsync(addresses, ct);

        public async Task ApplyAsync(CellChangeSet changes, CancellationToken ct)
        {
            await inner.ApplyAsync(changes, ct).ConfigureAwait(false);
            throw new InvalidOperationException(FaultMarker);
        }
    }
}
