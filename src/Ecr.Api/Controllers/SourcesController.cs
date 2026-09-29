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
    ListSourceEntitiesHandler list,
    CollectFromSourceHandler collect,
    Ecr.Application.Sources.PreviewMappingHandler preview,
    Ecr.Application.Sources.CreateSourceEntityHandler create,
    Ecr.Application.Sources.BindSourceEntityRegistryHandler bindRegistry) : ControllerBase
{
    /// <summary>
    /// Заводить сутність збору з позиції каталогу джерела (<c>ФВ-13.11</c>).
    /// Право <c>Integration.Manage</c>.
    /// </summary>
    /// <param name="request">З'єднання й позиція каталогу.</param>
    /// <param name="ct">Скасування.</param>
    /// <remarks>
    /// ⚠ Код уже зайнятий у цьому з'єднанні — <c>409 ECR-INT-0409</c>
    /// (<c>err.ECR-INT-0409.sourceEntityDuplicate</c>), а не другий рядок.
    /// </remarks>
    [HttpPost]
    [ProducesResponseType<Ecr.Application.Sources.SourceEntityDto>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Create([FromBody] CreateSourceEntityRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var created = await create
            .HandleAsync(
                new Ecr.Application.Sources.CreateSourceEntityCommand(
                    request.DataSourceId, request.Code, request.DisplayName, request.EntityPath, request.SourceKind),
                ct)
            .ConfigureAwait(false);

        return Created(new Uri("/api/v1/sources", UriKind.Relative), created);
    }

    /// <summary>
    /// Прив'язує сутність збору до довідника або відв'язує її (<c>ФВ-8.11</c>).
    /// Право <c>Integration.Manage</c> і, крім того, право редагувати дані
    /// довідника — <c>Registry.EditData</c> або грант <c>Write</c> на цей довідник.
    /// </summary>
    /// <param name="id">Сутність збору.</param>
    /// <param name="request">Довідник; <c>null</c> — відв'язати.</param>
    /// <param name="ct">Скасування.</param>
    /// <remarks>
    /// ⚠ Право на дані перевіряється для цільового довідника, а при відв'язці чи
    /// переприв'язці — і для поточного: після прив'язки синк пише в довідник від
    /// <c>svc-integration</c>, тож прив'язка — делегування права на його дані.
    /// Без права — <c>403 ECR-AUTH-0403</c>. <c>D-202</c>, доповнення 2026-09-29
    /// (<c>docs/tz/10-decisions.md</c> §1.19) — судження розробки, на підтвердження.
    /// </remarks>
    [HttpPut("{id:int}/registry")]
    [ProducesResponseType<Ecr.Application.Sources.SourceEntityDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> BindRegistry(
        int id, [FromBody] BindSourceEntityRegistryRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Ok(await bindRegistry.HandleAsync(id, request.RegistryDefId, ct).ConfigureAwait(false));
    }

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
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Collect(int id, [FromBody] CollectRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        // ⛔ Аудит 2026-09-28, B7. Час без зони (`Unspecified`) і час зі
        // зміщенням (`Local`, у поясі СЕРВЕРА) ішли в задачу як є: Web API
        // перераховував їх у UTC за поясом сервера (`ToUniversalTime`), а
        // покриття писалося за сирими значеннями як UTC — «покрито» те, що не
        // прочитано, і наздоганяння цієї дірки вже не бачить. PiSqlClient
        // узагалі передавав без конверсії, тобто транспорти розходились.
        // Нормалізація — тут, на вході, так само, як у `CollectionRunsController`.
        var from = ToUtc(request.FromUtc);
        var to = ToUtc(request.ToUtc);

        // Перевернутий або порожній проміжок не «виправляється» обміном меж: той,
        // хто його надіслав, помилився в одному з полів.
        if (from >= to)
        {
            throw new Ecr.Application.Errors.BusinessRuleException(
                Ecr.Domain.Errors.ErrorCodes.RequestInvalid,
                $"Проміжок збору порожній: початок {from:O} не раніший за кінець {to:O}.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-REQ-0422.collectionRunRange",
                    ["from"] = from.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                    ["to"] = to.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                });
        }

        // ⚠ 202 з jobId, а не 200 з даними: збір ходить по мережі до чужої
        // системи, і його тривалість визначає не наш код. Синхронна відповідь
        // тут — це таймаут проксі рівно тоді, коли джерело повільне.
        var jobId = await collect
            .HandleAsync(id, from, to, ct)
            .ConfigureAwait(false);

        return Accepted(new Contracts.JobAcceptedResponse(jobId));
    }

    /// <summary>Час без зони читається як UTC; зі зміщенням — переводиться в UTC.</summary>
    private static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        _ => value.ToUniversalTime(),
    };

    /// <summary>
    /// Перегляд мапінгу на реальних рядках джерела (<c>ФВ-13.14</c>).
    /// Право <c>Integration.Manage</c>.
    /// </summary>
    /// <param name="id">Сутність джерела.</param>
    /// <param name="fromUtc">Початок вікна; <c>null</c> — тиждень назад.</param>
    /// <param name="toUtc">Кінець вікна; <c>null</c> — «зараз».</param>
    /// <param name="ct">Скасування.</param>
    /// <remarks>
    /// ⛔ Відповідь несе не лише зв'язки, що зійшлися, а й **розриви**: поле
    /// джерела, яке не лягає нікуди; мапінг, під який у джерелі немає жодного
    /// рядка; колонку документа, за якою не стоїть нічого. Перегляд самих лише
    /// успішних зв'язків відповідав би на питання, якого ніхто не ставить.
    ///
    /// ⚠ Реальні рядки — це вже зібране (<c>ext.RawDataPoint</c>), а не
    /// читання з джерела наживо: перегляд не має падати разом із мережею до
    /// чужої системи.
    /// </remarks>
    [HttpGet("{id:int}/mapping/preview")]
    [ProducesResponseType<Ecr.Application.Sources.MappingPreview>(StatusCodes.Status200OK)]
    public async Task<IActionResult> MappingPreview(
        int id,
        [FromQuery] DateTime? fromUtc,
        [FromQuery] DateTime? toUtc,
        CancellationToken ct)
        => Ok(await preview.HandleAsync(id, fromUtc, toUtc, ct).ConfigureAwait(false));
}

/// <summary>Запит на збір.</summary>
/// <param name="FromUtc">Початок діапазону.</param>
/// <param name="ToUtc">Кінець діапазону.</param>
public sealed record CollectRequest(DateTime FromUtc, DateTime ToUtc);

/// <summary>Нова сутність збору — позиція каталогу джерела.</summary>
/// <param name="DataSourceId">З'єднання.</param>
/// <param name="Code">Код у джерелі.</param>
/// <param name="DisplayName">Підпис із каталогу.</param>
/// <param name="EntityPath">Шлях в ієрархії джерела.</param>
/// <param name="SourceKind">Хто master (ФВ-8.9); <c>null</c> — <c>External</c>.</param>
public sealed record CreateSourceEntityRequest(
    int DataSourceId,
    string? Code,
    string? DisplayName,
    string? EntityPath,
    Ecr.Domain.Enums.RegistrySourceKind? SourceKind);

/// <summary>Прив'язка сутності збору до довідника.</summary>
/// <param name="RegistryDefId">Довідник; <c>null</c> — відв'язати.</param>
public sealed record BindSourceEntityRegistryRequest(int? RegistryDefId);
