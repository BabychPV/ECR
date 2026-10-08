// tests/Ecr.Infrastructure.Tests/Persistence/MethodologyCategoryRuleStoreTests.cs
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Правило категорії константи (L-2, <c>calc.CategoryRule</c>) на РЕАЛЬНОМУ SQL Server: одне на
/// версію, читається рушієм, переходить у клон і прибирається разом із версією.
/// </summary>
/// <remarks>
/// ⛔ Мутаційні докази: без <c>UQ_CategoryRule_Version</c> друге правило версії пройшло б мовчки
/// (рушій читав би довільне); клон без перенесення правила повертав би 5.1/6.1 до
/// <c>constantAmbiguous</c> на новій чернетці; видалення версії без рядка правила зупиняє FK.
/// </remarks>
[Collection("SqlServer")]
public sealed class MethodologyCategoryRuleStoreTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Друге_правило_тієї_самої_версії_відхиляється_унікальним_індексом()
    {
        await using var db = CreateContext();
        var version = await NewVersionAsync(db, "uq");

        db.MethodologyCategoryRules.Add(version.SetCategoryRule(null, "!ECW_Location", Now));
        await db.SaveChangesAsync();

        db.MethodologyCategoryRules.Add(new MethodologyCategoryRule(version.Id, "!Other", Now));

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Рушій_читає_вираз_правила_версії_а_без_правила_отримує_null()
    {
        await using var db = CreateContext();
        var withRule = await NewVersionAsync(db, "read1");
        var without = await NewVersionAsync(db, "read2");

        db.MethodologyCategoryRules.Add(withRule.SetCategoryRule(null, "!ECW_Category", Now));
        await db.SaveChangesAsync();

        var store = new MethodologyStore(db, constantCap: 100);

        Assert.Equal("!ECW_Category", await store.GetCategoryRuleAsync(withRule.Id, CancellationToken.None));
        Assert.Null(await store.GetCategoryRuleAsync(without.Id, CancellationToken.None));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Клон_версії_переносить_правило_категорії_у_нову_чернетку()
    {
        await using var db = CreateContext();
        var source = await NewVersionAsync(db, "clone");

        db.MethodologyCategoryRules.Add(source.SetCategoryRule(null, "if(@Fuel = 'D', 'Diesel', 'Gas')", Now));
        await db.SaveChangesAsync();

        var drafts = new MethodologyDraftStore(db);
        var draft = new MethodologyVersion(source.MethodologyId, "2.0", CalculationLevel.Configuration, 1, Now);
        var draftId = await drafts.SaveDraftAsync(draft, source.Id, CancellationToken.None);

        var cloned = await drafts.FindCategoryRuleAsync(draftId, CancellationToken.None);

        Assert.NotNull(cloned);
        Assert.Equal("if(@Fuel = 'D', 'Diesel', 'Gas')", cloned!.Expression);
        Assert.Equal(draftId, cloned.MethodologyVersionId);

        // Джерело не зачеплене: правило лишилось його власним.
        var original = await drafts.FindCategoryRuleAsync(source.Id, CancellationToken.None);
        Assert.NotNull(original);
        Assert.NotEqual(cloned.Id, original!.Id);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Видалення_версії_прибирає_і_її_правило_категорії()
    {
        await using var db = CreateContext();
        var version = await NewVersionAsync(db, "del");

        db.MethodologyCategoryRules.Add(version.SetCategoryRule(null, "!K", Now));
        await db.SaveChangesAsync();

        var deletion = new MethodologyVersionDeletionStore(db);
        var children = await deletion.DeleteAsync(version.Id, CancellationToken.None);

        Assert.Equal(1, children);
        Assert.False(await db.MethodologyCategoryRules.AnyAsync(r => r.MethodologyVersionId == version.Id));
    }

    private static async Task<MethodologyVersion> NewVersionAsync(EcrDbContext db, string probe)
    {
        var tag = Guid.NewGuid().ToString("N")[..8];

        var methodology = new Methodology(
            EcrCode.Create($"ITEST_{probe}_{tag}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "category rule probe" }));
        db.Methodologies.Add(methodology);
        await db.SaveChangesAsync();

        var version = new MethodologyVersion(methodology.Id, "1.0", CalculationLevel.Configuration, 1, Now);
        db.MethodologyVersions.Add(version);
        await db.SaveChangesAsync();

        return version;
    }

    private EcrDbContext CreateContext()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);
}
