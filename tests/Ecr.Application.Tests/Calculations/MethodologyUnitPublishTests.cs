// tests/Ecr.Application.Tests/Calculations/MethodologyUnitPublishTests.cs
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
/// Перевірка одиниць при публікації методології (ФВ-16.6, ФВ-16.7): т + кг без
/// <c>CONVERT</c> — відмова <c>ECR-TMPL-4223</c> до продуктиву.
/// </summary>
/// <remarks>
/// Мутаційні докази (виміряно):
/// <list type="bullet">
/// <item>прибрати виклик <c>MethodologyUnitChecks.RequireCompatibleUnits</c> з
/// <c>PublishMethodologyHandler</c> — червоні всі відмови нижче (версія
/// публікується);</item>
/// <item>прибрати гілку <c>SymbolKind.Formula</c> з <c>UnitChecker</c> —
/// червоний <c>Формула_в_тоннах_плюс_константа_в_кілограмах_відхиляється</c>;</item>
/// <item>додати <c>expr.unit.productUndeclared</c> у
/// <c>MethodologyUnitChecks.BlockingKeys</c> — червоний
/// <c>Добуток_коефіцієнта_на_масу_публікується</c>;</item>
/// <item>у <c>MethodologyUnitContext</c> брати одиницю першого рядка константи
/// замість «невідома, якщо рядки розходяться» — червоний
/// <c>Константа_з_різними_одиницями_по_рядках_не_судиться</c>.</item>
/// </list>
/// </remarks>
public sealed class MethodologyUnitPublishTests
{
    private const int Author = 7;
    private const int Reviewer = 9;
    private const int VersionId = 71;
    private const int OwnerId = 6;
    private const int Tonne = 8;
    private const int Kilogram = 3;
    private const int CubicMetre = 12;
    private const int KgPerTonne = 21;
    private const byte Mass = 1;
    private const byte Volume = 2;

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

    private readonly MethodologyVersion _version;

    private readonly List<MethodologyOutput> _outputs = [];

    private List<MethodologyConstant> _constants =
    [
        Constant("A", 5m, Tonne),
        Constant("B", 300m, Kilogram),
        Constant("C", 2m, Tonne),
        Constant("V", 4m, CubicMetre),
        Constant("EF", 0.4m, KgPerTonne),
    ];

    public MethodologyUnitPublishTests()
    {
        _clock.UtcNow.Returns(Now);
        _user.UserId.Returns(Reviewer);
        _access.BuildProfileAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(Profile());

        var methodology = new Methodology(EcrCode.Create("HSE301"), Text("Flare"));
        SetId(methodology, OwnerId);

        _version = new MethodologyVersion(OwnerId, "1.0.0", CalculationLevel.Configuration, Author, Now);

        // ⚠ Strict: `CONVERT` — функція ярусу Extension, і в Legacy її відхилила б
        // інша перевірка (`ECR-CALC-0433`) раніше за цю.
        _version.SetModes(NumericMode.Strict, CalendarMode.Actual, TraceLevel.ErrorsOnly);
        SetId(_version, VersionId);
        methodology.AddVersion(_version);

        _store.FindByVersionAsync(VersionId, Arg.Any<CancellationToken>()).Returns(methodology);
        _store.GetTestCasesAsync(VersionId, Arg.Any<CancellationToken>()).Returns(TestCases());
        _store.GetConstantsAsync(VersionId, Arg.Any<CancellationToken>()).Returns(_ => _constants);
        _store.GetOutputsAsync(VersionId, Arg.Any<CancellationToken>()).Returns(_ => _outputs);
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
    }

    /// <summary>Приймання задачі: т + кг без CONVERT не публікується.</summary>
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-16.7")]
    [InlineData("CST.A + CST.B", "expr.unit.addNeedsConvert")]
    [InlineData("CST.A - CST.B", "expr.unit.addNeedsConvert")]
    [InlineData("if(CST.A > CST.B, 1, 0)", "expr.unit.compareNeedsConvert")]
    [InlineData("MAX(CST.A, CST.B)", "expr.unit.aggregateNeedsConvert")]
    [InlineData("CST.A + CST.V", "expr.unit.dimensionMismatch")]
    public async Task Різні_одиниці_без_CONVERT_відхиляються(string expression, string messageKey)
    {
        Formulas([Formula(1, "M", expression)]);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(Publish);

        Assert.Equal("ECR-TMPL-4223", error.ErrorCode);
        Assert.Equal(messageKey, error.Details!["messageKey"]);
        Assert.Equal("M", error.Details["formula"]);
        Assert.False(_version.IsPublished);
    }

    /// <remarks>
    /// ⚠ Одиниця <c>!M</c> — оголошена <c>OutputUnitId</c>. Без неї посилання на
    /// формулу було б безрозмірним, і т + кг через проміжну формулу проходило б.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-16.7")]
    public async Task Формула_в_тоннах_плюс_константа_в_кілограмах_відхиляється()
    {
        Formulas(
        [
            Formula(1, "M", "CST.A * 2", unit: Tonne),
            Formula(2, "T", "!M + CST.B"),
        ]);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(Publish);

        Assert.Equal("ECR-TMPL-4223", error.ErrorCode);
        Assert.Equal("expr.unit.addNeedsConvert", error.Details!["messageKey"]);
        Assert.Equal("T", error.Details["formula"]);
        Assert.False(_version.IsPublished);
    }

