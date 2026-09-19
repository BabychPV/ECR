// tests/Ecr.Application.Tests/Localization/UiStringCoverageTests.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Localization;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Localization;

/// <summary>Покриття перекладу, перелік без fallback і плейсхолдери (<c>BE-13</c>).</summary>
public sealed class UiStringCoverageTests
{
    private const int Editor = 5;

    // ⚠ `ru` навмисно неповна трьома РІЗНИМИ способами: ключа немає зовсім
    // (`common.cancel`), значення порожнє (`common.close`) і є зайвий ключ,
    // якого немає в еталоні (`legacy.only`).
    private readonly FakeUiStringCatalog _catalog = new FakeUiStringCatalog()
        .Add("en", "common.save", "Save")
        .Add("en", "common.cancel", "Cancel")
        .Add("en", "common.close", "Close")
        .Add("en", "audit.tooWide", "Wider than {maxDays} days, from {from}")
        .Add("ru", "common.save", "Сохранить")
        .Add("ru", "common.close", string.Empty)
        .Add("ru", "legacy.only", "Осталось")
        .Add("ru", "audit.tooWide", "С {from}: шире {maxDays} дней");

    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();

    public UiStringCoverageTests()
    {
        _user.UserId.Returns(Editor);
        Grant(SetUiStringHandler.Permission);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task Перелік_віддає_відсутній_переклад_порожнім_а_не_підміненим()
    {
        var response = await new ListUiStringsHandler(_catalog, _access, _user)
            .HandleAsync("ru", missingOnly: false, CancellationToken.None);

        var rows = response.Items.ToDictionary(r => r.Key, StringComparer.Ordinal);

        // ⛔ Саме це відрізняє перелік від каталогу: каталог тут дав би «Cancel».
        Assert.Null(rows["common.cancel"].Value);
        Assert.Equal("Cancel", rows["common.cancel"].Reference);

        // Порожній переклад — теж відсутній: екран однаково покаже англійський.
        Assert.Null(rows["common.close"].Value);
        Assert.Equal("Сохранить", rows["common.save"].Value);

        // Контроль: каталог для екрана ту саму прогалину ховає.
        var screen = await _catalog.GetAsync("ru", CancellationToken.None);
        Assert.Equal("Cancel", screen.Strings["common.cancel"]);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task Фільтр_лише_відсутні_лишає_рівно_неперекладене()
    {
        var response = await new ListUiStringsHandler(_catalog, _access, _user)
            .HandleAsync("ru", missingOnly: true, CancellationToken.None);

        Assert.Equal(["common.cancel", "common.close"], response.Items.Select(r => r.Key));
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task Покриття_рахується_від_ключів_мови_за_замовчуванням()
    {
        var response = await new GetUiStringCoverageHandler(_catalog, _access, _user)
            .HandleAsync(CancellationToken.None);

        var ru = Assert.Single(response.Languages, l => l.LanguageCode == "ru");

        // `legacy.only` у Total не входить: інакше «перекладено» перевищило б «усього».
        Assert.Equal((4, 2, 2), (ru.Total, ru.Translated, ru.Missing));

        var en = Assert.Single(response.Languages, l => l.LanguageCode == "en");
        Assert.Equal((4, 4, 0), (en.Total, en.Translated, en.Missing));
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task Без_права_локалізації_ані_покриття_ані_переліку()
    {
        Grant("Template.Edit");

        var coverage = await Assert.ThrowsAsync<AccessDeniedException>(
            () => new GetUiStringCoverageHandler(_catalog, _access, _user).HandleAsync(CancellationToken.None));
        var list = await Assert.ThrowsAsync<AccessDeniedException>(
            () => new ListUiStringsHandler(_catalog, _access, _user)
                .HandleAsync("ru", missingOnly: true, CancellationToken.None));

        Assert.Equal("ECR-AUTH-0403", coverage.ErrorCode);
        Assert.Equal("ECR-AUTH-0403", list.ErrorCode);
    }

    [Theory] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [InlineData("Шире {maxDays} дней")] // загублено {from}
    [InlineData("С {from}: шире {maxdays} дней")] // регістр: підстановка його розрізняє
    [InlineData("С {from}: шире {maxDays} дней, до {to}")] // зайвий
    public async Task Переклад_з_іншим_набором_плейсхолдерів_відхиляється(string translation)
    {
        var before = _catalog.Revision;

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Set("audit.tooWide", "ru", translation));

        Assert.Equal("ECR-REQ-0422", error.ErrorCode);
        Assert.Equal("err.ECR-REQ-0422.placeholderMismatch", error.Details!["messageKey"]);
        Assert.Equal("from, maxDays", error.Details["expected"]);

        // Відмова — ДО запису: версія каталогу не рухається.
        Assert.Equal(before, _catalog.Revision);
    }

    [Theory] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [InlineData("audit.tooWide", "ru", "{maxDays} дней, с {from} — да, с {from}")] // порядок і повтор вільні
    [InlineData("audit.tooWide", "ru", "")] // зняти переклад
    [InlineData("audit.tooWide", "en", "Too wide")] // еталон звіряти нема з чим
    [InlineData("brand.new", "ru", "Новый {x}")] // ключа в еталоні ще немає
    public async Task Перевірка_плейсхолдерів_не_заважає_законному_запису(string key, string lang, string value)
    {
        var before = _catalog.Revision;

        var after = await Set(key, lang, value);

        Assert.True(after > before);
    }

    private Task<int> Set(string key, string lang, string value)
    {
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(new DateTime(2026, 3, 1, 8, 0, 0, DateTimeKind.Utc));

        return new SetUiStringHandler(
                _catalog, _access, Substitute.For<IUnitOfWork>(), Substitute.For<IAuditWriter>(), _user, clock)
            .HandleAsync(key, lang, value, (byte)UiStringScope.Private, CancellationToken.None);
    }

    private void Grant(string permission)
        => _access.BuildProfileAsync(Editor, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = Editor }.Permission(permission).Build());
}
