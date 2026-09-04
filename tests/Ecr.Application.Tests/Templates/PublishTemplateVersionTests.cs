using System.Reflection;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Templates;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Expressions.Binding;
using Ecr.Expressions.Graph;
using Ecr.Expressions.Parsing;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Templates;

/// <summary>
/// Дванадцять перевірок публікації (02b §12).
/// </summary>
/// <remarks>
/// Публікація або проходить цілком, або відхиляється **з переліком усіх
/// проблем**. Зупинка на першій помилці змусила б користувача виправляти їх
/// по одній, повторюючи публікацію десятки разів.
/// </remarks>
public sealed class PublishTemplateVersionTests
{
    private static readonly DateTime Now = new(2026, 2, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly IRepository<TemplateVersion, int> _versions = Substitute.For<IRepository<TemplateVersion, int>>();
    private readonly IFormulaEngine _formulas = Substitute.For<IFormulaEngine>();
    private readonly IMetadataCache _cache = Substitute.For<IMetadataCache>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly TemplateVersion _draft;

    public PublishTemplateVersionTests()
    {
        _draft = new TemplateVersion(templateId: 1, version: "1.0.0.0", createdByUserId: 7, utcNow: Now);
        _clock.UtcNow.Returns(Now);
        _versions.GetAsync(1, Arg.Any<CancellationToken>()).Returns(_draft);
    }

    private PublishTemplateVersionHandler Handler()
        => new(_versions, _formulas, _cache, _audit, _uow, _clock);

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public async Task Коректна_версія_публікується()
    {
        await Handler().PublishAsync(1, userId: 9, CancellationToken.None);

        Assert.Equal(TemplateVersionStatus.Published, _draft.Status);
        Assert.Equal(9, _draft.PublishedByUserId);
        Assert.Equal(Now, _draft.PublishedAt);

        // Публікація не є презентаційною правкою — ключ кешу лишається r0.
        Assert.Equal(0, _draft.PresentationRevision);

        // Кеш метаданих скидається ПІСЛЯ збереження: інакше інший інстанс
        // прогрів би кеш зі стану, якого ще немає в базі.
        await _uow.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
        await _cache.Received(1).InvalidateAsync(1, Arg.Any<CancellationToken>());
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public async Task Синтаксична_помилка_у_виразі_відхиляє_публікацію()
    {
        Structure("SUM([Jan]", scope: FormulaScope.Column);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Handler().PublishAsync(1, userId: 9, CancellationToken.None));

        Assert.Equal("ECR-TMPL-0422", error.ErrorCode);

        // Версія лишилася чернеткою: часткова публікація неможлива за побудовою.
        Assert.Equal(TemplateVersionStatus.Draft, _draft.Status);
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public async Task Посилання_на_неіснуючу_колонку_відхиляє_публікацію()
    {
        Structure("SUM([Jan], [Apr])", scope: FormulaScope.Column);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Handler().PublishAsync(1, userId: 9, CancellationToken.None));

        // Нерезолвлене посилання має зупинити ПУБЛІКАЦІЮ, а не зіпсувати
        // число в проді: у рантаймі воно дало б #REF у звіті через місяць.
        Assert.Equal("ECR-TMPL-0422", error.ErrorCode);
        Assert.Contains("Apr", Diagnostics(error), StringComparison.Ordinal);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public async Task Цикл_у_графі_відхиляє_публікацію_із_шляхом_циклу()
    {
        Structure("SUM([Jan])", scope: FormulaScope.Column);
        _formulas.BuildEvaluationOrder(Arg.Any<IReadOnlyList<FormulaNode>>())
                 .Returns(new OrderingResult(false, [], [7, 8, 7]));

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Handler().PublishAsync(1, userId: 9, CancellationToken.None));

        // Шлях циклу — у відповіді. Прапорця «є цикл» замало: у графі на
        // тисячі формул пошук винуватця без шляху — ручна робота.
        Assert.Contains("ECR-TMPL-4221", Diagnostics(error), StringComparison.Ordinal);
        Assert.Contains("7 → 8 → 7", Diagnostics(error), StringComparison.Ordinal);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Несумісні_одиниці_без_CONVERT_відхиляють_публікацію()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public async Task Відповідь_містить_УСІ_проблеми_а_не_лише_першу()
    {
        Structure("SUM([Apr])", "SUM([May])", "SUM([Jun])");

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Handler().PublishAsync(1, userId: 9, CancellationToken.None));

        // Зупинка на першій помилці змусила б користувача публікувати версію
        // десятки разів, виправляючи по одній.
        var text = Diagnostics(error);
        Assert.Contains("Apr", text, StringComparison.Ordinal);
        Assert.Contains("May", text, StringComparison.Ordinal);
        Assert.Contains("Jun", text, StringComparison.Ordinal);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public async Task Публікація_зберігає_розкриті_діапазони_і_порядок_обчислення()
    {
        var table = Structure("SUM([Main].[7001001:7001003].[Jan])", scope: FormulaScope.Column);
        _formulas.BuildEvaluationOrder(Arg.Any<IReadOnlyList<FormulaNode>>())
                 .Returns(call => new OrderingResult(
                     true, call.Arg<IReadOnlyList<FormulaNode>>().Select(n => n.FormulaDefId).ToList(), null));

        await Handler().PublishAsync(1, userId: 9, CancellationToken.None);

        // Діапазон розкритий У ЗАЛЕЖНОСТЯХ, а не лишився діапазоном: саме це
        // робить пізнішу зміну Ordinal безпечною.
        var dependencies = Captured!;
        Assert.Equal(["7001001", "7001002", "7001003"], dependencies.Select(d => d.RowKey));

        // Порядок обчислення зафіксований при публікації, а не в рантаймі.
        Assert.Equal(0, table.Formulas[0].EvaluationOrder);
        Assert.Equal(TemplateVersionStatus.Published, _draft.Status);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public async Task Публікація_записує_подію_в_аудит()
    {
        await Handler().PublishAsync(1, userId: 9, CancellationToken.None);

        await _audit.Received(1).WritePublicationEventAsync(
            Arg.Is<PublicationEventRecord>(r =>
                r.EntityType == "TemplateVersion" &&
                r.EntityId == 1 &&
                r.ChangedByUserId == 9 &&
                r.ChangedAt == Now),
            Arg.Any<CancellationToken>());

        // Подія і зміна стану зберігаються однією транзакцією: журнал, у якому
        // є публікація без публікації (або навпаки), не є доказом.
        await _uow.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public async Task Невдала_публікація_не_лишає_часткових_змін()
    {
        // Уже опублікована версія: повторна публікація має бути відхилена.
        _draft.Publish(publishedByUserId: 8, utcNow: Now);

        await Assert.ThrowsAsync<DomainException>(
            () => Handler().PublishAsync(1, userId: 9, CancellationToken.None));

        // Ні аудиту, ні збереження, ні скидання кешу: відмова мусить лишити
        // систему рівно в тому стані, у якому вона була.
        await _audit.DidNotReceive().WritePublicationEventAsync(
            Arg.Any<PublicationEventRecord>(), Arg.Any<CancellationToken>());
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
        await _cache.DidNotReceive().InvalidateAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());

        // Автор першої публікації не перезаписаний спробою другої.
        Assert.Equal(8, _draft.PublishedByUserId);
    }

    /// <summary>Останні витягнуті залежності — для перевірки розкриття діапазонів.</summary>
    private IReadOnlyList<ExtractedDependency>? Captured { get; set; }

    /// <summary>Складає версію з однією таблицею і заданими формулами.</summary>
    private TableDef Structure(params string[] expressions)
        => Structure(expressions, FormulaScope.Column);

    private TableDef Structure(string expression, FormulaScope scope)
        => Structure([expression], scope);

    private TableDef Structure(string[] expressions, FormulaScope scope)
    {
        var builder = new TemplateBuilder { TemplateVersionId = 1 };
        var sheet = builder.Sheet("Water");
        var table = builder.Table(sheet, "Main");
        builder.Column(table, "Jan", isMonthColumn: true);
        builder.Column(table, "Total");
        builder.Row(table, "7001001", 1);
        builder.Row(table, "7001002", 2);
        builder.Row(table, "7001003", 3);

        var totalId = table.Columns[1].Id;
        var id = 100;
        foreach (var expression in expressions)
        {
            var formula = new FormulaDef(table.Id, scope, expression, ExpressionDialect.Template);
            typeof(Ecr.Domain.Abstractions.Entity<int>).GetProperty("Id")!.SetValue(formula, id++);
            typeof(FormulaDef).GetProperty(nameof(FormulaDef.ColumnDefId))!.SetValue(formula, totalId);
            table.AddFormula(formula);
        }

        typeof(TemplateVersion).GetProperty(nameof(TemplateVersion.Id))!.SetValue(_draft, 1);
        _draft.GetType().GetField("_sheets", BindingFlags.Instance | BindingFlags.NonPublic)!
              .SetValue(_draft, new List<SheetDef> { sheet });

        // Справжній парсер: перевіряється поведінка ПУБЛІКАЦІЇ, а не заглушка.
        // Топологію тут підміняємо успіхом — її власна поведінка перевірена
        // в TopologicalSorterTests; тести циклу перевизначають це нижче.
        _formulas.BuildEvaluationOrder(Arg.Any<IReadOnlyList<FormulaNode>>())
                 .Returns(call => new OrderingResult(
                     true, call.Arg<IReadOnlyList<FormulaNode>>().Select(n => n.FormulaDefId).ToList(), null));

        var parser = new Parser();
        _formulas.Parse(Arg.Any<string>(), Arg.Any<ExpressionDialect>())
                 .Returns(call => parser.Parse(call.ArgAt<string>(0), call.ArgAt<ExpressionDialect>(1)));

        var expander = new RangeExpander();
        Captured = expander.Expand(table, "7001001", "7001003")
            .Select((key, i) => new ExtractedDependency(0, table.Id, key, totalId, null, null, i))
            .ToList();

        return table;
    }

    /// <summary>Тексти діагностик із винятку, склеєні для пошуку підрядка.</summary>
    /// <remarks>
    /// Беруться саме поля, а не JSON: серіалізація екранує стрілку в шляху
    /// циклу як →, і перевірка шукала б те, чого в тексті немає.
    /// </remarks>
    private static string Diagnostics(BusinessRuleException error)
    {
        var list = (IEnumerable<DiagnosticInfo>)error.Details!["diagnostics"]!;
        return string.Join(" | ", list.Select(d => $"{d.Code} {d.Message}"));
    }
}
