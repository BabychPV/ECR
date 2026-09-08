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
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Templates;

/// <summary>
/// Авторство структури шаблону — третій вертикальний зріз: рядок фіксованої
/// таблиці (<c>ФВ-2.1</c>..<c>ФВ-2.5</c>, <c>W5.2</c>), за зразком
/// <see cref="SheetDefTests"/> (<c>W5.0</c>) і <see cref="ColumnDefTests"/>.
/// </summary>
public sealed class RowDefTests
{
    private static readonly DateTime Now = new(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc);

    private readonly ITemplateVersionStore _store = Substitute.For<ITemplateVersionStore>();
    private readonly IMetadataCache _metadataCache = Substitute.For<IMetadataCache>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();

    private readonly TemplateBuilder _builder = new() { TemplateVersionId = 1 };

    private readonly TemplateVersion _draft =
        new(templateId: 1, version: "1.0.0.0", createdByUserId: 7, utcNow: Now);

    private readonly TableDef _table;

    public RowDefTests()
    {
        var sheet = _builder.Sheet("Water");
        _table = _builder.Table(sheet, "Main", rowMode: TableRowMode.Fixed);
        _draft.AddSheet(sheet);

        _clock.UtcNow.Returns(Now);
        _user.UserId.Returns(9);
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }.Permission("Template.Edit").Build());

        _store.GetWithStructureAsync(1, Arg.Any<CancellationToken>()).Returns(_draft);
        _store.HasDocumentsAsync(1, Arg.Any<CancellationToken>()).Returns(false);
    }

    private static Dictionary<string, string> Label(string en) => new(StringComparer.OrdinalIgnoreCase) { ["en"] = en };

    private static SaveRowDefCommand Command(
        string en = "Intake", int? ordinal = null, RowKind rowKind = RowKind.Item,
        string? parentRowKey = null, bool readOnly = false)
        => new(Label(en), ordinal, rowKind, parentRowKey, readOnly);

    private SaveRowDefHandler Save()
        => new(_store, new ChangeClassifier(), _metadataCache, _audit, _uow, _clock, _access, _user);

    private DeleteRowDefHandler Delete()
        => new(_store, new ChangeClassifier(), _metadataCache, _audit, _uow, _clock, _access, _user);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-2.1")]
    public async Task Новий_рядок_зберігається_і_потрапляє_в_аудит_структурних_змін()
    {
        var saved = await Save().HandleAsync(1, _table.Id, "7001001", Command(), CancellationToken.None);

        Assert.Equal("7001001", saved.RowKey);
        Assert.Equal(0, saved.Ordinal);
        Assert.Single(_table.Rows);

        await _audit.Received(1).WriteStructureChangeAsync(
            Arg.Is<StructureChangeRecord>(r => r.EntityType == "RowDef" && r.Operation == "Create"),
            Arg.Any<CancellationToken>());

        await _metadataCache.Received(1).InvalidateAsync(1, Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-2.1")]
    public async Task Повторний_запис_тим_самим_ключем_оновлює_а_не_дублює()
    {
        await Save().HandleAsync(1, _table.Id, "7001001", Command(en: "Intake v1"), CancellationToken.None);
        var updated = await Save().HandleAsync(
            1, _table.Id, "7001001", Command(en: "Intake v2", readOnly: true), CancellationToken.None);

        Assert.Single(_table.Rows);
        Assert.Equal("Intake v2", updated.LabelL10n.Get("en"));
        Assert.True(updated.IsReadOnly);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-2.1")]
    public async Task Ordinal_не_переданий_явно_бере_наступний_за_наявними()
    {
        await Save().HandleAsync(1, _table.Id, "First", Command(), CancellationToken.None);
        var second = await Save().HandleAsync(1, _table.Id, "Second", Command(), CancellationToken.None);

        Assert.Equal(1, second.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Батьківський_рядок_розв_язується_за_ключем_у_тій_самій_таблиці()
    {
        await Save().HandleAsync(1, _table.Id, "7001", Command(rowKind: RowKind.Group), CancellationToken.None);
        var child = await Save().HandleAsync(
            1, _table.Id, "7001001", Command(parentRowKey: "7001"), CancellationToken.None);

        Assert.Equal("7001", child.ParentRowKey);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Невідомий_батьківський_ключ_відхиляється()
    {
        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Save().HandleAsync(
                1, _table.Id, "7001001", Command(parentRowKey: "Missing"), CancellationToken.None));

        Assert.Equal("ECR-TMPL-0422", error.ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Рядок_не_може_бути_батьком_самому_собі()
    {
        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Save().HandleAsync(
                1, _table.Id, "7001", Command(parentRowKey: "7001"), CancellationToken.None));

        Assert.Equal("ECR-TMPL-0422", error.ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Вид_рядка_незмінний_повторним_записом()
    {
        await Save().HandleAsync(1, _table.Id, "7001001", Command(rowKind: RowKind.Item), CancellationToken.None);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Save().HandleAsync(1, _table.Id, "7001001", Command(rowKind: RowKind.Balance), CancellationToken.None));

        Assert.Equal("ECR-TMPL-0422", error.ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Динамічна_таблиця_відхиляє_рядок_шаблону()
    {
        var dynamicSheet = _builder.Sheet("Extra");
        var dynamicTable = _builder.Table(dynamicSheet, "Dyn", rowMode: TableRowMode.Dynamic);
        _draft.AddSheet(dynamicSheet);

        var error = await Assert.ThrowsAsync<DomainException>(
            () => Save().HandleAsync(1, dynamicTable.Id, "7001001", Command(), CancellationToken.None));

        Assert.Equal("ECR-TMPL-0422", error.ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Таблиці_з_таким_Id_у_версії_немає()
    {
        var error = await Assert.ThrowsAsync<NotFoundException>(
            () => Save().HandleAsync(1, tableDefId: 999, "7001001", Command(), CancellationToken.None));

        Assert.Equal("ECR-TMPL-0404", error.ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-7.1")]
    public async Task Запис_рядка_в_опублікованій_версії_відхиляється()
    {
        _draft.Publish(publishedByUserId: 8, utcNow: Now);

        var error = await Assert.ThrowsAsync<DomainException>(
            () => Save().HandleAsync(1, _table.Id, "7001001", Command(), CancellationToken.None));

        Assert.Equal("ECR-TMPL-0409", error.ErrorCode);
        Assert.Empty(_table.Rows);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-7.6")]
    public async Task Видалення_прибирає_рядок_м_яко_а_не_фізично()
    {
        await Save().HandleAsync(1, _table.Id, "7001001", Command(), CancellationToken.None);
        _metadataCache.ClearReceivedCalls();

        await Delete().HandleAsync(1, _table.Id, "7001001", CancellationToken.None);

        var row = Assert.Single(_table.Rows);
        Assert.True(row.IsDeleted);

        await _metadataCache.Received(1).InvalidateAsync(1, Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Видалення_неіснуючого_рядка_відхиляється_404()
    {
        var error = await Assert.ThrowsAsync<NotFoundException>(
            () => Delete().HandleAsync(1, _table.Id, "Missing", CancellationToken.None));

        Assert.Equal("ECR-TMPL-0404", error.ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-7.1")]
    public async Task Видалення_рядка_в_опублікованій_версії_відхиляється()
    {
        await Save().HandleAsync(1, _table.Id, "7001001", Command(), CancellationToken.None);
        _draft.Publish(publishedByUserId: 8, utcNow: Now);

        var error = await Assert.ThrowsAsync<DomainException>(
            () => Delete().HandleAsync(1, _table.Id, "7001001", CancellationToken.None));

        Assert.Equal("ECR-TMPL-0409", error.ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Без_права_Template_Edit_рядок_не_записується()
    {
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }.Permission("Template.View").Build());

        await Assert.ThrowsAsync<AccessDeniedException>(
            () => Save().HandleAsync(1, _table.Id, "7001001", Command(), CancellationToken.None));

        Assert.Empty(_table.Rows);
    }
}
