using Ecr.Application.Common;
using Ecr.Application.Documents;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Workflow;

/// <summary>
/// Дисплей F-05 (<see cref="GetCalculationResultsHandler"/>) і далі питає
/// свіжість по ВСЬОМУ документу, хоча подання аркуша тепер питає лише по
/// таблицях свого аркуша (<c>SubmitSheetHandler</c>).
/// </summary>
/// <remarks>
/// ⚠ Регресійний сторож: порт отримав фільтр таблиць, і «передати щось» у
/// дисплеї було б тихою зміною поведінки — панель результатів перестала б
/// позначати застарілим число, чиї входи змінилися в іншому аркуші.
/// </remarks>
public sealed class CalculationResultsFreshnessScopeTests
{
    private const long Document = 700;
    private const int Period = 202601;
    private static readonly DateTime Now = new(2026, 2, 5, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Finding", "F-05")]
    public async Task Панель_результатів_бачить_застарілість_усіх_таблиць_документа()
    {
        var results = Substitute.For<ICalculationResultStore>();
        var methodologies = Substitute.For<IMethodologyStore>();
        var access = Substitute.For<IAccessDecisionService>();
        var user = Substitute.For<ICurrentUser>();

        user.UserId.Returns(9);
        access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
              .Returns(new AccessBuilder().Permission(GetCalculationResultsHandler.Permission).Build());
        access.CanReadDocumentAsync(Arg.Any<AccessProfile>(), Document, Arg.Any<CancellationToken>())
              .Returns(EditDecision.Allow());

        results.ReadCurrentAsync(Document, Period, Arg.Any<CancellationToken>())
               .Returns([new CalculationResultRow(1, "R1", "E_CO2", 10m, 1, null)]);
        methodologies.GetVersionLabelsAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
                     .Returns(new Dictionary<int, MethodologyVersionLabel>());

        // Порт відповідає як справжній: без фільтра (увесь документ) —
        // застаріло; з будь-яким фільтром — свіже (зміна була в іншій таблиці).
        methodologies.GetCalculationFreshnessAsync(
                Document, Period, Arg.Any<IReadOnlyCollection<int>?>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<IReadOnlyCollection<int>?>(2) is null
                ? new CalculationFreshness(Now.AddHours(-2), Now.AddHours(-1))
                : new CalculationFreshness(Now.AddHours(-2), null));

        var dto = Assert.Single(await new GetCalculationResultsHandler(results, methodologies, access, user)
            .HandleAsync(Document, Period, CancellationToken.None));

        Assert.True(dto.IsStale);
        Assert.Equal(Now.AddHours(-1), dto.InputsChangedAt);
        await methodologies.Received(1).GetCalculationFreshnessAsync(
            Document, Period, Arg.Is<IReadOnlyCollection<int>?>(t => t == null), Arg.Any<CancellationToken>());
    }
}
