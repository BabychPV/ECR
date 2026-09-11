// tests/Ecr.Infrastructure.Tests/Jobs/SqlDistributedLockTests.cs
using Ecr.Infrastructure.Jobs;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// <see cref="SqlDistributedLock"/> — на <b>реальному</b> SQL Server.
/// </summary>
/// <remarks>
/// ⛔ Q-223 (`Jobs`-секція): цей примітив — те, що заважає N інстансам
/// застосунку виконати той самий нічний/погодинний job одночасно. Мокати
/// <c>sp_getapplock</c> тут безглуздо — уся цінність тесту саме в тому, що
/// СЕРВЕР, а не наш код, вирішує, хто встиг першим.
/// </remarks>
[Collection("SqlServer")]
public sealed class SqlDistributedLockTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Другий_лок_на_той_самий_ресурс_не_проходить_поки_перший_тримає()
    {
        var resource = $"test-{Guid.NewGuid():N}";

        await using var first = await SqlDistributedLock.TryAcquireAsync(sql.ConnectionString, resource, CancellationToken.None);
        Assert.NotNull(first);

        await using var second = await SqlDistributedLock.TryAcquireAsync(sql.ConnectionString, resource, CancellationToken.None);

        // ⛔ Головне твердження: без цього класу другий виклик так само
        // "успішно" пройшов би далі, і обидва "інстанси" виконали б job.
        Assert.Null(second);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Звільнений_лок_дає_ресурс_знову_вільним()
    {
        var resource = $"test-{Guid.NewGuid():N}";

        var first = await SqlDistributedLock.TryAcquireAsync(sql.ConnectionString, resource, CancellationToken.None);
        Assert.NotNull(first);
        await first!.DisposeAsync();

        // ⚠ Те саме з'єднання, що трималося попереднім локом, тепер закрите —
        // без явного sp_releaseapplock (а не лише закриття з'єднання) це
        // теж спрацювало б, але перевіряємо саме контракт DisposeAsync.
        await using var second = await SqlDistributedLock.TryAcquireAsync(sql.ConnectionString, resource, CancellationToken.None);
        Assert.NotNull(second);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Різні_ресурси_не_блокують_одне_одного()
    {
        var resourceA = $"test-{Guid.NewGuid():N}";
        var resourceB = $"test-{Guid.NewGuid():N}";

        await using var lockA = await SqlDistributedLock.TryAcquireAsync(sql.ConnectionString, resourceA, CancellationToken.None);
        await using var lockB = await SqlDistributedLock.TryAcquireAsync(sql.ConnectionString, resourceB, CancellationToken.None);

        // ⛔ Нічна перевірка й погодинна задача — різні ресурси: одна не
        // повинна чекати на іншу лише тому, що обидві "recurring".
        Assert.NotNull(lockA);
        Assert.NotNull(lockB);
    }
}
