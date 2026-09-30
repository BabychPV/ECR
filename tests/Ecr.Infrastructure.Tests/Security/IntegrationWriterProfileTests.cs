// tests/Ecr.Infrastructure.Tests/Security/IntegrationWriterProfileTests.cs
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Entities.Security;
using Ecr.Infrastructure.Caching;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Security;

/// <summary>
/// <see cref="AccessProfile.IsIntegrationWriter"/> визначає КОНТЕКСТ задачі
/// інтеграції, а не ім'я користувача і не те, хто першим зігрів кеш профілів.
/// </summary>
/// <remarks>
/// ⛔ Умова 1 до права запису інтеграції. Кеш профілів — за <c>userId</c>
/// (+ штамп, + відбиток груп). Прапорець, покладений у кешований профіль,
/// означав би: хто перший побудував профіль <c>svc-integration</c> — HTTP-запит
/// чи задача, — той і визначив права іншого на 30 хвилин. Обидва порядки
/// перевіряються на ОДНОМУ <see cref="MemoryCache"/>.
/// </remarks>
[Collection("SqlServer")]
public sealed class IntegrationWriterProfileTests(SqlServerFixture sql)
{
    /// <remarks>
    /// ⛔ МУТАЦІЙНИЙ ДОКАЗ (е): перенести <c>IntegrationWriter(...)</c> усередину
    /// фабрики кешу (<c>LoadAsync</c>) → другий виклик отримує профіль першого:
    /// тут задача втрачає право, у дзеркальному тесті HTTP його отримує.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "P0-integration-writer")]
    public async Task Спершу_HTTP_потім_задача_прапорець_лише_в_задачі()
    {
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var svcId = await SvcIdAsync();

        Assert.False((await BuildAsync(memory, Http(svcId), svcId)).IsIntegrationWriter);
        Assert.True((await BuildAsync(memory, IntegrationJob(svcId), svcId)).IsIntegrationWriter);
        Assert.False((await BuildAsync(memory, Http(svcId), svcId)).IsIntegrationWriter);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "P0-integration-writer")]
    public async Task Спершу_задача_потім_HTTP_прапорець_лише_в_задачі()
    {
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var svcId = await SvcIdAsync();

        var job = await BuildAsync(memory, IntegrationJob(svcId), svcId);
        var http = await BuildAsync(memory, Http(svcId), svcId);

        Assert.True(job.IsIntegrationWriter);
        Assert.False(http.IsIntegrationWriter);

        // ⚠ Ключі теж різні: жоден споживач, що кешує за ключем профілю, не
        // змішає два профілі одного запису.
        Assert.NotEqual(job.CacheKey, http.CacheKey);

        // Профіль задачі — без функціональних прав, грантів і ролей.
        Assert.Empty(job.Permissions);
        Assert.Empty(job.Grants);
        Assert.Empty(job.RoleIds);
    }

    /// <summary>Не за ім'ям: <c>svc-integration</c> у задачі ЛЮДИНИ (звичайний <c>Enter</c>) права не має.</summary>
    /// <remarks>
    /// ⛔ МУТАЦІЙНИЙ ДОКАЗ: <c>currentUser.IsIntegrationJob</c> → перевірка
    /// <c>UserName == "svc-integration"</c> → тут і в HTTP-тестах вище прапорець є.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "P0-integration-writer")]
    public async Task Svc_integration_у_звичайній_задачі_прапорця_не_має()
    {
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var svcId = await SvcIdAsync();

        var scope = new JobActorScope();
        using var _ = scope.Enter(Actor(svcId));

        var profile = await BuildAsync(memory, new JobAwareCurrentUser(Anonymous(), scope), svcId);

        Assert.False(profile.IsIntegrationWriter);
    }

    /// <summary>Прапорець — лише для профілю САМОГО автора задачі, не для чужого <c>userId</c>.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "P0-integration-writer")]
    public async Task У_задачі_інтеграції_профіль_іншого_користувача_прапорця_не_має()
    {
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var svcId = await SvcIdAsync();
        var otherId = await OtherActiveUserIdAsync();

        var profile = await BuildAsync(memory, IntegrationJob(svcId), otherId);

        Assert.False(profile.IsIntegrationWriter);
    }

    private async Task<AccessProfile> BuildAsync(MemoryCache memory, ICurrentUser currentUser, int userId)
    {
        await using var db = sql.CreateContext();

        return await new AccessDecisionService(
                db,
                Substitute.For<IMetadataCache>(),
                new AccessProfileCache(memory),
                new TestClock(DateTime.UtcNow),
                currentUser,
                Substitute.For<IWorkflowStore>())
            .BuildProfileAsync(userId, CancellationToken.None);
    }

    /// <summary>HTTP-запит із cookie <c>svc-integration</c>: задачі немає.</summary>
    private static JobAwareCurrentUser Http(int svcId)
        => new JobAwareCurrentUser(RequestUser(svcId), new JobActorScope());

    /// <summary>Задача інтеграції: автора встановлено <c>EnterIntegration</c>, як у <c>IntegrationActor</c>.</summary>
    private static JobAwareCurrentUser IntegrationJob(int svcId)
    {
        var scope = new JobActorScope();
        scope.EnterIntegration(Actor(svcId));
        return new JobAwareCurrentUser(Anonymous(), scope);
    }

    private static JobActor Actor(int svcId)
        => new(svcId, User.IntegrationServiceUserName, "en", [], Guid.NewGuid().ToString("N"));

    private static ICurrentUser RequestUser(int userId)
    {
        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(userId);
        user.UserName.Returns(User.IntegrationServiceUserName);
        user.GroupSids.Returns([]);
        return user;
    }

    private static ICurrentUser Anonymous()
    {
        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns((int?)null);
        user.GroupSids.Returns([]);
        return user;
    }

    private async Task<int> SvcIdAsync()
    {
        await using var db = sql.CreateContext();
        return await db.Users.Where(u => u.UserName == User.IntegrationServiceUserName).Select(u => u.Id).SingleAsync();
    }

    /// <summary>Свіжий активний локальний користувач: у спільній базі іншого активного може не бути.</summary>
    private async Task<int> OtherActiveUserIdAsync()
    {
        await using var db = sql.CreateContext();

        var name = $"iw_{Guid.NewGuid():N}"[..20];
        var user = new User(name, name, Ecr.Domain.Enums.AuthProvider.Local);
        user.SetPassword("not-a-real-hash"); // CK_User_Provider: локальному — хеш.
        db.Users.Add(user);
        await db.SaveChangesAsync();

        return user.Id;
    }
}
