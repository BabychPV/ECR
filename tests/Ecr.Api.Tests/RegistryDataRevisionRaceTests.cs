using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// L5-05 (аудит 2026-10-03): <c>DataRevision</c> росте відносним <c>UPDATE … + n</c> у транзакції запису,
/// а не абсолютним значенням, прочитаним на початку запиту.
/// </summary>
/// <remarks>
/// ⛔ Мутаційний доказ: у <c>UnitOfWork.SaveChangesAsync</c> замінити <c>TakeRegistryRevisionBumps()</c> порожнім
/// списком (звичайний абсолютний <c>db.SaveChangesAsync</c>, як було) — і
/// <see cref="Дві_паралельні_правки_піднімають_ревізію_двічі"/> червоніє (було N+1 замість N+2): кеш переліків
/// довідника, чий ключ несе ревізію, до 15 хв віддавав би застарілий перелік.
///
/// ⚠ Гонитва відтворюється детерміновано, без паралелізму: два <c>DbContext</c> читають одну ревізію до того,
/// як будь-який із них зберіг — рівно «паралельні» запити.
/// </remarks>
[Collection("SqlServer")]
public sealed class RegistryDataRevisionRaceTests(SqlServerFixture sql)
{
    private readonly string _tag = Guid.NewGuid().ToString("N")[..8];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Дві_паралельні_правки_піднімають_ревізію_двічі()
    {
        var id = await CreateAsync();
        var before = await RevisionAsync(id);

        await using var a = Context();
        await using var b = Context();
        var defA = await a.RegistryDefs.SingleAsync(d => d.Id == id);
        var defB = await b.RegistryDefs.SingleAsync(d => d.Id == id);
        Assert.Equal(defA.DataRevision, defB.DataRevision);

        defA.BumpDataRevision();
        defB.BumpDataRevision();
        await new UnitOfWork(a).SaveChangesAsync(CancellationToken.None);
        await new UnitOfWork(b).SaveChangesAsync(CancellationToken.None);

        Assert.Equal(before + 2, await RevisionAsync(id));

        // Трекер другого запиту бачить справжню ревізію, а не власне «N+1».
        Assert.Equal(before + 2, defB.DataRevision);
        Assert.Equal(EntityState.Unchanged, b.Entry(defB).State);

        // Мітка зміни даних ставиться, як і раніше.
        await using var read = Context();
        Assert.NotNull((await read.RegistryDefs.AsNoTracking().SingleAsync(d => d.Id == id)).DataChangedAt);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Кілька_підйомів_в_одному_збереженні_додаються_а_не_зливаються()
    {
        var id = await CreateAsync();
        var before = await RevisionAsync(id);

        await using var db = Context();
        var def = await db.RegistryDefs.SingleAsync(d => d.Id == id);
        def.BumpDataRevision();
        def.BumpDataRevision();
        await new UnitOfWork(db).SaveChangesAsync(CancellationToken.None);

        Assert.Equal(before + 2, await RevisionAsync(id));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Збереження_без_підйому_ревізії_її_не_чіпає()
    {
        var id = await CreateAsync();
        var before = await RevisionAsync(id);

        await using var db = Context();
        var def = await db.RegistryDefs.SingleAsync(d => d.Id == id);
        def.BumpDefinitionVersion();
        await new UnitOfWork(db).SaveChangesAsync(CancellationToken.None);

        Assert.Equal(before, await RevisionAsync(id));
    }

    private async Task<int> CreateAsync()
    {
        await using var db = Context();
        var def = new RegistryDef(
            EcrCode.Create($"REVR_{_tag}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Revision race" }),
            isTemporal: false);
        db.RegistryDefs.Add(def);
        await db.SaveChangesAsync();
        return def.Id;
    }

    private async Task<int> RevisionAsync(int id)
    {
        await using var db = Context();
        return await db.RegistryDefs.AsNoTracking().Where(d => d.Id == id).Select(d => d.DataRevision).SingleAsync();
    }

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);
}
