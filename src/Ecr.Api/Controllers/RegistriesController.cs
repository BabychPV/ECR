using Ecr.Application.Common;
using Ecr.Application.Registries;
using Ecr.Application.Registries.Dto;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecr.Api.Controllers;

/// <summary>Довідники: перелік, записи, редагування, вікно дії.</summary>
[ApiController]
[Route("api/v1/registries")]
[Authorize]
public sealed class RegistriesController(
    ListRegistriesHandler listRegistries,
    CreateRegistryHandler createRegistry,
    GetRegistryEntriesHandler getEntries,
    UpsertRegistryEntryHandler upsert,
    SetEntryValidityHandler setValidity,
    SwitchRegistrySourceHandler switchSource,
    GetRegistryDefinitionHandler getDefinition,
    SaveRegistryDefinitionHandler saveDefinition,
    DeleteRegistryEntryHandler deleteEntry,
    GetRegistryHistoryHandler getHistory,
    GetRegistryUsageHandler getUsage,
    GetRegistryDefinitionDraftHandler getDraft,
    SaveRegistryDefinitionDraftHandler saveDraft,
    PublishRegistryDefinitionHandler publish,
    DiscardRegistryDefinitionDraftHandler discardDraft,
    ImportRegistryEntriesHandler importEntries,
    IConfiguration configuration) : ControllerBase
{
    /// <summary>Перелік довідників. Право <c>Registry.View</c>.</summary>
    /// <param name="ct">Токен скасування.</param>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<RegistryDefDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<RegistryDefDto>>> List(CancellationToken ct)
        => Ok(await listRegistries.HandleAsync(ct).ConfigureAwait(false));

    /// <summary>
    /// Заводить довідник-контейнер, без жодного поля. Право
    /// <c>Registry.EditDefinition</c>.
    /// </summary>
    /// <param name="dto">Код, назва мовами каталогу і темпоральність.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// ⛔ Поля заводяться ОКРЕМОЮ дією — тим самим <c>PUT …/{code}/definition</c>,
    /// що вже редагує наявний довідник (`ФВ-8.12`). Довідник без жодного поля
    /// нічого не порушує: обов'язковість ключового поля перевіряється лише при
    /// збереженні опису, коли поля вже є що перевіряти.
    /// </remarks>
    [HttpPost]
    [ProducesResponseType<RegistryDefDto>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create([FromBody] CreateRegistryDto dto, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(dto);

        var created = await createRegistry
            .HandleAsync(dto.Code, dto.NameL10n, dto.IsTemporal, ct)
            .ConfigureAwait(false);

        return CreatedAtAction(nameof(Definition), new { code = created.Code }, created);
    }

    /// <summary>
    /// Записи довідника на дату. Право <c>Registry.View</c>.
    /// </summary>
    /// <param name="code">Код довідника.</param>
    /// <param name="asOf">Дата періоду.</param>
    /// <param name="parentEntryId">Обраний батьківський запис для каскаду.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// <paramref name="asOf"/> обов'язковий за змістом: довідники темпоральні,
    /// і «поточний» набір записів залежить від дати періоду, а не від «сьогодні».
    /// Мовчазна підстановка сьогоднішньої дати давала б інші числа при
    /// перерахунку старого періоду.
    /// </remarks>
    [HttpGet("{code}/entries")]
    [ProducesResponseType<IReadOnlyList<RegistryEntryDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<RegistryEntryDto>>> Entries(
        string code, [FromQuery] DateOnly asOf, [FromQuery] long? parentEntryId, CancellationToken ct)
        => Ok(await getEntries.HandleAsync(code, asOf, parentEntryId, ct).ConfigureAwait(false));

    /// <summary>
    /// Повний опис довідника для конструктора. Право <c>Registry.View</c>.
    /// </summary>
    /// <param name="code">Код довідника.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// ⛔ Поля, зв'язки, правила і мапінг — ОДНІЄЮ відповіддю (<c>ФВ-8.12</c>).
    /// Вони описують один об'єкт і читаються разом; чотири запити давали б
    /// чотири різні моменти часу на одному екрані.
    /// </remarks>
    [HttpGet("{code}/definition")]
    [ProducesResponseType<RegistryDefinitionDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<RegistryDefinitionDto>> Definition(
        string code, CancellationToken ct)
        => Ok(await getDefinition.HandleAsync(code, ct).ConfigureAwait(false));

    /// <summary>
    /// Зберігає і одразу публікує опис довідника: поля і правила. Права
    /// <c>Registry.EditDefinition</c> і <c>Registry.Publish</c> (`BE-24`: без
    /// другого маршрут обходив би публікацію чернетки).
    /// </summary>
    /// <param name="code">Код довідника.</param>
    /// <param name="dto">Повний стан опису після правки.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// ⛔ Приймається ПОВНИЙ стан, а не набір правок. Опис довідника — це
    /// кілька десятків полів і одиниці правил; часткова правка вимагала б від
    /// клієнта тримати список того, що він змінив, і перша ж помилка в цьому
    /// списку давала б розбіжність, яку видно лише через рік.
    /// </remarks>
    [HttpPut("{code}/definition")]
    [ProducesResponseType<RegistryDefinitionVersionResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> SaveDefinition(
        string code, [FromBody] SaveRegistryDefinitionDto dto, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(dto);

        var version = await saveDefinition.HandleAsync(code, dto, ct).ConfigureAwait(false);

        // Повертається нова версія опису: саме вона відрізняє «збережено» від
        // «збережено і нічого не змінилося» для того, хто відкрив екран удруге.
        return Ok(new RegistryDefinitionVersionResponse(version));
    }

    /// <summary>Чернетка опису довідника, якщо є. Право <c>Registry.View</c> (`BE-24`).</summary>
    /// <param name="code">Код довідника.</param>
    /// <param name="ct">Токен скасування.</param>
    [HttpGet("{code}/definition/draft")]
    [ProducesResponseType<RegistryDefinitionDraftStateResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<RegistryDefinitionDraftStateResponse>> Draft(string code, CancellationToken ct)
        => Ok(await getDraft.HandleAsync(code, ct).ConfigureAwait(false));

    /// <summary>
    /// Зберігає чернетку опису; опублікований опис не змінюється. Право
    /// <c>Registry.EditDefinition</c> (`BE-24`).
    /// </summary>
    /// <param name="code">Код довідника.</param>
    /// <param name="request">Повний стан полів і правил, причина, версія чернетки.</param>
    /// <param name="ct">Токен скасування.</param>
    [HttpPut("{code}/definition/draft")]
    [ProducesResponseType<RegistryDefinitionDraftDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<RegistryDefinitionDraftDto>> SaveDraft(
        string code, [FromBody] SaveRegistryDefinitionDraftRequest request, CancellationToken ct)
        => Ok(await saveDraft.HandleAsync(code, request, ct).ConfigureAwait(false));

    /// <summary>
    /// Скасовує чернетку опису без публікації. Право <c>Registry.EditDefinition</c> (`BE-24`).
    /// </summary>
    /// <param name="code">Код довідника.</param>
    /// <param name="rowVersion">
    /// Версія чернетки. У query, а не в тілі: тіло DELETE частина клієнтів і
    /// проксі відкидає, а більше нічого запит не несе.
    /// </param>
    /// <param name="ct">Токен скасування.</param>
    [HttpDelete("{code}/definition/draft")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> DiscardDraft(string code, [FromQuery] string? rowVersion, CancellationToken ct)
    {
        await discardDraft.HandleAsync(code, rowVersion, ct).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>
    /// Публікує чернетку опису. Право <c>Registry.Publish</c> (`BE-24`).
    /// </summary>
    /// <param name="code">Код довідника.</param>
    /// <param name="request">Версія чернетки, яку публікують.</param>
    /// <param name="ct">Токен скасування.</param>
    [HttpPost("{code}/definition/publish")]
    [ProducesResponseType<RegistryDefinitionVersionResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Publish(
        string code, [FromBody] PublishRegistryDefinitionRequest request, CancellationToken ct)
        => Ok(new RegistryDefinitionVersionResponse(
            await publish.HandleAsync(code, request, ct).ConfigureAwait(false)));

    /// <summary>
    /// Історія змін опису довідника. Право <c>Registry.View</c>.
    /// </summary>
    /// <param name="code">Код довідника.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// ⚠ Історія ОПИСУ, а не записів: зміни записів живуть у журналі комірок і
    /// в самих вікнах чинності. Питання, на яке відповідає цей маршрут, —
    /// «чому тут з'явилося це поле».
    /// </remarks>
    [HttpGet("{code}/history")]
    [ProducesResponseType<IReadOnlyList<RegistryHistoryEntryDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<RegistryHistoryEntryDto>>> History(
        string code, CancellationToken ct)
        => Ok(await getHistory.HandleAsync(code, ct).ConfigureAwait(false));

    /// <summary>
    /// Де використано довідник. Право <c>Registry.EditDefinition</c> (`BE-24`).
    /// </summary>
    /// <param name="code">Код довідника.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// ⛔ Посилання на сам ДОВІДНИК, а не на окремий його запис: колонки
    /// шаблонів типу <c>Lookup</c>, поля сусідніх довідників, речовини
    /// методологій, сутності зовнішніх джерел. Відповідь потрібна ДО зміни
    /// опису — сьогодні перевипустити довідник можна наосліп.
    /// <para>
    /// ⚠ <c>total</c> і довжина <c>items</c> — різні числа: перелік обрізаний
    /// сторінкою, лічильник чесний.
    /// </para>
    /// </remarks>
    [HttpGet("{code}/usage")]
    [ProducesResponseType<UsageResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<UsageResponse>> Usage(string code, CancellationToken ct)
        => Ok(await getUsage.HandleAsync(code, ct).ConfigureAwait(false));

    /// <summary>Створює або оновлює запис. Право <c>Registry.EditData</c>.</summary>
    /// <param name="code">Код довідника.</param>
    /// <param name="dto">Опис запису.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// <c>Registry.EditData</c> і <c>Registry.EditDefinition</c> — різні права:
    /// змінювати значення і змінювати склад полів довідника може не той самий
    /// користувач.
    /// </remarks>
    [HttpPost("{code}/entries")]
    // ⛔ Тип відповіді оголошений ЯВНО, а тіло — іменований запис, а не
    // анонімний об'єкт. Інакше в схемі OpenAPI лишається порожня 200-ка,
    // згенерувати клієнтський тип ні з чого, і клієнт описує відповідь
    // рукописним інтерфейсом — з помилкою в назві поля, яку ніхто не
    // побачить (`A7-16`, `A7-32`).
    [ProducesResponseType<RegistryEntryIdResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<RegistryEntryIdResponse>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Upsert(
        string code, [FromBody] RegistryEntryUpsertDto dto, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(dto);

        var isNew = dto.Id is null;
        var id = await upsert.HandleAsync(dto, ct).ConfigureAwait(false);

        // 201 для нового запису, 200 для оновлення: різниця видима клієнтові й
        // означає, чи з'явився новий Id, який тепер лежатиме в комірках.
        // ⚠ Один і той самий ІМЕНОВАНИЙ запис в обох гілках. Тут стояв
        // анонімний `new { id }` для 201 — рівно те, від чого застерігає
        // коментар вище: форма збігалася випадково, і перше ж перейменування
        // поля розвело б 200 і 201 мовчки.
        return isNew
            ? CreatedAtAction(nameof(Entries), new { code }, new RegistryEntryIdResponse(id))
            : Ok(new RegistryEntryIdResponse(id));
    }

    /// <summary>
    /// Імпорт записів довідника з CSV. Право <c>Registry.EditData</c> (`BE-24`).
    /// </summary>
    /// <remarks>
    /// Звіт — завжди 200: помилки рядків є даними для того, хто імпортує.
    /// Хоч одна помилка або <c>dryRun</c> — не записано нічого. Стеля файлу —
    /// <c>Registries:ImportMaxBytes</c>. Колонки — коди полів ОПУБЛІКОВАНОГО
    /// опису плюс <c>code</c>; валідація значень — та сама, що при ручному
    /// редагуванні запису (<see cref="UpsertRegistryEntryHandler"/>).
    /// </remarks>
    /// <param name="code">Код довідника.</param>
    /// <param name="dryRun">Лише перевірка.</param>
    /// <param name="file">CSV у кодуванні UTF-8.</param>
    /// <param name="ct">Токен скасування.</param>
    [HttpPost("{code}/entries/import")]
    [ProducesResponseType<RegistryEntryImportReport>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> ImportEntries(
        string code, [FromQuery] bool dryRun, IFormFile file, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(file);

        var maxBytes = configuration.GetValue(
            "Registries:ImportMaxBytes", ImportRegistryEntriesHandler.DefaultMaxBytes);

        // Понад стелю файл не читається — обробник відмовить після перевірки права.
        var content = string.Empty;
        if (file.Length <= maxBytes)
        {
            using var reader = new StreamReader(file.OpenReadStream(), System.Text.Encoding.UTF8);
            content = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
        }

        return Ok(await importEntries
            .HandleAsync(code, content, file.Length, maxBytes, dryRun, ct)
            .ConfigureAwait(false));
    }

    /// <summary>
    /// Змінює вікно дії запису. Право <c>Registry.EditData</c>.
    /// </summary>
    /// <param name="code">Код довідника.</param>
    /// <param name="id">Запис.</param>
    /// <param name="request">Нове вікно.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// Запис не видаляється, а закривається датою: у комірці зберігається
    /// <c>Id</c>, і видалення зробило б історичні документи нечитабельними.
    /// </remarks>
    [HttpPost("{code}/entries/{id:long}/validity")]
    [ProducesResponseType<AffectedRowsResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> SetValidity(
        string code, long id, [FromBody] SetValidityRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var affected = await setValidity
            .HandleAsync(id, request.From, request.To, ct)
            .ConfigureAwait(false);

        // Повертається кількість зачеплених рядків: той, хто звузив вікно, має
        // бачити масштаб наслідку, а не лише «ок».
        return Ok(new AffectedRowsResponse(affected));
    }

    /// <summary>
    /// Видаляє запис довідника. Право <c>Registry.EditData</c> (ФВ-8.6).
    /// </summary>
    /// <param name="code">Код довідника.</param>
    /// <param name="id">Запис.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// ⛔ Запис, на який посилаються дані, не видаляється — <c>409 ECR-REG-0409</c>
    /// з кількістю посилань у <c>details.references</c>. Клієнт у відповідь
    /// пропонує закрити запис датою (<c>POST …/validity</c>), а не повторює
    /// спробу: повтор дасть ту саму відмову, бо змінити треба не запит, а намір.
    /// <para>
    /// ⚠ <c>code</c> у шляху перевіряється обробником: запис чужого довідника —
    /// <c>404</c>, а не мовчазне видалення «бо id збігся».
    /// </para>
    /// </remarks>
    [HttpDelete("{code}/entries/{id:long}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> DeleteEntry(string code, long id, CancellationToken ct)
    {
        await deleteEntry.HandleAsync(code, id, ct).ConfigureAwait(false);

        return NoContent();
    }

    /// <summary>
    /// Перемикає master-джерело <b>набору</b> довідників. Право <c>Integration.Manage</c>.
    /// </summary>
    /// <remarks>
    /// ⛔ Операція над НАБОРОМ, і сутності «група довідників» немає навмисно
    /// (<c>ФВ-13.10</c>): група — це факт одного перемикання, а не властивість
    /// довідника. Набір складає той, хто перемикає: він єдиний, хто знає, які
    /// довідники пов'язані <b>сьогодні</b>.
    ///
    /// ⚠ Усе або нічого: невідомий код у переліку відхиляє операцію цілком, а
    /// перевірка «немає відкритого періоду» робиться один раз на весь набір.
    /// Половина блоку в одному режимі, половина в іншому — гірше, ніж відмова.
    /// </remarks>
    [HttpPut("source-kind")]
    [ProducesResponseType<AffectedRowsResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> SwitchSourceKind(
        [FromBody] SwitchSourceKindRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var changed = await switchSource
            .HandleAsync(request.RegistryCodes, request.SourceKind, request.Reason, ct)
            .ConfigureAwait(false);

        // ⚠ Повертається, скільки СПРАВДІ змінилося, а не розмір набору: у
        // наборі постійно трапляються довідники, які вже в цільовому режимі,
        // і «перемкнуто 3» там, де змінився один, — це неправда в журналі.
        return Ok(new AffectedRowsResponse(changed));
    }
}

/// <summary>Запит на перемикання master-джерела набору довідників.</summary>
/// <param name="RegistryCodes">Коди довідників; порожній набір відхиляється.</param>
/// <param name="SourceKind">Нове джерело для всіх перелічених.</param>
/// <param name="Reason">
/// Причина. Обов'язкова: через рік питання «навіщо перемикали цей набір
/// разом» — єдине, на яке доведеться відповісти, і відповідь має бути в
/// журналі, а не в чиїйсь пам'яті.
/// </param>
public sealed record SwitchSourceKindRequest(
    IReadOnlyList<string> RegistryCodes,
    Ecr.Domain.Enums.RegistrySourceKind SourceKind,
    string Reason);

/// <summary>Запит на зміну вікна дії запису довідника.</summary>
/// <param name="From">Початок дії; <c>null</c> — без обмеження.</param>
/// <param name="To">Кінець дії; <c>null</c> — без обмеження.</param>
public sealed record SetValidityRequest(DateOnly? From, DateOnly? To);

/// <summary>Нова версія опису довідника після збереження.</summary>
/// <param name="DefinitionVersion">Версія опису; росте від зміни складу полів і правил.</param>
public sealed record RegistryDefinitionVersionResponse(int DefinitionVersion);

/// <summary>Ідентифікатор запису довідника.</summary>
/// <param name="Id">Запис.</param>
public sealed record RegistryEntryIdResponse(long Id);

/// <summary>Скільки рядків зачепила операція.</summary>
/// <param name="AffectedRows">Кількість.</param>
public sealed record AffectedRowsResponse(int AffectedRows);
