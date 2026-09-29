using Ecr.Application.Registries.Dto;
using Ecr.Application.Registries.Keys;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecr.Api.Controllers;

/// <summary>
/// Інструменти конструктора довідника, що нічого не зберігають: перевірки опису на наявних даних
/// до збереження (FEATURE-REGISTRY-TABLES §4.5, §7.1).
/// </summary>
/// <remarks>
/// ⚠ Окремий контролер, а не ще дії в <see cref="RegistriesController"/>: той уже несе сімнадцять
/// обробників, а тут — читання «що буде, якщо», яке росте власними кроками (ключі — RT-11,
/// правила — RT-17b).
/// </remarks>
[ApiController]
[Route("api/v1/registries")]
[Authorize]
public sealed class RegistryDefinitionToolsController(CheckRegistryKeyHandler checkKey) : ControllerBase
{
    /// <summary>
    /// Жива перевірка дублікатів майбутнього ключа на наявних записах. Право
    /// <c>Registry.EditDefinition</c>.
    /// </summary>
    /// <param name="code">Код довідника.</param>
    /// <param name="request">Поля ключа в порядку частин і порівняння тексту.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// ⛔ Той самий алгоритм, що й публікація ключа: «дублікатів немає» тут означає, що публікація
    /// не відмовить <c>409 existingDuplicates</c> на тих самих даних.
    /// </remarks>
    [HttpPost("{code}/keys/check")]
    [ProducesResponseType<RegistryKeyCheckResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<RegistryKeyCheckResponse>> CheckKey(
        string code, [FromBody] RegistryKeyCheckRequest request, CancellationToken ct)
        => Ok(await checkKey.HandleAsync(code, request, ct).ConfigureAwait(false));
}
