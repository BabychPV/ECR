// tests/Ecr.Infrastructure.Tests/Persistence/UnitOfWorkRetryRestoreTests.cs
using System.Data.Common;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// L6-15 / N1-03 (аудит 2026-10-09, AN-76): повтор <c>UnitOfWork.ExecuteInTransactionAsync</c> після
/// транзієнтного збою, що стався ПІСЛЯ успішного <c>SaveChanges</c>, не губить зміну сутності, яка була
/// завантажена до транзакції.
/// </summary>
/// <remarks>
/// ⛔ Що було. Збережену першою спробою сутність трекер вважав збереженою (<c>Unchanged</c>), хоча
/// транзакцію відкотили: повтор не бачив різниці між трекером і базою, обробник відповідав 200, а
/// зміни в базі не було. Видалена сутність після прийняття ставала <c>Detached</c>.
///
/// ⚠ Справжня база. Збій — на коміті першої спроби (після збереження): тестова стратегія повторює
/// лише його; у застосунку цю роль грає <c>EnableRetryOnFailure</c> (1205, обрив з'єднання).
/// Той самий прийом, що <c>DataSourceSaveRetryTests</c>. Жодних пауз.
///
/// ⛔ Мутація: у <c>UnitOfWork.ExecuteInTransactionAsync</c> повернути від'єднання лише нових
/// сутностей (прибрати <c>ChangeTrackerCheckpoint.Restore</c>) — перші три тести червоні.
/// </remarks>
[Collection("SqlServer")]
public sealed class UnitOfWorkRetryRestoreTests(SqlServerFixture sql)
{
    private const string OldEndpoint = "https://old.corp.example/piwebapi";
    private const string NewEndpoint = "https://new.corp.example/piwebapi";

