// tests/Ecr.Application.Tests/Security/ServiceAccountLoginTests.cs
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Security;

/// <summary>
/// Службовим записом <c>svc-integration</c> не входять — ні паролем, ні Windows.
/// </summary>
/// <remarks>
/// ⛔ Умова 3 до права запису інтеграції. Запис заведено сідом із випадковим
/// паролем, і «пароля ніхто не знає» трималося рівно до першого скидання
/// пароля адміністратором (<c>ResetPassword</c> не відрізняв службовий запис
/// від людини). Після скидання вхід проходив — і далі лише контекст задачі
/// відділяв HTTP-сеанс від права писати без грантів. Тепер вхід відкидає сам
/// запис, а тести нижче перевіряють саме ПРАВИЛЬНИЙ пароль.
/// </remarks>
public sealed class ServiceAccountLoginTests
{
    private static readonly DateTime Now = new(2026, 9, 28, 9, 0, 0, DateTimeKind.Utc);

    private readonly FakeUserStore _users = new();
    private readonly IPasswordHasher _hasher = Substitute.For<IPasswordHasher>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IClock _clock = Substitute.For<IClock>();

    public ServiceAccountLoginTests()
    {
        _clock.UtcNow.Returns(Now);
        _hasher.Verify("right", "hash").Returns(true);
    }

    /// <remarks>
    /// ⛔ МУТАЦІЙНИЙ ДОКАЗ (є): прибрати <c>|| user.IsServiceAccount</c> з
    /// <c>LoginHandler.HandleAsync</c> → вхід із правильним паролем успішний.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "P0-integration-writer")]
    public async Task Локальний_вхід_службовим_записом_із_правильним_паролем_відхиляється()
    {
        var service = Local(User.IntegrationServiceUserName);

        var error = await Assert.ThrowsAsync<AccessDeniedException>(
            () => Handler().HandleAsync(User.IntegrationServiceUserName, "right", "10.0.0.1", CancellationToken.None));

        // ⚠ Та сама відповідь, що й на невідоме ім'я: форма входу не має
        // підтверджувати, що службовий запис існує.
        Assert.Equal("ECR-AUTH-0401", error.ErrorCode);
        Assert.Equal("err.ECR-AUTH-0401.invalidCredentials", error.Details!["messageKey"]);
        Assert.Null(service.LastSignInAt);

        // Контроль: людина з тим самим паролем входить.
        Local("petrenko");
        var ok = await Handler().HandleAsync("petrenko", "right", "10.0.0.1", CancellationToken.None);
        Assert.Equal("petrenko", ok.UserName);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "P0-integration-writer")]
    public async Task Windows_вхід_на_SID_службового_запису_відхиляється()
    {
        var service = _users.Seed(FakeUserStore.DomainUser(User.IntegrationServiceUserName, "S-1-5-21-777"));

        var error = await Assert.ThrowsAsync<AccessDeniedException>(
            () => Handler().HandleWindowsAsync(
                "S-1-5-21-777", User.IntegrationServiceUserName, "svc", [], "10.0.0.1", CancellationToken.None));

        Assert.Equal("ECR-AUTH-0401", error.ErrorCode);
        Assert.Null(service.LastSignInAt);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "P0-integration-writer")]
    public async Task Windows_вхід_новим_доменним_записом_із_логіном_службового_відхиляється_і_не_заводить_запис()
    {
        var error = await Assert.ThrowsAsync<AccessDeniedException>(
            () => Handler().HandleWindowsAsync(
                "S-1-5-21-778", "SVC-Integration", "svc", [], "10.0.0.1", CancellationToken.None));

        Assert.Equal("ECR-AUTH-0401", error.ErrorCode);
        Assert.Null(await _users.FindByWindowsSidAsync("S-1-5-21-778", CancellationToken.None));
    }

    private User Local(string name)
    {
        var user = new User(name, name, AuthProvider.Local);
        user.SetPassword("hash");
        return _users.Seed(user);
    }

    private LoginHandler Handler() => new(_users, _hasher, _uow, _clock, NullLogger<LoginHandler>.Instance);
}
