using System.Reflection;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Expressions;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Application.Templates;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Expressions.Graph;
using Ecr.Expressions.Parsing;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Expressions;

/// <summary>
/// Перевірка виразу при введенні (<c>ФВ-9.15a</c>).
/// </summary>
/// <remarks>
/// ⛔ Головне твердження цих тестів одне: **редактор каже те саме, що скаже
/// публікація, і на тих самих позиціях**. Редактор, який світить зеленим те, що
/// публікація відхилить, гірший за відсутній: він навчає довіряти собі, і
/// відмова приходить тоді, коли її вже нікуди подіти — за годину до подання
/// форми.
/// </remarks>
public sealed class ValidateExpressionTests
{
    private static readonly DateTime Now = new(2026, 2, 1, 12, 0, 0, DateTimeKind.Utc);

    // ⚠ Мок ПОРТА СХОВИЩА, а не узагальненого репозиторію. Раніше тут стояв
    // `IRepository<TemplateVersion, int>`, і саме він приховував дефект:
    // справжня реалізація віддає версію без навігацій, а мок — граф, зібраний
    // у пам'яті. Мок відрізнявся від реалізації рівно тим, у чому полягав
    // дефект (`A7 §4.3`); те, що структура справді доїжджає, доводить
    // інтеграційний `TemplateVersionStructureTests` на живій базі.
    private readonly ITemplateVersionStore _versions = Substitute.For<ITemplateVersionStore>();

    // ⚠ Репозиторій потрібен публікації лише для `DeprecateAsync`; структуру
    // вона бере зі сховища (`_versions`).
    private readonly IRepository<TemplateVersion, int> _repository =
        Substitute.For<IRepository<TemplateVersion, int>>();
    private readonly IUnitCatalog _catalogue = Substitute.For<IUnitCatalog>();
    private readonly IMetadataCache _cache = Substitute.For<IMetadataCache>();
    private readonly IFormulaEngine _formulas = Substitute.For<IFormulaEngine>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly TemplateVersion _draft;

    public ValidateExpressionTests()
    {
        _draft = new TemplateVersion(templateId: 1, version: "1.0.0.0", createdByUserId: 7, utcNow: Now);
        _clock.UtcNow.Returns(Now);
        _versions.GetWithStructureAsync(1, Arg.Any<CancellationToken>()).Returns(_draft);
        _catalogue.GetAsync(Arg.Any<CancellationToken>()).Returns(UnitCatalogSnapshot.Empty);

        _user.UserId.Returns(9);
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>()).Returns(new AccessProfile
        {
            CacheKey = "p",
            UserId = 9,
            SecurityStamp = "s",
            Permissions = new HashSet<string>(StringComparer.Ordinal)
            {
                "Calculation.View", "Template.View", "Template.Publish",
            },
            Grants = new Dictionary<string, GrantLevel>(),
            Denies = new HashSet<string>(),
            RoleIds = new HashSet<int>(),
        });

        var parser = new Parser();
        _formulas.Parse(Arg.Any<string>(), Arg.Any<ExpressionDialect>())
                 .Returns(call => parser.Parse(call.ArgAt<string>(0), call.ArgAt<ExpressionDialect>(1)));

        // ⛔ Обхід AST теж справжній. Заглушка тут зробила б головний тест
        // порожнім: резолвінг посилань живе саме в ньому, і без нього ні
        // редактор, ні публікація не сказали б нічого про `[Apr]` — а тест
        // порівнював би дві однакові порожнечі й лишався зеленим завжди.
        var engine = new RealFormulaEngine();
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

