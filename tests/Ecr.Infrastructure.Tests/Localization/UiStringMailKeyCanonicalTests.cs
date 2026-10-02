// tests/Ecr.Infrastructure.Tests/Localization/UiStringMailKeyCanonicalTests.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Localization;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Infrastructure.Localization;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Localization;

/// <summary>
/// S7: колація <c>CI_AS</c> зіставляє <c>Notifications.x.Subject</c> і <c>x.body </c> зі справжнім ключем листа, тож
/// перевірка права мусить іти по КАНОНІЧНОМУ ключу бази. Живий SQL Server: фейк колації не має.
/// </summary>
[Collection("SqlServer")]
public sealed class UiStringMailKeyCanonicalTests(SqlServerFixture sql)
{
    // Власний ключ тесту: посіяні тексти листів не чіпаємо.
    private const string Real = "notifications.s7canon.body";
    private const int Editor = 5;

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [InlineData("Notifications.s7canon.Body")]
    [InlineData("notifications.s7canon.body ")]
    [InlineData("NOTIFICATIONS.S7CANON.BODY")]
    [InlineData("notifications.s7canon.body")]
    public async Task Інший_регістр_чи_кінцевий_пробіл_ключа_листа_без_права_на_сповіщення_це_403_і_текст_не_змінюється(string spelled)
    {
        // ⛔ МУТАЦІЇ: повернути Ordinal-порівняння в UiStringMailKeys.IsMailTemplate і прибрати звірку з
        // `FindKeyAsync` в SetUiStringHandler → перші три варіанти пишуть чужий текст і тест червоніє.
        await using var db = new EcrDbContext(
            new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var store = new UiStringCatalogStore(db, memory);

        await store.SetAsync(
            new UiStringWrite(Real, "en", "original", UiStringScope.Private, null, DateTime.UtcNow), CancellationToken.None);

        var access = Substitute.For<IAccessDecisionService>();
        access.BuildProfileAsync(Editor, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = Editor }.Permission(SetUiStringHandler.Permission).Build());
        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(Editor);
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(DateTime.UtcNow);
        var handler = new SetUiStringHandler(
            store, access, Substitute.For<IUnitOfWork>(), Substitute.For<IAuditWriter>(), user, clock);

        await Assert.ThrowsAsync<AccessDeniedException>(
            () => handler.HandleAsync(spelled, "en", "evil http://phish.example", (byte)UiStringScope.Private, CancellationToken.None));

        var catalog = await store.GetAsync("en", CancellationToken.None);
        Assert.Equal("original", catalog.Strings[Real]);
        Assert.Equal(Real, await store.FindKeyAsync(spelled, CancellationToken.None));
    }
}