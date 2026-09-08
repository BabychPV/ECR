using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Application.Templates;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Errors;
using Ecr.Domain.Services;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Templates;

/// <summary>
/// Авторство структури шаблону — перший вертикальний зріз: аркуш
/// (<c>ФВ-2.1</c>..<c>ФВ-2.5</c>).
/// </summary>
/// <remarks>
/// ⛔ До цього зрізу `SheetDef` створював лише `Ecr.DataGen`, і жоден тест
/// застосунку не перевіряв запис аркуша через обробник узагалі (аудит,
/// <c>S-03</c>/<c>S-04</c>). Тести тут — за зразком
/// <c>TableRelationTests</c>: та сама форма `PUT` за кодом, та сама заборона
/// на опублікованій версії (<c>ФВ-7.1</c>).
/// </remarks>
public sealed class SheetDefTests
{
    private static readonly DateTime Now = new(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc);

    private readonly ITemplateVersionStore _store = Substitute.For<ITemplateVersionStore>();
    private readonly IMetadataCache _metadataCache = Substitute.For<IMetadataCache>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();

    // ⚠ Не мок: обробник читає й пише через ЦЕЙ агрегат (`GetWithStructureAsync`
    // повертає його самого), і саме на ньому тримається перевірка «код уже
    // існує» та підйом Ordinal — так само, як `_draft` у `TableRelationTests`.
    private readonly TemplateVersion _draft =
        new(templateId: 1, version: "1.0.0.0", createdByUserId: 7, utcNow: Now);

    public SheetDefTests()
    {
        _clock.UtcNow.Returns(Now);
        _user.UserId.Returns(9);
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }.Permission("Template.Edit").Build());

        _store.GetWithStructureAsync(1, Arg.Any<CancellationToken>()).Returns(_draft);
        _store.HasDocumentsAsync(1, Arg.Any<CancellationToken>()).Returns(false);
    }

    private static Dictionary<string, string> Name(string en) => new(StringComparer.OrdinalIgnoreCase) { ["en"] = en };

    private static SaveSheetDefCommand Command(
        string en = "Water balance", int? ordinal = null, bool mandatory = false,
        bool visible = true, string? group = null)
        => new(Name(en), ordinal, group, mandatory, visible);

    private SaveSheetDefHandler Save()
        => new(_store, new ChangeClassifier(), _metadataCache, _audit, _uow, _clock, _access, _user);

    private DeleteSheetDefHandler Delete()
        => new(_store, new ChangeClassifier(), _metadataCache, _audit, _uow, _clock, _access, _user);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-2.1")]
    public async Task Новий_аркуш_зберігається_і_потрапляє_в_аудит_структурних_змін()
    {
        var saved = await Save().HandleAsync(1, "Water", Command(), CancellationToken.None);

        Assert.Equal("Water", saved.Code);
        Assert.Equal(0, saved.Ordinal);
        Assert.Single(_draft.Sheets);

        // ⚠ Аудит — не косметика: структурна зміна версії має слід, інакше
        // питання «хто додав цей аркуш» не має відповіді.
        await _audit.Received(1).WriteStructureChangeAsync(
            Arg.Is<StructureChangeRecord>(r => r.EntityType == "SheetDef" && r.Operation == "Create"),
            Arg.Any<CancellationToken>());

        // ⛔ Без цього виклику прогрітий кеш метаданих віддавав би структуру
        // без щойно доданого аркуша — див. коментар у `SaveSheetDefHandler`.
        await _metadataCache.Received(1).InvalidateAsync(1, Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-2.1")]
    public async Task Повторний_запис_тим_самим_кодом_оновлює_а_не_дублює()
    {
        await Save().HandleAsync(1, "Water", Command(en: "Water v1"), CancellationToken.None);
        var updated = await Save().HandleAsync(
            1, "Water", Command(en: "Water v2", mandatory: true), CancellationToken.None);

        // ⛔ Ідентичність — код: другий запис тим самим кодом ЗМІНЮЄ наявний
        // аркуш, а не заводить другий (`D2-147`).
        Assert.Single(_draft.Sheets);
        Assert.Equal("Water v2", updated.NameL10n.Get("en"));
        Assert.True(updated.IsMandatory);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-2.1")]
    public async Task Ordinal_не_переданий_явно_бере_наступний_за_наявними()
    {
        await Save().HandleAsync(1, "First", Command(), CancellationToken.None);
        var second = await Save().HandleAsync(1, "Second", Command(), CancellationToken.None);

        Assert.Equal(1, second.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-7.1")]
    public async Task Запис_аркуша_в_опублікованій_версії_відхиляється()
    {
        _draft.Publish(publishedByUserId: 8, utcNow: Now);

        // ⛔ Опублікована версія структурно незмінна: додавання чи зміна
        // аркуша тихо змінили б уже подані форми.
        var error = await Assert.ThrowsAsync<DomainException>(
            () => Save().HandleAsync(1, "Water", Command(), CancellationToken.None));

        Assert.Equal("ECR-TMPL-0409", error.ErrorCode);
        Assert.Empty(_draft.Sheets);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-7.6")]
    public async Task Видалення_прибирає_аркуш_м_яко_а_не_фізично()
    {
        await Save().HandleAsync(1, "Water", Command(), CancellationToken.None);
        _metadataCache.ClearReceivedCalls();

        await Delete().HandleAsync(1, "Water", CancellationToken.None);

        // ⛔ Запис лишається: на нього можуть посилатися формули й правила
        // інших частин структури (ФВ-7.6).
        var sheet = Assert.Single(_draft.Sheets);
        Assert.True(sheet.IsDeleted);

        await _metadataCache.Received(1).InvalidateAsync(1, Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Видалення_неіснуючого_аркуша_відхиляється_404()
    {
        var error = await Assert.ThrowsAsync<NotFoundException>(
            () => Delete().HandleAsync(1, "Missing", CancellationToken.None));

        Assert.Equal("ECR-TMPL-0404", error.ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "ФВ-7.1")]
    public async Task Видалення_аркуша_в_опублікованій_версії_відхиляється()
    {
        await Save().HandleAsync(1, "Water", Command(), CancellationToken.None);
        _draft.Publish(publishedByUserId: 8, utcNow: Now);

        var error = await Assert.ThrowsAsync<DomainException>(
            () => Delete().HandleAsync(1, "Water", CancellationToken.None));

        Assert.Equal("ECR-TMPL-0409", error.ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public async Task Без_права_Template_Edit_аркуш_не_записується()
    {
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }.Permission("Template.View").Build());

        await Assert.ThrowsAsync<AccessDeniedException>(
            () => Save().HandleAsync(1, "Water", Command(), CancellationToken.None));

        Assert.Empty(_draft.Sheets);
    }
}
