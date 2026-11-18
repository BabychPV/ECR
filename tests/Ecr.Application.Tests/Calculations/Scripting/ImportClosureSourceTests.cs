using Ecr.Application.Calculations.Scripting.Migration;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Calculations.Scripting;

/// <summary>
/// Склад імпорту для перетворювача й відбитка — лише з того, що досяжне з формул версії (замикання старого рушія):
/// необов'язкові імена з ПОТРІБНИХ формул бібліотек і категорії констант версій бібліотек, у які веде хоч одне ім'я.
/// </summary>
/// <remarks>
/// Мутація: брати маркери й категорії з УСІХ формул/констант версії бібліотеки (як було) — червоніють
/// <see cref="Маркер_у_невикористаній_формулі_бібліотеки_не_потрапляє_в_імпорт"/> і
/// <see cref="Бібліотека_на_яку_формули_не_посилаються_не_дає_категорій"/>.
/// </remarks>
public sealed class ImportClosureSourceTests
{
    private const int CallerId = 5;
    private const int CallerVersionId = 51;
    private const int CommonId = 900;
    private const int CommonVersionId = 910;
    private const int BaseId = 901;
    private const int BaseVersionId = 920;
    private const int IdleId = 902;
    private const int IdleVersionId = 930;

    private static readonly DateOnly OnDate = new(2026, 1, 31);

    private readonly IMethodologyStore _store = Substitute.For<IMethodologyStore>();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Маркер_у_невикористаній_формулі_бібліотеки_не_потрапляє_в_імпорт()
    {
        var common = Content(
            CommonId, "Common", CommonVersionId, ["K1"],
            Formula(CommonVersionId, "Common_A", "@Flow", 1, "Flow;Gap?"),
            Formula(CommonVersionId, "Common_Spare", "@Idle", 2, "Idle?"));
        Contents(CallerVersionId, common);

        var imports = await BuildAsync([Formula(CallerVersionId, "Total", "!Common_A", 1, null)], common);

        var import = Assert.Single(imports);
        Assert.Equal(["Gap"], import.OptionalArguments);
        Assert.Equal(["K1"], import.ConstantCategories);
        Assert.Equal(["Common_A", "Common_Spare"], import.Members);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Бібліотека_бібліотеки_дає_свої_маркери_і_категорії_прямому_імпорту()
    {
        var common = Content(
            CommonId, "Common", CommonVersionId, ["K1"],
            Formula(CommonVersionId, "Common_A", "!Base_K + 1", 1, "Own?"));
        var baseLibrary = Content(
            BaseId, "Base", BaseVersionId, ["K2"],
            Formula(BaseVersionId, "Base_K", "@Deep", 1, "Deep?"),
            Formula(BaseVersionId, "Base_Spare", "@Never", 2, "Never?"));
        Contents(CallerVersionId, common);
        Contents(CommonVersionId, baseLibrary);

        var imports = await BuildAsync([Formula(CallerVersionId, "Total", "!Common_A", 1, null)], common);

        var import = Assert.Single(imports);
        Assert.Equal(["Deep", "Own"], import.OptionalArguments);
        Assert.Equal(["K1", "K2"], import.ConstantCategories);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Бібліотека_на_яку_формули_не_посилаються_не_дає_категорій()
    {
        var common = Content(
            CommonId, "Common", CommonVersionId, ["K1"], Formula(CommonVersionId, "Common_A", "@Flow", 1, "Flow?"));
        var idle = Content(
            IdleId, "Idle", IdleVersionId, ["K9"], Formula(IdleVersionId, "Idle_A", "@Zzz", 1, "Zzz?"));
        _store.GetLibraryContentsAsync(CallerVersionId, OnDate, Arg.Any<CancellationToken>())
              .Returns(new List<MethodologyLibraryContent> { common, idle });

        var imports = await BuildAsync(
            [Formula(CallerVersionId, "Total", "!Common_A", 1, null)], common, idle);

        Assert.Equal(2, imports.Count);
        var reached = imports.Single(i => i.LibraryCode == "Common");
        var unreached = imports.Single(i => i.LibraryCode == "Idle");
        Assert.Equal(["K1"], reached.ConstantCategories);
        Assert.Equal(["Flow"], reached.OptionalArguments);
        Assert.Empty(unreached.ConstantCategories!);
        Assert.Empty(unreached.OptionalArguments!);
        Assert.Equal(["Idle_A"], unreached.Members);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Версія_що_не_посилається_на_бібліотеку_не_питає_сховище_про_вміст()
    {
        var common = Content(CommonId, "Common", CommonVersionId, ["K1"], Formula(CommonVersionId, "Common_A", "1", 1, null));

        var imports = await BuildAsync([Formula(CallerVersionId, "Total", "1", 1, null)], common);

        var import = Assert.Single(imports);
        Assert.Empty(import.OptionalArguments!);
        Assert.Empty(import.ConstantCategories!);
        await _store.DidNotReceiveWithAnyArgs().GetLibraryContentsAsync(default, default, default);
    }

    // ─────────────────────────────────────────────────────────────────────────

    private Task<IReadOnlyList<SourceImport>> BuildAsync(List<MethodologyFormula> formulas, params MethodologyLibraryContent[] direct)
        => ImportClosureSource.BuildAsync(
            _store, new RealFormulaEngine(), CallerId, CallerVersionId, formulas, OnDate,
            [.. direct.Select(c => c.Library)], CancellationToken.None);

    private void Contents(int versionId, MethodologyLibraryContent content)
        => _store.GetLibraryContentsAsync(versionId, OnDate, Arg.Any<CancellationToken>())
                 .Returns(new List<MethodologyLibraryContent> { content });

    private static MethodologyLibraryContent Content(
        int methodologyId, string code, int versionId, string[] categories, params MethodologyFormula[] formulas)
    {
        var constants = categories.Select(category =>
        {
            var constant = new MethodologyConstant(versionId, EcrCode.Create("EF"), 0.5m, unitId: 1);
            constant.SetScope(category, substanceEntryId: null);
            return constant;
        }).ToList();

        return new MethodologyLibraryContent(
            new MethodologyLibrary(methodologyId, code, versionId, [.. formulas.Select(f => f.Code)]),
            NumericMode.Strict,
            CalendarMode.Actual,
            formulas,
            constants);
    }

    private static MethodologyFormula Formula(int versionId, string code, string expression, int order, string? csv)
    {
        var formula = new MethodologyFormula(versionId, EcrCode.Create(code), expression);
        formula.SetEvaluationOrder(order);
        formula.SetArguments(csv);
        typeof(Entity<int>).GetProperty(nameof(Entity<int>.Id))!.SetValue(formula, (versionId * 100) + order);
        return formula;
    }
}
