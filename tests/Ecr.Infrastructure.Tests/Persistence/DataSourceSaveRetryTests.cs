// tests/Ecr.Infrastructure.Tests/Persistence/DataSourceSaveRetryTests.cs
using Ecr.Application.Common;
using Ecr.Application.Integration;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// ent4 P2-2: повтор стратегії виконання в збереженні з'єднання (D8) не має відповідати
/// успіхом без запису і лишати в журналі зміну, якої немає.
/// </summary>
/// <remarks>
/// Справжня база, справжні <c>SaveDataSourceHandler</c>, <c>UnitOfWork</c>, <c>DataSourceStore</c>
/// і <c>AuditWriter</c>. Транзієнтний збій — на ПЕРШІЙ спробі: на <c>SaveChanges</c> (зміни лишились
/// у трекері) або на коміті ПІСЛЯ <c>SaveChanges</c> (трекер уже вважає їх збереженими, а
/// транзакцію відкочено). Тестова стратегія повторює лише цей збій; у застосунку ту саму роль
/// грає <c>EnableRetryOnFailure</c> (1205, обрив з'єднання).
/// <para>
/// ⛔ До фіксу випадок «коміт» давав: створення — 201 без рядка в <c>ext.DataSource</c> і запис
/// журналу з <c>EntityId</c> відкоченої identity; зміна — 200, адреса в базі стара, а журнал
/// каже «змінено». Мутація: прибрати гілку <c>attempt++ &gt; 0</c> в обробнику → обидва
/// випадки «коміт» червоні (прогнано, опис коміту).
/// </para>
/// </remarks>
[Collection("SqlServer")]
public sealed class DataSourceSaveRetryTests(SqlServerFixture sql)
{
    private const int Actor = 9;

    private static readonly Dictionary<string, string> Name = new() { ["en"] = "AF retry" };

    /// <summary>Де саме падає перша спроба.</summary>
    public enum FaultAt
    {
        /// <summary>На <c>SaveChanges</c> — зміни в трекері лишаються.</summary>
        SaveChanges,

        /// <summary>На коміті після <c>SaveChanges</c> — зміни вже прийняті трекером.</summary>
        Commit,
    }

    [Theory]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [InlineData(FaultAt.SaveChanges)]
    [InlineData(FaultAt.Commit)]
    public async Task Створення_після_транзієнтного_збою_зберігає_джерело_разом_із_журналом(FaultAt at)
    {
        var code = NewCode();
        var fault = new FirstAttemptFault(at);

        await using (var db = Context(fault))
        {
            var view = await Handler(db).CreateAsync(
                code, Name, ExternalTransport.PiWebApi, "https://pi.corp.example/piwebapi",
                null, null, null, null, CancellationToken.None);

            Assert.True(fault.Fired, "Збій першої спроби не спрацював — тест нічого не довів.");
            Assert.Equal(await IdOfAsync(code), view.Id);
        }

        var id = await IdOfAsync(code);
        Assert.True(id > 0, "Обробник відповів успіхом, а з'єднання в базі немає.");

        // Рівно один запис журналу — і про те з'єднання, що справді існує.
        Assert.Equal([id], await JournalIdsAsync(code, "Create"));
    }

    [Theory]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [InlineData(FaultAt.SaveChanges)]
    [InlineData(FaultAt.Commit)]
    public async Task Зміна_адреси_після_транзієнтного_збою_зберігається_разом_із_журналом(FaultAt at)
    {
        var code = NewCode();
        var (id, version) = await ArrangeAsync(code, "https://old.corp.example/piwebapi");
        var fault = new FirstAttemptFault(at);

        await using (var db = Context(fault))
        {
            await Handler(db).UpdateAsync(
                id, Name, ExternalTransport.PiWebApi, "https://new.corp.example/piwebapi", null, null, null, true,
                $"\"{version}\"", secretConfirmation: null, confirmEndpointChange: true, CancellationToken.None);

            Assert.True(fault.Fired, "Збій першої спроби не спрацював — тест нічого не довів.");
        }

        Assert.Equal("https://new.corp.example/piwebapi", await EndpointOfAsync(id));
        Assert.Equal([id], await JournalIdsAsync(code, "Update"));
    }

    // ── Підготовка ──────────────────────────────────────────────────────

