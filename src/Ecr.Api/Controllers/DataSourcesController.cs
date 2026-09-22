using Ecr.Application.Integration;
using Ecr.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace Ecr.Api.Controllers;

/// <summary>
/// Джерела даних: конфігурація підключення (<c>BE-21</c>, ФВ-14.3).
/// </summary>
/// <remarks>
/// ⚠ Не плутати з <c>/api/v1/sources</c>: там СУТНОСТІ збору всередині джерела
/// (елементи й атрибути AF, таблиці FLERT) і запуск збору. Тут — самі
/// підключення, з яких ті сутності беруться.
///
/// ⛔ Поля секрету немає в жодному тілі запиту й жодній відповіді. Пряме
/// рішення людини на <c>Q15-06</c>: джерела ходять під Windows-автентифікацією
/// службового облікового запису, сховища секретів у застосунку немає. У
/// відповіді лишається <c>hasSecret</c> — ознака того, що середовище все-таки
/// дає секрет під це джерело.
/// </remarks>
[ApiController]
[Route("api/v1/data-sources")]
[Authorize]
public sealed class DataSourcesController(
    ListDataSourcesHandler list,
    SaveDataSourceHandler save,
    DeleteDataSourceHandler delete,
    TestDataSourceConnectionHandler test,
    BrowseSourceCatalogHandler catalog,
    ProbeSourcePathHandler probe) : ControllerBase
{
    /// <summary>
    /// Каталог імен джерела для мапінгу (ФВ-13.13). Право <c>Integration.Manage</c>.
    /// </summary>
    /// <remarks>
    /// Без <c>path</c> — кореневі елементи; зі шляхом — дочірні елементи й
    /// атрибути елемента. Джерело не відповіло в межу
    /// <c>Integration:CatalogTimeoutSeconds</c> або лежить — <c>503 ECR-INT-0503</c>;
    /// відмовило в автентифікації — <c>502 ECR-INT-0502</c>.
    /// </remarks>
    [HttpGet("{id:int}/catalog")]
    [ProducesResponseType<SourceCatalogPage>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Catalog(
        int id,
        [FromQuery] string? path,
        [FromQuery] string? search,
        [FromQuery] string? cursor,
        [FromQuery] int? limit,
        CancellationToken ct)
        => Ok(await catalog.HandleAsync(id, path, search, cursor, limit, ct).ConfigureAwait(false));

    /// <summary>
    /// «Перевірити конфігурацію» до першого збору (ФВ-13.17). Право <c>Integration.Manage</c>.
    /// </summary>
    /// <remarks>
    /// ⚠ Читає ОДНЕ значення тим самим адаптером, яким потім збиратимуть, і
    /// нічого не зберігає — на відміну від <c>POST …/collect</c>. Шляху немає
    /// в каталозі джерела — <c>404 ECR-INT-0404</c> з переліком найближчих
    /// імен (<c>suggestions</c>, до 5) серед елементів каталогу того самого
    /// рівня; джерело не відповіло в межу <c>Integration:CatalogTimeoutSeconds</c>
    /// або лежить — <c>503 ECR-INT-0503</c>; відмова в автентифікації —
    /// <c>502 ECR-INT-0502</c>.
    /// </remarks>
    [HttpPost("{id:int}/probe")]
    [ProducesResponseType<SourcePathProbeResult>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Probe(int id, [FromBody] ProbeDataSourceRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Ok(await probe.HandleAsync(id, request.Path, ct).ConfigureAwait(false));
    }

    /// <summary>Перелік джерел. Право <c>Integration.View</c> або <c>Integration.Manage</c>.</summary>
    /// <remarks>
    /// ⚠ Віддаються і вимкнені джерела: екран, з якого джерело вмикають назад,
    /// без них показував би порожнє місце замість причини.
    /// </remarks>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<DataSourceView>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> List(CancellationToken ct)
        => Ok(await list.HandleAsync(ct).ConfigureAwait(false));

    /// <summary>Заводить джерело. Право <c>Integration.Manage</c>.</summary>
    /// <remarks>
    /// Код зайнятий, назви немає, адреса задовга або несе облікові дані —
    /// <c>422 ECR-REQ-0422</c> ДО запису.
    /// </remarks>
    [HttpPost]
    [ProducesResponseType<DataSourceView>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Create([FromBody] SaveDataSourceRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var created = await save
            .CreateAsync(
                request.Code ?? string.Empty, request.NameL10n, request.Transport, request.Endpoint,
                request.SecondaryEndpoint, request.Catalog, request.MaxParallel, ct)
            .ConfigureAwait(false);

        return Created(new Uri("/api/v1/data-sources", UriKind.Relative), created);
    }

    /// <summary>Змінює джерело. Право <c>Integration.Manage</c>.</summary>
    /// <remarks>
    /// ⚠ Код не змінюється: на нього спираються сутності збору, і
    /// перейменування ключа виглядало б як правка підпису, а було б переїздом
    /// усієї конфігурації збору.
    /// </remarks>
    [HttpPut("{id:int}")]
    [ProducesResponseType<DataSourceView>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Update(
        int id, [FromBody] SaveDataSourceRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        // ⚠ `If-Match` із `rowVersion` обов'язковий: немає — 422, чужа версія —
        // 409 ECR-JOB-0409. Читається з запиту, а не `[FromHeader]` — та сама
        // причина, що в `CollectionSchedulesController`.
        var ifMatch = Request.Headers[HeaderNames.IfMatch].ToString();

        return Ok(await save
            .UpdateAsync(
                id, request.NameL10n, request.Transport, request.Endpoint, request.SecondaryEndpoint,
                request.Catalog, request.MaxParallel, request.IsActive, ifMatch, ct)
            .ConfigureAwait(false));
    }

    /// <summary>
    /// Прибирає джерело, на яке ніщо не спирається. Право <c>Integration.Manage</c>.
    /// </summary>
    /// <remarks>
    /// ⛔ Каскаду немає: джерело із сутностями збору або розкладами — це
    /// <c>409 ECR-JOB-0409</c> із лічильниками в деталях. Видалення потягнуло б
    /// за собою зібрані точки й журнал покриття, за якими вже пораховані
    /// документи. Джерело, з якого більше не збирають, вимикається
    /// (<c>isActive = false</c>). Потребує <c>If-Match</c>, як і зміна.
    /// </remarks>
    [HttpDelete("{id:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var ifMatch = Request.Headers[HeaderNames.IfMatch].ToString();

        await delete.HandleAsync(id, ifMatch, ct).ConfigureAwait(false);

        return NoContent();
    }

    /// <summary>
    /// Перевіряє з'єднання з джерелом. Право <c>Integration.Manage</c>.
    /// </summary>
    /// <remarks>
    /// ⚠ <c>200</c>, а не <c>202</c>: проба читає ОДИН рівень каталогу джерела
    /// і вкладається у відповідь — на відміну від збору, який іде по діапазону
    /// і тому віддає <c>jobId</c>.
    ///
    /// ⚠ Причина обов'язкова і йде в журнал безпеки; відмова джерела — це
    /// <c>{ ok: false, error }</c>, а не помилка запиту. Проба цього ж джерела,
    /// яка вже виконується, — <c>409 ECR-JOB-0409</c>.
    /// </remarks>
    [HttpPost("{id:int}/test")]
    [ProducesResponseType<DataSourceTestResult>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Test(
        int id, [FromBody] TestDataSourceRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Ok(await test.HandleAsync(id, request.Reason, ct).ConfigureAwait(false));
    }
}

/// <summary>Тіло створення і зміни джерела.</summary>
/// <remarks>⛔ Поля секрету тут немає навмисно (<c>Q15-06</c>).</remarks>
/// <param name="Code">Код джерела; при зміні ігнорується.</param>
/// <param name="NameL10n">Назва мовами каталогу; хоча б одна мова.</param>
/// <param name="Transport">Транспорт: <c>PiWebApi</c>, <c>PiSqlClient</c>, <c>Sql</c>.</param>
/// <param name="Endpoint">Адреса або рядок з'єднання — без облікових даних.</param>
/// <param name="SecondaryEndpoint">Запасна адреса; <c>null</c> — немає.</param>
/// <param name="Catalog">Каталог або база джерела.</param>
/// <param name="MaxParallel">Стеля паралельних звернень; <c>null</c> — типова.</param>
/// <param name="IsActive">Чи збирати з джерела; при створенні ігнорується.</param>
public sealed record SaveDataSourceRequest(
    string? Code,
    IReadOnlyDictionary<string, string>? NameL10n,
    ExternalTransport Transport,
    string? Endpoint,
    string? SecondaryEndpoint = null,
    string? Catalog = null,
    int? MaxParallel = null,
    bool IsActive = true);

/// <summary>Тіло перевірки з'єднання.</summary>
/// <param name="Reason">Причина; обов'язкова, потрапляє в журнал безпеки.</param>
public sealed record TestDataSourceRequest(string Reason);

/// <summary>Тіло проби конфігурації мапінгу (ФВ-13.17).</summary>
/// <param name="Path">Шлях мапінгу в джерелі, від 1 до 500 символів.</param>
public sealed record ProbeDataSourceRequest(string? Path);
