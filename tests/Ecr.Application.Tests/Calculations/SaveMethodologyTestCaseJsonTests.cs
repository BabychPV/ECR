// tests/Ecr.Application.Tests/Calculations/SaveMethodologyTestCaseJsonTests.cs
using Ecr.Application.Calculations;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Calculations;

/// <summary>
/// P2 (walk-reg 2026-10-01): тест золотого набору з некоректним JSON не зберігається
/// з 200 (після чого publish/simulate падали 500), а відхиляється <c>ECR-CALC-0422</c>
/// з іменем тесту й поля.
/// </summary>
public sealed class SaveMethodologyTestCaseJsonTests
{
    private readonly IMethodologyDraftStore _drafts = Substitute.For<IMethodologyDraftStore>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();

    public SaveMethodologyTestCaseJsonTests()
    {
        _user.UserId.Returns(9);
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }.Permission(SaveMethodologyTestCaseHandler.Permission).Build());
        _drafts.FindVersionAsync(5, Arg.Any<CancellationToken>()).Returns(
            new MethodologyVersion(1, "1.0.0", CalculationLevel.Configuration, 9, new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc)));
    }

    private Task<Ecr.Application.Calculations.Dto.MethodologyTestCaseDto> SaveAsync(string input, string expected) =>
        new SaveMethodologyTestCaseHandler(_drafts, _uow, _access, _user)
            .HandleAsync(5, "t1", input, expected, 0m, CancellationToken.None);

    [Theory]
    [InlineData("{ not json", "{}", "inputJson")]
    [InlineData("[1]", "{}", "inputJson")]
    [InlineData("null", "{}", "inputJson")]
    [InlineData("{}", "{\"tons\":\"abc\"}", "expectedJson")]
    [InlineData("{}", "null", "expectedJson")]
    public async Task Некоректний_json_дає_0422_з_тестом_і_полем_і_нічого_не_пише(string input, string expected, string field)
    {
        var error = await Assert.ThrowsAsync<BusinessRuleException>(() => SaveAsync(input, expected));

        Assert.Equal("ECR-CALC-0422", error.ErrorCode);
        Assert.Equal("err.ECR-CALC-0422.testCaseJsonInvalid", error.Details!["messageKey"]);
        Assert.Equal("t1", error.Details["testCode"]);
        Assert.Equal(field, error.Details["field"]);
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
        _drafts.DidNotReceive().Add(Arg.Any<MethodologyTestCaseEntity>());
    }

    [Fact]
    public async Task Коректний_json_зберігається()
    {
        await SaveAsync("{}", "{\"tons\":1.5}");

        await _uow.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }
}