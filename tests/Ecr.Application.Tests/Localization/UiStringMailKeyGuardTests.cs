// tests/Ecr.Application.Tests/Localization/UiStringMailKeyGuardTests.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Localization;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Localization;

/// <summary>S7 ent6: тексти листів (<c>notifications.*.subject|body</c>) правляться лише з правом на сповіщення.</summary>
public sealed class UiStringMailKeyGuardTests
{
    private const int Editor = 5;
    private const string MailKey = "notifications.periodOpened.body";

    private readonly FakeUiStringCatalog _catalog = new FakeUiStringCatalog()
        .Add("en", MailKey, "A new period {period} opened.", UiStringScope.Private)
        .Add("en", "notifications.title", "Notifications", UiStringScope.Private)
        .Add("ru", MailKey, "Открыт период {period}.", UiStringScope.Private);

    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly IClock _clock = Substitute.For<IClock>();

    public UiStringMailKeyGuardTests()
    {
        _user.UserId.Returns(Editor);
        _clock.UtcNow.Returns(new DateTime(2026, 10, 2, 8, 0, 0, DateTimeKind.Utc));
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [InlineData("notifications.periodOpened.subject", true)]
    [InlineData("notifications.periodGraceStarted.body", true)]
    [InlineData("notifications.title", false)]
    [InlineData("notifications.kind.Smtp", false)]
    [InlineData("notifications.test.smtp.dns", false)]
    [InlineData("other.periodOpened.body", false)]
    public void Ключі_листів_відрізняються_від_підписів_сторінки_за_суфіксом(string key, bool mail)
        => Assert.Equal(mail, UiStringMailKeys.IsMailTemplate(key));

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task Правка_тексту_листа_без_права_на_сповіщення_відхиляється_403_а_підпис_сторінки_проходить()
    {
        // ⚠ МУТАЦІЯ: прибрати перевірку IsMailTemplate у SetUiStringHandler → перший виклик не кидає.
        Grant(SetUiStringHandler.Permission);

        var denied = await Assert.ThrowsAsync<AccessDeniedException>(() => Set().HandleAsync(
            MailKey, "ru", "Нажмите http://evil.example {period}", (byte)UiStringScope.Private, CancellationToken.None));
        Assert.Equal(UiStringMailKeys.Permission, denied.Details!["permission"]);

        var revision = await Set().HandleAsync(
            "notifications.title", "ru", "Уведомления", (byte)UiStringScope.Private, CancellationToken.None);
        Assert.True(revision > 0);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task Власник_обох_прав_правит_текст_листа()
    {
        Grant(SetUiStringHandler.Permission, UiStringMailKeys.Permission);

        var revision = await Set().HandleAsync(
            MailKey, "ru", "Период {period} открыт.", (byte)UiStringScope.Private, CancellationToken.None);

        Assert.True(revision > 0);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task CSV_імпорт_без_права_на_сповіщення_відхиляє_рядок_листа_із_правом_403()
    {
        // ⚠ МУТАЦІЯ: прибрати гілку `!mayEditMail && IsMailTemplate` в UiStringImportHandler → помилок немає.
        Grant(SetUiStringHandler.Permission);
        var csv = $"key,ru\r\n{MailKey},Откройте http://evil.example {{period}}\r\nnotifications.title,Уведомления\r\n";

        var report = await Import(csv);

        var error = Assert.Single(report.Errors);
        Assert.Equal(MailKey, error.Key);
        Assert.Equal("err.ECR-AUTH-0403.permission", error.MessageKey);
        Assert.False(report.Applied);

        Grant(SetUiStringHandler.Permission, UiStringMailKeys.Permission);
        Assert.Empty((await Import(csv)).Errors);
    }

    private Task<UiStringImportReport> Import(string csv)
    {
        var handler = new UiStringImportHandler(
            _catalog, _access, Substitute.For<IUnitOfWork>(), Substitute.For<IAuditWriter>(), _user, _clock);

        return handler.HandleAsync("ru", csv, csv.Length, UiStringImportHandler.DefaultMaxBytes, dryRun: true, default);
    }

    private SetUiStringHandler Set()
        => new(_catalog, _access, Substitute.For<IUnitOfWork>(), Substitute.For<IAuditWriter>(), _user, _clock);

    private void Grant(params string[] permissions)
    {
        var builder = new AccessBuilder { UserId = Editor };

        foreach (var permission in permissions)
        {
            builder.Permission(permission);
        }

        _access.BuildProfileAsync(Editor, Arg.Any<CancellationToken>()).Returns(builder.Build());
    }
}