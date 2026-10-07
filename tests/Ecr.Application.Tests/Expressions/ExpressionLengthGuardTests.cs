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
        Assert.Equal(
            ExpressionLengthGuard.MaxFor(dialect).ToString(System.Globalization.CultureInfo.InvariantCulture),
            error.Details["max"]);
        Assert.Equal("63999", error.Details["length"]);
    }

    [Theory]
    [InlineData(ExpressionDialect.Template)]
    [InlineData(ExpressionDialect.Report)]
    public async Task Вираз_правила_чи_формули_понад_2000_відхиляється_а_2000_проходить(ExpressionDialect dialect)
    {
        // a4-03b: колонки cfg.ValidationRule/cfg.FormulaDef вміщують 2000; раніше межа 4000
        // пропускала 2001–4000, і вираз проходив перевірку, але не зберігався (422).
        var ok = "100" + string.Concat(Enumerable.Repeat("+100", 499)) + "0";
        Assert.Equal(2000, ok.Length);
        var tooLong = ok + "0";

        var accepted = await ValidateAsync(ok, dialect);
        Assert.Empty(accepted.Diagnostics);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(() => ValidateAsync(tooLong, dialect));
        Assert.Equal(ErrorCodes.RequestInvalid, error.ErrorCode);
        Assert.Equal("err.ECR-REQ-0422.expressionTooLong", error.Details!["messageKey"]);
        Assert.Equal("2000", error.Details["max"]);
        Assert.Equal("2001", error.Details["length"]);
    }

    [Fact]
    public async Task Вираз_рівно_на_межі_перевіряється_як_звичайно()
    {
        // 4000 символів — найдовший вираз методології
        // (`MethodologyFormula.MaxExpressionLength`); межа його не відхиляє.
        // ✎ L7-01: ланки по 4 символи (`+100`) — 1000 ланок, у межах
        // `Parser.MaxChainLinks` (1024); вироджений `1+1+…` на 2000 ланок тепер
        // відхиляє межа ланцюга, а не межа довжини.
        var text = "100" + string.Concat(Enumerable.Repeat("+100", 999)) + "0";
        Assert.Equal(4000, text.Length);

        var result = await ValidateAsync(text, ExpressionDialect.Methodology);

        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public async Task Вираз_на_символ_понад_межу_відхиляється_а_рівно_на_межі_проходить()
    {
        // 4000 — проходить (див. тест вище), 4001 — відмова: межа точна, не «приблизно».
        var text = "100" + string.Concat(Enumerable.Repeat("+100", 999)) + "00";
        Assert.Equal(4001, text.Length);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => ValidateAsync(text, ExpressionDialect.Methodology));

        Assert.Equal(ErrorCodes.RequestInvalid, error.ErrorCode);
        Assert.Equal("err.ECR-REQ-0422.expressionTooLong", error.Details!["messageKey"]);
        Assert.Equal("4000", error.Details["max"]);
        Assert.Equal("4001", error.Details["length"]);
    }

    [Fact]
    public void Збереження_формули_і_правила_відхиляє_задовгий_вираз_до_розбору()
    {
        // ⚠ Шлях збереження (`FormulaDefHandlers`, `ValidationRuleHandlers`) іде через
        // `ExpressionRejection.RequireValid`, а не через `ValidateExpressionHandler`:
        // без власної перевірки тут межа діяла б лише на ендпоінті перевірки (L7-01).
        var version = new Ecr.Domain.Entities.Configuration.TemplateVersion(
            1, "1.0.0.0", 1, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var engine = new RealFormulaEngine();
        var site = new Ecr.Application.Templates.ExpressionSite(1, null, 1);
        var text = string.Join("+", Enumerable.Repeat("1", 32_000));

        var error = Assert.Throws<BusinessRuleException>(
            () => Ecr.Application.Templates.ExpressionRejection.RequireValid(
                engine, version, text, ExpressionDialect.Template, site));

        Assert.Equal(ErrorCodes.RequestInvalid, error.ErrorCode);
        Assert.Equal("err.ECR-REQ-0422.expressionTooLong", error.Details!["messageKey"]);
        Assert.Equal("63999", error.Details["length"]);
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
