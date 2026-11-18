using Ecr.Infrastructure.Jobs;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// B167 (правило 429): <see cref="FlertRegistryProvisioner"/> не виконується одночасно — його кличе лише
/// <c>FlertCompositionSyncJob.ApplyAsync</c>, а той іде ПІСЛЯ applock-а <see cref="FlertCompositionSyncJob.LockResource"/>
/// (сеансовий, між інстансами, без очікування). Другий прогін (інший інстанс, розклад + кнопка) — відкладення, а не другий
/// провізор: гонки на <c>UQ_RegistryDef</c>/<c>UQ_RegistryValue</c> у довідниках дзеркала немає.
/// </summary>
public sealed partial class FlertCompositionSyncJobTests
{
    /// <summary>
    /// e5: поки інший прогін тримає applock синку, прогін відкладається (<see cref="JobDeferredException"/>) і не заводить
    /// жодного довідника; після звільнення той самий прогін заводить усі чотири.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "B167")]
    public async Task e5_Другий_прогін_під_applock_ом_першого_відкладено_і_провізор_не_виконано()
    {
        var source = new FakeFlert(Snapshot());
        var dataSource = await ArrangeSourceAsync();
        await using var provider = BuildProvider();

        await using (var held = await SqlDistributedLock.TryAcquireAsync(sql.ConnectionString, FlertCompositionSyncJob.LockResource, CancellationToken.None))
        {
            Assert.NotNull(held);
            await Assert.ThrowsAsync<JobDeferredException>(() => RunAsync(provider, source, dataSource));

            await using var db = Context();
            Assert.Equal(0, await db.RegistryDefs.AsNoTracking().CountAsync(d => d.Code.EndsWith(_tag)));
        }

        var after = await RunAsync(provider, source, dataSource);
        Assert.Equal(FlertCompositionSyncJob.Succeeded, after.Status);
        Assert.Equal(4, after.CreatedRegistries.Count);
    }
}