    private static readonly LocalizedText Name = new(new Dictionary<string, string> { ["en"] = "L6-15 source" });

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "L6-15")]
    public async Task Сутність_завантажена_до_транзакції_і_змінена_в_замиканні_записується_після_повтору()
    {
        var (id, _) = await ArrangeAsync();
        var fault = new CommitFaultOnce();

        await using (var db = Context(fault))
        {
            var source = await db.DataSources.SingleAsync(s => s.Id == id);
            var seenAtStart = new List<string>();

            await new UnitOfWork(db).ExecuteInTransactionAsync(
                async ct =>
                {
                    seenAtStart.Add(source.Endpoint);
                    source.Update(Name, ExternalTransport.PiWebApi, NewEndpoint, isActive: true);
                    await db.SaveChangesAsync(ct);
                },
                CancellationToken.None);

            Assert.True(fault.Fired, "Збій першої спроби не спрацював — тест нічого не довів.");

            // ⚠ Друга спроба бачить сутність такою, якою вона була ДО першої: доменна перевірка стану
            // (подання → «уже подано») на ній не відмовить.
            Assert.Equal([OldEndpoint, OldEndpoint], seenAtStart);
        }

        Assert.Equal(NewEndpoint, await EndpointOfAsync(id));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "L6-15")]
    public async Task Зміна_зроблена_викликачем_до_транзакції_записується_після_повтору()
    {
        var (id, _) = await ArrangeAsync();
        var fault = new CommitFaultOnce();

        await using (var db = Context(fault))
        {
            var source = await db.DataSources.SingleAsync(s => s.Id == id);

            // Зміна — ДО транзакції: замикання лише зберігає.
            source.Update(Name, ExternalTransport.PiWebApi, NewEndpoint, isActive: true);

            await new UnitOfWork(db).ExecuteInTransactionAsync(
                ct => db.SaveChangesAsync(ct),
                CancellationToken.None);

            Assert.True(fault.Fired, "Збій першої спроби не спрацював — тест нічого не довів.");
        }

        Assert.Equal(NewEndpoint, await EndpointOfAsync(id));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "L6-15")]
    public async Task Видалення_завантаженої_до_транзакції_сутності_виконується_після_повтору()
    {
        var (id, _) = await ArrangeAsync();
        var fault = new CommitFaultOnce();

        await using (var db = Context(fault))
        {
            var source = await db.DataSources.SingleAsync(s => s.Id == id);

            await new UnitOfWork(db).ExecuteInTransactionAsync(
                async ct =>
                {
                    db.DataSources.Remove(source);
                    await db.SaveChangesAsync(ct);
                },
                CancellationToken.None);

            Assert.True(fault.Fired, "Збій першої спроби не спрацював — тест нічого не довів.");
        }

        Assert.Null(await EndpointOfAsync(id));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "L6-15")]
    public async Task Додана_в_замиканні_сутність_з_двома_збереженнями_після_повтору_вставляється_один_раз()
    {
        var code = NewCode();
        var fault = new CommitFaultOnce();

        await using (var db = Context(fault))
        {
            await new UnitOfWork(db).ExecuteInTransactionAsync(
                async ct =>
                {
                    var source = new DataSource(
                        EcrCode.Create(code), Name, ExternalTransport.PiWebApi, OldEndpoint, "DataSource." + code);
                    db.DataSources.Add(source);
                    await db.SaveChangesAsync(ct);

                    // Друге збереження в тій самій транзакції — вставка першого не повторюється.
                    source.Update(Name, ExternalTransport.PiWebApi, NewEndpoint, isActive: true);
                    await db.SaveChangesAsync(ct);
                },
                CancellationToken.None);

            Assert.True(fault.Fired, "Збій першої спроби не спрацював — тест нічого не довів.");
        }

        await using var check = Context();
        var rows = await check.DataSources.AsNoTracking().Where(s => s.Code == code).Select(s => s.Endpoint).ToListAsync();
        Assert.Equal([NewEndpoint], rows);
    }

    /// <summary>
    /// Без збою кілька збережень в одній транзакції працюють як працювали: прийняття після кожного
    /// збереження лишається (охорона від варіанта <c>acceptAllChangesOnSuccess: false</c>).
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "L6-15")]
    public async Task Два_збереження_в_одній_транзакції_без_збою_не_повторюють_вставку()
    {
        var code = NewCode();

        await using (var db = Context())
        {
            await new UnitOfWork(db).ExecuteInTransactionAsync(
                async ct =>
                {
                    var source = new DataSource(
                        EcrCode.Create(code), Name, ExternalTransport.PiWebApi, OldEndpoint, "DataSource." + code);
                    db.DataSources.Add(source);
                    await db.SaveChangesAsync(ct);

                    source.Update(Name, ExternalTransport.PiWebApi, NewEndpoint, isActive: true);
                    await db.SaveChangesAsync(ct);
                },
                CancellationToken.None);
        }

        await using var check = Context();
        Assert.Equal(
            [NewEndpoint],
            await check.DataSources.AsNoTracking().Where(s => s.Code == code).Select(s => s.Endpoint).ToListAsync());
    }

    // ── Підготовка ──────────────────────────────────────────────────────

    private static string NewCode() => $"L615{Guid.NewGuid():N}"[..20].ToUpperInvariant();

    private EcrDbContext Context(CommitFaultOnce? fault = null)
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

    private async Task<(int Id, string Code)> ArrangeAsync()
    {
        var code = NewCode();
        await using var db = Context();
        var source = new DataSource(
            EcrCode.Create(code), Name, ExternalTransport.PiWebApi, OldEndpoint, "DataSource." + code);
        db.DataSources.Add(source);
        await db.SaveChangesAsync();
        return (source.Id, code);
    }

    private async Task<string?> EndpointOfAsync(int id)
    {
        await using var db = Context();
        return await db.DataSources.AsNoTracking().Where(s => s.Id == id).Select(s => s.Endpoint).SingleOrDefaultAsync();
    }

    // ── Імітація транзієнтного збою ─────────────────────────────────────

    /// <summary>Збій, який тестова стратегія вважає транзієнтним (замість 1205).</summary>
    private sealed class TransientTestFault() : Exception("Імітований транзієнтний збій (L6-15).");

    /// <summary>Стратегія, що повторює лише <see cref="TransientTestFault"/>.</summary>
    private sealed class RetryOnTestFault(ExecutionStrategyDependencies dependencies)
        : ExecutionStrategy(dependencies, maxRetryCount: 3, maxRetryDelay: TimeSpan.FromMilliseconds(1))
    {
        protected override bool ShouldRetryOn(Exception exception) => exception is TransientTestFault;
    }

    /// <summary>Кидає один раз — на першому коміті, тобто після успішного збереження першої спроби.</summary>
    private sealed class CommitFaultOnce : DbTransactionInterceptor
    {
        /// <summary>Чи спрацював збій.</summary>
        public bool Fired { get; private set; }

        public override ValueTask<InterceptionResult> TransactionCommittingAsync(
            DbTransaction transaction, TransactionEventData eventData, InterceptionResult result,
            CancellationToken cancellationToken = default)
        {
            if (!Fired)
            {
                Fired = true;
                throw new TransientTestFault();
            }

            return base.TransactionCommittingAsync(transaction, eventData, result, cancellationToken);
        }
    }
}