    private static string NewCode() => $"D8R{Guid.NewGuid():N}"[..20].ToUpperInvariant();

    private EcrDbContext Context(FirstAttemptFault? fault = null)
    {
        var builder = new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString, o =>
            {
                o.MigrationsHistoryTable("__EFMigrationsHistory", "dbo");
                o.ExecutionStrategy(d => new RetryOnTestFault(d));
            });

        if (fault is not null)
        {
            builder.AddInterceptors(fault);
        }

        return new EcrDbContext(builder.Options);
    }

    private static SaveDataSourceHandler Handler(EcrDbContext db)
    {
        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(Actor);

        var access = Substitute.For<IAccessDecisionService>();
        access.BuildProfileAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = Actor }.Permission(SaveDataSourceHandler.Permission).Build());

        return new SaveDataSourceHandler(
            new DataSourceStore(db), Substitute.For<ISecretProvider>(), access, new UnitOfWork(db),
            new AuditWriter(db), user, new TestClock(new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc)));
    }

    private async Task<(int Id, string Version)> ArrangeAsync(string code, string endpoint)
    {
        await using var db = Context();
        var source = new DataSource(
            EcrCode.Create(code), new LocalizedText(Name), ExternalTransport.PiWebApi, endpoint, "DataSource." + code);
        db.DataSources.Add(source);
        await db.SaveChangesAsync();
        return (source.Id, Convert.ToBase64String(source.RowVersion));
    }

    private async Task<int> IdOfAsync(string code)
    {
        await using var db = Context();
        return await db.DataSources.AsNoTracking().Where(s => s.Code == code).Select(s => s.Id).FirstOrDefaultAsync();
    }

    private async Task<string?> EndpointOfAsync(int id)
    {
        await using var db = Context();
        return await db.DataSources.AsNoTracking().Where(s => s.Id == id).Select(s => s.Endpoint).SingleAsync();
    }

    private async Task<List<int>> JournalIdsAsync(string code, string operation)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT EntityId FROM aud.StructureChange
             WHERE EntityType = N'ext.DataSource' AND Operation = @operation AND NewJson LIKE @code
            """;
        command.Parameters.AddWithValue("@operation", operation);
        command.Parameters.AddWithValue("@code", $"%\"{code}\"%");

        var ids = new List<int>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetInt32(0));
        }

        return ids;
    }

    // ── Імітація транзієнтного збою ─────────────────────────────────────

    /// <summary>Збій, який тестова стратегія вважає транзієнтним.</summary>
    private sealed class TransientTestFault() : Exception("Імітований транзієнтний збій (ent4 P2-2).");

    /// <summary>Стратегія, що повторює лише <see cref="TransientTestFault"/>.</summary>
    private sealed class RetryOnTestFault(ExecutionStrategyDependencies dependencies)
        : ExecutionStrategy(dependencies, maxRetryCount: 3, maxRetryDelay: TimeSpan.FromMilliseconds(1))
    {
        protected override bool ShouldRetryOn(Exception exception) => exception is TransientTestFault;
    }

    /// <summary>
    /// Кидає <see cref="TransientTestFault"/> один раз: на збереженні, що несе змінене джерело,
    /// або на коміті одразу після нього.
    /// </summary>
    private sealed class FirstAttemptFault(FaultAt at) : SaveChangesInterceptor, IDbTransactionInterceptor
    {
        private bool _targetSaved;

        /// <summary>Чи спрацював збій.</summary>
        public bool Fired { get; private set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var carriesTarget = eventData.Context!.ChangeTracker.Entries<DataSource>()
                .Any(e => e.State is EntityState.Added or EntityState.Modified);

            if (!Fired && carriesTarget)
            {
                if (at == FaultAt.SaveChanges)
                {
                    Fired = true;
                    throw new TransientTestFault();
                }

                _targetSaved = true;
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }

        public ValueTask<InterceptionResult> TransactionCommittingAsync(
            System.Data.Common.DbTransaction transaction, TransactionEventData eventData, InterceptionResult result,
            CancellationToken cancellationToken = default)
        {
            if (!Fired && _targetSaved)
            {
                Fired = true;
                throw new TransientTestFault();
            }

            return ValueTask.FromResult(result);
        }
    }
}
