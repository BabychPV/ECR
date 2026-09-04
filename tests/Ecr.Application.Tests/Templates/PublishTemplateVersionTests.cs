using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Templates;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
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
    public void Синтаксична_помилка_у_виразі_відхиляє_публікацію()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Посилання_на_неіснуючу_колонку_відхиляє_публікацію()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Цикл_у_графі_відхиляє_публікацію_із_шляхом_циклу()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Несумісні_одиниці_без_CONVERT_відхиляють_публікацію()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Відповідь_містить_УСІ_проблеми_а_не_лише_першу()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Публікація_зберігає_розкриті_діапазони_і_порядок_обчислення()
        => Assert.Fail("not implemented");

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
}
