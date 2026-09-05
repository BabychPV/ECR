using Ecr.Application.Integration;
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
public sealed class SourcesController(
    ListSourceEntitiesHandler list, CollectFromSourceHandler collect) : ControllerBase
{
    /// <summary>
    /// Перелік сутностей збору. Право <c>Integration.Manage</c>.
    /// </summary>
    /// <remarks>
    /// ⚠ Разом із кожною сутністю віддається найстаріша непокрита прогалина.
    /// Ознака здоров'я інтеграції — журнал покриття, а не тиша (ІНТ-3.3):
    /// джерело, яке щоночі успішно віддає нуль точок, і джерело, яке віддає
    /// дані, у переліку прогонів виглядають однаково.
    /// </remarks>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<Ecr.Application.Ports.SourceEntityStatus>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken ct)
        => Ok(await list.HandleAsync(ct).ConfigureAwait(false));

    /// <summary>
    /// Запускає збір для сутності джерела. Право <c>Integration.Manage</c>.
    /// </summary>
    /// <remarks>
    /// Збір ідемпотентний за природним ключем: повторний запуск того самого
    /// діапазону не дублює даних (ФВ-11.3). Відмова джерела — не збій операції:
    /// діапазон іде в catch-up, а прогін завершується успішно.
    /// </remarks>
    [HttpPost("{id:int}/collect")]
    [ProducesResponseType<Contracts.JobAcceptedResponse>(StatusCodes.Status202Accepted)]
    public async Task<IActionResult> Collect(int id, [FromBody] CollectRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        // ⚠ 202 з jobId, а не 200 з даними: збір ходить по мережі до чужої
        // системи, і його тривалість визначає не наш код. Синхронна відповідь
        // тут — це таймаут проксі рівно тоді, коли джерело повільне.
        var jobId = await collect
            .HandleAsync(id, request.FromUtc, request.ToUtc, ct)
            .ConfigureAwait(false);

        return Accepted(new Contracts.JobAcceptedResponse(jobId));
    }
}

/// <summary>Запит на збір.</summary>
/// <param name="FromUtc">Початок діапазону.</param>
/// <param name="ToUtc">Кінець діапазону.</param>
public sealed record CollectRequest(DateTime FromUtc, DateTime ToUtc);
