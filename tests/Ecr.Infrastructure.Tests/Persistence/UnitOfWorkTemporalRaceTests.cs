// tests/Ecr.Infrastructure.Tests/Persistence/UnitOfWorkTemporalRaceTests.cs
using Ecr.Application.Errors;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// B150: гонитва двох змін того самого рядка системно-версійованої таблиці (<c>dic.RegistryEntry</c>) — SQL 13535
/// «transaction time was earlier than period start time» — дає <c>409 ECR-CELL-0409 concurrentChange</c>, а не 500.
/// </summary>
/// <remarks>
/// Детерміновано: транзакція «програшного» запиту ПОЧИНАЄТЬСЯ раніше (запис у сусідній рядок фіксує час початку),
/// «переможець» змінює запис і комітить, і лише тоді програшний читає запис (бачить новий стан) і пише його.
/// Через HTTP той самий шлях — <c>DuplicateRequestHttpTests.Вікно_чинності_запису_довідника_двічі_ідемпотентне</c>
/// (недетерміновано). Мутація: <c>SqlConflict.IsTemporalPeriodRace</c> на інше число → червоний (сирий виняток).
/// </remarks>
[Collection("SqlServer")]
public sealed class UnitOfWorkTemporalRaceTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "B150")]
    public async Task Пізніший_запис_рядка_з_ранішою_транзакцією_дає_409_concurrentChange()
    {
        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        int registryId;
        long entryId;

        await using (var setup = Context())
        {
            var registry = new RegistryDef(EcrCode.Create($"B150T_{tag}"), Text("B150 temporal race"), isTemporal: true);
            setup.RegistryDefs.Add(registry);
            await setup.SaveChangesAsync();

            var entry = new RegistryEntry(registry.Id, EcrCode.Create($"E_{tag}"), Text("Entry"));
            setup.RegistryEntries.Add(entry);
            await setup.SaveChangesAsync();

            registryId = registry.Id;
            entryId = entry.Id;
        }

        try
        {
            await using var loser = Context();
            await using var transaction = await loser.Database.BeginTransactionAsync();

            // Час початку транзакції фіксується першим записом — до коміту «переможця».
            await loser.RegistryDefs.Where(d => d.Id == registryId)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.Code, d => d.Code));
            await Task.Delay(50);

            await using (var winner = Context())
            {
                var won = await winner.RegistryEntries.SingleAsync(e => e.Id == entryId);
                won.SetValidity(new DateOnly(2099, 1, 1), null);
                await winner.SaveChangesAsync();
            }

            var mine = await loser.RegistryEntries.SingleAsync(e => e.Id == entryId);
            mine.SetValidity(new DateOnly(2099, 2, 1), null);

            var thrown = await Assert.ThrowsAsync<ConcurrencyConflictException>(
                () => new UnitOfWork(loser).SaveChangesAsync(CancellationToken.None));

            Assert.Equal("ECR-CELL-0409", thrown.ErrorCode);
            Assert.Equal("err.ECR-CELL-0409.concurrentChange", thrown.Details!["messageKey"]);
        }
        finally
        {
            await using var cleanup = Context();
            await cleanup.RegistryEntries.Where(e => e.Id == entryId).ExecuteDeleteAsync();
            await cleanup.RegistryDefs.Where(d => d.Id == registryId).ExecuteDeleteAsync();
        }
    }

    private static LocalizedText Text(string en) => new(new Dictionary<string, string> { ["en"] = en });

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString, o => o.MigrationsHistoryTable("__EFMigrationsHistory", "dbo"))
            .Options);
}
