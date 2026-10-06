using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Expressions;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Application.Templates;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Expressions;

/// <summary>
/// RC5: формула глибше за межу обчислення (96 рівнів) відхиляється при
/// збереженні, публікації й у редакторі — з поясненням, а не мовчазним
/// <c>#BUDGET</c> у готовому документі; а довга пласка сума проходить.
/// </summary>
/// <remarks>
/// ⚠ Справжню глибину в тексті дає лише ланцюг унарних мінусів: парсер пускає
/// ~190 підряд (<c>ParseUnary</c> не заходить у лічильник вкладеності), тоді як
/// дужки, <c>SUM(</c> і <c>IF(</c> зупиняє розбір на ~63 рівнях. Тому саме він —
/// форма тесту; пласка сума глибини не додає (обчислювач обходить лівий гребінь
/// циклом).
/// </remarks>
[Trait(TestCategories.Stage, TestCategories.Stage2)]
public sealed class ExpressionNestingRejectionTests
{
    private readonly ITemplateVersionStore _versions = Substitute.For<ITemplateVersionStore>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();

    public static TheoryData<ExpressionDialect> Dialects => [ExpressionDialect.Template, ExpressionDialect.Report];

    /// <summary>Унарний ланцюг із <paramref name="signs"/> мінусів: глибина = мінуси + 1.</summary>
    private static string Minuses(int signs) => string.Concat(Enumerable.Repeat("- ", signs)) + "1";

    [Theory]
    [MemberData(nameof(Dialects))]
    public async Task Редактор_відхиляє_формулу_глибше_за_межу_з_ключем_і_числами(ExpressionDialect dialect)
    {
        var result = await ValidateAsync(Minuses(100), dialect);

        var diagnostic = Assert.Single(result.Diagnostics, d => d.MessageKey == "expr.tooDeep");
        Assert.Equal("101", diagnostic.MessageParams!["depth"]);
        Assert.Equal("96", diagnostic.MessageParams["max"]);
    }

    [Fact]
    public async Task Межа_точна_95_мінусів_проходять_а_96_ні()
    {
        // Число 96 написане цифрами: тест не повинен погоджуватися зі зміною межі.
        Assert.Equal(96, Ecr.Expressions.Evaluation.EvaluationBudget.MaxNestingDepth);

        Assert.DoesNotContain(
            (await ValidateAsync(Minuses(95), ExpressionDialect.Template)).Diagnostics, d => d.MessageKey == "expr.tooDeep");
        Assert.Contains(
            (await ValidateAsync(Minuses(96), ExpressionDialect.Template)).Diagnostics, d => d.MessageKey == "expr.tooDeep");
    }

    [Fact]
    public void Збереження_і_публікація_відхиляють_ECR_EXPR_0422_із_людським_ключем()
    {
        var error = Assert.Throws<BusinessRuleException>(() => ExpressionRejection.RequireValid(
            new RealFormulaEngine(), NewVersion(), Minuses(100), ExpressionDialect.Template, new ExpressionSite(1, null, 1)));

        // Той самий ECR-EXPR-0422, що й у сторожа стека обходів; нового коду немає.
        Assert.Equal(ErrorCodes.ExpressionTooComplex, error.ErrorCode);
        Assert.Equal("expr.tooDeep", error.Details!["messageKey"]);
        Assert.Equal("101", error.Details["depth"]);
        Assert.Equal("96", error.Details["max"]);
    }

    [Theory]
    [InlineData(100)]
    [InlineData(1000)]
    public async Task Пласка_сума_зі_100_і_1000_доданків_приймається_редактором_і_збереженням(int terms)
    {
        var text = string.Join("+", Enumerable.Repeat("1", terms));

        Assert.Empty((await ValidateAsync(text, ExpressionDialect.Template)).Diagnostics);

        // Збереження не кидає: до RC5 такий ланцюг приймався ТАК САМО, а в документі
        // давав #BUDGET; тепер приймається й рахується.
        ExpressionRejection.RequireValid(
            new RealFormulaEngine(), NewVersion(), text, ExpressionDialect.Template, new ExpressionSite(1, null, 1));
    }

    private static TemplateVersion NewVersion()
        => new(1, "1.0.0.0", 1, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

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