    /// <summary>Перелік — усі формули, а не перша.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-16.7")]
    public async Task Відмова_називає_всі_формули_з_несумісними_одиницями()
    {
        Formulas(
        [
            Formula(1, "M", "CST.A + CST.B"),
            Formula(2, "N", "CST.C + CST.A"),
            Formula(3, "P", "CST.C - CST.B"),
        ]);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(Publish);

        Assert.Equal("M, P", error.Details!["formulas"]);
        Assert.Equal("2", error.Details["count"]);
        Assert.Contains("«P»", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-16.6")]
    public async Task CONVERT_між_розмірностями_відхиляється()
    {
        Formulas([Formula(1, "M", "CONVERT(CST.V, 'm3', 'kg')")]);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(Publish);

        Assert.Equal("ECR-TMPL-4223", error.ErrorCode);
        Assert.Equal("expr.unit.convertDimensions", error.Details!["messageKey"]);
        Assert.Equal("m3", error.Details["from"]);
        Assert.Equal("kg", error.Details["to"]);
    }

    /// <summary>Сумісні одиниці, явний CONVERT і безрозмірні аргументи публікуються.</summary>
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-16.7")]
    [InlineData("CST.A + CST.C")]
    [InlineData("CST.A + CONVERT(CST.B, 'kg', 't')")]
    [InlineData("if(CST.A > CST.C, CST.A, CST.C)")]
    [InlineData("CST.A * 1000 + @tons")]
    [InlineData("CST.A / CST.C")]
    public async Task Сумісні_одиниці_публікуються(string expression)
    {
        Formulas([Formula(1, "M", expression, arguments: "tons")]);

        await Publish();

        Assert.True(_version.IsPublished);
    }

    /// <remarks>
    /// ⛔ Коефіцієнт × маса (кг/т × т) — звичайна форма методології, а похідних
    /// одиниць для множення довідник не має. Відмова тут заблокувала б кожну
    /// методологію з емісійним коефіцієнтом.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-16.6")]
    public async Task Добуток_коефіцієнта_на_масу_публікується()
    {
        Formulas(
        [
            Formula(1, "M", "CST.A * 2", unit: Tonne),
            Formula(2, "E", "CST.EF * !M"),
        ]);

        await Publish();

        Assert.True(_version.IsPublished);
    }

    /// <remarks>
    /// ⚠ Константа по речовинах у різних одиницях: одиниця <c>CST.K</c> залежить
    /// від рядка, і статично її не знає ніхто — тому не перевіряється, а не
    /// відхиляється за першим рядком.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-16.6")]
    public async Task Константа_з_різними_одиницями_по_рядках_не_судиться()
    {
        var first = Constant("K", 1m, Tonne);
        first.SetScope(null, 901);
        var second = Constant("K", 2m, Kilogram);
        second.SetScope(null, 902);
        _constants = [.. _constants, first, second];

        Formulas([Formula(1, "M", "CST.K + CST.B", scope: MethodologyFormulaScope.Substance)]);

        await Publish();

        Assert.True(_version.IsPublished);
    }

    /// <summary>Результат у масі, а оголошено об'єм — відмова (третя клауза ФВ-16.6).</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-16.6")]
    public async Task Результат_в_іншій_розмірності_ніж_оголошений_відхиляється()
    {
        Formulas([Formula(1, "M", "CST.A * 2", unit: CubicMetre)]);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(Publish);

        Assert.Equal("ECR-TMPL-4223", error.ErrorCode);
        Assert.Equal("expr.unit.dimensionMismatch", error.Details!["messageKey"]);
        Assert.Equal("M", error.Details["formula"]);
        Assert.False(_version.IsPublished);
    }

    /// <remarks>
    /// Невідомий результат (коефіцієнт × маса, без похідної) і однакова
    /// розмірність не судяться: інакше відмова заблокувала б легальні формули.
    /// </remarks>
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-16.6")]
    [InlineData("CST.A * 2", Tonne)]
    [InlineData("CST.A * 2", Kilogram)]
    [InlineData("CST.EF * CST.A", CubicMetre)]
    [InlineData("@x", CubicMetre)]
    public async Task Збіг_або_невідомий_результат_з_оголошеним_публікується(string expression, int unit)
    {
        Formulas([Formula(1, "M", expression, unit: unit, arguments: "x")]);

        await Publish();

        Assert.True(_version.IsPublished);
    }

    // ─────────────────────────────────────────────────────────────────────────

    private Task<MethodologyPublicationDiff> Publish()
        => new PublishMethodologyHandler(
                _module, _store, _formulas, _bindings, _units, _uow, _audit, _access, _user, _clock)
            .HandleAsync(VersionId, "ФВ-16.7", From, CancellationToken.None);

    private static UnitCatalogSnapshot Catalogue()
        => new(
            new Dictionary<string, UnitRef>(StringComparer.OrdinalIgnoreCase)
            {
                ["t"] = new(Tonne, "t", Mass, 1000m),
                ["kg"] = new(Kilogram, "kg", Mass),
                ["m3"] = new(CubicMetre, "m3", Volume),
                ["kg/t"] = new(KgPerTonne, "kg/t", 3),
            },
            new Dictionary<string, int>(StringComparer.Ordinal));

    private static MethodologyConstant Constant(string code, decimal value, int unit)
        => new(VersionId, EcrCode.Create(code), value, unit);

    private void Formulas(List<MethodologyFormula> formulas)
        => _store.GetFormulasAsync(VersionId, Arg.Any<CancellationToken>()).Returns(formulas);

    private static MethodologyFormula Formula(
        int id,
        string code,
        string expression,
        int? unit = null,
        string? arguments = null,
        MethodologyFormulaScope scope = MethodologyFormulaScope.Row)
    {
        var formula = new MethodologyFormula(VersionId, EcrCode.Create(code), expression);
        formula.SetScope(scope);

        if (arguments is not null)
        {
            formula.SetArguments(arguments);
        }

        if (unit is { } unitId)
        {
            formula.SetOutputUnit(unitId);
        }

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
        new("golden-a3a",
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
