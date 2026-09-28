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
using Ecr.Expressions.Evaluation;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// C3b: два одночасні <c>PATCH …/cells</c>, кожен зі своїм НОВИМ рядком у
/// Dynamic-таблиці зі стелею <c>MaxDynamicRows</c>.
/// </summary>
/// <remarks>
/// ⛔ Інваріант: одночасні батчі, що створюють рядки, не перевищують
/// <c>MaxDynamicRows</c>. Доти стеля перевірялася ДО транзакції
/// (<c>EnforceRowCreationRules</c>, число рядків прочитане під RCSI), а
/// транзакція брала лише СПІЛЬНЕ блокування аркуша, сумісне саме з собою, — і
/// обидва батчі вставляли свій рядок.
///
/// ⚠ Гонка ДЕТЕРМІНОВАНА: обидва запити зустрічаються в точці перемикання —
/// <c>CanCreateRowsAsync</c> (обробник кличе його ПІСЛЯ швидкої перевірки
/// стелі й ДО транзакції запису). Зустріч поза блокуванням, тож із фіксом вона
/// не зависає: обидва проходять її, а далі транзакції серіалізуються
/// винятковим блокуванням і другий перераховує рядки вже під ним.
///
/// ⚠ <c>Ordinal</c> тут НЕ перевіряється: PATCH і до `C3b` давав усім новим
/// рядкам <c>Ordinal = 0</c> (<c>MaterializeNewRowsAsync</c>), тобто
/// «незадвоєний ordinal» для цього шляху не був інваріантом і тест його не
/// вигадує.
/// </remarks>
[Collection("SqlServer")]
public sealed class PatchCellsRowLimitRaceTests(SqlServerFixture sql)
{
    private const int UserId = 1;

    /// <summary>Скільки рядків заводить будівник.</summary>
    private const int SeededRows = 4;

    /// <summary>Скільки перший запит чекає на другого в точці зустрічі.</summary>
    private static readonly TimeSpan RendezvousWindow = TimeSpan.FromSeconds(3);

    /// <summary>Стеля на будь-яке очікування — щоб зламаний тест падав, а не висів.</summary>
    private static readonly TimeSpan HookTimeout = TimeSpan.FromSeconds(60);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-3.2")]
    public async Task Одночасні_PATCH_із_новими_рядками_не_перевищують_MaxDynamicRows()
    {
        var doc = await new TestDocumentBuilder(sql.ConnectionString)
            .BuildAsync(rowCount: SeededRows, rowMode: TableRowMode.Dynamic, ct: CancellationToken.None);

        // Місце рівно на ОДИН рядок.
        const int max = SeededRows + 1;

        var arrived = 0;
        var bothArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task Rendezvous()
        {
            if (Interlocked.Increment(ref arrived) >= 2)
            {
                bothArrived.TrySetResult();
            }

            await Task.WhenAny(bothArrived.Task, Task.Delay(RendezvousWindow)).ConfigureAwait(false);
        }

        await using var firstDb = CreateContext();
        await using var secondDb = CreateContext();
        var first = BuildHandler(firstDb, doc, max, Rendezvous);
        var second = BuildHandler(secondDb, doc, max, Rendezvous);

        var firstTask = Task.Run(() => first.HandleAsync(NewRowRequest(doc, "DYN-A", 1m), CancellationToken.None));
        var secondTask = Task.Run(() => second.HandleAsync(NewRowRequest(doc, "DYN-B", 2m), CancellationToken.None));

        var errors = new[] { await OutcomeAsync(firstTask), await OutcomeAsync(secondTask) };

        // ⚠ Точка зустрічі мусила бути досягнута ОБОМА — інакше тест не
        // створив гонки й нічого не доводить.
        Assert.Equal(2, Volatile.Read(ref arrived));

        var rows = await RowCountAsync(doc);

        // ⛔ ГОЛОВНЕ: стеля тримає і під гонкою.
        Assert.True(
            rows <= max,
            $"MaxDynamicRows = {max}, а в таблиці {rows} рядків: два одночасні PATCH обидва пройшли перевірку стелі.");

        // Рівно один пройшов, другий — чиста відмова за стелею, а не збій.
        Assert.Equal(max, rows);
        var rejected = Assert.Single(errors, e => e is not null);
        var rule = Assert.IsType<BusinessRuleException>(rejected);
        Assert.Equal("ECR-ROW-0409", rule.ErrorCode);
        Assert.Equal("err.ECR-ROW-0409.dynamicRowLimit", rule.Details?["messageKey"]?.ToString());

        // ⚠ Прийнятий батч записав свою комірку, відхилений — нічого.
        var newCells = await ScalarAsync<int>(
            $"SELECT COUNT(*) FROM doc.CellValue c JOIN doc.TableRow r " +
            $"ON r.PeriodKey = c.PeriodKey AND r.Id = c.TableRowId " +
            $"WHERE r.PeriodKey = {doc.PeriodKey.Value} AND r.TableInstanceId = {doc.TableInstanceId} " +
            $"AND r.RowKey IN ('DYN-A', 'DYN-B') AND c.ColumnDefId = {doc.ColumnDefIds[1]}");
        Assert.Equal(1, newCells);
    }

    private static PatchCellsRequest NewRowRequest(TestDocument doc, string rowKey, decimal value)
        => new(doc.TableInstanceId, doc.PeriodKey.Value, "UserEdit",
            [new PatchRow(rowKey, BaseVersion: null, [new PatchCell(CodeOf(doc, 2), value)])]);

    /// <summary>
    /// Обробник на справжніх сховищах, <see cref="UnitOfWork"/> і
    /// <see cref="SheetEditGate"/>; <paramref name="beforePersist"/> — точка перемикання.
    /// </summary>
    private static PatchCellsHandler BuildHandler(
        EcrDbContext db, TestDocument doc, int maxRows, Func<Task> beforePersist)
    {
        var clock = new FixedClock(new DateTime(2026, 2, 1, 9, 0, 1, DateTimeKind.Utc));
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
        table.SetMaxDynamicRows(maxRows);
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

        // ⚠ Точка перемикання: служба доступу — останнє, що обробник питає
        // ПІСЛЯ швидкої перевірки стелі й ДО транзакції запису.
        access.CanCreateRowsAsync(
                  Arg.Any<AccessProfile>(), doc.TableInstanceId,
                  Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
              .Returns(async call =>
              {
                  await beforePersist().ConfigureAwait(false);
                  return (IReadOnlyDictionary<string, NewRowAccess>)call.ArgAt<IReadOnlyCollection<string>>(2)
                      .ToDictionary(
                          k => k, _ => new NewRowAccess(EditDecision.Allow(), new Dictionary<int, EditDecision>()),
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

    private static async Task<Exception?> OutcomeAsync(Task task)
    {
        try
        {
            await task.WaitAsync(HookTimeout).ConfigureAwait(false);
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

    private Task<int> RowCountAsync(TestDocument doc)
        => ScalarAsync<int>(
            $"SELECT COUNT(*) FROM doc.TableRow WHERE PeriodKey = {doc.PeriodKey.Value} " +
            $"AND TableInstanceId = {doc.TableInstanceId}");

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
