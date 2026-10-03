using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Expressions;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Expressions;

/// <summary>
/// Перевірка виразу відмовляє задовгому тексту ДО розбору (L7-01).
/// </summary>
/// <remarks>
/// ⛔ Аудит 2026-10-03, L7-01 critical: <c>POST /expressions/validate</c> (право
/// <c>Calculation.View</c>) приймав 64 КіБ, тобто ланцюг на ~32 000 доданків, і
/// процес-зонд на коді до фіксу (потік зі стеком 1 МБ) завершувався
/// <c>Stack overflow.</c>, код виходу 134. Тут доводиться перша лінія: до
/// рушія такий текст не доходить узагалі.
/// </remarks>
[Trait(TestCategories.Stage, TestCategories.Stage2)]
public sealed class ExpressionLengthGuardTests
{
    private readonly ITemplateVersionStore _versions = Substitute.For<ITemplateVersionStore>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();

    public static TheoryData<ExpressionDialect> Dialects =>
        [ExpressionDialect.Template, ExpressionDialect.Methodology, ExpressionDialect.Report];

    [Theory]
    [MemberData(nameof(Dialects))]
    public async Task Ланцюг_у_32_тисячі_доданків_відхиляється_до_розбору(ExpressionDialect dialect)
    {
        var text = string.Join("+", Enumerable.Repeat("1", 32_000));

        var error = await Assert.ThrowsAsync<BusinessRuleException>(() => ValidateAsync(text, dialect));

        Assert.Equal(ErrorCodes.RequestInvalid, error.ErrorCode);
        Assert.Equal("err.ECR-REQ-0422.expressionTooLong", error.Details!["messageKey"]);
        Assert.Equal("4000", error.Details["max"]);
        Assert.Equal("63999", error.Details["length"]);
    }

    [Fact]
    public async Task Вираз_рівно_на_межі_перевіряється_як_звичайно()
    {
        // 4000 символів — найдовший вираз, що зберігається
        // (`MethodologyFormula.MaxExpressionLength`); межа його не відхиляє.
        var text = "1" + string.Concat(Enumerable.Repeat("+1", 1999)) + "0";
        Assert.Equal(4000, text.Length);

        var result = await ValidateAsync(text, ExpressionDialect.Template);

        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Межа_дорівнює_найдовшому_виразу_що_зберігається()
        => Assert.Equal(4000, ExpressionLengthGuard.MaxLength);

    private Task<ExpressionValidationDto> ValidateAsync(string text, ExpressionDialect dialect)
    {
        _user.UserId.Returns(9);
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }.Permission("Calculation.View").Build());

        var handler = new ValidateExpressionHandler(
            _versions, Substitute.For<IUnitCatalog>(), new RealFormulaEngine(), _access, _user);

        return handler.HandleAsync(
            new ExpressionValidationRequest(text, dialect, null, null, null, null),
            CancellationToken.None);
    }
}
