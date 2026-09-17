using Ecr.Api.Auth;
using Ecr.TestKit;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// <c>Accept-Language</c> приходить ТЕГОМ BCP-47, а реєстр
/// <c>sys_ecr.Language</c> зберігає внутрішні КОДИ — це різні алфавіти, і
/// <see cref="CurrentUser.Language"/> має переводити один в інший.
/// </summary>
/// <remarks>
/// ⛔ Дефект, який це закриває: браузер із казахською оголошує
/// <c>Accept-Language: kk-KZ</c>, а в реєстрі мова історично записана кодом
/// <c>kz</c> (це код КРАЇНИ, використаний як код мови). Зріз до двох літер
/// давав <c>kk</c> — коду, якого в реєстрі немає, тож для НЕавтентифікованого
/// користувача каталог <c>IUiStringCatalog</c> підмінював усе мовою за
/// замовчуванням: тексти помилок, <c>/health</c>, сторінка входу. Тобто
/// «мова за перевагами браузера» мовчки не працювала саме для тієї мови,
/// заради якої розбіжність кодів і виникла.
///
/// ⚠ Серверне дзеркало клієнтської правки в <c>shared/i18n/index.ts</c>
/// (<c>preferredLanguage()</c>): там той самий зріз до двох літер давав ту
/// саму <c>kk</c>.
/// </remarks>
public sealed class AcceptLanguageTagTests
{
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    // Тег із регіоном — саме те, що надсилає браузер із казахською локаллю.
    [InlineData("kk-KZ", "kz")]
    // Той самий тег без регіону: перевід не має залежати від наявності регіону.
    [InlineData("kk", "kz")]
    // Повний заголовок із вагами: перший тег виграє й ПІСЛЯ переведення.
    [InlineData("kk-KZ,kk;q=0.9,ru;q=0.8,en;q=0.7", "kz")]
    // Регістр заголовка не має значення.
    [InlineData("KK-kz", "kz")]
    public void Тег_BCP47_казахської_стає_кодом_реєстру(string header, string expected)
    {
        var user = UserWith(header);

        Assert.Equal(expected, user.Language);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    // ⚠ Мови, у яких тег і код збігаються, мають проходити НЕЗМІННО: мапа —
    // це перелік винятків, а не перелік дозволених мов. Інакше четверта мова
    // в реєстрі (ФВ-2.2, D-95: «додати мову = запис у реєстр, не збірка»)
    // вимагала б правки коду.
    [InlineData("ru-RU,ru;q=0.9,en;q=0.8", "ru")]
    [InlineData("en-US", "en")]
    [InlineData("uk-UA", "uk")]
    public void Мови_без_розбіжності_проходять_як_є(string header, string expected)
    {
        var user = UserWith(header);

        Assert.Equal(expected, user.Language);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(";q=0.9")]
    public void Порожній_чи_безглуздий_заголовок_дає_мову_за_замовчуванням(string header)
    {
        var user = UserWith(header);

        Assert.Equal("en", user.Language);
    }

    private static CurrentUser UserWith(string acceptLanguage)
    {
        var context = new DefaultHttpContext();
        if (acceptLanguage.Length > 0)
        {
            context.Request.Headers.AcceptLanguage = acceptLanguage;
        }

        var accessor = new HttpContextAccessor { HttpContext = context };
        return new CurrentUser(accessor);
    }
}
