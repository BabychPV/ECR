// tests/Ecr.Infrastructure.Tests/Persistence/CollectionScheduleDependencyLockTests.cs
using Ecr.Application.Errors;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// S-D1/S-D2: замок залежностей джерела, свіже читання ланцюга і розпізнавання порушення FK (547) — на справжньому
/// SQL Server: <c>sp_getapplock</c>, RCSI і зовнішній ключ-самопосилання існують лише в базі.
/// </summary>
[Collection("SqlServer")]
public sealed class CollectionScheduleDependencyLockTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Замок_джерела_тримається_до_кінця_транзакції_а_другий_запит_отримує_409_а_не_500()
    {
        await using var first = Context();
        await using var second = Context();
        var firstStore = new CollectionScheduleStore(first);
        var secondStore = new CollectionScheduleStore(second) { DependencyLockTimeoutMs = 300 };
        const int dataSource = 987_654;

        await using (var tx = await first.Database.BeginTransactionAsync())
        {
            await firstStore.LockDependenciesAsync(dataSource, CancellationToken.None);

            // ⛔ МУТАЦІЇ: прибрати sp_getapplock → тут нічого не кидається; `@LockOwner = Session` → замок
            // переживає транзакцію і наступний блок червоніє.
            await using var tx2 = await second.Database.BeginTransactionAsync();
            var conflict = await Assert.ThrowsAsync<ConcurrencyConflictException>(
                () => secondStore.LockDependenciesAsync(dataSource, CancellationToken.None));
            Assert.Equal("err.ECR-JOB-0409.collectionScheduleChanged", conflict.Details!["messageKey"]);

            // Інше з'єднання (джерело) — окремий замок.
            await secondStore.LockDependenciesAsync(dataSource + 1, CancellationToken.None);
            await tx.RollbackAsync();
        }

        await using var tx3 = await second.Database.BeginTransactionAsync();
        await secondStore.LockDependenciesAsync(dataSource, CancellationToken.None);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Свіже_читання_залежності_бачить_закомічене_паралельним_запитом_а_відстежувана_сутність_ні()
    {
        await using var reader = Context();
        var (a, b) = await ArrangeAsync();
        var readerStore = new CollectionScheduleStore(reader);

        // Відстежувана копія завантажена ДО паралельної зміни (як ранній етап `RequireValidAsync`).
        var tracked = await readerStore.FindAsync(a, CancellationToken.None);
        Assert.Null(tracked!.Schedule.DependsOnScheduleId);

        await using (var writer = Context())
        {
            var row = await writer.CollectionSchedules.SingleAsync(s => s.Id == a);
            row.SetDependency(b);
            await writer.SaveChangesAsync();
        }

        // ⛔ МУТАЦІЯ: ReadDependsOnAsync через FindAsync (відстежувана сутність) → червоніє.
        Assert.Equal(b, await readerStore.ReadDependsOnAsync(a, CancellationToken.None));
        Assert.Null(await readerStore.ReadDependsOnAsync(a + 100_000, CancellationToken.None));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Порушення_зовнішнього_ключа_видалення_розкладу_із_залежним_розпізнається_інші_винятки_ні()
    {
        var (a, b) = await ArrangeAsync();
        await using (var setup = Context())
        {
            var dependent = await setup.CollectionSchedules.SingleAsync(s => s.Id == b);
            dependent.SetDependency(a);
            await setup.SaveChangesAsync();
        }

        await using var db = Context();
        var store = new CollectionScheduleStore(db);
        db.CollectionSchedules.Remove(await db.CollectionSchedules.SingleAsync(s => s.Id == a));
        var failure = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(CancellationToken.None));

        Assert.True(store.IsForeignKeyViolation(failure));
        Assert.False(store.IsForeignKeyViolation(new InvalidOperationException("x")));
        Assert.False(store.IsForeignKeyViolation(new DbUpdateException("x")));

        // Інший зовнішній ключ (розклад на неіснуючу сутність) — теж 547, але не гонка залежностей.
        // ⛔ МУТАЦІЯ: прибрати перевірку імені FK_CS_DependsOn → цей рядок червоніє.
        await using var other = Context();
        other.CollectionSchedules.Add(new CollectionSchedule(2_000_000_000, "0 5 * * * ?"));
        var unrelated = await Assert.ThrowsAsync<DbUpdateException>(() => other.SaveChangesAsync(CancellationToken.None));
        Assert.False(new CollectionScheduleStore(other).IsForeignKeyViolation(unrelated));
    }

    /// <summary>Два незалежні розклади одного з'єднання.</summary>
    private async Task<(int First, int Second)> ArrangeAsync()
    {
        await using var db = Context();
        var tag = Guid.NewGuid().ToString("N")[..8];

        var dataSource = new DataSource(
            EcrCode.Create($"Dl{tag}"), new LocalizedText(new Dictionary<string, string> { ["en"] = "dep lock" }),
            ExternalTransport.PiWebApi, "https://example.test", "secret");
        db.DataSources.Add(dataSource);
        await db.SaveChangesAsync();

        // ⚠ Сутності НЕАКТИВНІ навмисно: активна без завершеного збору робить `SourcesHealthCheck` жовтим.
        var one = new SourceEntity(dataSource.Id, $"A{tag}", RegistrySourceKind.External);
        var two = new SourceEntity(dataSource.Id, $"B{tag}", RegistrySourceKind.External);
        one.Deactivate();
        two.Deactivate();
        db.SourceEntities.AddRange(one, two);
        await db.SaveChangesAsync();

        var first = new CollectionSchedule(one.Id, "0 5 * * * ?");
        var second = new CollectionSchedule(two.Id, "0 15 2 * * ?");
        db.CollectionSchedules.AddRange(first, second);
        await db.SaveChangesAsync();

        return (first.Id, second.Id);
    }

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);
}