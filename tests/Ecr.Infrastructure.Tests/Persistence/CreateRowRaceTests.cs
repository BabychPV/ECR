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
/// C3: додавання рядка (<c>POST …/rows</c>, <see cref="CreateRowHandler"/>), що
/// перетинається в часі з поданням аркуша або з іншим додаванням рядка.
/// </summary>
/// <remarks>
/// ⛔ Два інваріанти:
/// <list type="number">
/// <item>рядок, доданий поки подання тримає аркуш, або відхилено, або його
/// немає — поданий аркуш не отримує рядка поза своїм зрізом;</item>
/// <item>одночасні додавання не перевищують <c>MaxDynamicRows</c> і не
/// задвоюють <c>Ordinal</c>.</item>
/// </list>
///
/// ⚠ Обидві гонки — ДЕТЕРМІНОВАНІ: подання тримає СПРАВЖНІЙ
/// <see cref="SheetEditGate"/> у власній транзакції окремого з'єднання, а
/// додавання рядків зустрічаються в точці перемикання — <c>CanCreateRowsAsync</c>
/// (обробник кличе його ПІСЛЯ підрахунку рядків і ДО вставки). Зустріч має
/// стелю очікування: коли обробник серіалізований, другий запит до неї не
/// дійде, поки перший не зафіксується, і перший просто йде далі.
///
/// ⚠ Стан аркуша в моку служби доступу читається так, як його читає справжня
/// <c>AccessDecisionService</c>: окремим запитом, <c>AsNoTracking</c>, під RCSI.
/// </remarks>
[Collection("SqlServer")]
public sealed class CreateRowRaceTests(SqlServerFixture sql)
{
    private const int UserId = 1;

    /// <summary>Скільки рядків заводить будівник.</summary>
    private const int SeededRows = 4;

    /// <summary>Скільки чекати, поки додавання дійде до кінця або стане в чергу за поданням.</summary>
    private static readonly TimeSpan OverlapWindow = TimeSpan.FromSeconds(2);

    /// <summary>Скільки перший запит чекає на другого в точці зустрічі.</summary>
    private static readonly TimeSpan RendezvousWindow = TimeSpan.FromSeconds(3);

    /// <summary>Стеля на будь-яке очікування — щоб зламаний тест падав, а не висів.</summary>
    private static readonly TimeSpan HookTimeout = TimeSpan.FromSeconds(60);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-9.4")]
    public async Task Рядок_поки_аркуш_подається_відхилено_і_не_вставлено()
    {
        var doc = await new TestDocumentBuilder(sql.ConnectionString)
            .BuildAsync(rowCount: SeededRows, rowMode: TableRowMode.Dynamic, ct: CancellationToken.None);

        // ── Подання: виняткове блокування взято, стан `Submitted` записано, коміту ще немає.
        await using var holder = CreateContext();
        await using var submit = await holder.Database.BeginTransactionAsync();
        await new SheetEditGate(holder)
            .EnterSubmitAsync(doc.DocumentId, doc.SheetDefId, doc.PeriodKey, CancellationToken.None);
        var state = new ApprovalState(doc.DocumentId, doc.SheetDefId, doc.PeriodKey.Value);
        state.Submit(UserId, new DateTime(2026, 2, 1, 9, 0, 0, DateTimeKind.Utc));
        holder.ApprovalStates.Add(state);
        await holder.SaveChangesAsync();

        await using var createDb = CreateContext();
        var handler = BuildHandler(createDb, doc, maxRows: null, beforeInsert: null);

        var createTask = Task.Run(() => handler.HandleAsync(
            doc.DocumentId, doc.TableInstanceId, requestedKey: null, Profile(), CancellationToken.None));
        await Task.WhenAny(createTask, Task.Delay(OverlapWindow));

        await submit.CommitAsync();

        var error = await OutcomeAsync(createTask);
        var rows = await RowCountAsync(doc);

        // ⛔ ГОЛОВНЕ: після подання в таблиці рівно ті рядки, що були до нього.
        Assert.True(
            rows == SeededRows,
            $"Аркуш подано з {SeededRows} рядками, а в таблиці їх {rows}: рядок додано поза зрізом подання " +
            $"(результат додавання: {error?.GetType().Name ?? "прийнято"}).");

        var denied = Assert.IsType<AccessDeniedException>(error);
        Assert.Equal("ECR-ACCS-0403", denied.ErrorCode);
        Assert.Equal(nameof(EditDenyReason.DocumentSubmitted), denied.Details?["reason"]?.ToString());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-3.2")]
    public async Task Одночасні_додавання_не_перевищують_MaxDynamicRows_і_не_задвоюють_Ordinal()
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

        var firstTask = Task.Run(() => first.HandleAsync(
            doc.DocumentId, doc.TableInstanceId, requestedKey: null, Profile(), CancellationToken.None));
        var secondTask = Task.Run(() => second.HandleAsync(
            doc.DocumentId, doc.TableInstanceId, requestedKey: null, Profile(), CancellationToken.None));

        var errors = new[] { await OutcomeAsync(firstTask), await OutcomeAsync(secondTask) };

        var rows = await RowCountAsync(doc);
        var duplicateOrdinals = await ScalarAsync<int>(
            $"SELECT COUNT(*) FROM (SELECT Ordinal FROM doc.TableRow WHERE PeriodKey = {doc.PeriodKey.Value} " +
            $"AND TableInstanceId = {doc.TableInstanceId} GROUP BY Ordinal HAVING COUNT(*) > 1) d");

        // ⛔ ГОЛОВНЕ: стеля тримає і під гонкою.
        Assert.True(
            rows <= max,
            $"MaxDynamicRows = {max}, а в таблиці {rows} рядків: два одночасні додавання обидва пройшли перевірку стелі.");
        Assert.Equal(0, duplicateOrdinals);

        // Рівно одне пройшло, друге — чиста відмова за стелею, а не збій.
        Assert.Equal(max, rows);
        var rejected = Assert.Single(errors, e => e is not null);
        var rule = Assert.IsType<BusinessRuleException>(rejected);
        Assert.Equal("ECR-ROW-0409", rule.ErrorCode);
        Assert.Equal("err.ECR-ROW-0409.rowLimitReached", rule.Details?["messageKey"]?.ToString());
    }

