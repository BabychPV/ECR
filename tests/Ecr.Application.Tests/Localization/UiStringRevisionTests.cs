// tests/Ecr.Application.Tests/Localization/UiStringRevisionTests.cs
using Ecr.Application.Common;
using Ecr.Application.Localization;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Localization;

/// <summary>Версія каталогу як `ETag` (ФВ-14.9c).</summary>
public sealed class UiStringRevisionTests
{
    private const int Editor = 5;

    private readonly FakeUiStringCatalog _catalog = new FakeUiStringCatalog()
        .Add("en", "common.save", "Save", UiStringScope.Public);

    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly IClock _clock = Substitute.For<IClock>();

    public UiStringRevisionTests()
    {
        _user.UserId.Returns(Editor);
        _user.CorrelationId.Returns("c1");
        _clock.UtcNow.Returns(new DateTime(2026, 3, 1, 8, 0, 0, DateTimeKind.Utc));

        _access.BuildProfileAsync(Editor, Arg.Any<CancellationToken>())
               .Returns(new AccessBuilder { UserId = Editor }
                   .Permission(SetUiStringHandler.Permission)
                   .Build());
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task Будь_який_запис_інкрементує_Revision()
    {
        var before = _catalog.Revision;

        var after = await Handler().HandleAsync(
            "common.save", "en", "Зберегти", (byte)UiStringScope.Public, CancellationToken.None);

        // Без інкременту клієнт із чинним ETag ніколи не побачить правку —
        // переклад «застосується», але на екрані лишиться старий текст.
        Assert.True(after > before, $"Версія не змінилася: було {before}, стало {after}.");

        var catalog = await _catalog.GetScopedAsync("en", UiStringScope.Public, CancellationToken.None);
        Assert.Equal("Зберегти", catalog.Strings["common.save"]);
        Assert.Equal(after, catalog.Revision);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Збіг_If_None_Match_дає_304()
    {
        var etag = UiStringResolver.ETag(UiStringScope.Public, "en", revision: 7);

        Assert.True(UiStringResolver.IsNotModified(etag, etag));

        // Слабкий валідатор — те саме: байтова тотожність каталогу нікого не
        // цікавить, збіг версії цілком достатній.
        Assert.True(UiStringResolver.IsNotModified("W/" + etag, etag));
        Assert.True(UiStringResolver.IsNotModified($"\"stale\", {etag}", etag));

        // Інша версія — повна відповідь; інакше клієнт назавжди лишиться зі
        // старими підписами.
        Assert.False(UiStringResolver.IsNotModified(
            UiStringResolver.ETag(UiStringScope.Public, "en", revision: 8), etag));
        Assert.False(UiStringResolver.IsNotModified(ifNoneMatch: null, etag));
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task Два_одночасні_записи_дають_різні_версії()
    {
        var handler = Handler();

        var revisions = await Task.WhenAll(
            handler.HandleAsync("a.one", "en", "One", (byte)UiStringScope.Public, CancellationToken.None),
            handler.HandleAsync("a.two", "en", "Two", (byte)UiStringScope.Public, CancellationToken.None));

        // ⚠ Інкремент і читання нової версії — один statement із OUTPUT (R-B7).
        // Розділені, вони дали б двом адміністраторам однакову версію: другий
        // ETag збігся б із першим при різному вмісті, і клієнт застряг би на
        // проміжному стані каталогу.
        Assert.Equal(2, revisions.Distinct().Count());
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Різні_області_мають_незалежні_ETag()
    {
        const int revision = 7;

        var publicTag = UiStringResolver.ETag(UiStringScope.Public, "en", revision);
        var privateTag = UiStringResolver.ETag(UiStringScope.Private, "en", revision);

        // ⚠ Версія в базі ОДНА (sys_ecr.UiStringRevision має CHECK (Id = 1)),
        // тому область мусить входити в сам ETag. Інакше клієнт, що закешував
        // публічний зріз, дістав би 304 на запит приватного — і показав би
        // сторінку входу замість застосунку.
        Assert.NotEqual(publicTag, privateTag);
        Assert.False(UiStringResolver.IsNotModified(publicTag, privateTag));

        // Мова — з тієї самої причини: зріз іншою мовою має інший вміст.
        Assert.NotEqual(publicTag, UiStringResolver.ETag(UiStringScope.Public, "ru", revision));
    }

    private SetUiStringHandler Handler() => new(_catalog, _access, _uow, _audit, _user, _clock);
}
