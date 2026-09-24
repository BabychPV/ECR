using Ecr.Application.Common;
using Ecr.Application.Documents;
using Ecr.Application.Documents.Dto;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Expressions.Evaluation;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Documents;

/// <summary>
/// <c>GET</c>/<c>PATCH …/documents/{id}/header</c> — читання й запис значень
/// шапки документа. Право на <c>PATCH</c> — грант <c>Write</c> на проєкт
/// (той самий рівень грануляції, що <see cref="ChangeDocumentKeyHandler"/>),
/// БЕЗ окремого функціонального права — той самий підхід, що
/// <c>PatchCellsHandler</c>.
/// </summary>
public sealed class DocumentHeaderHandlersTests
{
    private const long DocumentId = 501;
    private const int ProjectId = 10;
    private const int TemplateVersionId = 1;

    private readonly IDocumentStore _documents = Substitute.For<IDocumentStore>();
    private readonly IMetadataCache _metadataCache = Substitute.For<IMetadataCache>();
    private readonly IDocumentHeaderStore _headers = Substitute.For<IDocumentHeaderStore>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();

    private readonly HeaderFieldDef _area = new(
        TemplateVersionId, EcrCode.Create("Area"),
        new LocalizedText(new Dictionary<string, string> { ["en"] = "Area" }), 0, CellDataType.String);

    private readonly HeaderFieldDef _count = new(
        TemplateVersionId, EcrCode.Create("Count"),
        new LocalizedText(new Dictionary<string, string> { ["en"] = "Count" }), 1, CellDataType.Int);

    private const int PermitRegistryDefId = 7;

    private readonly HeaderFieldDef _permit = new(
        TemplateVersionId, EcrCode.Create("Permit"),
        new LocalizedText(new Dictionary<string, string> { ["en"] = "Permit" }), 2, CellDataType.Lookup);

