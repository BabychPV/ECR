// tests/Ecr.Application.Tests/Calculations/SaveMethodologyCategoryRuleHandlerTests.cs
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
/// Збереження правила категорії (<c>PUT …/category-rule</c>): вираз відхиляється за довжиною ДО розбору.
/// </summary>
/// <remarks>
/// ⛔ L7-01 (AN-72): колонка <c>calc.CategoryRule.Expression</c> довжини не обмежує, а
/// <c>RequireUsable</c> одразу кликав <c>formulaEngine.Parse</c>. Користувач із
/// <c>Calculation.EditRule</c> міг покласти процес API виразом на ~130 КБ. Мутаційний доказ: прибрати
/// <c>ExpressionLengthGuard.Require</c> з <c>RequireUsable</c> — червоний лічильник <c>Parse</c> нижче.
/// </remarks>
public sealed class SaveMethodologyCategoryRuleHandlerTests
{
    private const int VersionId = 81;
    private const int OwnerId = 6;
    private const int Author = 7;

    private static readonly DateTime Now = new(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);

    private readonly IMethodologyDraftStore _drafts = Substitute.For<IMethodologyDraftStore>();
    private readonly IMethodologyStore _methodologies = Substitute.For<IMethodologyStore>();
    private readonly ICalculationBindingStore _bindings = Substitute.For<ICalculationBindingStore>();
    private readonly IFormulaEngine _formulas = Substitute.For<IFormulaEngine>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly IClock _clock = Substitute.For<IClock>();

    public SaveMethodologyCategoryRuleHandlerTests()
    {
        _clock.UtcNow.Returns(Now);
        _user.UserId.Returns(Author);
        _access.BuildProfileAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(new AccessProfile
        {
            CacheKey = "p",
            UserId = Author,
            SecurityStamp = "s",
            Permissions = new HashSet<string>(StringComparer.Ordinal) { SaveMethodologyCategoryRuleHandler.Permission },
            Grants = new Dictionary<string, GrantLevel>(),
            Denies = new HashSet<string>(),
            RoleIds = new HashSet<int>(),
        });

        var version = new MethodologyVersion(OwnerId, "1.0.0", CalculationLevel.Configuration, Author, Now);
        typeof(Entity<int>).GetProperty(nameof(Entity<int>.Id))!.SetValue(version, VersionId);
        _drafts.FindVersionAsync(VersionId, Arg.Any<CancellationToken>()).Returns(version);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [InlineData(4001)]
    [InlineData(130_000)]
    public async Task Правило_довше_4000_422_до_розбору(int length)
    {
        var tooLong = new string('(', length);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(() => Handler().HandleAsync(VersionId, tooLong, CancellationToken.None));

        Assert.Equal("ECR-REQ-0422", error.ErrorCode);
        Assert.Equal("err.ECR-REQ-0422.expressionTooLong", error.Details!["messageKey"]);

        // ⛔ Саме «ДО розбору»: розбір довгого виразу і є вектором атаки.
        _formulas.DidNotReceiveWithAnyArgs().Parse(default!, default);
        _drafts.DidNotReceiveWithAnyArgs().Add(Arg.Any<MethodologyCategoryRule>());
        _ = _uow.DidNotReceiveWithAnyArgs().SaveChangesAsync(default);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Правило_глибше_96_відхиляється_422_при_збереженні()
    {
        // ⛔ N2-01 (AN-72): таке правило проходило і збереження, і публікацію, а в роботі давало #BUDGET.
        var engine = new RealFormulaEngine();
        _formulas.Parse(Arg.Any<string>(), Arg.Any<ExpressionDialect>())
                 .Returns(call => engine.Parse(call.ArgAt<string>(0), call.ArgAt<ExpressionDialect>(1)));
        _methodologies.GetFormulasAsync(VersionId, Arg.Any<CancellationToken>())
                      .Returns(new List<MethodologyFormula>());
        _methodologies.GetConstantsAsync(VersionId, Arg.Any<CancellationToken>())
                      .Returns(new List<MethodologyConstant>());
        var deep = string.Concat(Enumerable.Repeat("- ", 100)) + "'a'";

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Handler().HandleAsync(VersionId, deep, CancellationToken.None));

        Assert.Equal("ECR-CALC-0422", error.ErrorCode);
        Assert.Equal("publish.problem.formulaTooDeep", error.Details!["messageKey"]);
        Assert.Equal("101", error.Details["depth"]);
        Assert.Equal("96", error.Details["max"]);
        Assert.Equal("CategoryRule", error.Details["formula"]);
        _drafts.DidNotReceiveWithAnyArgs().Add(Arg.Any<MethodologyCategoryRule>());
    }

    private SaveMethodologyCategoryRuleHandler Handler()
        => new(_drafts, _methodologies, _bindings, _formulas, _uow, _access, _user, _clock);
}
