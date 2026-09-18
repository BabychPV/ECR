// tests/Ecr.Infrastructure.Tests/Persistence/PatchCellsRegistryLookupTests.cs
using Ecr.Application.Common;
using Ecr.Application.Documents;
using Ecr.Application.Documents.Dto;
using Ecr.Application.Errors;
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
/// Директива registry-lookup, PR A2: наскрізний доказ на реальному SQL
/// Server, з РЕАЛЬНИМ <see cref="RegistryStore"/> (не заглушкою) — патч
/// комірки <c>Lookup</c> із неіснуючим записом довідника має дати чисту
/// бізнес-помилку, а не сире порушення <c>FK_CellValue_Entry</c> (`Q-316`).
/// </summary>
/// <remarks>
/// Той самий прийом підміни метаданих/прав/періоду, що вже
/// <c>PatchCellsAtomicityTests</c> (Q-243): вони НЕ пишуть у транзакцію, що
/// перевіряється. `RegistryStore` — єдина залежність, чия РЕАЛЬНІСТЬ тут
/// має значення: саме вона робить запит "чи існує id" до справжньої бази.
/// </remarks>
[Collection("SqlServer")]
public sealed class PatchCellsRegistryLookupTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Патч_Lookup_комірки_з_неіснуючим_записом_дає_чисту_помилку_а_не_500()
    {
        var doc = await new TestDocumentBuilder(sql.ConnectionString)
            .BuildAsync(columnCount: 3, ct: CancellationToken.None);
        var realRowKey = await FirstRowKeyAsync(doc);
        var baseVersion = await RowVersionBase64Async(doc, realRowKey);

        await using var db = CreateContext();
        var clock = new FixedClock(new DateTime(2026, 2, 1, 9, 0, 0, DateTimeKind.Utc));
        var bulk = new BulkCellLoader(sql.ConnectionString, 1000);

        var handler = BuildHandler(
            new NormalizedCellStore(db), new RowStore(db, bulk, clock), new DocumentStore(db),
            new AuditWriter(db), new UnitOfWork(db), new RegistryStore(db), clock, doc);

        const long nonExistentEntryId = 999_999_999;
        var request = new PatchCellsRequest(doc.TableInstanceId, doc.PeriodKey.Value, "UserEdit",
            [new PatchRow(realRowKey, baseVersion, [new PatchCell(CodeOf(doc, 3), nonExistentEntryId)])]);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => handler.HandleAsync(request, CancellationToken.None));

        Assert.Equal("ECR-CELL-4223", error.ErrorCode);

        // ⛔ Нічого не записано: перевірка існування спрацювала ДО запису, не
        // ПІСЛЯ невдалої спроби (а раз так — жодного `SqlException` про
        // порушення FK не могло статися взагалі).
        var written = await ScalarOrNullAsync<long?>(
            $"SELECT ValueRegistryEntryId FROM doc.CellValue WHERE PeriodKey = {doc.PeriodKey.Value} " +
            $"AND TableRowId = (SELECT TOP 1 Id FROM doc.TableRow WHERE TableInstanceId = {doc.TableInstanceId} " +
            $"AND RowKey = '{realRowKey}') AND ColumnDefId = {doc.ColumnDefIds[2]}");
        Assert.Null(written);
    }

    private PatchCellsHandler BuildHandler(
        ICellStore cells, IRowStore rows, IDocumentStore documents, IAuditWriter audit, IUnitOfWork uow,
        IRegistryStore registries, IClock clock, TestDocument doc)
    {
        var column1 = ColumnDefFor(doc, 1, CellDataType.String);
        var column2 = ColumnDefFor(doc, 2, CellDataType.Decimal);
        var lookupColumn = ColumnDefFor(doc, 3, CellDataType.Lookup);
        lookupColumn.SetLookup(registryDefId: 1);

        var sheet = new SheetDef(doc.TemplateVersionId, EcrCode.Create($"SH{doc.SheetDefId}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Sheet" }), 1);
        var table = new TableDef(sheet.Id, EcrCode.Create($"TB{doc.TableDefId}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Table" }), 1,
            TableLayoutKind.PerPeriodInstance, TableRowMode.Dynamic);
        typeof(Entity<int>).GetProperty("Id")!.SetValue(table, doc.TableDefId);
        table.AddColumn(column1);
        table.AddColumn(column2);
        table.AddColumn(lookupColumn);
        sheet.AddTable(table);

        var snapshot = new TemplateVersionSnapshot(
            TemplateVersionId: doc.TemplateVersionId, PresentationRevision: 0, Sheets: [sheet],
            ColumnsById: new Dictionary<int, ColumnDef>
            {
                [doc.ColumnDefIds[0]] = column1,
                [doc.ColumnDefIds[1]] = column2,
                [doc.ColumnDefIds[2]] = lookupColumn,
            },
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
        // ⛔ Рішення на КОЖНУ пару (рядок, колонка) зрізу — саме так поводиться
        // справжній `AccessDecisionService.CanEditSliceAsync` (`:280-295`:
        // подвійний цикл по рядках і колонках, без пропусків). Тут стояв
        // ПОРОЖНІЙ словник, і тест проходив лише тому, що обробник трактував
        // відсутність рішення як дозвіл (`DAT-04`). Предмет цього файлу — не
        // права, тож передумова тепер названа явно, а не отримана з дефекту.
        access.CanEditSliceAsync(Arg.Any<AccessProfile>(), doc.TableInstanceId, Arg.Any<CancellationToken>())
              .Returns(doc.RowIds
                  .SelectMany(rowId => doc.ColumnDefIds
                      .Select(columnId => new CellAddress(doc.PeriodKey, rowId, columnId)))
                  .ToDictionary(address => address, _ => EditDecision.Allow()));
        access.CanCreateRowsAsync(
                  Arg.Any<AccessProfile>(), doc.TableInstanceId, Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
              .Returns(new Dictionary<string, NewRowAccess>());

        var jobs = Substitute.For<IBackgroundJobScheduler>();
        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(1);

        var methodologies = Substitute.For<IMethodologyStore>();
        methodologies.GetMethodologyIdsBoundToTableAsync(doc.TableDefId, Arg.Any<CancellationToken>())
                     .Returns(Task.FromResult<IReadOnlyList<int>>([]));

        return new PatchCellsHandler(
            cells, rows, documents, periods, metadata, access,
            new Ecr.Application.Validation.ValidationEngine(new RealFormulaEngine()),
            methodologies, registries, audit, jobs, uow, user, clock);
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

    private async Task<T> ScalarAsync<T>(string sqlText)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sqlText;
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private async Task<T?> ScalarOrNullAsync<T>(string sqlText)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sqlText;
        var result = await command.ExecuteScalarAsync();
        return result is null or DBNull ? default : (T)result;
    }

    private EcrDbContext CreateContext()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

    /// <summary>Годинник із фіксованим часом — детермінований <c>ModifiedAt</c>.</summary>
    private sealed class FixedClock(DateTime utcNow) : IClock
    {
        public DateTime UtcNow { get; } = utcNow;
    }
}
