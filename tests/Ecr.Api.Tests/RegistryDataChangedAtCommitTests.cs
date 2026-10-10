using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// D1-03 (R5-D1): мітка <c>DataChangedAt</c> довідника ставиться на момент КОМІТУ транзакції запису, а не на
/// момент першого <c>SaveChangesAsync</c> усередині неї.
/// </summary>
/// <remarks>
/// ⛔ Дефект: пакет сітки й CSV зберігають записи, а потім у тій самій транзакції ще секунди рахують правила.
/// Прогін розрахунку, що стартував у цьому вікні (<c>StartedAt</c> = 10:00:02), читав знімок без незакомічених
/// рядків — тобто рахував на старому довіднику, — а мітка 10:00:00 була РАНІША за його старт, тож
/// <c>StaleResultsQuery</c>/<c>RegistryImpactStore</c> (<c>DataChangedAt &gt; StartedAt</c>) вважали результат
/// свіжим. Тест моделює «секунди після збереження» годинником, що йде вперед усередині транзакції. До
/// виправлення мітка лишалася 10:00:00 — тест червоніє.
/// </remarks>
[Collection("SqlServer")]
public sealed class RegistryDataChangedAtCommitTests(SqlServerFixture sql)
{
    private static readonly DateTime Start = new(2026, 10, 9, 10, 0, 0, DateTimeKind.Utc);

    private readonly string _tag = Guid.NewGuid().ToString("N")[..8];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Мітка_зміни_даних_ставиться_перед_комітом_а_не_на_збереженні_всередині_транзакції()
    {
        var id = await CreateAsync();
        var clock = new TestClock(Start);

        await using var db = Context();
        var uow = new UnitOfWork(db, clock);
        var def = await db.RegistryDefs.SingleAsync(d => d.Id == id);

        await uow.ExecuteInTransactionAsync(
            async token =>
            {
                def.BumpDataRevision();
                await uow.SaveChangesAsync(token);

                // Правила довідника, перерахунок сиріт тощо — ще 5 с у тій самій транзакції.
                clock.Advance(TimeSpan.FromSeconds(5));
            },
            CancellationToken.None);

        var expected = Start.AddSeconds(5);
        Assert.Equal(expected, await ChangedAtAsync(id));

        // Відстежуваний екземпляр — у тому самому стані, що й рядок, і без позначки «змінено».
        Assert.Equal(expected, def.DataChangedAt);
        Assert.Equal(EntityState.Unchanged, db.Entry(def).State);

        // Прогін, що стартував у вікні між збереженням і комітом, тепер РАНІШИЙ за мітку → «застарілий».
        Assert.True(await ChangedAtAsync(id) > Start.AddSeconds(2));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Одиночне_збереження_ставить_мітку_на_свій_момент()
    {
        var id = await CreateAsync();
        var clock = new TestClock(Start);

        await using var db = Context();
        var def = await db.RegistryDefs.SingleAsync(d => d.Id == id);
        def.BumpDataRevision();
        await new UnitOfWork(db, clock).SaveChangesAsync(CancellationToken.None);

        Assert.Equal(Start, await ChangedAtAsync(id));
    }

    private async Task<int> CreateAsync()
    {
        await using var db = Context();
        var def = new RegistryDef(
            EcrCode.Create($"DCHG_{_tag}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Data changed at" }),
            isTemporal: false);
        db.RegistryDefs.Add(def);
        await db.SaveChangesAsync();
        return def.Id;
    }

    private async Task<DateTime?> ChangedAtAsync(int id)
    {
        await using var db = Context();
        return await db.RegistryDefs.AsNoTracking().Where(d => d.Id == id).Select(d => d.DataChangedAt).SingleAsync();
    }

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);
}
