// tests/Ecr.Application.Tests/Calculations/MethodologyRegistryPublishTests.cs
using System.Globalization;
using Ecr.Application.Calculations;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Expressions.Graph;
using Ecr.Expressions.Parsing;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Calculations;

/// <summary>
/// Публікація методології з функціями довідників (RT-23b, FEATURE-REGISTRY-TABLES §5.5
/// перевірки 15–21, §5.8): описка в довіднику, полі чи ключі — 422 з позицією;
/// <c>Legacy</c> + функції довідників — <c>ECR-CALC-0433</c>; ребра <c>cfg.RegistryUse</c>
/// переписуються.
/// </summary>
/// <remarks>
/// Мутаційні докази (виміряно 2026-09-30, кожен окремо, потім відкочено):
/// <list type="bullet">
/// <item>прибрати виклик <c>MethodologyRegistryChecks.RequireValidReferences</c> з
/// <c>PublishMethodologyHandler</c> — червоні 7: усі відмови 15–19 і попередження 21а;</item>
/// <item>у <c>MethodologyRegistryChecks.FormulaRegistries</c> не додавати формулу за
/// <c>REGFIND</c> — червоні 4: <c>Описка_в_полі_REGFIELD_від_формули_запису_422_з_позицією</c>,
/// <c>Запис_в_арифметиці_422</c>, <c>Одиниця_поля_довідника_…</c>, <c>Ребра_версії_…</c>;</item>
/// <item>прибрати <c>RejectExtensionFunctions</c> — червоні обидва
/// <c>Legacy_з_функцією_довідника_ECR_CALC_0433</c>;</item>
/// <item>не передавати форми довідників у <c>MethodologyUnitContext</c> — червоний
/// <c>Одиниця_поля_довідника_бере_участь_у_перевірці_одиниць</c>;</item>
/// <item>у <c>RegistryShapeCatalog.From</c> не заповнювати ключі — червоні 8, серед них
/// <c>REGFIND_з_неповним_ключем_422</c> і <c>Формули_форми_301_…_публікуються</c>;</item>
/// <item>не писати ребро <c>ROW.a.b</c> у <c>MethodologyRegistryChecks.Uses</c> — червоний
/// <c>Ребра_версії_переписуються_формулами_з_полями</c>;</item>
/// <item>не кликати <c>ReplaceRegistryUsesAsync</c> після графа — червоні 3:
/// <c>Ребра_версії_…</c>, <c>Версія_без_довідників_прибирає_старі_ребра</c>,
/// <c>Legacy_без_функцій_довідника_…</c>.</item>
/// </list>
/// ⚠ Запис ребер у SQL (<c>RegistryUseStore</c>) і тест
/// <c>Публікація_записує_використання</c> — окремий крок на реальній БД; тут — порт у пам'яті.
/// </remarks>
public sealed class MethodologyRegistryPublishTests
{
    private const int Author = 7;
    private const int Reviewer = 9;
    private const int VersionId = 71;
    private const int OwnerId = 6;
    private const int Tonne = 8;
    private const int Kilogram = 3;
    private const int GramPerMol = 30;
    private const byte Mass = 1;

    private const int Stream = 101;
    private const int Component = 102;
    private const int StreamCase = 103;
    private const int GasComposition = 104;

