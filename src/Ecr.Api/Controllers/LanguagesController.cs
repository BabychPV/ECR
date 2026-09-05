using Ecr.Application.Ports;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecr.Api.Controllers;

/// <summary>Мови інтерфейсу з реєстру.</summary>
/// <remarks>
/// ⛔ Окремий контролер, а не дія в <c>UiStringsController</c>: там маршрут
/// <c>{lang}</c> займає перший сегмент, і <c>/api/v1/ui-strings/languages</c>
/// читалося б як «каталог мовою languages». Літерал виграв би в маршрутизації,
/// але тільки доти, доки хтось не переставить атрибути.
///
/// ⚠ Ендпоінт з'явився через `ФВ-14.9`: «додавання мови — запис у реєстр, не
/// збірка клієнта». Обіцянку тримав сервер (переклад приймається будь-якою
/// мовою) і не тримав клієнт: перелік мов не віддавав ніхто, тому поле
/// локалізованої назви і редактор перекладів довелося б зашити константою
/// <c>['en','ru','kz']</c> — тобто зробити рівно те, що вимога забороняє.
/// </remarks>
[ApiController]
[Route("api/v1/languages")]
[Authorize]
public sealed class LanguagesController(IUiStringCatalog catalog) : ControllerBase
{
    /// <summary>Увімкнені мови в порядку показу.</summary>
    /// <remarks>
    /// Без права: перелік мов не є таємницею, а потрібен кожному екрану, що
    /// має локалізовану назву. Анонімно теж не віддається — сторінка входу
    /// обирає мову з браузера і збереженого вибору, реєстр їй не потрібен.
    /// </remarks>
    /// <param name="ct">Токен скасування.</param>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<LanguageDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken ct)
        => Ok(await catalog.ListLanguagesAsync(ct).ConfigureAwait(false));
}