    public DocumentHeaderHandlersTests()
    {
        _permit.SetLookup(PermitRegistryDefId);

        _user.UserId.Returns(9);

        _documents.FindAsync(DocumentId, Arg.Any<PeriodKeyFilter>(), Arg.Any<CancellationToken>())
            .Returns(new DocumentSummary(
                DocumentId, ProjectId, "FILE-1", DateTime.UtcNow, SheetCount: 1,
                new Dictionary<string, string>(StringComparer.Ordinal)));
        _documents.GetTemplateVersionIdAsync(DocumentId, Arg.Any<CancellationToken>()).Returns(TemplateVersionId);

        _metadataCache.GetAsync(TemplateVersionId, Arg.Any<CancellationToken>()).Returns(new TemplateVersionSnapshot(
            TemplateVersionId, 0, [], new Dictionary<int, ColumnDef>(), new Dictionary<(int, string), RowDef>())
        {
            HeaderFields = [_area, _count, _permit],
        });

        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }
                .Permission("Document.View")
                .Grant(ResourceKind.Project, ProjectId, GrantLevel.Write)
                .Build());
        _access.CanReadDocumentAsync(Arg.Any<AccessProfile>(), DocumentId, Arg.Any<CancellationToken>())
            .Returns(EditDecision.Allow());

        _headers.GetValuesAsync(DocumentId, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, DocumentHeaderValueData>());
    }

    private GetDocumentHeaderHandler Get() => new(_documents, _metadataCache, _headers, _access, _user);
    private PatchDocumentHeaderHandler Patch() => new(_documents, _metadataCache, _headers, _access, _user);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public async Task GET_повертає_усі_поля_версії_навіть_без_значення()
    {
        var result = await Get().HandleAsync(DocumentId, CancellationToken.None);

        Assert.Equal(3, result.Fields.Count);
        var field = Assert.Single(result.Fields, f => f.Code == "Area");
        Assert.Null(field.Value);
        Assert.False(field.IsRequired);
        Assert.Null(field.LookupRegistryDefId);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public async Task GET_Lookup_поле_несе_LookupRegistryDefId()
    {
        var result = await Get().HandleAsync(DocumentId, CancellationToken.None);

        var permit = Assert.Single(result.Fields, f => f.Code == "Permit");
        Assert.Equal(PermitRegistryDefId, permit.LookupRegistryDefId);

        var nonLookup = Assert.Single(result.Fields, f => f.Code == "Area");
        Assert.Null(nonLookup.LookupRegistryDefId);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public async Task GET_повертає_записане_значення()
    {
        _headers.GetValuesAsync(DocumentId, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, DocumentHeaderValueData>
            {
                [_area.Id] = new() { ValueString = "Kashagan" },
            });

        var result = await Get().HandleAsync(DocumentId, CancellationToken.None);

        Assert.Equal("Kashagan", Assert.Single(result.Fields, f => f.Code == "Area").Value);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public async Task GET_без_доступу_до_документа_відхиляється()
    {
        _access.CanReadDocumentAsync(Arg.Any<AccessProfile>(), DocumentId, Arg.Any<CancellationToken>())
            .Returns(EditDecision.Deny(EditDenyReason.NoGrant));

        // ⛔ B-08: невидимий документ — 404, як і відсутній, а не 403.
        var notFound = await Assert.ThrowsAsync<NotFoundException>(() => Get().HandleAsync(DocumentId, CancellationToken.None));
        Assert.Equal("ECR-DOC-0404", notFound.ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public async Task PATCH_записує_значення_і_повертає_оновлений_стан()
    {
        // ⚠ Store — не мок значень: SaveValuesAsync лише перевіряється на
        // виклик з очікуваним словником, а GetValuesAsync (друге читання
        // всередині обробника) підмінюється настроєним результатом — так
        // само, як PatchCellsHandler-тести не тримають справжнє сховище.
        _headers.GetValuesAsync(DocumentId, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, DocumentHeaderValueData> { [_area.Id] = new() { ValueString = "Kashagan" } });

        var result = await Patch().HandleAsync(
            DocumentId,
            new PatchDocumentHeaderRequest([new PatchHeaderField("Area", "Kashagan", false)]),
            CancellationToken.None);

        await _headers.Received(1).SaveValuesAsync(
            DocumentId,
            Arg.Is<IReadOnlyDictionary<int, DocumentHeaderValueData>>(
                d => d.Count == 1 && d[_area.Id].ValueString == "Kashagan"),
            Arg.Any<CancellationToken>());

        Assert.Equal("Kashagan", Assert.Single(result.Fields, f => f.Code == "Area").Value);
        Assert.Equal(PermitRegistryDefId, Assert.Single(result.Fields, f => f.Code == "Permit").LookupRegistryDefId);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public async Task PATCH_невідомий_код_поля_дає_ECR_HDR_0404()
    {
        var error = await Assert.ThrowsAsync<NotFoundException>(() => Patch().HandleAsync(
            DocumentId,
            new PatchDocumentHeaderRequest([new PatchHeaderField("NoSuchField", "x", false)]),
            CancellationToken.None));

        Assert.Equal("ECR-HDR-0404", error.ErrorCode);
        Assert.Equal("err.ECR-HDR-0404.headerField", error.Details!["messageKey"]);

        await _headers.DidNotReceive().SaveValuesAsync(
            Arg.Any<long>(), Arg.Any<IReadOnlyDictionary<int, DocumentHeaderValueData>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public async Task PATCH_типова_невідповідність_дає_ECR_HDR_0422()
    {
        // Count — Int; нечисловий рядок не парситься як число взагалі
        // (на відміну від String-поля, яке через HeaderValueReader.Text
        // приймає будь-яке значення через ToString — тому мішень тут саме
        // числове поле, а не Area).
        var error = await Assert.ThrowsAsync<BusinessRuleException>(() => Patch().HandleAsync(
            DocumentId,
            new PatchDocumentHeaderRequest([new PatchHeaderField("Count", "not-a-number", false)]),
            CancellationToken.None));

        Assert.Equal("ECR-HDR-0422", error.ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public async Task PATCH_явна_порожнеча_стирає_значення()
    {
        _headers.GetValuesAsync(DocumentId, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, DocumentHeaderValueData> { [_area.Id] = DocumentHeaderValueData.Empty });

        await Patch().HandleAsync(
            DocumentId,
            new PatchDocumentHeaderRequest([new PatchHeaderField("Area", null, true)]),
            CancellationToken.None);

        await _headers.Received(1).SaveValuesAsync(
            DocumentId,
            Arg.Is<IReadOnlyDictionary<int, DocumentHeaderValueData>>(d => d[_area.Id].IsEmpty),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public async Task PATCH_без_гранта_Write_на_проєкт_відхиляється()
    {
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }.Grant(ResourceKind.Project, ProjectId, GrantLevel.Read).Build());

        await Assert.ThrowsAsync<AccessDeniedException>(() => Patch().HandleAsync(
            DocumentId,
            new PatchDocumentHeaderRequest([new PatchHeaderField("Area", "x", false)]),
            CancellationToken.None));

        await _headers.DidNotReceive().SaveValuesAsync(
            Arg.Any<long>(), Arg.Any<IReadOnlyDictionary<int, DocumentHeaderValueData>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public async Task PATCH_чужого_документа_дає_404_а_не_403()
    {
        _documents.FindAsync(DocumentId, Arg.Any<PeriodKeyFilter>(), Arg.Any<CancellationToken>())
            .Returns((DocumentSummary?)null);

        var error = await Assert.ThrowsAsync<NotFoundException>(() => Patch().HandleAsync(
            DocumentId,
            new PatchDocumentHeaderRequest([new PatchHeaderField("Area", "x", false)]),
            CancellationToken.None));

        Assert.Equal("ECR-DOC-0404", error.ErrorCode);
    }
}
