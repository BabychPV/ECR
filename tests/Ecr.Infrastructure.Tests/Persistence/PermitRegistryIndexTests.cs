// tests/Ecr.Infrastructure.Tests/Persistence/PermitRegistryIndexTests.cs
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// The permit-type index learns a newly published type at once after <c>Invalidate</c> (called by the publish handler and
/// the group create handler) — not within its 30 s interval.
/// </summary>
/// <remarks>
/// ⛔ e2e 42630598595 (full run): a type published and used within the interval of an earlier read was not «permit-type»
/// yet; a months-in-columns permit column fell back to the period-end check and a legit permit was refused. Red without
/// <c>Invalidate</c> (the interface default does nothing): the new header stays unknown.
/// </remarks>
[Collection("SqlServer")]
public sealed class PermitRegistryIndexTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "E2E-42630598595")]
    public async Task A_type_published_after_a_read_is_known_at_once_after_invalidate()
    {
        await using var services = new ServiceCollection()
            .AddDbContext<EcrDbContext>(o => o.UseSqlServer(sql.ConnectionString))
            .BuildServiceProvider();
        using var index = new PermitRegistryIndex(services.GetRequiredService<IServiceScopeFactory>());

        _ = await index.PermitTargetIdsAsync(CancellationToken.None); // warm: the cache is fresh for 30 s

        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        int headerId;
        await using (var db = sql.CreateContext())
        {
            var text = new LocalizedText(new Dictionary<string, string> { ["en"] = "Idx" });
            var header = new RegistryDef(EcrCode.Create($"PRM_IX{tag}"), text, isTemporal: true);
            db.RegistryDefs.Add(header);
            await db.SaveChangesAsync();
            var type = new PermitTypeDef(EcrCode.Create($"IX{tag}"), text, PermitCategory.Water, header.Id);
            type.Publish(null, DateTime.UtcNow);
            db.PermitTypeDefs.Add(type);
            await db.SaveChangesAsync();
            headerId = header.Id;
        }

        // Still the warm set — the staleness the e2e hit.
        Assert.DoesNotContain(headerId, await index.PermitTargetIdsAsync(CancellationToken.None));

        index.Invalidate();

        Assert.Contains(headerId, await index.PermitTargetIdsAsync(CancellationToken.None));
    }
}
