// tests/Ecr.Application.Tests/Localization/SetUiStringValidationTests.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Localization;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Localization;

/// <summary>
/// B-03: <c>PUT /ui-strings/{lang}/{key}</c> відмовляє поясненням на невідому
/// мову й задовгий текст, а не необробленим <c>SqlException</c> (500).
/// </summary>
public sealed class SetUiStringValidationTests
{
    private const int Editor = 5;

    private readonly FakeUiStringCatalog _catalog = new FakeUiStringCatalog()
        .Add("en", "common.save", "Save", UiStringScope.Public);

    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly IClock _clock = Substitute.For<IClock>();

    public SetUiStringValidationTests()
    {
        _user.UserId.Returns(Editor);
        _user.CorrelationId.Returns("c1");
        _clock.UtcNow.Returns(new DateTime(2026, 3, 1, 8, 0, 0, DateTimeKind.Utc));

        _access.BuildProfileAsync(Editor, Arg.Any<CancellationToken>())
               .Returns(new AccessBuilder { UserId = Editor }
                   .Permission(SetUiStringHandler.Permission)
                   .Build());
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task Невідома_мова_дає_відмову_з_ключем_а_не_виняток_бази()
    {
        var error = await Assert.ThrowsAsync<BusinessRuleException>(() => Handler().HandleAsync(
            "common.save", "xx", "Значення", (byte)UiStringScope.Public, CancellationToken.None));

        Assert.Equal(ErrorCodes.RequestInvalid, error.ErrorCode);
        Assert.Equal("err.ECR-REQ-0422.uiStringUnknownLanguage", error.Details!["messageKey"]);
        Assert.Equal("xx", error.Details["lang"]);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task Задовгий_текст_дає_відмову_з_ключем_а_не_обрізання_базою()
    {
        var tooLong = new string('a', 1001);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(() => Handler().HandleAsync(
            "common.save", "en", tooLong, (byte)UiStringScope.Public, CancellationToken.None));

        Assert.Equal(ErrorCodes.RequestInvalid, error.ErrorCode);
        Assert.Equal("err.ECR-REQ-0422.uiStringTooLong", error.Details!["messageKey"]);
        Assert.Equal("1000", error.Details["maxLength"]);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task Рівно_стеля_довжини_проходить()
    {
        var exactly = new string('a', 1000);

        var revision = await Handler().HandleAsync(
            "common.save", "en", exactly, (byte)UiStringScope.Public, CancellationToken.None);

        Assert.True(revision > 0);
    }

    private SetUiStringHandler Handler() => new(_catalog, _access, _uow, _audit, _user, _clock);
}
