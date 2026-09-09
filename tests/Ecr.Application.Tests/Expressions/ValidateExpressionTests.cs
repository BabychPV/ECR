using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Expressions;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Application.Templates;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Expressions.Binding;
using Ecr.Expressions.Evaluation;
using Ecr.Expressions.Graph;
using Ecr.Expressions.Parsing;
using Ecr.Infrastructure.Caching;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Expressions;

/// <summary>
/// Перевірка виразу при введенні (<c>ФВ-9.15a</c>). На <b>реальному</b> SQL Server.
/// </summary>
/// <remarks>
/// ⛔ Головне твердження цих тестів одне: **редактор каже те саме, що скаже
/// публікація, і на тих самих позиціях**. Редактор, який світить зеленим те, що
/// публікація відхилить, гірший за відсутній: він навчає довіряти собі, і
/// відмова приходить тоді, коли її вже нікуди подіти — за годину до подання
/// форми.
///
/// ⛔ Переведено з моків на живу базу (директива №09 §8.2). Обидві половини
/// порівняння брали структуру з ОДНОГО мока <c>ITemplateVersionStore</c>, який
/// віддавав граф, зібраний рефлексією в пам'яті. Тотожність двох відповідей на
/// вигаданій структурі не є доказом тотожності на справжній: розійтися вони
/// можуть саме там, де структура приходить із бази (порожні навігації, м'яко
/// видалені колонки, чужа версія) — тобто рівно в тому, чого мок не відтворює.
///
/// ⛔ Публікація тут — СПРАВЖНІЙ <c>PublishTemplateVersionHandler</c> з усіма
/// реальними складниками: сховище, репозиторій, рушій, кеш метаданих, довідник
/// одиниць, аудит, транзакція. Це важливо саме для цього тесту: він мусить
/// упасти й тоді, коли перевірку додали в ОБРОБНИК повз спільний
/// <c>PublishChecks.CheckExpression</c>, а не лише всередині <c>Run</c>.
///
/// ⚠ <c>IAccessDecisionService</c> лишається підробкою: переписування 34 таких
/// місць директива §8.2 виносить за межі цього проходу.
/// </remarks>
[Collection("SqlServer")]
public sealed class ValidateExpressionTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 2, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();

    [Theory]
    [InlineData("SUM([Jan]")]
    [InlineData("SUM([Jan], [Apr])")]
    [InlineData("VLOOKUP([Jan], 1, 2)")]
    [InlineData("SUM(@Fuel)")]
    [InlineData("ROUND([Jan])")]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
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
        var version = await ArrangeAsync(expression);

        var fromPublish = await PublishDiagnosticsAsync(version);
        var fromEditor = await EditorDiagnosticsAsync(version, expression);

        Assert.NotEmpty(fromPublish);
        Assert.Equal(Render(fromPublish), Render(fromEditor));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-9.15a")]
    public async Task Правильний_вираз_не_дає_жодного_зауваження()
    {
        var version = await ArrangeAsync("SUM([Jan])");

        await using var db = Context();
        var result = await Handler(db)
            .HandleAsync(Request(version, "SUM([Jan])"), CancellationToken.None);

        Assert.Empty(result.Diagnostics);

        // ⚠ Тип результату — частина відповіді: без нього редактор не може
        // сказати «ця формула дає число», а саме це питання виникає першим,
        // коли формулу прив'язують до колонки з оголошеним типом.
        Assert.Equal("Number", result.ResultType);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-9.15a")]
    public async Task Без_версії_шаблону_названо_чого_НЕ_перевіряли()
    {
        // ⛔ Порожній перелік зауважень без структури означає «синтаксис
        // цілий», а не «вираз правильний». Мовчазна різниця між цими двома
        // твердженнями і є способом пообіцяти публікацію, якої не буде: вираз
        // із посиланням на неіснуючу колонку тут виглядав би бездоганним.
        await using var db = Context();
        var result = await Handler(db).HandleAsync(
            new ExpressionValidationRequest(
                "SUM([НемаТакої])", ExpressionDialect.Template,
                TemplateVersionId: null, TableDefId: null, RowKey: null, ColumnDefId: null),
            CancellationToken.None);

        Assert.Empty(result.Diagnostics);

        Assert.Contains(ValidateExpressionHandler.SkippedReferences, result.SkippedChecks, StringComparer.Ordinal);
        Assert.Contains(ValidateExpressionHandler.SkippedTypes, result.SkippedChecks, StringComparer.Ordinal);
        Assert.Contains(ValidateExpressionHandler.SkippedUnits, result.SkippedChecks, StringComparer.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-9.15a")]
    public async Task Цикл_названо_непереверненим_завжди()
    {
        // ⛔ Цикл є властивістю ВЕРСІЇ, а не виразу: один текст не містить у
        // собі відповіді, чи утворює він коло з рештою формул. Промовчати тут
        // означало б сказати «циклу немає» — і публікація відмовляла б після
        // зеленого редактора, не пояснивши, що змінилося.
        var version = await ArrangeAsync("SUM([Jan])");

        await using var db = Context();
        var result = await Handler(db)
            .HandleAsync(Request(version, "SUM([Jan])"), CancellationToken.None);

        Assert.Contains(ValidateExpressionHandler.SkippedCycle, result.SkippedChecks, StringComparer.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-9.15a")]
    public async Task Без_права_на_структуру_перевірка_із_версією_відмовляє()
    {
        // ⛔ Діагностика називає коди колонок і рядків, тобто ВІДДАЄ структуру.
        // Без цієї перевірки ендпоінт був би обхідним шляхом до неї для того,
        // хто права на структуру не має.
        var version = await ArrangeAsync("SUM([Jan])");

        await using var db = Context();
        var handler = Handler(db, "Calculation.View");

        await Assert.ThrowsAsync<AccessDeniedException>(
            () => handler.HandleAsync(Request(version, "SUM([Jan])"), CancellationToken.None));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
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
        //
        // ⚠ Рушій тут — СПРАВЖНІЙ, лише обгорнутий записувачем. Підміна
        // рушія заглушкою зробила б головне порівняння вище звірянням двох
        // порожнеч; записувач же нічого не змінює у відповідях.
        var version = await ArrangeAsync("SUM([Jan], [Apr])");
        var engine = new RecordingFormulaEngine();

        await PublishDiagnosticsAsync(version, engine);
        var afterPublish = engine.Snapshots.Count;

        await EditorDiagnosticsAsync(version, "SUM([Jan], [Apr])", engine);
        var afterEditor = engine.Snapshots.Count;

        Assert.True(afterPublish > 0, "публікація має ходити за залежностями через порт");
        Assert.True(
            afterEditor > afterPublish,
            "редактор теж має ходити через порт, а не обходити його власним розкривачем");

        // ⛔ Знімок — саме ЧЕРНЕТКИ, і приходить він параметром. Це і є та
        // обставина, через яку метод порту раніше був недосяжним: із кешу
        // чернетка не прийшла б узагалі, і перевірка мовчки працювала б над
        // попередньою редакцією структури.
        Assert.All(engine.Snapshots, snapshot =>
        {
            Assert.NotNull(snapshot);
            Assert.Equal(version.VersionId, snapshot!.TemplateVersionId);
        });
    }

    // ─────────────────────────────────────────────────────────────────────────

    private static ExpressionValidationRequest Request(DraftVersion version, string expression)
        => new(expression, ExpressionDialect.Template, version.VersionId, version.TableDefId, null, version.TotalColumnDefId);

    private ValidateExpressionHandler Handler(
        EcrDbContext db, string? onlyPermission = null, IFormulaEngine? engine = null)
    {
        Profile(onlyPermission);

        return new ValidateExpressionHandler(
            new TemplateVersionStore(db),
            new UnitCatalog(db),
            engine ?? new RealFormulaEngine(),
            _access,
            _user);
    }

    /// <summary>Зауваження, які видала б публікація цієї версії.</summary>
    /// <remarks>
    /// ⛔ Обробник СПРАВЖНІЙ і зібраний із реальних складників. Попередник
    /// будував його з десяти моків, тож перевірка, додана в сам обробник повз
    /// <c>PublishChecks</c>, лишалася непоміченою — а це рівно та розбіжність
    /// між редактором і публікацією, від якої тест і стереже.
    /// </remarks>
    private async Task<IReadOnlyList<DiagnosticInfo>> PublishDiagnosticsAsync(
        DraftVersion version, IFormulaEngine? engine = null)
    {
        Profile(null);

        await using var db = Context();
        using var memory = new MemoryCache(new MemoryCacheOptions());

        var handler = new PublishTemplateVersionHandler(
            new Repository<TemplateVersion, int>(db),
            new TemplateVersionStore(db),
            engine ?? new RealFormulaEngine(),
            new MetadataCache(memory, db),
            new UnitCatalog(db),
            _access,
            _user,
            new AuditWriter(db),
            new UnitOfWork(db),
            new TestClock(Now));

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => handler.PublishAsync(version.VersionId, userId: 9, reason: "Тест", CancellationToken.None));

        return [.. (IEnumerable<DiagnosticInfo>)error.Details!["diagnostics"]!];
    }

    /// <summary>Зауваження, які видає перевірка при введенні.</summary>
    private async Task<IReadOnlyList<DiagnosticInfo>> EditorDiagnosticsAsync(
        DraftVersion version, string expression, IFormulaEngine? engine = null)
    {
        await using var db = Context();
        var result = await Handler(db, null, engine)
            .HandleAsync(Request(version, expression), CancellationToken.None);

        return result.Diagnostics;
    }

    /// <summary>Порівнянний вигляд: код, позиція, довжина і текст.</summary>
    private static string Render(IEnumerable<DiagnosticInfo> diagnostics)
        => string.Join("\n", diagnostics.Select(d => $"{d.Code}@{d.Position}+{d.Length}: {d.Message}"));

    private void Profile(string? onlyPermission)
    {
        var builder = new AccessBuilder { UserId = 9 };

        if (onlyPermission is null)
        {
            builder.Permission("Calculation.View").Permission("Template.View").Permission("Template.Publish");
        }
        else
        {
            builder.Permission(onlyPermission);
        }

        _user.UserId.Returns(9);
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>()).Returns(builder.Build());
    }

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(sql.ConnectionString, o => o.MigrationsHistoryTable("__EFMigrationsHistory", "dbo"))
            .Options);

    /// <summary>
    /// Версія-чернетка з таблицею, двома колонками, рядком і однією формулою
    /// колонки — <b>у базі</b>.
    /// </summary>
    /// <remarks>
    /// ⚠ Вираз кладеться сирим текстом: <c>SaveFormulaDefHandler</c> синтаксис
    /// на запис не перевіряє, і саме тому зламане взагалі може опинитися в
    /// структурі (`S-08`). Без цієї обставини половину тестів файла не було б
    /// на чому поставити.
    /// </remarks>
    private async Task<DraftVersion> ArrangeAsync(string expression)
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        await using var db = Context();

        var template = new Template(EcrCode.Create($"VE{tag}"), Name($"Template {tag}"), 1, Now);
        db.Templates.Add(template);
        await db.SaveChangesAsync();

        var version = new TemplateVersion(template.Id, "1.0.0.0", 1, Now);
        db.TemplateVersions.Add(version);
        await db.SaveChangesAsync();

        var sheet = new SheetDef(version.Id, EcrCode.Create($"S{tag}"), Name("Water"), 1);
        db.SheetDefs.Add(sheet);
        await db.SaveChangesAsync();

        var table = new TableDef(
            sheet.Id, EcrCode.Create($"Main{tag}"), Name("Main"), 1,
            TableLayoutKind.MonthsInColumns, TableRowMode.Fixed);
        db.TableDefs.Add(table);
        await db.SaveChangesAsync();

        var jan = new ColumnDef(table.Id, EcrCode.Create("Jan"), Name("Jan"), 1, CellDataType.Decimal);
        var total = new ColumnDef(table.Id, EcrCode.Create("Total"), Name("Total"), 2, CellDataType.Decimal);
        db.ColumnDefs.Add(jan);
        db.ColumnDefs.Add(total);
        db.RowDefs.Add(new RowDef(table.Id, RowKey.Create("7001001"), 1, Name("Row"), RowKind.Item));
        await db.SaveChangesAsync();

        // ⛔ Формула чіпляється НАВІГАЦІЄЮ `table.AddFormula`, а не
        // `db.FormulaDefs.Add`. Це не стиль: у `cfg.FormulaDef` ДВА зовнішні
        // ключі на `cfg.TableDef` — оголошений `TableDefId` і тіньовий
        // `TableDefId1`, який EF завів під навігацію `TableDef.Formulas`
        // (`HasOne<TableDef>().WithMany()` — без навігації). `GetWithStructureAsync`
        // вантажить формули саме через навігацію, тож рядок, вставлений лише з
        // `TableDefId`, для публікації НЕ ІСНУЄ. Бойовий шлях
        // (`FormulaDefHandlers`) кличе `AddFormula`, і тест мусить іти ним же.
        //
        // ⚠ Мок цього побачити не міг за побудовою — див. `Q-163`.
        var formula = new FormulaDef(table.Id, FormulaScope.Column, expression, ExpressionDialect.Template);
        formula.AssignColumn(total.Id);
        table.AddFormula(formula);
        await db.SaveChangesAsync();

        return new DraftVersion(version.Id, table.Id, total.Id);
    }

    private static LocalizedText Name(string value)
        => new(new Dictionary<string, string> { ["en"] = value });

    private sealed record DraftVersion(int VersionId, int TableDefId, int TotalColumnDefId);

    /// <summary>
    /// Справжній рушій, який ЗАПАМ'ЯТОВУЄ знімки, з якими його просили витягти
    /// залежності.
    /// </summary>
    /// <remarks>
    /// ⚠ Це спостерігач, а не підробка: усі відповіді — від
    /// <see cref="RealFormulaEngine"/>. Замінити рушій заглушкою тут означало б
    /// перевіряти заглушку; не бачити викликів — не мати чим довести, що обидва
    /// шляхи ходять через ПОРТ, а не тримають по власному обходу AST.
    /// </remarks>
    private sealed class RecordingFormulaEngine : IFormulaEngine
    {
        private readonly RealFormulaEngine _inner = new();

        /// <summary>Знімки, передані в <c>ExtractDependencies</c>, у порядку викликів.</summary>
        public List<TemplateVersionSnapshot?> Snapshots { get; } = [];

        public ParseResult Parse(string expression, ExpressionDialect dialect)
            => _inner.Parse(expression, dialect);

        public DependencyExtraction ExtractDependencies(
            ParsedExpression expression, TemplateVersionSnapshot? snapshot, DependencyContext context)
        {
            Snapshots.Add(snapshot);
            return _inner.ExtractDependencies(expression, snapshot, context);
        }

        public EvaluationResult Evaluate(
            ParsedExpression expression, IEvaluationContext context, NumericMode mode = NumericMode.Strict)
            => _inner.Evaluate(expression, context, mode);

        public OrderingResult BuildEvaluationOrder(IReadOnlyList<FormulaNode> nodes)
            => _inner.BuildEvaluationOrder(nodes);
    }
}
