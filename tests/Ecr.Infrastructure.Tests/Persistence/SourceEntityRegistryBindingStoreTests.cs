// tests/Ecr.Infrastructure.Tests/Persistence/SourceEntityRegistryBindingStoreTests.cs
using Ecr.Application.Errors;
using Ecr.Application.Sources;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// AN-34 L4-01: сховище сутностей збору на живому SQL Server - «довідник уже тримає інша сутність
/// з'єднання» (перевірка обробника) і програна гонка (порушення <c>UQ_SourceEntity_Registry</c>)
/// дають ту саму доменну відмову <c>409 ECR-INT-0409 registryAlreadyBound</c>, а не голий 500.
/// </summary>
[Collection("SqlServer")]
public sealed class SourceEntityRegistryBindingStoreTests(SqlServerFixture sql)
{
    private readonly string _tag = Guid.NewGuid().ToString("N")[..8];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "AN-34-L4-01")]
    public async Task Інша_сутність_того_ж_з_єднання_на_довіднику_видима_а_чужого_з_єднання_чи_власна_ні()
    {
        var (dataSourceId, otherDataSourceId, registryId, a, b, c) = await ArrangeAsync();

        await using var db = Context();
        var store = new CollectionStore(db, new TestClock(DateTime.UtcNow));

        // A тримає довідник: для B - зайнято; для самої A (її власний рядок) - ні.
        Assert.True(await store.RegistryBoundByOtherEntityAsync(dataSourceId, registryId, b, CancellationToken.None));
        Assert.False(await store.RegistryBoundByOtherEntityAsync(dataSourceId, registryId, a, CancellationToken.None));

        // Інше з'єднання тримає той самий довідник незалежно (C): для самої C - не конфлікт, а A у
        // з'єднання 1 не бачить C; довідник без прив'язок - вільний.
        Assert.False(await store.RegistryBoundByOtherEntityAsync(otherDataSourceId, registryId, c, CancellationToken.None));
        Assert.False(await store.RegistryBoundByOtherEntityAsync(dataSourceId, registryId + 1_000_000, b, CancellationToken.None));

        // ⛔ Вимкнена A теж тримає довідник: індекс не дивиться на IsActive.
        var tracked = await db.SourceEntities.SingleAsync(e => e.Id == a);
        tracked.Deactivate();
        await db.SaveChangesAsync();
        Assert.True(await store.RegistryBoundByOtherEntityAsync(dataSourceId, registryId, b, CancellationToken.None));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "AN-34-L4-01")]
    public async Task Збереження_другої_прив_язки_ловить_індекс_і_дає_409_registryAlreadyBound()
    {
        var (dataSourceId, _, registryId, _, b, _) = await ArrangeAsync();

        // Гонка: перевірка обробника пройшла б (стан читається до чужого коміту), а запис б'ється
        // об UQ_SourceEntity_Registry. Мутація: прибрати catch у CollectionStore.SaveSourceEntityAsync -
        // DbUpdateException замість BusinessRuleException.
        await using var db = Context();
        var store = new CollectionStore(db, new TestClock(DateTime.UtcNow));
        var second = await store.FindSourceEntityAsync(b, CancellationToken.None);
        second!.BindRegistry(registryId);

        var ex = await Assert.ThrowsAsync<BusinessRuleException>(
            () => store.SaveSourceEntityAsync(second, CancellationToken.None));

        Assert.Equal(ErrorCodes.EntityFieldMapStateConflict, ex.ErrorCode);
        Assert.Equal(BindSourceEntityRegistryHandler.RegistryAlreadyBoundKey, ex.Details!["messageKey"]);
        Assert.Equal(
            dataSourceId.ToString(System.Globalization.CultureInfo.InvariantCulture), ex.Details["dataSourceId"]);
    }

    /// <summary>Довідник, два з'єднання; сутність A (з'єднання 1) тримає довідник, B (з'єднання 1) вільна, C (з'єднання 2) теж тримає.</summary>
    private async Task<(int DataSourceId, int OtherDataSourceId, int RegistryId, int A, int B, int C)> ArrangeAsync()
    {
        await using var db = Context();

        var registry = new RegistryDef(EcrCode.Create($"AN34R_{_tag}"), Text("Registry"), isTemporal: false);
        db.RegistryDefs.Add(registry);
        var first = new DataSource(
            EcrCode.Create($"AN34A{_tag}"), Text("PI A"), ExternalTransport.PiWebApi, "https://af-a.test", "secret");
        var second = new DataSource(
            EcrCode.Create($"AN34B{_tag}"), Text("PI B"), ExternalTransport.PiWebApi, "https://af-b.test", "secret");
        db.DataSources.AddRange(first, second);
        await db.SaveChangesAsync();

        var a = new SourceEntity(first.Id, $"PlantA_{_tag}", RegistrySourceKind.External);
        a.BindRegistry(registry.Id);
        var b = new SourceEntity(first.Id, $"PlantB_{_tag}", RegistrySourceKind.External);
        var c = new SourceEntity(second.Id, $"PlantC_{_tag}", RegistrySourceKind.External);
        c.BindRegistry(registry.Id);
        db.SourceEntities.AddRange(a, b, c);
        await db.SaveChangesAsync();

        return (first.Id, second.Id, registry.Id, a.Id, b.Id, c.Id);
    }

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

    private static LocalizedText Text(string value) => new(new Dictionary<string, string> { ["en"] = value });
}