    private static readonly DateTime Now = new(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly From = new(2026, 10, 1);

    private readonly IMethodologyStore _store = Substitute.For<IMethodologyStore>();
    private readonly ICalculationBindingStore _bindings = Substitute.For<ICalculationBindingStore>();
    private readonly ICalculationModule _module = Substitute.For<ICalculationModule>();
    private readonly IFormulaEngine _formulas = Substitute.For<IFormulaEngine>();
    private readonly IUnitCatalog _units = Substitute.For<IUnitCatalog>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly IRegistryStore _registries = Substitute.For<IRegistryStore>();
    private readonly IRegistryKeyStore _keys = Substitute.For<IRegistryKeyStore>();
    private readonly InMemoryRegistryUseStore _uses = new();

    private readonly MethodologyVersion _version;

    private readonly List<MethodologyConstant> _constants =
    [
        new(VersionId, EcrCode.Create("M_S"), 32.064m, GramPerMol),
        new(VersionId, EcrCode.Create("M_CO2"), 44.00m, GramPerMol),
        new(VersionId, EcrCode.Create("A"), 5m, Tonne),
    ];

    public MethodologyRegistryPublishTests()
    {
        _clock.UtcNow.Returns(Now);
        _user.UserId.Returns(Reviewer);
        _access.BuildProfileAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(Profile());

        var methodology = new Methodology(EcrCode.Create("HSE301"), Text("Flare"));
        SetId(methodology, OwnerId);

        _version = new MethodologyVersion(OwnerId, "1.0.0", CalculationLevel.Configuration, Author, Now);
        _version.SetModes(NumericMode.Strict, CalendarMode.Actual, TraceLevel.ErrorsOnly);
        SetId(_version, VersionId);
        methodology.AddVersion(_version);

        _store.FindByVersionAsync(VersionId, Arg.Any<CancellationToken>()).Returns(methodology);
        _store.GetTestCasesAsync(VersionId, Arg.Any<CancellationToken>()).Returns(TestCases());
        _store.GetConstantsAsync(VersionId, Arg.Any<CancellationToken>()).Returns(_ => _constants);
        _store.GetOutputsAsync(VersionId, Arg.Any<CancellationToken>()).Returns(new List<MethodologyOutput>());
        _store.GetRulesAsync(VersionId, Arg.Any<CancellationToken>()).Returns(new List<MethodologyRule>());
        _store.ResolveImportsAsync(VersionId, From, Arg.Any<CancellationToken>())
              .Returns(new List<MethodologyLibrary>());
        _bindings.ListAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(new List<CalculationBinding>());
        _units.GetAsync(Arg.Any<CancellationToken>()).Returns(Catalogue());

        _module.ExecuteAsync(Arg.Any<CalculationInput>(), Arg.Any<CancellationToken>())
               .Returns(call => Output(call.Arg<CalculationInput>()));

        var engine = new RealFormulaEngine();
        _formulas.Parse(Arg.Any<string>(), Arg.Any<ExpressionDialect>())
                 .Returns(call => engine.Parse(call.ArgAt<string>(0), call.ArgAt<ExpressionDialect>(1)));
        _formulas.ExtractDependencies(
                     Arg.Any<ParsedExpression>(),
                     Arg.Any<TemplateVersionSnapshot?>(),
                     Arg.Any<DependencyContext>())
                 .Returns(call => engine.ExtractDependencies(
                     call.ArgAt<ParsedExpression>(0),
                     call.ArgAt<TemplateVersionSnapshot?>(1),
                     call.ArgAt<DependencyContext>(2)));
        _formulas.BuildEvaluationOrder(Arg.Any<IReadOnlyList<FormulaNode>>())
                 .Returns(call => new OrderingResult(
                     true, call.Arg<IReadOnlyList<FormulaNode>>().Select(n => n.FormulaDefId).ToList(), null));

        Registries();
    }

    /// <summary>Формули форми 301 (§5.6) на описаних довідниках публікуються.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.18")]
    public async Task Формули_форми_301_на_описаних_довідниках_публікуються()
    {
        Formulas(Hse301());

        var diff = await Publish();

        Assert.True(_version.IsPublished);
        Assert.DoesNotContain(diff.Warnings ?? [], w => w.Contains("GAS_COMPOSITION", StringComparison.Ordinal));
    }

    /// <summary>Приймання RT-23b: описка в полі — 422 з позицією, версія не публікується.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.18")]
    public async Task Описка_в_полі_ROW_422_з_позицією()
    {
        const string mu = "REGSUM('GAS_COMPOSITION', ROW.CASE = !CASE, ROW.MOL_PCT * ROW.COMPONENT.WM) / 100";
        Formulas(Hse301(("MU", mu)));

        var error = await Assert.ThrowsAsync<BusinessRuleException>(Publish);

        Assert.Equal("ECR-TMPL-4222", error.ErrorCode);
        Assert.Equal("expr.registryFieldUnknown", error.Details!["messageKey"]);
        Assert.Equal("MU", error.Details["formula"]);
        Assert.Equal("COMPONENT", error.Details["registry"]);
        Assert.Equal("WM", error.Details["field"]);
        Assert.Equal(mu.IndexOf("ROW.COMPONENT.WM", StringComparison.Ordinal).ToString(CultureInfo.InvariantCulture), error.Details["position"]);
        Assert.False(_version.IsPublished);
        Assert.Equal(0, _uses.Replacements);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.18")]
    public async Task Невідомий_довідник_422_з_позицією_літерала()
    {
        const string mu = "REGSUM('GAS_COMPOSITON', ROW.MOL_PCT > 0, ROW.MOL_PCT) / 100";
        Formulas([Formula(1, "MU", mu)]);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(Publish);

        Assert.Equal("ECR-TMPL-4222", error.ErrorCode);
        Assert.Equal("expr.registryUnknown", error.Details!["messageKey"]);
        Assert.Equal("GAS_COMPOSITON", error.Details["registry"]);
        Assert.Equal(mu.IndexOf("'GAS_COMPOSITON'", StringComparison.Ordinal).ToString(CultureInfo.InvariantCulture), error.Details["position"]);
        Assert.False(_version.IsPublished);
    }

    /// <remarks>
    /// ⚠ <c>!CASE</c> — запис <c>STREAM_CASE</c> лише тому, що формула <c>CASE</c> — це
    /// <c>REGFIND</c> по ньому. Без цього зв'язку <c>REGFIELD(!CASE, …)</c> не мав би
    /// довідника, і описка пройшла б мовчки (Д-5).
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.18")]
    public async Task Описка_в_полі_REGFIELD_від_формули_запису_422_з_позицією()
    {
        const string sound = "91.5 * Sqrt(1.3 * (273 + REGFIELD(!CASE, 'T_CC')))";
        Formulas(Hse301(("W_SOUND", sound)));

        var error = await Assert.ThrowsAsync<BusinessRuleException>(Publish);

        Assert.Equal("ECR-TMPL-4222", error.ErrorCode);
        Assert.Equal("expr.registryFieldUnknown", error.Details!["messageKey"]);
        Assert.Equal("W_SOUND", error.Details["formula"]);
        Assert.Equal("STREAM_CASE", error.Details["registry"]);
        Assert.Equal("T_CC", error.Details["field"]);
        Assert.Equal(sound.IndexOf("T_CC", StringComparison.Ordinal).ToString(CultureInfo.InvariantCulture), error.Details["position"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.18")]
    public async Task REGFIND_з_неповним_ключем_422()
    {
        Formulas(Hse301(("CASE", "REGFIND('STREAM_CASE', @Stream)")));

        var error = await Assert.ThrowsAsync<BusinessRuleException>(Publish);

        Assert.Equal("ECR-TMPL-0422", error.ErrorCode);
        Assert.Equal("expr.registryKeyArity", error.Details!["messageKey"]);
        Assert.Equal("2", error.Details["expected"]);
        Assert.Equal("1", error.Details["actual"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.18")]
    public async Task Запис_в_арифметиці_422()
    {
        Formulas(Hse301(("MU", "!CASE * 2")));

        var error = await Assert.ThrowsAsync<BusinessRuleException>(Publish);

        Assert.Equal("ECR-TMPL-4222", error.ErrorCode);
        Assert.Equal("expr.entryRefMisuse", error.Details!["messageKey"]);
        Assert.Equal("MU", error.Details["formula"]);
    }

    /// <summary>Перелік — усі описки версії, а не перша.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.18")]
    public async Task Відмова_називає_всі_описки_версії()
    {
        Formulas(Hse301(
            ("MU", "REGSUM('GAS_COMPOSITION', ROW.CASE = !CASE, ROW.MOL_PCT * ROW.COMPONENT.WM) / 100"),
            ("S_WT", "CST.M_S * REGSUM('GAS_COMPOSITION', ROW.CASE = !CASE, ROW.COMPONENT.NS * ROW.MOL_PCT) / !MU")));

        var error = await Assert.ThrowsAsync<BusinessRuleException>(Publish);

        Assert.Equal("2", error.Details!["count"]);
        Assert.Contains("«MU»", error.Message, StringComparison.Ordinal);
        Assert.Contains("«S_WT»", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Приймання RT-23b (AC-9): функція довідника в <c>Legacy</c> — <c>ECR-CALC-0433</c>, а
    /// не перевірка полів: чинний рушій довідників не бачив, відтворювати нема чого.
    /// </summary>
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.18")]
    [InlineData("REGSUM('GAS_COMPOSITION', ROW.CASE = !CASE, ROW.MOL_PCT) / 100")]
    [InlineData("REGFIND('STREAM_CASE', @Stream, @HmbCase)")]
    public async Task Legacy_з_функцією_довідника_ECR_CALC_0433(string expression)
    {
        _version.SetModes(NumericMode.Legacy, CalendarMode.Actual, TraceLevel.ErrorsOnly);
        Formulas(Hse301(("MU", expression)));

        var error = await Assert.ThrowsAsync<BusinessRuleException>(Publish);

        Assert.Equal("ECR-CALC-0433", error.ErrorCode);
        Assert.Equal("err.ECR-CALC-0433.legacyExtensionFunction", error.Details!["messageKey"]);
        Assert.False(_version.IsPublished);
    }

    /// <summary>
    /// <c>Legacy</c> без функцій довідників публікується як до кроку: жодного звернення до
    /// сховищ довідників, ребра версії — порожня множина.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.18")]
    public async Task Legacy_без_функцій_довідника_публікується_без_звернень_до_довідників()
    {
        _version.SetModes(NumericMode.Legacy, CalendarMode.Actual, TraceLevel.ErrorsOnly);
        Formulas([Formula(1, "M", "CST.A * 2")]);

        await Publish();

        Assert.True(_version.IsPublished);
        await _registries.DidNotReceiveWithAnyArgs().ListDefinitionsAsync(default);
        await _keys.DidNotReceiveWithAnyArgs().ListActiveKeysAsync(default, default);
        Assert.Equal(1, _uses.Replacements);
        Assert.Empty(_uses.Uses);
    }

    /// <summary>Фільтр без індексного шляху — попередження 21а, публікацію не зупиняє.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.18")]
    public async Task Фільтр_без_індексу_попередження_а_не_відмова()
    {
        Formulas([Formula(1, "TOTAL", "REGSUM('GAS_COMPOSITION', ROW.MOL_PCT > 0, ROW.MOL_PCT)")]);

        var diff = await Publish();

        Assert.True(_version.IsPublished);
        Assert.Contains(diff.Warnings ?? [], w => w.Contains("«TOTAL»", StringComparison.Ordinal)
                                                  && w.Contains("GAS_COMPOSITION", StringComparison.Ordinal));
    }

    /// <summary>Перевірка 20: одиниця поля довідника бере участь у перевірці одиниць.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-16.7")]
    public async Task Одиниця_поля_довідника_бере_участь_у_перевірці_одиниць()
    {
        Formulas(Hse301(("W_SOUND", "REGFIELD(!CASE, 'T_C') + CST.A")));

        var error = await Assert.ThrowsAsync<BusinessRuleException>(Publish);

        Assert.Equal("ECR-TMPL-4223", error.ErrorCode);
        Assert.Equal("expr.unit.addNeedsConvert", error.Details!["messageKey"]);
        Assert.Equal("W_SOUND", error.Details["formula"]);
    }

    /// <summary>
    /// Ребра <c>cfg.RegistryUse</c> (§5.8): довідник цілком для <c>REGFIND</c>/агрегата,
    /// поля <c>ROW.a.b</c> — довідника області, <c>REGFIELD</c> — довідника запису; ребро
    /// попередньої редакції версії прибрано, чужі — лишились.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.18")]
    public async Task Ребра_версії_переписуються_формулами_з_полями()
    {
        _uses.Seed(RegistryUse.ForMethodologyFormula(VersionId, "OLD", Stream, "NAME"));
        _uses.Seed(RegistryUse.ForMethodologyFormula(VersionId + 1, "OTHER", Stream, null));
        Formulas(Hse301(("W_SOUND", "91.5 * Sqrt(1.3 * (273 + REGFIELD(!CASE, 'T_C')) / !MU)")));

        await Publish();

        var mine = _uses.Uses
            .Where(u => u.SourceId == VersionId)
            .Select(u => $"{u.FormulaCode}:{u.RegistryDefId}:{u.FieldPath}")
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(
            new[]
            {
                $"CASE:{StreamCase}:",
                $"EF_RAW:{GasComposition}:",
                $"EF_RAW:{GasComposition}:CASE",
                $"EF_RAW:{GasComposition}:COMPONENT.N_C",
                $"EF_RAW:{GasComposition}:MOL_PCT",
                $"MU:{GasComposition}:",
                $"MU:{GasComposition}:CASE",
                $"MU:{GasComposition}:COMPONENT.MW",
                $"MU:{GasComposition}:MOL_PCT",
                $"S_WT:{GasComposition}:",
                $"S_WT:{GasComposition}:CASE",
                $"S_WT:{GasComposition}:COMPONENT.N_S",
                $"S_WT:{GasComposition}:MOL_PCT",
                $"W_SOUND:{StreamCase}:T_C",
            },
            mine);
        Assert.Single(_uses.Uses, u => u.SourceId == VersionId + 1);
        Assert.All(_uses.Uses, u => Assert.Equal(RegistryUse.MethodologyVersionSource, u.SourceKind));
    }

    /// <summary>Версія, що перестала читати довідник, не лишає ребер попередньої редакції.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.18")]
    public async Task Версія_без_довідників_прибирає_старі_ребра()
    {
        _uses.Seed(RegistryUse.ForMethodologyFormula(VersionId, "MU", GasComposition, null));
        Formulas([Formula(1, "M", "CST.A * 2")]);

        await Publish();

        Assert.Empty(_uses.Uses);
    }

    /// <summary>
    /// L2-4: довідник, який читає ЛИШЕ правило категорії константи, фіксується ребром
    /// <c>cfg.RegistryUse</c> (код джерела — <c>CategoryRule</c>), як і для формули: інакше зміна
    /// довідника не позначала б результати версії застарілими (RT-19/RT-25).
    /// </summary>
    /// <remarks>
    /// Мутаційний доказ: не додавати правило в перелік для <c>ReplaceRegistryUsesAsync</c> —
    /// червоний цей тест.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.18")]
    public async Task Довідник_лише_у_правилі_категорії_дає_ребро_використання()
    {
        _uses.Seed(RegistryUse.ForMethodologyFormula(VersionId, "OLD", Stream, "NAME"));
        Formulas([Formula(1, "M", "CST.A * 2")]);
        _store.GetCategoryRuleAsync(VersionId, Arg.Any<CancellationToken>())
              .Returns("REGFIELD(REGFIND('STREAM_CASE', @Stream, @HmbCase), 'CASE_NAME')");

        await Publish();

        Assert.True(_version.IsPublished);
        var mine = _uses.Uses
            .Where(u => u.SourceId == VersionId)
            .Select(u => $"{u.FormulaCode}:{u.RegistryDefId}:{u.FieldPath}")
            .Order(StringComparer.Ordinal)
            .ToList();
        Assert.Equal(
            new[] { $"{MethodologyCategoryRuleChecks.RuleCode}:{StreamCase}:", $"{MethodologyCategoryRuleChecks.RuleCode}:{StreamCase}:CASE_NAME" },
            mine);
    }

    /// <summary>L2-4: описка в полі <c>REGFIELD</c> у правилі категорії ловиться публікацією, а не рядком розрахунку.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.18")]
    public async Task Описка_в_полі_REGFIELD_правила_категорії_422_з_позицією()
    {
        Formulas([Formula(1, "M", "CST.A * 2")]);
        const string rule = "REGFIELD(REGFIND('STREAM_CASE', @Stream, @HmbCase), 'NO_SUCH')";
        _store.GetCategoryRuleAsync(VersionId, Arg.Any<CancellationToken>()).Returns(rule);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(Publish);

        Assert.Equal("ECR-TMPL-4222", error.ErrorCode);
        Assert.Equal("expr.registryFieldUnknown", error.Details!["messageKey"]);
        Assert.Equal(MethodologyCategoryRuleChecks.RuleCode, error.Details["formula"]);
        Assert.False(_version.IsPublished);
    }

    // ─────────────────────────────────────────────────────────────────────────

    private Task<MethodologyPublicationDiff> Publish()
        => new PublishMethodologyHandler(
                _module, _store, _formulas, _bindings, _units, _uow, _audit, _access, _user, _clock,
                _registries, _keys, _uses)
            .HandleAsync(VersionId, "RT-23b", From, CancellationToken.None);

    /// <summary>Формули §5.6; <paramref name="overrides"/> заміняють вираз за кодом.</summary>
    private static List<MethodologyFormula> Hse301(params (string Code, string Expression)[] overrides)
    {
        List<(string Code, string Expression)> formulas =
        [
            ("CASE", "REGFIND('STREAM_CASE', @Stream, @HmbCase)"),
            ("MU", "REGSUM('GAS_COMPOSITION', ROW.CASE = !CASE, ROW.MOL_PCT * ROW.COMPONENT.MW) / 100"),
            ("S_WT", "CST.M_S * REGSUM('GAS_COMPOSITION', ROW.CASE = !CASE, ROW.COMPONENT.N_S * ROW.MOL_PCT) / !MU"),
            ("EF_RAW", "CST.M_CO2 * REGSUM('GAS_COMPOSITION', ROW.CASE = !CASE, ROW.COMPONENT.N_C * ROW.MOL_PCT) / 100 / !MU"),
        ];

        foreach (var (code, expression) in overrides)
        {
            var index = formulas.FindIndex(f => f.Code == code);
            if (index >= 0)
            {
                formulas[index] = (code, expression);
            }
            else
            {
                formulas.Add((code, expression));
            }
        }

        return [.. formulas.Select((f, i) => Formula(i + 1, f.Code, f.Expression))];
    }

    /// <summary>
    /// Довідники форми 301 так, як їх віддає БД: описи з полями й первинні ключі
    /// (<c>STREAM_CASE</c>: <c>STREAM + CASE_NAME</c>; <c>GAS_COMPOSITION</c>: <c>CASE + COMPONENT</c>).
    /// </summary>
    private void Registries()
    {
        var stream = Registry("STREAM", Stream, ("NAME", CellDataType.String, null, null));
        var component = Registry(
            "COMPONENT", Component,
            ("MW", CellDataType.Decimal, GramPerMol, null),
            ("N_C", CellDataType.Int, null, null),
            ("N_S", CellDataType.Int, null, null));
        var streamCase = Registry(
            "STREAM_CASE", StreamCase,
            ("STREAM", CellDataType.Lookup, null, Stream),
            ("CASE_NAME", CellDataType.String, null, null),
            ("T_C", CellDataType.Decimal, Kilogram, null));
        var composition = Registry(
            "GAS_COMPOSITION", GasComposition,
            ("CASE", CellDataType.Lookup, null, StreamCase),
            ("COMPONENT", CellDataType.Lookup, null, Component),
            ("MOL_PCT", CellDataType.Decimal, null, null));

        _registries.ListDefinitionsAsync(Arg.Any<CancellationToken>())
                   .Returns(new List<RegistryDef> { stream, component, streamCase, composition });

        _keys.ListActiveKeysAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
             .Returns(new List<RegistryKeyDef>());
        _keys.ListActiveKeysAsync(StreamCase, Arg.Any<CancellationToken>())
             .Returns(new List<RegistryKeyDef> { PrimaryKey(streamCase, "STREAM", "CASE_NAME") });
        _keys.ListActiveKeysAsync(GasComposition, Arg.Any<CancellationToken>())
             .Returns(new List<RegistryKeyDef> { PrimaryKey(composition, "CASE", "COMPONENT") });
    }

    private static RegistryDef Registry(
        string code, int id, params (string Code, CellDataType Type, int? Unit, int? Target)[] fields)
    {
        var registry = new RegistryDef(EcrCode.Create(code), Text(code), isTemporal: false);
        SetId(registry, id);

        var ordinal = 0;
        foreach (var (fieldCode, type, unit, target) in fields)
        {
            ordinal++;
            var field = new RegistryFieldDef(id, EcrCode.Create(fieldCode), Text(fieldCode), type, ordinal);
            SetId(field, (id * 10) + ordinal);
            field.Update(Text(fieldCode), ordinal, isRequired: true);
            field.MeasureIn(unit);
            field.PointTo(target);
            registry.AddField(field);
        }

        return registry;
    }

    private static RegistryKeyDef PrimaryKey(RegistryDef registry, params string[] fieldCodes)
    {
        var key = new RegistryKeyDef(
            registry.Id,
            EcrCode.Create("PK"),
            Text("PK"),
            [.. fieldCodes.Select(c => registry.Fields.Single(f => f.Code == c))],
            isPrimary: true,
            ignoreCase: true,
            Author,
            Now);
        SetId(key, registry.Id * 100);
        return key;
    }

    private static UnitCatalogSnapshot Catalogue()
        => new(
            new Dictionary<string, UnitRef>(StringComparer.OrdinalIgnoreCase)
            {
                ["t"] = new(Tonne, "t", Mass, 1000m),
                ["kg"] = new(Kilogram, "kg", Mass),
                ["g/mol"] = new(GramPerMol, "g/mol", 4),
            },
            new Dictionary<string, int>(StringComparer.Ordinal));

    private void Formulas(List<MethodologyFormula> formulas)
        => _store.GetFormulasAsync(VersionId, Arg.Any<CancellationToken>()).Returns(formulas);

    private static MethodologyFormula Formula(int id, string code, string expression)
    {
        var formula = new MethodologyFormula(VersionId, EcrCode.Create(code), expression);
        formula.SetScope(MethodologyFormulaScope.Row);
        SetId(formula, id);
        return formula;
    }

    private static void SetId(Entity<int> entity, int id)
        => typeof(Entity<int>).GetProperty(nameof(Entity<int>.Id))!.SetValue(entity, id);

    private static AccessProfile Profile() => new()
    {
        CacheKey = "p",
        UserId = Reviewer,
        SecurityStamp = "s",
        Permissions = new HashSet<string>(StringComparer.Ordinal) { "Calculation.Publish" },
        Grants = new Dictionary<string, GrantLevel>(),
        Denies = new HashSet<string>(),
        RoleIds = new HashSet<int>(),
    };

    private static List<MethodologyTestCase> TestCases() =>
    [
        new("golden-rt23b",
            new CalculationInput(
                new MethodologyDescriptor(
                    OwnerId, VersionId, "HSE301", "1.0.0", CalculationLevel.Configuration,
                    NumericMode.Strict, CalendarMode.Actual, TraceLevel.Off),
                DocumentId: 700,
                TableInstanceId: 500,
                PeriodKey: new PeriodKey(202601),
                SourceRowKey: "E-1",
                Arguments: []),
            new Dictionary<string, decimal> { ["tons"] = 1m },
            Tolerance: 0.000001m),
    ];

    private static CalculationOutput Output(CalculationInput input) =>
        new(input.DocumentId, input.SourceRowKey,
            [new CalculationOutputValue(input.Methodology.MethodologyVersionId, null, "tons", 1m, Tonne)],
            []);

    private static LocalizedText Text(string value)
        => new(new Dictionary<string, string> { ["en"] = value });
}
