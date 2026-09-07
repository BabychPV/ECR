using Ecr.Application.Calculations;
using Ecr.Application.Common;
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
/// Граф залежностей методології будується за <b>точним</b> збігом токена
/// <c>!Code</c> (пастка 4 директиви ПК-1 №05 §7).
/// </summary>
/// <remarks>
/// ⛔ Регресія тут не видна ніде, крім чисел у черзі перерахунку: пошук
/// підрядка вважає, що <c>!k1_GasComp</c> містить посилання на <c>k1</c>, і граф
/// отримує ребро, якого у виразі немає. На чинному корпусі це 786 хибних ребер
/// із 8474 — 9,3 %.
///
/// ⚠ Наслідок стосується ІНВАЛІДАЦІЇ, а не підстановки значень: зайве ребро не
/// робить число неправильним, воно лише додає формулу в чергу. Тому вада і
/// прожила стільки — вона не має симптому на екрані.
/// </remarks>
public sealed class MethodologyDependencyGraphTests
{
    private const int Author = 7;
    private const int Reviewer = 9;
    private const int VersionId = 51;

    /// <summary>Коротке ім'я, яке є ПРЕФІКСОМ довшого — джерело хибного ребра.</summary>
    private const int ShortNameId = 101;

    /// <summary>Довше ім'я, що починається з короткого.</summary>
    private const int LongNameId = 102;

    /// <summary>Формула, яка посилається ЛИШЕ на довше ім'я.</summary>
    private const int ReferencingId = 103;

    /// <summary>Формула, яка читає ВЛАСНИЙ результат.</summary>
    private const int SelfReferenceId = 104;

