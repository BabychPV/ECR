using Ecr.Application.Common;
using Ecr.Application.Documents;
using Ecr.Application.Documents.Dto;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;
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
/// X8-05 (R6): видалення чернетки зафіксувалося, поки <c>PATCH …/cells</c> готував запит поза
/// транзакцією. Запис має відмовити <c>404 ECR-DOC-0404</c>, а не впасти на FK 547 (тобто 500,
/// який клієнт ще й повторює як минущий) і не лишити сиріт.
/// </summary>
/// <remarks>
/// ⚠ Гонка ДЕТЕРМІНОВАНА, тим самим прийомом, що в <see cref="PatchCellsRowLimitRaceTests"/>:
/// точка перемикання — <c>CanCreateRowsAsync</c> (обробник кличе її ПІСЛЯ побудови контексту й
/// ДО транзакції запису). Видалення — справжнє: власний контекст, власна транзакція, структура
/// документа ВИНЯТКОВО (як у <see cref="DeleteDocumentHandler"/>) і <see cref="DocumentDeletionStore"/>.
///
/// ⛔ Мутація: прибрати <c>_goneDocuments.Add</c> у <c>SheetEditGate.EnterStructureAsync</c> —
/// <c>EnsureUnchanged</c> пропускає <c>null</c>, <c>EnterEditAsync</c> без стану дає <c>Draft</c>,
/// і вставка нового <c>doc.TableRow</c> на видалений <c>doc.TableInstance</c> падає FK 547:
/// виняток уже не <see cref="NotFoundException"/>.
/// </remarks>
[Collection("SqlServer")]
public sealed class PatchCellsDocumentDeletedRaceTests(SqlServerFixture sql)
{
    private const int UserId = 1;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task PATCH_після_коміту_видалення_документа_дає_404_а_не_FK_547()
    {
        var doc = await new TestDocumentBuilder(sql.ConnectionString)
            .BuildAsync(rowCount: 2, rowMode: TableRowMode.Dynamic, ct: CancellationToken.None);

        var deleted = false;
        async Task DeleteDocument()
        {
            await using var deleter = CreateContext();
            await new UnitOfWork(deleter).ExecuteInTransactionAsync(
                async ct =>
                {
                    await new SheetEditGate(deleter).EnterStructureAsync(doc.DocumentId, exclusive: true, ct);
                    await new DocumentDeletionStore(deleter).DeleteAsync(doc.DocumentId, ct);
                },
                CancellationToken.None);
            deleted = true;
        }

        await using var writerDb = CreateContext();
        var writer = BuildHandler(writerDb, doc, DeleteDocument);

        var thrown = await Record.ExceptionAsync(
            () => writer.HandleAsync(NewRowRequest(doc, "DYN-GONE", 7m), CancellationToken.None));

        Assert.True(deleted, "Перемикання не спрацювало: видалення не відбулося у вікні перед записом.");
        var notFound = Assert.IsType<NotFoundException>(thrown);
        Assert.Equal(ErrorCodes.DocumentNotFound, notFound.ErrorCode);
        Assert.Equal("err.ECR-DOC-0404.document", notFound.Details?["messageKey"]?.ToString());

        // Жодних сиріт: ні документа, ні екземпляра, ні нового рядка.
        Assert.Equal(0, await ScalarAsync<int>($"SELECT COUNT(*) FROM doc.Document WHERE Id = {doc.DocumentId}"));
        Assert.Equal(0, await ScalarAsync<int>($"SELECT COUNT(*) FROM doc.TableInstance WHERE DocumentId = {doc.DocumentId}"));
        Assert.Equal(0, await ScalarAsync<int>(
            $"SELECT COUNT(*) FROM doc.TableRow WHERE TableInstanceId = {doc.TableInstanceId}"));
    }

    private static PatchCellsRequest NewRowRequest(TestDocument doc, string rowKey, decimal value)
        => new(doc.TableInstanceId, doc.PeriodKey.Value, "UserEdit",
            [new PatchRow(rowKey, BaseVersion: null, [new PatchCell(CodeOf(doc, 2), value)])]);

    /// <summary>
    /// Обробник на справжніх сховищах, <see cref="UnitOfWork"/> і <see cref="SheetEditGate"/>;
    /// <paramref name="beforePersist"/> — точка перемикання перед транзакцією запису.
    /// </summary>
    private static PatchCellsHandler BuildHandler(EcrDbContext db, TestDocument doc, Func<Task> beforePersist)
    {
        var clock = new FixedClock(new DateTime(2026, 2, 1, 9, 0, 2, DateTimeKind.Utc));
        var bulk = new BulkCellLoader(db.Database.GetConnectionString()!, 1000);

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
            ColumnsById: new Dictionary<int, ColumnDef> { [doc.ColumnDefIds[0]] = column1, [doc.ColumnDefIds[1]] = column2 },
            RowsByKey: new Dictionary<(int, string), RowDef>());

        var metadata = Substitute.For<IMetadataCache>();
        metadata.GetAsync(doc.TemplateVersionId, Arg.Any<CancellationToken>()).Returns(snapshot);

        var periods = Substitute.For<IPeriodStore>();
        periods.FindPeriodStateAsync(doc.DocumentId, doc.PeriodKey.Value, Arg.Any<CancellationToken>())
               .Returns((PeriodState?)PeriodState.Open);

        var access = Substitute.For<IAccessDecisionService>();
        access.BuildProfileAsync(UserId, Arg.Any<CancellationToken>()).Returns(Profile());
        access.CanReadDocumentAsync(Arg.Any<AccessProfile>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
              .Returns(EditDecision.Allow());

        // ⚠ Точка перемикання: останнє, що обробник питає ДО транзакції запису.
        access.CanCreateRowsAsync(
                  Arg.Any<AccessProfile>(), doc.TableInstanceId,
                  Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
              .Returns(async call =>
              {
                  await beforePersist().ConfigureAwait(false);
                  return (IReadOnlyDictionary<string, NewRowAccess>)call.ArgAt<IReadOnlyCollection<string>>(2)
                      .ToDictionary(
                          k => k, _ => new NewRowAccess(
                              EditDecision.Allow(),
                              doc.ColumnDefIds.ToDictionary(id => id, _ => EditDecision.Allow())),
                          StringComparer.Ordinal);
              });

        var methodologies = Substitute.For<IMethodologyStore>();
        methodologies.GetMethodologyIdsBoundToTableAsync(doc.TableDefId, Arg.Any<CancellationToken>())
                     .Returns(Task.FromResult<IReadOnlyList<int>>([]));

        var headers = Substitute.For<IDocumentHeaderStore>();
        headers.GetExpressionValuesAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
               .Returns(new Dictionary<string, ExpressionValue>());

        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(UserId);

        return new PatchCellsHandler(
            new NormalizedCellStore(db), new RowStore(db, bulk, clock), new DocumentStore(db), periods, metadata,
            access, new Ecr.Application.Validation.ValidationEngine(new RealFormulaEngine()),
            methodologies, Substitute.For<IRegistryStore>(), headers, new AuditWriter(db),
            Substitute.For<IAuditReader>(), Substitute.For<IBackgroundJobScheduler>(), new UnitOfWork(db), user,
            clock, new SheetEditGate(db), Substitute.For<IUnitCatalog>());
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

    private static AccessProfile Profile() => new()
    {
        CacheKey = "p", UserId = UserId, SecurityStamp = "s",
        Permissions = new HashSet<string>(), Grants = new Dictionary<string, GrantLevel>(),
        Denies = new HashSet<string>(), RoleIds = new HashSet<int>(),
    };

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
