using Ecr.Application.Calculations;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Calculations;

/// <summary>
/// Аудит L7-10: ключ очікування золотого тесту перевіряється при ЗБЕРЕЖЕННІ
/// (422), а не вперше на публікації (500).
/// </summary>
/// <remarks>
/// ⛔ Що було. <c>SaveMethodologyTestCaseHandler</c> перевіряв лише, що
/// <c>expectedJson</c> — словник «рядок → число», тож <c>tons@abc</c>,
/// <c>tons@</c>, <c>@901</c> зберігались, а <c>GoldenSet.Judge</c> на публікації
/// й симуляції кидав InvalidOperationException → 500.
/// </remarks>
public sealed class TestCaseExpectedKeyTests
{
    private const string Input = """{"arguments":[]}""";

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [InlineData("tons@abc")]
    [InlineData("tons@")]
    [InlineData("@901")]
    public async Task Ключ_очікування_що_не_читається_відхиляється_422(string key)
    {
        var drafts = Substitute.For<IMethodologyDraftStore>();

        var error = await Assert.ThrowsAsync<BusinessRuleException>(() => Handler(drafts).HandleAsync(
            5, "T1", Input, $$"""{"{{key}}": 1.5}""", 0m, CancellationToken.None));

        Assert.Equal("ECR-CALC-0422", error.ErrorCode);
        Assert.Equal("err.ECR-CALC-0422.testCaseJsonInvalid", error.Details!["messageKey"]);
        Assert.Equal("expectedJson", error.Details!["field"]);
        Assert.Equal($"$.{key}", error.Details!["reason"]);
        await drafts.DidNotReceiveWithAnyArgs().FindVersionAsync(default, default);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [InlineData("tons", true, null, null)]
    [InlineData("tons@901", true, "tons", 901)]
    [InlineData("tons@abc", false, null, null)]
    [InlineData("@901", false, null, null)]
    public void Розбір_ключа_один_для_збереження_й_публікації(string key, bool ok, string? code, int? substance)
    {
        Assert.Equal(ok, GoldenSet.TryParseKey(key, out var parsedCode, out var parsedSubstance));
        if (ok)
        {
            Assert.Equal(code ?? key, parsedCode);
            Assert.Equal(substance, parsedSubstance);
        }
    }

    private static SaveMethodologyTestCaseHandler Handler(IMethodologyDraftStore drafts)
    {
        var access = Substitute.For<IAccessDecisionService>();
        access.BuildProfileAsync(1, Arg.Any<CancellationToken>()).Returns(new AccessBuilder { UserId = 1 }
            .Permission(SaveMethodologyTestCaseHandler.Permission)
            .Build());

        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(1);

        return new SaveMethodologyTestCaseHandler(drafts, Substitute.For<IUnitOfWork>(), access, user);
    }
}
