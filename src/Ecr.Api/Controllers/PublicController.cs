using System.Reflection;
using Ecr.Application.PublicApi;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecr.Api.Controllers;

/// <summary>Дані, які віддаються БЕЗ автентифікації (<c>BE-07</c>).</summary>
/// <remarks>
/// ⛔ Окремий контролер із власним префіксом <c>/api/v1/public</c>, а не дія в
/// <see cref="AuthController"/>. Причина не стильова: анонімна поверхня має
/// бути ПЕРЕЛІЧУВАНОЮ. Доки анонімні дії розкидані по контролерах поруч із
/// закритими, єдиний спосіб дізнатися повний список — перечитати всі
/// контролери й не помилитися; з префіксом це один пошук. Третій анонімний
/// маршрут (після входу й каталогу рядків) — саме той момент, коли ціна
/// розкиданості стає видимою.
///
/// ⚠ Що саме сюди можна класти — у
/// <see cref="GetPublicBootstrapHandler"/>. Коротко: поле дозволене, лише якщо
/// воно НЕ дозволяє відрізнити наявний логін від неіснуючого і НЕ називає
/// внутрішніх деталей розгортання.
/// </remarks>
[ApiController]
[Route("api/v1/public")]
public sealed class PublicController(
    GetPublicBootstrapHandler bootstrap,
    IAuthenticationSchemeProvider schemes) : ControllerBase
{
    /// <summary>Версія продукту, мови і доступні способи входу.</summary>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// ⚠ Дія не приймає ЖОДНОГО параметра — ні шляху, ні рядка запиту, ні тіла.
    /// Це і є доказ того, що відповідь не може залежати від імені користувача:
    /// їй нізвідки його взяти.
    /// </remarks>
    [HttpGet("bootstrap")]
    [AllowAnonymous]
    [ProducesResponseType<PublicBootstrapResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Bootstrap(CancellationToken ct)
    {
        // ⛔ Стан схем береться з КОНВЕЄРА, а не перечитуванням
        // `Auth:EnableNegotiate` із конфігурації. Ключ і фактична реєстрація —
        // дві різні речі: `AddEcrAuthentication` читає ключ один раз на старті,
        // і будь-яка майбутня умова навколо `builder.AddNegotiate()` (відсутній
        // пакет, платформа, збій) розвела б їх мовчки. Тоді екран входу
        // показував би кнопку доменного входу, яка гарантовано дає 401.
        var registered = await schemes.GetAllSchemesAsync().ConfigureAwait(false);
        var names = registered.Select(scheme => scheme.Name).ToHashSet(StringComparer.Ordinal);

        var response = await bootstrap
            .HandleAsync(
                new PublicBootstrapRequest(
                    typeof(PublicController).Assembly
                        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                        ?.InformationalVersion,
                    names.Contains(NegotiateDefaults.AuthenticationScheme),
                    names.Contains(CookieAuthenticationDefaults.AuthenticationScheme)),
                ct)
            .ConfigureAwait(false);

        return Ok(response);
    }
}
