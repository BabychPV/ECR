// src/Ecr.Application/PublicApi/GetPublicBootstrapHandler.cs

using System.Text.RegularExpressions;
using Ecr.Application.Ports;

namespace Ecr.Application.PublicApi;

/// <summary>
/// Дані, потрібні екрану входу ДО автентифікації (<c>BE-07</c>).
/// </summary>
/// <remarks>
/// ⛔ Це найнебезпечніша відповідь у системі за одним критерієм: її отримує
/// будь-хто, хто дістався порту. Тому склад полів — не «що зручно показати», а
/// «що вже й так видно ззовні»:
/// <list type="bullet">
/// <item><b>версія продукту</b> — рядок вигляду <c>1.0.0</c> і НІЧОГО більше
/// (див. <see cref="MarketingVersion"/>): метадані збірки містять хеш коміту й
/// ім'я гілки, тобто називають внутрішні деталі розгортання;</item>
/// <item><b>мови реєстру</b> — той самий перелік, який анонімний
/// <c>GET /api/v1/ui-strings/{lang}?scope=public</c> і так дозволяє перебрати
/// кодом мови;</item>
/// <item><b>два прапорці входу</b> — наявність самих маршрутів
/// <c>/api/v1/login/windows</c> і <c>/api/v1/login/local</c> перевіряється
/// одним запитом без жодної відповіді сервера про облікові записи.</item>
/// </list>
///
/// ⛔ Чого тут НЕМАЄ і не буде (рішення <c>D15-14</c> директиви №15): лічильника
/// «лишилось N спроб», часу блокування, політики паролів, будь-якого поля, що
/// приймає ім'я користувача. «Лишилось 2 спроби» для НЕІСНУЮЧОГО логіна проти
/// того самого речення для існуючого — це оракул перебору логінів, тобто
/// подарунок нападникові за рахунок зручності, якої ніхто не просив. Блокування
/// повідомляється лише у відповіді на САМ вхід і однаково для обох випадків.
///
/// ⚠ Обробник не приймає ЖОДНОГО параметра запиту — і це властивість, а не
/// недогляд. Відповідь, яка ні від чого не залежить, не може розрізнити двох
/// анонімів; сторож <c>Відповідь_однакова_для_наявного_і_неіснуючого_логіна</c>
/// тримає цю властивість, а не мою обіцянку її дотримати.
/// </remarks>
public sealed partial class GetPublicBootstrapHandler(IUiStringCatalog catalog)
{
    /// <summary>Версія, яку віддаємо, коли збірка не назвала жодної.</summary>
    /// <remarks>
    /// ⚠ Не порожній рядок: клієнт показує значення як є, і порожнє місце в
    /// підвалі форми входу читається як дефект верстки, а не як «версії немає».
    /// </remarks>
    public const string UnknownVersion = "0.0.0";

    /// <summary>Складає відповідь екрана входу.</summary>
    /// <param name="request">Те, що знає про себе ХОСТ: версія і стан схем входу.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task<PublicBootstrapResponse> HandleAsync(
        PublicBootstrapRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        // ⚠ Той самий порт, що й в авторизованому `GET /api/v1/languages`, а не
        // другий запит до `sys_ecr.Language`: два читачі одного реєстру рано чи
        // пізно розійдуться порядком або фільтром «увімкнена», і перемикач мови
        // до входу показував би не те, що після.
        var languages = await catalog.ListLanguagesAsync(ct).ConfigureAwait(false);