    /// <summary>Обробник на справжніх сховищах; <paramref name="beforeInsert"/> — точка перемикання.</summary>
    private static CreateRowHandler BuildHandler(
        EcrDbContext db, TestDocument doc, int? maxRows, Func<Task>? beforeInsert)
    {
        var bulk = new BulkCellLoader(db.Database.GetConnectionString()!, 1000);
        var clock = new FixedClock(new DateTime(2026, 2, 1, 9, 0, 1, DateTimeKind.Utc));

        var access = Substitute.For<IAccessDecisionService>();
        access.CanReadDocumentAsync(Arg.Any<AccessProfile>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
              .Returns(EditDecision.Allow());

        // ⚠ Стан аркуша — так, як його читає `AccessDecisionService`: тим самим
        // контекстом, `AsNoTracking`, окремим запитом.
        access.CanCreateRowsAsync(
                  Arg.Any<AccessProfile>(), doc.TableInstanceId,
                  Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
              .Returns(async call =>
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

                  if (beforeInsert is not null)
                  {
                      await beforeInsert().ConfigureAwait(false);
                  }

                  return (IReadOnlyDictionary<string, NewRowAccess>)call.ArgAt<IReadOnlyCollection<string>>(2)
                      .ToDictionary(
                          k => k, _ => new NewRowAccess(decision, new Dictionary<int, EditDecision>()),
                          StringComparer.Ordinal);
              });

        return new CreateRowHandler(
            new RowStore(db, bulk, clock), new DocumentStore(db), Metadata(doc, maxRows), access,
            new UnitOfWork(db), clock, new SheetEditGate(db));
    }

    /// <summary>Знімок структури з РЕАЛЬНИМИ ідентифікаторами аркуша й таблиці.</summary>
    private static IMetadataCache Metadata(TestDocument doc, int? maxRows)
    {
        var sheet = new SheetDef(doc.TemplateVersionId, EcrCode.Create($"SH{doc.SheetDefId}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Sheet" }), 1);
        typeof(Entity<int>).GetProperty("Id")!.SetValue(sheet, doc.SheetDefId);
        var table = new TableDef(doc.SheetDefId, EcrCode.Create($"TB{doc.TableDefId}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Table" }), 1,
            TableLayoutKind.PerPeriodInstance, TableRowMode.Dynamic);
        typeof(Entity<int>).GetProperty("Id")!.SetValue(table, doc.TableDefId);
        if (maxRows is { } m)
        {
            table.SetMaxDynamicRows(m);
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
