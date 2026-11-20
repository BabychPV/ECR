using Ecr.Application.Calculations;
using Ecr.Application.Calculations.Scripting;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Enums;
using Ecr.Expressions;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Calculations.Scripting;

/// <summary>
/// <c>scriptManaged</c> на РІВНІ ОБРОБНИКІВ (MS 5.1): версія з рушієм <c>Script</c> не редагується вручну і тоді, коли
/// обробник кличуть повз контролер; формульна версія — як і було.
/// </summary>
/// <remarks>
/// Мутаційний доказ: прибрати <c>MethodologyVersionScope.RequireHandEditable</c> з будь-якого з десяти обробників —
/// червоніє рядок цього обробника в <see cref="Кожен_обробник_ручної_правки_відмовляє_на_версії_скрипті"/>.
/// </remarks>
public sealed class ScriptManagedHandlerGuardTests
{
    private const int VersionId = 11;

    private static readonly DateTime Now = new(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly IMethodologyDraftStore _drafts = Substitute.For<IMethodologyDraftStore>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();

    private static readonly string[] Permissions =
    [
        SaveMethodologyFormulaHandler.Permission,
        DeleteMethodologyFormulaHandler.Permission,
        SaveMethodologyConstantHandler.Permission,
        SaveMethodologyCategoryHandler.Permission,
        DeleteMethodologyCategoryHandler.Permission,
        SaveMethodologyCategoryRuleHandler.Permission,
        DeleteMethodologyCategoryRuleHandler.Permission,
        SaveMethodologyRuleHandler.Permission,
        SaveMethodologySubstancesHandler.Permission,
        SaveMethodologyOutputHandler.Permission,
    ];

    public ScriptManagedHandlerGuardTests()
    {
        _user.UserId.Returns(9);
        var builder = new AccessBuilder { UserId = 9 };
        foreach (var permission in Permissions)
        {
            builder.Permission(permission);
        }

        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>()).Returns(builder.Build());
    }

    private void Use(MethodologyEngine engine)
    {
        var version = ScriptTestKit.WithId(
            new MethodologyVersion(1, "1.0", CalculationLevel.Configuration, 9, Now, engine), VersionId);
        _drafts.FindVersionAsync(VersionId, Arg.Any<CancellationToken>()).Returns(version);
    }

    private IReadOnlyList<(string Name, Func<Task> Call)> Calls()
    {
        var units = Substitute.For<IUnitCatalog>();
        var methodologies = Substitute.For<IMethodologyStore>();
        var registries = Substitute.For<IRegistryStore>();
        var ct = CancellationToken.None;

        return
        [
            ("formula save", () => new SaveMethodologyFormulaHandler(_drafts, units, _uow, _access, _user)
                .HandleAsync(VersionId, "F1", "1 + 1", FormulaResultType.Number, null, null, null, ct)),
            ("formula delete", () => new DeleteMethodologyFormulaHandler(_drafts, _uow, _access, _user)
                .HandleAsync(VersionId, "F1", ct)),
            ("constant save", () => new SaveMethodologyConstantHandler(_drafts, registries, units, _uow, _access, _user)
                .HandleAsync(
                    VersionId, "EF_1", new SaveMethodologyConstant(ConstantKind.Numeric, 1.5m, 5, null, null, null, null, null, null), ct)),
            ("category save", () => new SaveMethodologyCategoryHandler(_drafts, _uow, _access, _user)
                .HandleAsync(VersionId, "Cat1", null, null, null, ct)),
            ("category delete", () => new DeleteMethodologyCategoryHandler(_drafts, _uow, _access, _user)
                .HandleAsync(VersionId, "Cat1", ct)),
            ("category rule save", () => new SaveMethodologyCategoryRuleHandler(
                    _drafts, methodologies, Substitute.For<ICalculationBindingStore>(), Substitute.For<IFormulaEngine>(),
                    _uow, _access, _user, Substitute.For<IClock>())
                .HandleAsync(VersionId, "'A'", ct)),
            ("category rule delete", () => new DeleteMethodologyCategoryRuleHandler(_drafts, _uow, _access, _user)
                .HandleAsync(VersionId, ct)),
            ("rule save", () => new SaveMethodologyRuleHandler(_drafts, _uow, _access, _user)
                .HandleAsync(VersionId, "R1", "{}", 1, true, ct)),
            ("substances save", () => new SaveMethodologySubstancesHandler(_drafts, methodologies, registries, _uow, _access, _user)
                .HandleAsync(VersionId, [1L], ct)),
            ("output save", () => new SaveMethodologyOutputHandler(_drafts, units, _uow, _access, _user)
                .HandleAsync(VersionId, "OUT_A", 1, 1, ct)),
        ];
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "MS-T3.2")]
    public async Task Кожен_обробник_ручної_правки_відмовляє_на_версії_скрипті()
    {
        Use(MethodologyEngine.Script);

        foreach (var (name, call) in Calls())
        {
            var error = await Assert.ThrowsAsync<BusinessRuleException>(call);
            Assert.True(
                (string?)error.Details!["messageKey"] == "err.ECR-CALC-0409.scriptManaged",
                $"{name}: {error.Details["messageKey"]}");
            Assert.Equal("ECR-CALC-0409", error.ErrorCode);
        }

        // Нічого не збережено.
        await _uow.DidNotReceiveWithAnyArgs().SaveChangesAsync(default);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "MS-T3.2")]
    public async Task На_формульній_версії_заборони_немає()
    {
        Use(MethodologyEngine.Formulas);

        foreach (var (name, call) in Calls())
        {
            // Далі йдуть звичайні перевірки обробника (заглушки сховищ порожні), але не заборона скрипта.
            var error = await Record.ExceptionAsync(call);
            Assert.True(
                error is not BusinessRuleException { Details: { } details }
                    || (string?)details.GetValueOrDefault("messageKey") != "err.ECR-CALC-0409.scriptManaged",
                $"{name}: хибна заборона на формульній версії");
        }
    }
}
