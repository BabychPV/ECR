// tests/Ecr.Infrastructure.Tests/Persistence/MethodologyCategoryStoreTests.cs
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Persistence.Migrations;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// План CONST-CATEGORY і похідні аргументи на РЕАЛЬНОМУ SQL Server: перелік категорій (сід міграції, клон,
/// видалення версії, лічба констант) і крок старту <see cref="MethodologyArgumentsBackfill"/>.
/// </summary>
/// <remarks>
/// ⛔ Мутаційні докази: клон без циклу <c>MethodologyCategories</c> у <c>CopyChildrenAsync</c> — червоніє
/// <see cref="Клон_версії_переносить_перелік_категорій"/>; видалення версії без рядка категорій зупиняє FK
/// (<see cref="Видалення_версії_прибирає_і_її_категорії"/>).
/// </remarks>
[Collection("SqlServer")]
public sealed class MethodologyCategoryStoreTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 11, 2, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Сід_міграції_будує_перелік_з_наявних_категорій_констант_і_повторюваний()
    {
        await using var db = CreateContext();
        var version = await NewVersionAsync(db, "seed");
        var unit = await db.Units.OrderBy(u => u.Id).Select(u => u.Id).FirstAsync();

        db.MethodologyConstants.AddRange(
            Constant(version.Id, unit, "EF_1", "Loc_BeforeMR_B"),
            Constant(version.Id, unit, "EF_2", "Loc_BeforeMR_B", new DateOnly(2026, 1, 1)),
            Constant(version.Id, unit, "EF_1", "Diesel_2754", new DateOnly(2025, 1, 1)),
            Constant(version.Id, unit, "RHO", null));
        await db.SaveChangesAsync();

        await db.Database.ExecuteSqlRawAsync(METH01_MethodologyCategory.SeedCategoriesSql);
        await db.Database.ExecuteSqlRawAsync(METH01_MethodologyCategory.SeedCategoriesSql);

        var list = await db.MethodologyCategories.AsNoTracking()
            .Where(c => c.MethodologyVersionId == version.Id)
            .OrderBy(c => c.Ordinal)
            .Select(c => new { c.Code, c.Ordinal, c.IsRetired })
            .ToListAsync();

        // Регістр збережено, спільна (NULL) у перелік не йде, повтор нічого не дублює.
        Assert.Equal(["Diesel_2754", "Loc_BeforeMR_B"], list.Select(c => c.Code));
        Assert.Equal([1, 2], list.Select(c => c.Ordinal));
        Assert.All(list, c => Assert.False(c.IsRetired));

        var usage = await new MethodologyDraftStore(db).CountConstantsByCategoryAsync(version.Id, CancellationToken.None);
        Assert.Equal(2, usage["Loc_BeforeMR_B"]);
        Assert.Equal(1, usage["Diesel_2754"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Клон_версії_переносить_перелік_категорій()
    {
        await using var db = CreateContext();
        var source = await NewVersionAsync(db, "clone");

        var retired = source.AddCategory("Old", 2);
        retired.Describe("legacy", 2, isRetired: true);
        db.MethodologyCategories.AddRange(source.AddCategory("Diesel", 1), retired);
        await db.SaveChangesAsync();

        var drafts = new MethodologyDraftStore(db);
        var draft = new MethodologyVersion(source.MethodologyId, "2.0", CalculationLevel.Configuration, 1, Now);
        var draftId = await drafts.SaveDraftAsync(draft, source.Id, CancellationToken.None);

        var cloned = await drafts.GetCategoriesTrackedAsync(draftId, CancellationToken.None);

        Assert.Equal(["Diesel", "Old"], cloned.Select(c => c.Code));
        Assert.Equal([false, true], cloned.Select(c => c.IsRetired));
        Assert.Equal("legacy", cloned[1].Description);
        Assert.All(cloned, c => Assert.Equal(draftId, c.MethodologyVersionId));
        Assert.Equal(2, await db.MethodologyCategories.CountAsync(c => c.MethodologyVersionId == source.Id));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Той_самий_код_в_іншому_регістрі_відхиляє_унікальний_індекс()
    {
        await using var db = CreateContext();
        var version = await NewVersionAsync(db, "uq");

        db.MethodologyCategories.Add(version.AddCategory("Diesel", 1));
        await db.SaveChangesAsync();

        db.MethodologyCategories.Add(version.AddCategory("DIESEL", 2));
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Видалення_версії_прибирає_і_її_категорії()
    {
        await using var db = CreateContext();
        var version = await NewVersionAsync(db, "del");

        db.MethodologyCategories.Add(version.AddCategory("Diesel", 1));
        await db.SaveChangesAsync();

        var children = await new MethodologyVersionDeletionStore(db).DeleteAsync(version.Id, CancellationToken.None);

        Assert.Equal(1, children);
        Assert.False(await db.MethodologyCategories.AnyAsync(c => c.MethodologyVersionId == version.Id));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Крок_старту_переносить_маркери_по_версії_і_вдруге_нічого_не_пише()
    {
        await using var db = CreateContext();
        var version = await NewVersionAsync(db, "bf");

        var a = Formula(version.Id, "A", "@Land_Measure_RunningHours * CST.K", "@Land_Measure_RunningHours?;CST.K;CST.Unused");
        var b = Formula(version.Id, "B", "@Land_Measure_RunningHours + @Land_Power", "@Land_Measure_RunningHours;@Land_Power;@Ghost?");
        db.MethodologyFormulas.AddRange(a, b);
        await db.SaveChangesAsync();

        var first = await new MethodologyArgumentsBackfill(db).RunAsync(CancellationToken.None);

        var rows = await db.MethodologyFormulas.AsNoTracking()
            .Where(f => f.MethodologyVersionId == version.Id)
            .OrderBy(f => f.Code)
            .Select(f => new { f.Code, f.Expression, f.ArgumentsCsv })
            .ToListAsync();

        Assert.Equal("@Land_Measure_RunningHours? * CST.K", rows[0].Expression);
        Assert.Equal("@Land_Measure_RunningHours?;CST.K", rows[0].ArgumentsCsv);
        Assert.Equal("@Land_Measure_RunningHours? + @Land_Power", rows[1].Expression);
        Assert.Equal("@Land_Measure_RunningHours?;@Land_Power", rows[1].ArgumentsCsv);

        var label = $"{(await db.Methodologies.AsNoTracking().SingleAsync(m => m.Id == version.MethodologyId)).Code}/1.0";
        Assert.Contains($"{label}: Ghost", first.DroppedOptional);
        Assert.Contains($"{label}/A: CST.Unused", first.DroppedDeclared);
        Assert.Contains($"{label}/B: @Ghost?", first.DroppedDeclared);

        // Другий старт: версія вже похідна — жодного запису.
        var second = await new MethodologyArgumentsBackfill(db).RunAsync(CancellationToken.None);
        Assert.DoesNotContain(second.DroppedOptional, d => d.StartsWith(label, StringComparison.Ordinal));
        Assert.Equal(0, second.Versions);
    }

    private static MethodologyFormula Formula(int versionId, string code, string expression, string csv)
    {
        var formula = new MethodologyFormula(versionId, EcrCode.Create(code), expression);
        formula.SetArguments(csv);
        return formula;
    }

    private static MethodologyConstant Constant(int versionId, int unitId, string code, string? category, DateOnly? from = null)
    {
        var constant = new MethodologyConstant(versionId, EcrCode.Create(code), 1m, unitId);
        constant.SetScope(category, null);
        constant.SetValidity(from, null);
        return constant;
    }

    private static async Task<MethodologyVersion> NewVersionAsync(EcrDbContext db, string probe)
    {
        var tag = Guid.NewGuid().ToString("N")[..8];

        var methodology = new Methodology(
            EcrCode.Create($"ITEST_CAT_{probe}_{tag}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "category list probe" }));
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
