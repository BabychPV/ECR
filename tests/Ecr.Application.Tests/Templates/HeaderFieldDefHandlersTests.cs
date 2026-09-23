using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Application.Templates;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;
using Ecr.Domain.Services;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Templates;

/// <summary>
/// Поля шапки документа версії шаблону — той самий draft→publish зріз, що
/// <see cref="ColumnDefTests"/>, але на рівні версії, не таблиці.
/// </summary>
public sealed class HeaderFieldDefHandlersTests
{
    private static readonly DateTime Now = new(2026, 9, 20, 9, 0, 0, DateTimeKind.Utc);

    private readonly ITemplateVersionStore _store = Substitute.For<ITemplateVersionStore>();
    private readonly IMetadataCache _metadataCache = Substitute.For<IMetadataCache>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();

    // ⚠ Не мок: обробник читає й пише через ЦЕЙ агрегат, так само, як
    // ColumnDefTests._draft.
    private readonly TemplateVersion _draft =
        new(templateId: 1, version: "1.0.0.0", createdByUserId: 7, utcNow: Now);

    public HeaderFieldDefHandlersTests()
    {
        _uow.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Func<CancellationToken, Task>>(0)(call.ArgAt<CancellationToken>(1)));

        _clock.UtcNow.Returns(Now);
        _user.UserId.Returns(9);
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }.Permission("Template.Edit").Permission("Template.View").Build());

        _store.GetWithStructureAsync(1, Arg.Any<CancellationToken>()).Returns(_draft);
        _store.HasDocumentsAsync(1, Arg.Any<CancellationToken>()).Returns(false);
    }

    private static Dictionary<string, string> Label(string en) => new(StringComparer.OrdinalIgnoreCase) { ["en"] = en };

    private static SaveHeaderFieldDefCommand Command(
        string en = "Area", int? ordinal = null, CellDataType dataType = CellDataType.String,
        bool required = false, int? lookupRegistryDefId = null)
        => new(Label(en), ordinal, dataType, required, lookupRegistryDefId);

    private SaveHeaderFieldDefHandler Save()
        => new(_store, new ChangeClassifier(), _metadataCache, _audit, _uow, _clock, _access, _user);

    private GetHeaderFieldDefsHandler Get()
        => new(_metadataCache, _access, _user);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public async Task Нове_поле_зберігається_і_потрапляє_в_аудит_структурних_змін()
    {
        var saved = await Save().HandleAsync(1, "Area", Command(), CancellationToken.None);

        Assert.Equal("Area", saved.Code);
        Assert.Equal(0, saved.Ordinal);
        Assert.Equal(CellDataType.String, saved.DataType);
        Assert.Single(_draft.HeaderFields);

        await _audit.Received(1).WriteStructureChangeAsync(
            Arg.Is<StructureChangeRecord>(r => r.EntityType == "HeaderFieldDef" && r.Operation == "Create"),
            Arg.Any<CancellationToken>());

        await _metadataCache.Received(1).InvalidateAsync(1, Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public async Task Повторний_запис_тим_самим_кодом_оновлює_а_не_дублює()
    {
        await Save().HandleAsync(1, "Area", Command(en: "Area v1"), CancellationToken.None);
        var updated = await Save().HandleAsync(1, "Area", Command(en: "Area v2", required: true), CancellationToken.None);

        Assert.Single(_draft.HeaderFields);
        Assert.Equal("Area v2", updated.LabelL10n.Get("en"));
        Assert.True(updated.IsRequired);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public async Task Ordinal_не_переданий_явно_бере_наступний_за_наявними()
    {
        await Save().HandleAsync(1, "First", Command(), CancellationToken.None);
        var second = await Save().HandleAsync(1, "Second", Command(), CancellationToken.None);

        Assert.Equal(1, second.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public async Task Тип_даних_незмінний_повторним_записом()
    {
        await Save().HandleAsync(1, "Area", Command(dataType: CellDataType.String), CancellationToken.None);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Save().HandleAsync(1, "Area", Command(dataType: CellDataType.Decimal), CancellationToken.None));

        Assert.Equal("ECR-TMPL-0422", error.ErrorCode);
        Assert.Equal("err.ECR-TMPL-0422.headerFieldDataTypeImmutable", error.Details!["messageKey"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public async Task Довідник_на_полі_не_типу_Lookup_відхиляється_доменом()
    {
        var error = await Assert.ThrowsAsync<DomainException>(
            () => Save().HandleAsync(
                1, "Facility", Command(dataType: CellDataType.String, lookupRegistryDefId: 5), CancellationToken.None));

        Assert.Equal("ECR-TMPL-0422", error.ErrorCode);
        Assert.Equal("err.ECR-TMPL-0422.headerFieldLookupRequiresLookupType", error.Details!["messageKey"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Requirement", "ФВ-7.1")]
    public async Task Запис_поля_в_опублікованій_версії_відхиляється()
    {
        _draft.Publish(publishedByUserId: 8, utcNow: Now);

        var error = await Assert.ThrowsAsync<DomainException>(
            () => Save().HandleAsync(1, "Area", Command(), CancellationToken.None));

        Assert.Equal("ECR-TMPL-0409", error.ErrorCode);
        Assert.Empty(_draft.HeaderFields);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public async Task Без_права_Template_Edit_поле_не_записується()
    {
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }.Permission("Template.View").Build());

        await Assert.ThrowsAsync<AccessDeniedException>(
            () => Save().HandleAsync(1, "Area", Command(), CancellationToken.None));

        Assert.Empty(_draft.HeaderFields);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public async Task GetHeaderFieldDefsHandler_читає_поля_з_кешу_структури_у_порядку_Ordinal()
    {
        var field1 = new HeaderFieldDef(
            1, EcrCode.Create("Second"), new Domain.ValueObjects.LocalizedText(Label("Second")), 1, CellDataType.String);
        var field0 = new HeaderFieldDef(
            1, EcrCode.Create("First"), new Domain.ValueObjects.LocalizedText(Label("First")), 0, CellDataType.String);

        _metadataCache.GetAsync(1, Arg.Any<CancellationToken>()).Returns(new TemplateVersionSnapshot(
            1, 0, [], new Dictionary<int, ColumnDef>(),
            new Dictionary<(int, string), RowDef>())
        {
            HeaderFields = [field1, field0],
        });

        var result = await Get().HandleAsync(1, CancellationToken.None);

        Assert.Equal(["First", "Second"], result.Select(f => f.Code));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public async Task GetHeaderFieldDefsHandler_без_права_Template_View_відхиляється()
    {
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }.Build());

        _metadataCache.GetAsync(1, Arg.Any<CancellationToken>()).Returns(new TemplateVersionSnapshot(
            1, 0, [], new Dictionary<int, ColumnDef>(), new Dictionary<(int, string), RowDef>()));

        await Assert.ThrowsAsync<AccessDeniedException>(() => Get().HandleAsync(1, CancellationToken.None));
    }
}
