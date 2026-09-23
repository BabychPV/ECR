using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Expressions;
using Ecr.Expressions.Binding;
using Ecr.Expressions.Parsing;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Expressions.Tests.Binding;

/// <summary>
/// Резолвінг <c>HDR.Код</c> — посилання на поле шапки документа.
/// </summary>
/// <remarks>
/// ⛔ До цієї перевірки код поля в <c>HDR("code")</c> не звірявся ЗОВСІМ:
/// <c>DependencyExtractor.Visit</c> додавав Header-залежність за
/// <c>header.Name</c> без жодного резолвінгу, а <c>PublishChecks.Snapshot</c>
/// не передавав <c>HeaderFields</c> у знімок узагалі (властивість лишалася на
/// дефолтному порожньому списку). Разом це означало, що <c>HDR("TYPO")</c> з
/// неіснуючим кодом публікувався без жодного зауваження і в рантаймі мовчки
/// рахувався як <c>Null</c> — той самий клас мовчазного дефекту, що вже
/// закритий для невідомої таблиці/колонки (<see cref="ReferenceResolver.Resolve"/>).
///
/// ⚠ Тести тут перевіряють САМ РЕЗОЛВІНГ (<see cref="ReferenceResolver.ResolveHeader"/>
/// і те, як ним користується <see cref="DependencyExtractor"/>); те, що
/// <c>PublishChecks.Snapshot</c> справді наповнює <c>HeaderFields</c> знімка
/// з версії, доводить окремий тест у
/// <c>Ecr.Application.Tests.Templates.PublishChecksTests</c>.
/// </remarks>
public sealed class HeaderReferenceResolutionTests
{
    private static readonly RangeExpander Expander = new();

