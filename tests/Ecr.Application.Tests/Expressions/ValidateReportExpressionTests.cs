using Ecr.Application.Common;
using Ecr.Application.Expressions;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Enums;
using Ecr.Expressions;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Expressions;

/// <summary>
/// <c>POST /expressions/validate</c> для діалекту <c>Report</c>: оточення приходить у
/// запиті, версії шаблону не потрібно.
/// </summary>
/// <remarks>
/// ⚠ Сховища — заглушки НАВМИСНО: цей шлях до них не ходить, і виклик будь-якого з
/// них тут був би дефектом (структура шаблону правилам звіту не потрібна).
/// </remarks>
[Trait(TestCategories.Stage, TestCategories.Stage2)]
public sealed class ValidateReportExpressionTests
{
    private readonly ITemplateVersionStore _versions = Substitute.For<ITemplateVersionStore>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();

    private static readonly ReportExpressionContext Context = new(
        [new("Value", "number"), new("UnitCode", "text")],
        [new("Threshold", "Number")],
        "Boolean");

    [Fact]
    public async Task Умова_над_колонками_і_параметром_проходить_без_структури_шаблону()
    {
        var result = await ValidateAsync("[UnitCode] = 't' AND [Value] > @Threshold", Context);

        Assert.Empty(result.Diagnostics);
        Assert.Equal("Boolean", result.ResultType);
        Assert.Empty(result.SkippedChecks);
        Assert.Empty(_versions.ReceivedCalls());
    }

    [Theory]
    [InlineData("[Nope] > 1", ExpressionErrors.Unresolved)]            // невідома колонка
    [InlineData("[Value] * 2", ExpressionErrors.Unresolved)]           // число там, де чекають умову
    [InlineData("SUM([Value]) > 1", ExpressionErrors.Syntax)]          // агрегат поза набором
    [InlineData("[Main].[7001001].[Jan] > 1", ExpressionErrors.Unresolved)]
    public async Task Порушення_меж_діалекту_приходить_зауваженням_з_усталеним_кодом(string text, string code)
    {
        var result = await ValidateAsync(text, Context);

        Assert.Contains(result.Diagnostics, d => d.Code == code);
    }

    [Fact]
    public async Task Без_оточення_перевіряється_лише_розбір_і_це_названо()
    {
        var result = await ValidateAsync("[Nope] > 1", null);

        Assert.Empty(result.Diagnostics);
        Assert.Equal(
            [ValidateExpressionHandler.SkippedReferences, ValidateExpressionHandler.SkippedTypes],
            result.SkippedChecks);
    }

    [Fact]
    public async Task Невідомий_тип_колонки_це_зауваження_а_не_виняток()
    {
        var result = await ValidateAsync(
            "[Value] > 1", new ReportExpressionContext([new("Value", "money")], null, null));

        Assert.Contains(result.Diagnostics, d => d.MessageKey == "expr.report.unknownColumnType" && d.MessageParams!["type"] == "money");
    }

    private Task<ExpressionValidationDto> ValidateAsync(string text, ReportExpressionContext? context)
    {
        _user.UserId.Returns(9);
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }.Permission("Calculation.View").Build());

        var handler = new ValidateExpressionHandler(
            _versions, Substitute.For<IUnitCatalog>(), new RealFormulaEngine(), _access, _user);

        return handler.HandleAsync(
            new ExpressionValidationRequest(text, ExpressionDialect.Report, null, null, null, null, context),
            CancellationToken.None);
    }
}
