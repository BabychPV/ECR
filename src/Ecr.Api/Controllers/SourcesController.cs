using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecr.Api.Controllers;

/// <summary>Зовнішні джерела даних.</summary>
/// <remarks>
/// ⚠ Запису в зовнішнє джерело тут немає і не буде: PI AF — **виключно
/// джерело** (<c>D-44</c>, ФВ-11.5). Парного ендпоінта «опублікувати в AF» не
/// існує навіть як заглушки.
/// </remarks>
[ApiController]
[Route("api/v1/sources")]
[Authorize]
public sealed class SourcesController : ControllerBase
{
    /// <summary>
    /// Запускає збір для сутності джерела. Право <c>Integration.Manage</c>.
    /// </summary>
    /// <remarks>
    /// Збір ідемпотентний за природним ключем: повторний запуск того самого
    /// діапазону не дублює даних (ФВ-11.3). Відмова джерела — не збій операції:
    /// діапазон іде в catch-up, а прогін завершується успішно.
    /// </remarks>
    [HttpPost("{id:int}/collect")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    public Task<IActionResult> Collect(int id, [FromBody] CollectRequest request, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO: перевірити Integration.Manage; поставити CollectionJob через IBackgroundJobScheduler; " +
            "202 із jobId. Транспорт обирається за полем джерела, а не тут (ФВ-11.2).");
}

/// <summary>Запит на збір.</summary>
/// <param name="FromUtc">Початок діапазону.</param>
/// <param name="ToUtc">Кінець діапазону.</param>
public sealed record CollectRequest(DateTime FromUtc, DateTime ToUtc);