    private static readonly DateTime Now = new(2026, 2, 10, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly From = new(2026, 6, 1);

    private readonly IMethodologyStore _store = Substitute.For<IMethodologyStore>();
    private readonly ICalculationModule _module = Substitute.For<ICalculationModule>();
    private readonly IFormulaEngine _formulas = Substitute.For<IFormulaEngine>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly IClock _clock = Substitute.For<IClock>();

    private readonly Methodology _methodology;

    /// <summary>Вузли, з якими публікація пішла в топологічне сортування.</summary>
    private IReadOnlyList<FormulaNode> _nodes = [];

    public MethodologyDependencyGraphTests()
    {
        _clock.UtcNow.Returns(Now);
        _user.UserId.Returns(Reviewer);
        _access.BuildProfileAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(Profile());

        _methodology = new Methodology(EcrCode.Create("GAS_FLARE"), Text("Flare"));

        var version = new MethodologyVersion(
            _methodology.Id, "1.1.0.0", CalculationLevel.Configuration, Author, Now);

        typeof(Entity<int>).GetProperty(nameof(Entity<int>.Id))!.SetValue(version, VersionId);
        _methodology.AddVersion(version);

        _store.FindByVersionAsync(VersionId, Arg.Any<CancellationToken>()).Returns(_methodology);
        _store.GetFormulasAsync(VersionId, Arg.Any<CancellationToken>()).Returns(Formulas());
        _store.GetTestCasesAsync(VersionId, Arg.Any<CancellationToken>()).Returns(TestCases());

        // Набір зелений: предмет тесту — ребра графа, а не правила публікації.
        _module.ExecuteAsync(Arg.Any<CalculationInput>(), Arg.Any<CancellationToken>())
               .Returns(call => Output(call.Arg<CalculationInput>()));

        // ⚠ Розбір і обхід AST — СПРАВЖНІ. Заглушити їх означало б перевіряти
        // заглушку: саме з них публікація дізнається, на що посилається вираз.
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

        // Сортувальник підмінений, бо предмет перевірки — те, ЩО в нього
        // приходить. Його власну поведінку перевіряє TopologicalSorterTests.
        _formulas.BuildEvaluationOrder(Arg.Any<IReadOnlyList<FormulaNode>>())
                 .Returns(call =>
                 {
                     _nodes = call.Arg<IReadOnlyList<FormulaNode>>();
                     return new OrderingResult(true, _nodes.Select(n => n.FormulaDefId).ToList(), null);
                 });
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.4")]
    public async Task Токен_який_є_ПРЕФІКСОМ_іншого_не_створює_ребра_в_графі()
    {
        // ⛔ Тест упаде, щойно ребра почнуть шукати підрядком у тексті виразу.
        // Формула посилається рівно на `!k1_GasComp`; `!k1` у ній не написано
        // жодного разу, і пошук підрядка знаходить його лише тому, що коротке
        // ім'я є початком довшого. Це дефект ІНВАЛІДАЦІЇ: хибне ребро не
        // псує число, воно тягне на перерахунок формулу, якої зміна не
        // торкалася, — і тому не має симптому, за яким його можна помітити.
        await Handler().HandleAsync(VersionId, "Уточнення", From, CancellationToken.None);

        var referencing = _nodes.Single(n => n.FormulaDefId == ReferencingId);

        Assert.Equal([LongNameId], referencing.DependsOnFormulaDefIds);
        Assert.DoesNotContain(ShortNameId, referencing.DependsOnFormulaDefIds);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.4")]
    public async Task Формула_без_посилань_лишається_без_ребер()
    {
        // ⚠ Другий бік того самого твердження: граф має збігатися з токенами
        // ТОЧНО, тобто не лише не вигадувати ребер, а й не втрачати їх. Без
        // цієї половини «жодного ребра ніколи» теж пройшло б.
        await Handler().HandleAsync(VersionId, "Уточнення", From, CancellationToken.None);

        Assert.Empty(_nodes.Single(n => n.FormulaDefId == ShortNameId).DependsOnFormulaDefIds);
        Assert.Empty(_nodes.Single(n => n.FormulaDefId == LongNameId).DependsOnFormulaDefIds);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.4")]
    public async Task Самопосилання_доходить_до_графа_а_не_відсіюється()
    {
        // ⛔ Цей тест існує тому, що фільтр самопосилання вже повертався.
        // `I.21` його зняв, а злиття сусідньої гілки принесло назад разом
        // із переписаним циклом резолвінгу — і жоден тест не впав: шлях
        // методологій не був покритий взагалі, покритий був лише шаблонний.
        //
        // ⚠ Формула, яка читає власний результат, не має порядку
        // обчислення, і `BuildEvaluationOrder` документує, що публікація
        // має це побачити (`ФВ-9.4`). Щоб побачити — ребро мусить дійти.
        await Handler().HandleAsync(VersionId, "Уточнення", From, CancellationToken.None);

        var node = _nodes.Single(n => n.FormulaDefId == SelfReferenceId);

        Assert.Contains(SelfReferenceId, node.DependsOnFormulaDefIds);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "ФВ-9.14")]
    public async Task Оголошений_список_аргументів_доходить_від_сутності_до_перевірки()
    {
        // ⛔ Стережеться ЛАНКА, а не звірка. Звірка була написана й
        // покрита дванадцятьма мутаціями — і мовчала у продуктиві: колонки
        // під `FInfo_Arguments` не існувало, тож `DeclaredArguments` завжди був
        // `null`, а на `null` звірка за домовленістю мовчить (`Q-091`).
        //
        // ⚠ Тест йде через СПРАВЖНІЙ обробник публікації навмисно:
        // виклик `MethodologyPublishChecks.Check` напряму доводить лише, що
        // звірка вміє відмовляти, і нічого — про те, чи є єй що читати.
        var declared = Formula(ShortNameId, "k1", "@FuelConsumption * 2");
        declared.SetArguments("Duration");

        _store.GetFormulasAsync(VersionId, Arg.Any<CancellationToken>())
              .Returns(new List<MethodologyFormula> { declared });

        var error = await Assert.ThrowsAsync<Ecr.Application.Errors.BusinessRuleException>(
            () => Handler().HandleAsync(VersionId, "Уточнення", From, CancellationToken.None));

        Assert.Equal("ECR-CALC-0432", error.ErrorCode);
        Assert.Contains("FuelConsumption", error.Message, StringComparison.Ordinal);
    }

    // ─────────────────────────────────────────────────────────────────────────

    private PublishMethodologyHandler Handler()
        => new(_module, _store, _formulas, _uow, _audit, _access, _user, _clock);

    /// <summary>Профіль із небезпечним правом публікації методології.</summary>
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

    /// <summary>
    /// Три формули, два імені з яких навмисно в стосунку «префікс — продовження».
    /// </summary>
    /// <remarks>
    /// ⚠ Пара взята з чинного корпусу, а не вигадана: там таких збігів серед
    /// коротких імен констант чотирнадцять (<c>k1 ⊂ k10</c>, <c>a ⊂ a0</c>,
    /// <c>LHV ⊂ LHV0</c>, <c>Vch ⊂ Vchmax</c>) і сімдесят п'ять у родині складу
    /// газу.
    /// </remarks>
    private static List<MethodologyFormula> Formulas() =>
    [
        Formula(ShortNameId, "k1", "10"),
        Formula(LongNameId, "k1_GasComp", "20"),
        Formula(ReferencingId, "Total", "!k1_GasComp * 2"),
        Formula(SelfReferenceId, "SelfRef", "!SelfRef + 1"),
    ];

    private static MethodologyFormula Formula(int id, string code, string expression)
    {
        var formula = new MethodologyFormula(VersionId, EcrCode.Create(code), expression);

        // Ідентифікатор дає база; у тесті — руками, бо саме за ним публікація
        // зіставляє вузли графа з формулами.
        typeof(Entity<int>).GetProperty(nameof(Entity<int>.Id))!.SetValue(formula, id);
        return formula;
    }

    private static List<MethodologyTestCase> TestCases() =>
    [
        new("golden-flare",
            new CalculationInput(
                new MethodologyDescriptor(
                    0, VersionId, "GAS_FLARE", "1.1.0.0", CalculationLevel.Configuration,
                    NumericMode.Legacy, CalendarMode.Actual, TraceLevel.Off),
                DocumentId: 700,
                TableInstanceId: 500,
                PeriodKey: new PeriodKey(202601),
                SourceRowKey: "7001001",
                Arguments: []),
            new Dictionary<string, decimal> { ["tons"] = 1m },
            Tolerance: 0.000001m),
    ];

    private static CalculationOutput Output(CalculationInput input) =>
        new(input.DocumentId, input.SourceRowKey,
            [new CalculationOutputValue(input.Methodology.MethodologyVersionId, null, "tons", 1m, 8)],
            []);

    private static LocalizedText Text(string value)
        => new(new Dictionary<string, string> { ["en"] = value });
}
