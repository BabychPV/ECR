// tests/Ecr.Infrastructure.Tests/Persistence/RegistryRetargetConsumersTests.cs
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Хто йде через поле-посилання довідника (<c>FIELD.attr</c> у <c>cfg.RegistryUse</c>) — доказ
/// на реальному SQL Server для відмови ретаргета (<c>ФВ-8.12</c>, <c>lookupRetargetUsedByRules</c>).
/// </summary>
/// <remarks>
/// Мутаційні докази: без фільтра за префіксом поля червоний
/// <c>Знаходить_правило_що_йде_через_поле_і_не_чіпає_решту</c> (чуже поле й шлях без переходу
/// потрапили б у перелік); без `RegistryDefId == …` — те саме для чужого довідника.
/// </remarks>
[Collection("SqlServer")]
public sealed class RegistryRetargetConsumersTests(SqlServerFixture sql)
{
    private readonly string _tag = Guid.NewGuid().ToString("N")[..8];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-8.12")]
    public async Task Знаходить_правило_що_йде_через_поле_і_не_чіпає_решту()
    {
        var (ownerId, ownerCode) = await SeedAsync();

        await using var db = Context();
        var store = new RegistryStore(db);

        var found = await store.FindFieldChainConsumersAsync(ownerId, ["LINK"], 20, default);

        // Лише правило з `LINK.Capacity` (регістр шляху не важливий); `LINK` без переходу,
        // `OTHER.X` і ребро ІНШОГО довідника в перелік не потрапили.
        Assert.Equal(1, found.Total);
        var item = Assert.Single(found.Items);
        Assert.Equal($"{ownerCode}.CAP_{_tag}", item.Label);
        Assert.Equal(Ecr.Application.Common.UsageKinds.RegistryField, item.Kind);

        Assert.Equal(0, (await store.FindFieldChainConsumersAsync(ownerId, ["NOPE"], 20, default)).Total);
        Assert.Equal(0, (await store.FindFieldChainConsumersAsync(ownerId, [], 20, default)).Total);

        // Перелік обмежується `take`, а кількість лишається повною.
        var capped = await store.FindFieldChainConsumersAsync(ownerId, ["LINK", "OTHER"], 1, default);
        Assert.Equal(2, capped.Total);
        Assert.Single(capped.Items);
    }

    private async Task<(int OwnerId, string OwnerCode)> SeedAsync()
    {
        await using var db = Context();

        var owner = new RegistryDef(EcrCode.Create($"OWN_{_tag}"), Text("Власник"), isTemporal: false);
        var stranger = new RegistryDef(EcrCode.Create($"STR_{_tag}"), Text("Чужий"), isTemporal: false);
        db.RegistryDefs.AddRange(owner, stranger);
        await db.SaveChangesAsync();

        var capacity = new RegistryRuleDef(
            owner.Id, EcrCode.Create($"CAP_{_tag}"), RegistryRuleKind.Expression, "ROW.LINK.Capacity > 0",
            ValidationSeverity.Error, Text("cap"));
        var other = new RegistryRuleDef(
            owner.Id, EcrCode.Create($"OTH_{_tag}"), RegistryRuleKind.Expression, "ROW.OTHER.X > 0",
            ValidationSeverity.Error, Text("oth"));
        var plain = new RegistryRuleDef(
            owner.Id, EcrCode.Create($"PLN_{_tag}"), RegistryRuleKind.Expression, "ROW.LINK != null",
            ValidationSeverity.Error, Text("plain"));
        var foreign = new RegistryRuleDef(
            stranger.Id, EcrCode.Create($"FOR_{_tag}"), RegistryRuleKind.Expression, "ROW.LINK.Capacity > 0",
            ValidationSeverity.Error, Text("for"));
        db.RegistryRuleDefs.AddRange(capacity, other, plain, foreign);
        await db.SaveChangesAsync();

        db.RegistryUses.AddRange(
            RegistryUse.ForRegistryRule(capacity.Id, owner.Id, "link.capacity"),
            RegistryUse.ForRegistryRule(other.Id, owner.Id, "OTHER.X"),
            RegistryUse.ForRegistryRule(plain.Id, owner.Id, "LINK"),
            RegistryUse.ForRegistryRule(foreign.Id, stranger.Id, "LINK.Capacity"));
        await db.SaveChangesAsync();

        return (owner.Id, owner.Code);
    }

    private static LocalizedText Text(string value)
        => new(new Dictionary<string, string> { ["en"] = value });

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString)
            .Options);
}
