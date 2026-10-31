// tests/Ecr.Infrastructure.Tests/Jobs/SourceEventSyncJobTests.D465.cs
using Ecr.Application.Integration.RawData;
using Ecr.Application.Ports;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// D-465: комірку рядка події востаннє правила людина (D-118) — синк лишає її за людиною, а перегляд показує
/// «ручна правка» з причиною <c>KeptManual</c>, значення PI за останнім прочитанням і те, що записав би синк.
/// </summary>
/// <remarks>
/// Мутація: у <c>PiEventComparison.Decide</c> перевіряти «людину» після «не оновлено» — червоніє (порівняння
/// <c>NotUpdated</c>).
/// </remarks>
public sealed partial class SourceEventSyncJobTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-465")]
    public async Task Правка_людини_у_перегляді_ManualEdit_KeptManual_зі_значенням_PI()
    {
        await using var stand = await ArrangeAsync();
        var source = new FakeEventSource(Ev("E1", Start, End, volume: 10m));
        await RunAsync(stand, source);

        await HumanWriteAsync(stand, "EF-E1", new Application.Documents.Dto.PatchCell(stand.VolumeCode, 42m));

        source.Result = new SourceEventResult([Ev("E1", Start, End, volume: 99m)], false, null);
        await RunAsync(stand, source);

        await using var scope = stand.Provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EcrDbContext>();
        var store = new SourceEventViewStore(db, scope.ServiceProvider.GetRequiredService<IUnitCatalog>());
        var map = (await store.MapAsync(stand.MapId, CancellationToken.None))!;
        var context = await store.ContextAsync(map, CancellationToken.None);
        var link = Assert.Single(await store.LinksOfRowAsync(stand.JanuaryInstanceId, "EF-E1", 5, CancellationToken.None));
        var values = await store.ValuesAsync([link.Id], CancellationToken.None);
        var cells = await store.CellsAsync(
            [new PiRowAddress(202601, stand.JanuaryInstanceId, "EF-E1", stand.DocumentId)], [stand.VolumeColumn], CancellationToken.None);

        var volume = PiEventComparison
            .Compare(link, context, SiteTimeZone.Create(map.TimeZoneId).ToTimeZoneInfo(), values, c => cells.FirstOrDefault(x => x.ColumnDefId == c), _ => true)
            .Single(a => a.ColumnDefId == stand.VolumeColumn);

        Assert.Equal(nameof(PiValueComparison.ManualEdit), volume.Comparison);
        Assert.Equal(nameof(PiValueReason.KeptManual), volume.Reason);
        Assert.Equal(99m, volume.PiValueNumeric);
        Assert.Equal("42", volume.CellValue);
        Assert.Equal(Application.Documents.CellChangeOrigins.UserEdit, volume.CellOrigin);
    }
}
