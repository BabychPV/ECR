// tests/Ecr.Application.Tests/Localization/UiStringCatalogTests.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Localization;
using Ecr.Application.Ports;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Localization;

/// <summary>
/// Каталог рядків інтерфейсу (ФВ-14.9). **Порожнеча не повертається ніколи**:
/// одна забута локалізація не має ламати екран.
/// </summary>
public sealed class UiStringCatalogTests
{
    private readonly FakeUiStringCatalog _catalog = new FakeUiStringCatalog()
        .Add("en", "common.save", "Save", UiStringScope.Public)
        .Add("en", "common.cancel", "Cancel", UiStringScope.Public)
        .Add("ru", "common.cancel", "Отмена", UiStringScope.Public)
        .Add("en", "err.ECR-PWD-0428", "Password change is required.", UiStringScope.Public)
        .Add("en", "nav.admin.permissions", "Permissions", UiStringScope.Private);

    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-0.4")]
    public async Task Відсутній_переклад_підмінюється_мовою_за_замовчуванням()
    {
        var catalog = await Handler(userId: 5).HandleAsync("ru", publicOnly: true, CancellationToken.None);

        // ⚠ Ключі беруться з ОБОХ мов. Якби бралися лише з запитаної, нова мова
        // з двома перекладами дала б екран із двома підписами — гірше, ніж
        // англійські вкраплення.
        Assert.Equal("Отмена", catalog.Strings["common.cancel"]);
        Assert.Equal("Save", catalog.Strings["common.save"]);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task Ключ_якого_немає_ніде_повертається_як_сам_ключ()
    {
        var catalog = await Handler(userId: 5).HandleAsync("ru", publicOnly: true, CancellationToken.None);

        // ⛔ Не порожній рядок: кнопка без назви не каже користувачеві ні що
        // вона робить, ні кому про це повідомити. Ключ принаймні називає місце.
        Assert.Equal("grid.paste.confirm", UiStringResolver.Resolve(catalog, "grid.paste.confirm"));
        Assert.Equal("Save", UiStringResolver.Resolve(catalog, "common.save"));
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-14.9a")]
    public async Task Тексти_помилок_резолвляться_з_того_самого_каталогу()
    {
        var catalog = await Handler(userId: 5).HandleAsync("ru", publicOnly: true, CancellationToken.None);

        // Механізм локалізації ОДИН: тексти помилок живуть під ключами
        // err.<код> поруч із підписами кнопок (ФВ-14.9a, D-111). Друге сховище
        // означало б другий fallback, другу версію і другу забуту мову.
        Assert.Equal("Password change is required.", UiStringResolver.ResolveError(catalog, "ECR-PWD-0428"));

        // Код без тексту віддається як ключ — клієнт покаже «err.ECR-SYS-0500»,
        // і це все одно діагностика, а не порожній діалог.
        Assert.Equal("err.ECR-SYS-0500", UiStringResolver.ResolveError(catalog, "ECR-SYS-0500"));
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task Публічна_область_не_містить_адміністративних_підписів()
    {
        var catalog = await Handler(userId: null).HandleAsync("en", publicOnly: true, CancellationToken.None);

        // ⚠ Анонімний каталог із назвами адміністративних областей і прав
        // розкрив би поверхню функціоналу тому, хто ще не увійшов (D-114,
        // ФВ-14.2) — приблизно як увімкнений перелік каталогів на вебсервері.
        Assert.True(catalog.Strings.ContainsKey("common.save"));
        Assert.False(catalog.Strings.ContainsKey("nav.admin.permissions"));
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task Анонімний_запит_приватної_області_відхиляється()
    {
        var handler = Handler(userId: null);

        var error = await Assert.ThrowsAsync<AccessDeniedException>(
            () => handler.HandleAsync("en", publicOnly: false, CancellationToken.None));

        Assert.Equal("ECR-AUTH-0401", error.ErrorCode);

        // Публічна область тому самому анонімові доступна: сторінка входу
        // потребує підписів кнопок раніше, ніж хтось автентифікований.
        var publicCatalog = await handler.HandleAsync("en", publicOnly: true, CancellationToken.None);
        Assert.NotEmpty(publicCatalog.Strings);
    }

    private GetUiStringsHandler Handler(int? userId)
    {
        _user.UserId.Returns(userId);
        return new GetUiStringsHandler(_catalog, _user);
    }
}
