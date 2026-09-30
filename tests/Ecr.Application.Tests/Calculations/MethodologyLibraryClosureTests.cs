// tests/Ecr.Application.Tests/Calculations/MethodologyLibraryClosureTests.cs
using Ecr.Application.Calculations;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Calculations;

/// <summary>
/// HSE301 L3: транзитивне замикання бібліотечних формул — що саме з <c>Common</c>
/// рахується для викликача і куди веде кожне ім'я.
/// </summary>
/// <remarks>
/// Мутаційний доказ: прибрати перевірку шляху (<c>scope.Path.Contains</c>) у
/// <c>MethodologyLibraryClosure.LinkAsync</c> —
/// <see cref="Імпорт_назад_у_викликача_стає_посиланням_циклу_а_не_новою_областю"/>
/// червоний (викликач завантажується як бібліотека, посилання без <c>IsCycle</c>).
/// </remarks>
public sealed class MethodologyLibraryClosureTests
{
    private const int CallerId = 5;
    private const int CallerVersionId = 51;
    private const int CommonId = 900;
    private const int CommonVersionId = 910;
    private const int BaseId = 901;
    private const int BaseVersionId = 920;

    private static readonly DateOnly OnDate = new(2026, 1, 31);

    private readonly IMethodologyStore _store = Substitute.For<IMethodologyStore>();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public async Task Версія_без_посилань_за_межу_не_питає_сховище()
    {
        var closure = await LoadAsync([Formula(CallerVersionId, "A", "1", 1), Formula(CallerVersionId, "B", "!A * 2", 2)]);

        Assert.Null(closure);
        await _store.DidNotReceiveWithAnyArgs().GetLibraryContentsAsync(default, default, default);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public async Task У_замикання_йдуть_лише_потрібні_формули_у_порядку_бібліотеки()
    {
        Contents(CallerVersionId, Common(
            Formula(CommonVersionId, "Common_B", "@Flow", 1),
            Formula(CommonVersionId, "Common_Unused", "999", 2),
            Formula(CommonVersionId, "Common_A", "!Common_B * CST.EF", 3)));

        var closure = await LoadAsync([Formula(CallerVersionId, "Total", "!Common_A * 2", 1)]);

        var link = Assert.Single(closure!.Imports);
        Assert.Equal("Common_A", link.Key);
        Assert.Equal(new LibraryLink(CommonId, "Common", CommonVersionId, IsCycle: false), link.Value);

        var library = Assert.Single(closure.Versions);
        Assert.Equal(CommonVersionId, library.MethodologyVersionId);
        Assert.Equal(NumericMode.Strict, library.NumericMode);

        // Порядок — EvaluationOrder бібліотеки, а не порядок, у якому їх знайшов обхід.
        Assert.Equal(["Common_B", "Common_A"], library.Formulas.Select(f => f.Code));
        Assert.Equal(0.5m, Assert.Single(library.Constants["EF"]).Value);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public async Task Бібліотека_бібліотеки_входить_у_замикання_транзитивно()
    {
        Contents(CallerVersionId, Common(Formula(CommonVersionId, "Common_A", "!Base_K + 1", 1)));
        Contents(CommonVersionId, Library(BaseId, "Base", BaseVersionId, Formula(BaseVersionId, "Base_K", "2", 1)));

        var closure = await LoadAsync([Formula(CallerVersionId, "Total", "!Common_A + !Common_A", 1)]);

        Assert.Equal([CommonVersionId, BaseVersionId], closure!.Versions.Select(v => v.MethodologyVersionId));

        var common = closure.Versions[0];
        Assert.Equal(BaseVersionId, common.Imports["Base_K"].MethodologyVersionId);
        Assert.Equal(["Base_K"], closure.Versions[1].Formulas.Select(f => f.Code));

        // Кожна версія читається один раз, хоч би скільки посилань на неї вело.
        await _store.Received(1).GetLibraryContentsAsync(CallerVersionId, OnDate, Arg.Any<CancellationToken>());
        await _store.Received(1).GetLibraryContentsAsync(CommonVersionId, OnDate, Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public async Task Імпорт_назад_у_викликача_стає_посиланням_циклу_а_не_новою_областю()
    {
        // `Common` перевидали вже ПІСЛЯ публікації викликача — з імпортом самого викликача.
        Contents(CallerVersionId, Common(Formula(CommonVersionId, "Common_A", "!Own_F * 2", 1)));
        Contents(CommonVersionId, Library(CallerId, "HSE400", CallerVersionId, Formula(CallerVersionId, "Own_F", "!Common_A", 1)));

        var closure = await LoadAsync([Formula(CallerVersionId, "Total", "!Common_A", 1)]);

        var common = Assert.Single(closure!.Versions);
        Assert.True(common.Imports["Own_F"].IsCycle);
        Assert.Equal(["Common_A"], common.Formulas.Select(f => f.Code));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public async Task Своя_формула_перекриває_бібліотечну_і_не_тягне_бібліотеку()
    {
        Contents(CallerVersionId, Common(Formula(CommonVersionId, "Common_A", "1", 1)));

        var closure = await LoadAsync(
        [
            Formula(CallerVersionId, "Common_A", "5", 1),
            Formula(CallerVersionId, "Total", "!Common_A * 2", 2),
        ]);

        Assert.Null(closure);
    }

    // ─────────────────────────────────────────────────────────────────────────

    private Task<CalculationLibraries?> LoadAsync(List<MethodologyFormula> formulas)
        => MethodologyLibraryClosure.LoadAsync(
            _store, new RealFormulaEngine(), CallerId, CallerVersionId, formulas, OnDate, CancellationToken.None);

    private void Contents(int versionId, MethodologyLibraryContent content)
        => _store.GetLibraryContentsAsync(versionId, OnDate, Arg.Any<CancellationToken>())
                 .Returns(new List<MethodologyLibraryContent> { content });

    private static MethodologyLibraryContent Common(params MethodologyFormula[] formulas)
        => Library(CommonId, "Common", CommonVersionId, formulas);

    private static MethodologyLibraryContent Library(
        int methodologyId, string code, int versionId, params MethodologyFormula[] formulas)
    {
        var constant = new MethodologyConstant(versionId, EcrCode.Create("EF"), 0.5m, unitId: 1);

        return new MethodologyLibraryContent(
            new MethodologyLibrary(methodologyId, code, versionId, [.. formulas.Select(f => f.Code)]),
            NumericMode.Strict,
            CalendarMode.Actual,
            formulas,
            [constant]);
    }

    private static MethodologyFormula Formula(int versionId, string code, string expression, int order)
    {
        var formula = new MethodologyFormula(versionId, EcrCode.Create(code), expression);
        formula.SetEvaluationOrder(order);
        typeof(Entity<int>).GetProperty(nameof(Entity<int>.Id))!.SetValue(formula, (versionId * 100) + order);
        return formula;
    }
}