    /// <summary>Знімок з одним живим полем шапки <c>Area</c> (тип String).</summary>
    private static TemplateVersionSnapshot SnapshotWithHeaderField(string code = "Area")
    {
        var builder = new TemplateBuilder { TemplateVersionId = 1 };
        var sheet = builder.Sheet("Water");
        builder.Table(sheet, "Main");

        var field = new HeaderFieldDef(
            builder.TemplateVersionId, EcrCode.Create(code),
            new LocalizedText(new Dictionary<string, string> { ["en"] = code }), ordinal: 1, CellDataType.String);

        return builder.Build() with { HeaderFields = [field] };
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void HDR_з_відомим_кодом_дає_Header_залежність_без_зауважень()
    {
        var snapshot = SnapshotWithHeaderField("Area");
        var extractor = new DependencyExtractor(new ReferenceResolver(snapshot), Expander);
        var root = Expr.Parse("HDR.Area").Expression!.Root;
        var diagnostics = new List<ExpressionDiagnostic>();

        var dependencies = extractor.Extract(root, currentTableDefId: 1, currentRowKey: null, diagnostics: diagnostics);

        var header = Assert.Single(dependencies, d => d.DependsOnKind == DependencyExtractor.KindHeader);
        Assert.Equal("Area", header.RowKey);
        Assert.Null(header.TableDefId);
        Assert.Null(header.ColumnDefId);
        Assert.Empty(diagnostics);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void HDR_код_звіряється_без_урахування_регістру()
    {
        // Той самий контракт, що й для решти кодів у цьому файлі
        // (`ReferenceResolver.FindTable`/`ResolveColumn` — StringComparison.OrdinalIgnoreCase).
        var snapshot = SnapshotWithHeaderField("Area");
        var extractor = new DependencyExtractor(new ReferenceResolver(snapshot), Expander);
        var root = Expr.Parse("HDR.area").Expression!.Root;
        var diagnostics = new List<ExpressionDiagnostic>();

        var dependencies = extractor.Extract(root, currentTableDefId: 1, currentRowKey: null, diagnostics: diagnostics);

        Assert.Single(dependencies, d => d.DependsOnKind == DependencyExtractor.KindHeader);
        Assert.Empty(diagnostics);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void HDR_з_невідомим_кодом_не_дає_залежності_і_дає_зауваження_Unresolved()
    {
        var snapshot = SnapshotWithHeaderField("Area");
        var extractor = new DependencyExtractor(new ReferenceResolver(snapshot), Expander);
        var root = Expr.Parse("1 + HDR.TYPO").Expression!.Root;
        var diagnostics = new List<ExpressionDiagnostic>();

        var dependencies = extractor.Extract(root, currentTableDefId: 1, currentRowKey: null, diagnostics: diagnostics);

        Assert.DoesNotContain(dependencies, d => d.DependsOnKind == DependencyExtractor.KindHeader);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(ExpressionErrors.Unresolved, diagnostic.Code);
        Assert.Contains("TYPO", diagnostic.Message, StringComparison.Ordinal);

        // ⚠ Позиція йде з САМОГО SymbolReferenceNode ("HDR.TYPO" усередині
        // "1 + HDR.TYPO"), а не з 0 — інакше редактор виразів (ФВ-9.15a)
        // підсвітив би початок формули замість реального місця помилки, так
        // само, як він уже робить для невідомої таблиці/колонки.
        Assert.Equal("1 + HDR.TYPO".IndexOf("HDR", StringComparison.Ordinal), diagnostic.Position);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Версія_без_визначеної_шапки_відхиляє_будь_який_HDR()
    {
        // Знімок без жодного поля шапки — той самий стан, що дає
        // `TemplateVersionSnapshot.HeaderFields` за замовчуванням, поки версія
        // шапки не має.
        var builder = new TemplateBuilder { TemplateVersionId = 1 };
        var sheet = builder.Sheet("Water");
        builder.Table(sheet, "Main");
        var snapshot = builder.Build();

        var extractor = new DependencyExtractor(new ReferenceResolver(snapshot), Expander);
        var root = Expr.Parse("HDR.Area").Expression!.Root;
        var diagnostics = new List<ExpressionDiagnostic>();

        var dependencies = extractor.Extract(root, currentTableDefId: 1, currentRowKey: null, diagnostics: diagnostics);

        Assert.Empty(dependencies);
        Assert.NotEmpty(diagnostics);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void ResolveHeader_сам_по_собі_не_фільтрує_IsDeleted_це_відповідальність_Snapshot()
    {
        // ⚠ Фільтр `!IsDeleted` лежить у `PublishChecks.Snapshot` (той самий,
        // яким уже живиться `MetadataCache.LoadAsync`), НЕ в резолвері: якби
        // тут стояла друга перевірка `IsDeleted`, вона дублювала б межу
        // відповідальності й розійшлася б із фільтром `Snapshot` на першій же
        // правці одного з двох місць. Цей тест фіксує межу явно — поле,
        // видалене чи ні, резолвиться за самим кодом, якщо воно є в
        // `HeaderFields` знімка, що сюди переданий.
        var builder = new TemplateBuilder { TemplateVersionId = 1 };
        var sheet = builder.Sheet("Water");
        builder.Table(sheet, "Main");

        var field = new HeaderFieldDef(
            builder.TemplateVersionId, EcrCode.Create("Area"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Area" }), ordinal: 1, CellDataType.String);
        field.SoftDelete(userId: 1, DateTime.UtcNow);

        // Знімок тут будується напряму (а не через PublishChecks.Snapshot,
        // якого в Ecr.Expressions.Tests немає) — з видаленим полем УСЕРЕДИНІ,
        // щоб перевірити саме резолвер, а не фільтр публікації.
        var snapshot = builder.Build() with { HeaderFields = [field] };

        var extractor = new DependencyExtractor(new ReferenceResolver(snapshot), Expander);
        var root = Expr.Parse("HDR.Area").Expression!.Root;
        var diagnostics = new List<ExpressionDiagnostic>();

        var dependencies = extractor.Extract(root, currentTableDefId: 1, currentRowKey: null, diagnostics: diagnostics);

        Assert.Single(dependencies, d => d.DependsOnKind == DependencyExtractor.KindHeader);
    }
}