    [Theory]
    [InlineData("SUM([Jan]")]
    [InlineData("SUM([Jan], [Apr])")]
    [InlineData("VLOOKUP([Jan], 1, 2)")]
    [InlineData("SUM(@Fuel)")]
    [InlineData("ROUND([Jan])")]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.15a")]
    public async Task Редактор_каже_те_саме_що_публікація_і_на_тих_самих_позиціях(string expression)
    {
        // ⛔ ЦЕ головний тест усього `B-4`. Він порівнює два переліки, здобуті
        // різними шляхами: один — з відмови публікації, другий — з перевірки
        // при введенні. Вони мусять збігатися ПОВНІСТЮ, разом із позиціями,
        // бо саме позиції підкреслюють текст під курсором.
        //
        // ⚠ Тест упаде, щойно хтось додасть перевірку в публікацію повз
        // спільний `CheckExpression` — тобто рівно тоді, коли редактор почне
        // брехати. Без нього розбіжність виявляли б користувачі.
        var table = Structure(expression);

        var fromPublish = await PublishDiagnosticsAsync().ConfigureAwait(true);
        var fromEditor = await EditorDiagnosticsAsync(expression, table).ConfigureAwait(true);

        Assert.NotEmpty(fromPublish);
        Assert.Equal(Render(fromPublish), Render(fromEditor));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.15a")]
    public async Task Правильний_вираз_не_дає_жодного_зауваження()
    {
        var table = Structure("SUM([Jan])");

        var result = await Handler()
            .HandleAsync(Request("SUM([Jan])", table), CancellationToken.None)
            .ConfigureAwait(true);

        Assert.Empty(result.Diagnostics);

        // ⚠ Тип результату — частина відповіді: без нього редактор не може
        // сказати «ця формула дає число», а саме це питання виникає першим,
        // коли формулу прив'язують до колонки з оголошеним типом.
        Assert.Equal("Number", result.ResultType);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.15a")]
    public async Task Без_версії_шаблону_названо_чого_НЕ_перевіряли()
    {
        // ⛔ Порожній перелік зауважень без структури означає «синтаксис
        // цілий», а не «вираз правильний». Мовчазна різниця між цими двома
        // твердженнями і є способом пообіцяти публікацію, якої не буде: вираз
        // із посиланням на неіснуючу колонку тут виглядав би бездоганним.
        var result = await Handler()
            .HandleAsync(
                new ExpressionValidationRequest(
                    "SUM([НемаТакої])", ExpressionDialect.Template,
                    TemplateVersionId: null, TableDefId: null, RowKey: null, ColumnDefId: null),
                CancellationToken.None)
            .ConfigureAwait(true);

        Assert.Empty(result.Diagnostics);

        Assert.Contains(ValidateExpressionHandler.SkippedReferences, result.SkippedChecks, StringComparer.Ordinal);
        Assert.Contains(ValidateExpressionHandler.SkippedTypes, result.SkippedChecks, StringComparer.Ordinal);
        Assert.Contains(ValidateExpressionHandler.SkippedUnits, result.SkippedChecks, StringComparer.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.15a")]
    public async Task Цикл_названо_непереверненим_завжди()
    {
        // ⛔ Цикл є властивістю ВЕРСІЇ, а не виразу: один текст не містить у
        // собі відповіді, чи утворює він коло з рештою формул. Промовчати тут
        // означало б сказати «циклу немає» — і публікація відмовляла б після
        // зеленого редактора, не пояснивши, що змінилося.
        var table = Structure("SUM([Jan])");

        var result = await Handler()
            .HandleAsync(Request("SUM([Jan])", table), CancellationToken.None)
            .ConfigureAwait(true);

        Assert.Contains(ValidateExpressionHandler.SkippedCycle, result.SkippedChecks, StringComparer.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.15a")]
    public async Task Без_права_на_структуру_перевірка_із_версією_відмовляє()
    {
        // ⛔ Діагностика називає коди колонок і рядків, тобто ВІДДАЄ структуру.
        // Без цієї перевірки ендпоінт був би обхідним шляхом до неї для того,
        // хто права на структуру не має.
        var table = Structure("SUM([Jan])");

        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>()).Returns(new AccessProfile
        {
            CacheKey = "p",
            UserId = 9,
            SecurityStamp = "s",
            Permissions = new HashSet<string>(StringComparer.Ordinal) { "Calculation.View" },
            Grants = new Dictionary<string, GrantLevel>(),
            Denies = new HashSet<string>(),
            RoleIds = new HashSet<int>(),
        });

        await Assert.ThrowsAsync<AccessDeniedException>(
            () => Handler().HandleAsync(Request("SUM([Jan])", table), CancellationToken.None))
            .ConfigureAwait(true);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Requirement", "ФВ-9.15a")]
    public async Task І_редактор_і_публікація_витягують_залежності_через_ПОРТ_зі_знімком_чернетки()
    {
        // ⛔ Тест упаде, щойно хтось знову заведе власний `DependencyExtractor`
        // у редакторі або в публікації. Саме так і було: порт брав знімок із
        // КЕШУ, а обидва шляхи працюють над ЧЕРНЕТКОЮ, якої в кеші немає за
        // побудовою, — тож обидва йшли повз порт і тримали по власній копії
        // обходу AST. Дві копії відповіді на питання «від чого залежить
        // формула» розходяться на першій правці, і розбіжність видно не як
        // помилку, а як довіру до зеленого редактора, після якого публікація
        // відмовляє (`H-3`, директива №06 §1).
        var table = Structure("SUM([Jan], [Apr])");

        await PublishDiagnosticsAsync().ConfigureAwait(true);
        var afterPublish = Extractions();

        await EditorDiagnosticsAsync("SUM([Jan], [Apr])", table).ConfigureAwait(true);
        var afterEditor = Extractions();

        Assert.NotEmpty(afterPublish);
        Assert.True(
            afterEditor.Count > afterPublish.Count,
            "редактор теж має ходити через порт, а не обходити його власним розкривачем");

        // ⛔ Знімок — саме ЧЕРНЕТКИ, і приходить він параметром. Це і є та
        // обставина, через яку метод порту раніше був недосяжним: із кешу
        // чернетка не прийшла б узагалі, і перевірка мовчки працювала б над
        // попередньою редакцією структури.
        Assert.All(afterEditor, snapshot =>
        {
            Assert.NotNull(snapshot);
            Assert.Equal(_draft.Id, snapshot.TemplateVersionId);
        });
    }

    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Знімки, з якими рушій просили витягти залежності.</summary>
    private List<TemplateVersionSnapshot?> Extractions()
        => [.. _formulas.ReceivedCalls()
            .Where(c => string.Equals(
                c.GetMethodInfo().Name,
                nameof(IFormulaEngine.ExtractDependencies),
                StringComparison.Ordinal))
            .Select(c => (TemplateVersionSnapshot?)c.GetArguments()[1])];

    private ValidateExpressionHandler Handler()
        => new(_versions, _catalogue, _formulas, _access, _user);

    private static ExpressionValidationRequest Request(string expression, TableDef table)
        => new(expression, ExpressionDialect.Template, 1, table.Id, null, table.Columns[1].Id);

    /// <summary>Зауваження, які видала б публікація цієї версії.</summary>
    private async Task<IReadOnlyList<DiagnosticInfo>> PublishDiagnosticsAsync()
    {
        var handler = new PublishTemplateVersionHandler(
            _repository, _versions, _formulas, _cache, _catalogue,
            _access, _user, _audit, _uow, _clock);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => handler.PublishAsync(1, userId: 9, CancellationToken.None)).ConfigureAwait(true);

        return (IReadOnlyList<DiagnosticInfo>)((IEnumerable<DiagnosticInfo>)error.Details!["diagnostics"]!)
            .ToList();
    }

    /// <summary>Зауваження, які видає перевірка при введенні.</summary>
    private async Task<IReadOnlyList<DiagnosticInfo>> EditorDiagnosticsAsync(
        string expression, TableDef table)
    {
        var result = await Handler()
            .HandleAsync(Request(expression, table), CancellationToken.None)
            .ConfigureAwait(true);

        return result.Diagnostics;
    }

    /// <summary>Порівнянний вигляд: код, позиція, довжина і текст.</summary>
    private static string Render(IEnumerable<DiagnosticInfo> diagnostics)
        => string.Join("\n", diagnostics.Select(d => $"{d.Code}@{d.Position}+{d.Length}: {d.Message}"));

    /// <summary>Версія з однією таблицею і однією формулою колонки.</summary>
    private TableDef Structure(string expression)
    {
        var builder = new TemplateBuilder { TemplateVersionId = 1 };
        var sheet = builder.Sheet("Water");
        var table = builder.Table(sheet, "Main");
        builder.Column(table, "Jan", isMonthColumn: true);
        builder.Column(table, "Total");
        builder.Row(table, "7001001", 1);

        var formula = new FormulaDef(
            table.Id, FormulaScope.Column, expression, ExpressionDialect.Template);

        typeof(Entity<int>).GetProperty("Id")!.SetValue(formula, 100);
        typeof(FormulaDef).GetProperty(nameof(FormulaDef.ColumnDefId))!
            .SetValue(formula, table.Columns[1].Id);

        table.AddFormula(formula);

        typeof(TemplateVersion).GetProperty(nameof(TemplateVersion.Id))!.SetValue(_draft, 1);
        _draft.GetType().GetField("_sheets", BindingFlags.Instance | BindingFlags.NonPublic)!
              .SetValue(_draft, new List<SheetDef> { sheet });

        return table;
    }
}
