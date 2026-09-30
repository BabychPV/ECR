// tests/Ecr.Infrastructure.Tests/Persistence/RegistryUseStoreTests.cs
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Ребра <c>cfg.RegistryUse</c> версії методології (<c>SourceKind = 1</c>, RT-23b, FEATURE-REGISTRY-TABLES
/// §5.8): <see cref="RegistryUseStore.ReplaceMethodologyUsesAsync"/> на справжній базі, зі збереженням
/// через <see cref="UnitOfWork"/> — так, як його викликає публікація.
/// </summary>
/// <remarks>
/// Мутаційні докази: без <c>AddRange</c> → <see cref="Публікація_записує_використання"/> червоний;
/// без <c>RemoveRange</c> → <see cref="Повторна_публікація_перезаписує_а_не_дублює"/> червоний; без фільтра
/// <c>SourceKind</c> → <see cref="Чужі_ребра_не_чіпаються"/> червоний.
/// </remarks>
[Collection("SqlServer")]
public sealed class RegistryUseStoreTests(SqlServerFixture sql)
{
    private readonly string _tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    // Версії методології без FK (SourceId поліморфний); унікальні на прогін, щоб не стикатись з іншими тестами.
    private readonly int _version = Random.Shared.Next(1_000_000, 900_000_000);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Публікація_записує_використання()
    {
        var registry = await SeedRegistryAsync("A");

        await ReplaceAsync(_version,
            RegistryUse.ForMethodologyFormula(_version, "F1", registry, "COMPONENT.MW"),
            RegistryUse.ForMethodologyFormula(_version, "F2", registry, null));

        Assert.Equal(
            [$"F1:{registry}:COMPONENT.MW", $"F2:{registry}:"],
            await UsesAsync(_version));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Повторна_публікація_перезаписує_а_не_дублює()
    {
        var registry = await SeedRegistryAsync("A");
        var other = await SeedRegistryAsync("B");

        await ReplaceAsync(_version, RegistryUse.ForMethodologyFormula(_version, "F1", registry, "X"));
        // Та сама публікація вдруге — ті самі ребра, без дубля.
        await ReplaceAsync(_version, RegistryUse.ForMethodologyFormula(_version, "F1", registry, "X"));
        Assert.Equal([$"F1:{registry}:X"], await UsesAsync(_version));

        // Нова редакція формул — старе ребро зникає, а не лишається поруч.
        await ReplaceAsync(_version, RegistryUse.ForMethodologyFormula(_version, "F1", other, "Y"));
        Assert.Equal([$"F1:{other}:Y"], await UsesAsync(_version));

        // Версія більше не читає довідників — усі ребра прибрано.
        await ReplaceAsync(_version);
        Assert.Empty(await UsesAsync(_version));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Чужі_ребра_не_чіпаються()
    {
        var registry = await SeedRegistryAsync("A");
        var neighbour = _version + 1;

        // Ребро шаблону й правила з тим самим SourceId, ребро сусідньої версії.
        await using (var db = Context())
        {
            db.RegistryUses.AddRange(
                RegistryUse.ForTemplateFormula(_version, registry, "T"),
                RegistryUse.ForRegistryRule(_version, registry, "R"),
                RegistryUse.ForMethodologyFormula(neighbour, "N", registry, "Z"));
            await db.SaveChangesAsync();
        }

        await ReplaceAsync(_version, RegistryUse.ForMethodologyFormula(_version, "F1", registry, "X"));
        await ReplaceAsync(_version);

        await using var check = Context();
        var left = await check.RegistryUses.AsNoTracking()
            .Where(u => u.SourceId == _version || u.SourceId == neighbour)
            .ToListAsync();
        Assert.Equal(
            ["0:T", "1:Z", "2:R"],
            left.Select(u => $"{u.SourceKind}:{u.FieldPath}").Order(StringComparer.Ordinal));
    }

    private async Task ReplaceAsync(int versionId, params RegistryUse[] uses)
    {
        await using var db = Context();
        await new RegistryUseStore(db).ReplaceMethodologyUsesAsync(versionId, uses, CancellationToken.None);
        await new UnitOfWork(db).SaveChangesAsync(CancellationToken.None);
    }

    /// <summary>Ребра версії як «формула:довідник:шлях», упорядковано.</summary>
    private async Task<string[]> UsesAsync(int versionId)
    {
        await using var db = Context();
        var uses = await db.RegistryUses.AsNoTracking()
            .Where(u => u.SourceKind == RegistryUse.MethodologyVersionSource && u.SourceId == versionId)
            .ToListAsync();
        return [.. uses.Select(u => $"{u.FormulaCode}:{u.RegistryDefId}:{u.FieldPath}").Order(StringComparer.Ordinal)];
    }

    private async Task<int> SeedRegistryAsync(string suffix)
    {
        await using var db = Context();
        var def = new RegistryDef(
            EcrCode.Create($"MU{_tag}{suffix}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Registry" }),
            isTemporal: false);
        db.RegistryDefs.Add(def);
        await db.SaveChangesAsync();
        return def.Id;
    }

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);
}