        return new PublicBootstrapResponse(
            MarketingVersion(request.InformationalVersion),
            languages,
            request.WindowsSignInEnabled,
            request.LocalSignInEnabled);
    }

    /// <summary>
    /// Лишає від версії збірки тільки <c>Major.Minor[.Patch[.Revision]]</c>.
    /// </summary>
    /// <remarks>
    /// ⛔ <c>AssemblyInformationalVersion</c> у типовій збірці має вигляд
    /// <c>1.0.0+9f3c1ab…</c>, а з SourceLink чи CI-скриптом туди дописують і
    /// гілку. Для анонімного споживача це не «версія», а інвентаризація
    /// майданчика. Тому вирізається ЧИСЛОВИЙ ПРЕФІКС, а не «все після плюса»:
    /// білий список доводить, ЩО саме поїхало назовні, а чорний доводив би лише,
    /// що одну відому форму метаданих прибрано.
    ///
    /// ⚠ Рядок, який числовим префіксом не починається взагалі
    /// (<c>"dev"</c>, <c>"main-42"</c>), не «чиститься», а замінюється на
    /// <see cref="UnknownVersion"/>: віддати половину невідомого рядка гірше,
    /// ніж чесно сказати «невідомо».
    /// </remarks>
    /// <param name="informational">Сирий рядок версії збірки; може бути <c>null</c>.</param>
    public static string MarketingVersion(string? informational)
    {
        if (string.IsNullOrWhiteSpace(informational))
        {
            return UnknownVersion;
        }

        var match = NumericPrefix.Match(informational);

        return match.Success ? match.Groups[1].Value : UnknownVersion;
    }

    /// <summary>Числовий префікс версії: <c>1</c>, <c>1.0</c>, <c>1.0.0</c>, <c>1.0.0.3</c>.</summary>
    [GeneratedRegex(@"^(\d+(?:\.\d+){0,3})")]
    private static partial Regex NumericPrefix { get; }
}

/// <summary>
/// Те, що про себе знає ХОСТ, а не застосунок.
/// </summary>
/// <remarks>
/// ⚠ Версія збірки і набір зареєстрованих схем автентифікації — факти рівня
/// ASP.NET і того складального артефакту, який підняли. <c>Ecr.Application</c>
/// не бачить ні <c>IAuthenticationSchemeProvider</c>, ні того, яка саме збірка
/// є «продуктом» (під <c>TestServer</c> точкою входу є тестовий хост), тому їх
/// приносить контролер — а не «здогадується» шар застосунку.
/// </remarks>
/// <param name="InformationalVersion">Сирий <c>AssemblyInformationalVersion</c> збірки API.</param>
/// <param name="WindowsSignInEnabled">Чи зареєстрована схема Negotiate.</param>
/// <param name="LocalSignInEnabled">Чи зареєстрована схема cookie.</param>
public sealed record PublicBootstrapRequest(
    string? InformationalVersion,
    bool WindowsSignInEnabled,
    bool LocalSignInEnabled);

/// <summary>
/// Публічні дані екрана входу.
/// </summary>
/// <remarks>
/// ⚠ Іменований запис, а не анонімний об'єкт (<c>A7-16</c>): без імені типу в
/// OpenAPI немає що генерувати, і клієнт описує відповідь рукописним
/// інтерфейсом — рівно так з'являлися розбіжності в назвах полів.
///
/// ⚠ Мови — наявний <see cref="LanguageDto"/>, а не власний <c>PublicLanguageDto</c>
/// з тими самими трьома полями, як пропонує текст директиви. Два імені для
/// одного поняття дали б клієнтові два типи однієї мови і питання «який із них
/// правильний» на кожному екрані; поля збігаються повністю
/// (<c>code</c>, <c>nameNative</c>, <c>isDefault</c>).
/// </remarks>
/// <param name="ProductVersion">Версія продукту без метаданих збірки.</param>
/// <param name="Languages">Увімкнені мови реєстру в порядку показу.</param>
/// <param name="WindowsSignInEnabled">Чи показувати кнопку доменного входу.</param>
/// <param name="LocalSignInEnabled">Чи показувати форму локального входу.</param>
public sealed record PublicBootstrapResponse(
    string ProductVersion,
    IReadOnlyList<LanguageDto> Languages,
    bool WindowsSignInEnabled,
    bool LocalSignInEnabled);
