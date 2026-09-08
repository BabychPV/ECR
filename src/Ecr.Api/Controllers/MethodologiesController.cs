using Ecr.Application.Calculations;
using Ecr.Application.Calculations.Dto;
using Ecr.Application.Common;
using Ecr.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ecr.Api.Controllers;

/// <summary>
/// Методології розрахунку: перелік, версії, формули, публікація, симуляція.
/// </summary>
/// <remarks>
/// ⛔ Дві половини цього контролера відповідають на різні питання. Читання для
/// РОЗРАХУНКУ бачить лише опубліковане (<c>GET /methodologies</c>), бо рахувати
/// чернеткою не можна ніколи. Читання для РЕДАГУВАННЯ бачить чернетки
/// (<c>GET /methodologies/{id}/versions</c>), бо правити можна лише їх
/// (ФВ-9.15). Злити їх в один маршрут із прапорцем означало б, що одна
/// необережна зміна фільтра пускає незавершену версію в числа звіту.
/// </remarks>
[ApiController]
[Route("api/v1/methodologies")]
[Authorize]
public sealed class MethodologiesController(
    ListMethodologiesHandler listMethodologies,
    PublishMethodologyHandler publish,
    SimulateMethodologyHandler simulate,
    ListMethodologyVersionsHandler listVersions,
    CreateMethodologyVersionHandler createVersion,
    ListMethodologyFormulasHandler listFormulas,
    SaveMethodologyFormulaHandler saveFormula,
    DeleteMethodologyFormulaHandler deleteFormula,
    CreateMethodologyHandler createMethodology,
    ListMethodologyConstantsHandler listConstants,
    SaveMethodologyConstantHandler saveConstant,
    ListMethodologyRulesHandler listRules,
    SaveMethodologyRuleHandler saveRule,
    ListMethodologyOutputsHandler listOutputs,
    SaveMethodologyOutputHandler saveOutput,
    ListMethodologyTestCasesHandler listTests,
    SaveMethodologyTestCaseHandler saveTest,
    SetMethodologyModesHandler setModes,
    ListCalculationBindingsHandler listBindings,
    SaveCalculationBindingHandler saveBinding) : ControllerBase
{
    /// <summary>
    /// Заводить методологію-контейнер. Право <c>Calculation.EditFormula</c>.
    /// </summary>
    /// <param name="request">Код, назва, природа й група.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// ⛔ Дії не існувало, і це був корінь, а не незручність: <c>POST
    /// …/{id}/versions</c> вимагає ідентифікатора методології, брати який не
    /// було звідки. Увесь конфігуратор версій працював лише над тим, що завіз
    /// офлайновий генератор тестових даних.
    ///
    /// ⚠ Створюється саме КОНТЕЙНЕР, без жодної версії. Перша версія — окрема
    /// дія (<c>POST …/{id}/versions</c> з <c>copyFromVersionId = null</c>): у
    /// неї свій рівень драбини виразності і свої режими, і склеїти обидві дії
    /// означало б ухвалити ці рішення за методолога в момент, коли він ще навіть
    /// не назвав методологію.
    /// </remarks>
    [HttpPost]
    [ProducesResponseType<MethodologySummaryDto>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<MethodologySummaryDto>> Create(
        [FromBody] CreateMethodologyRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var created = await createMethodology
            .HandleAsync(request.Code, request.NameL10n, request.Kind, request.Group, ct)
            .ConfigureAwait(false);

        return Created($"/api/v1/methodologies/{created.Id}/versions", created);
    }

    /// <summary>Перелік методологій. Право <c>Calculation.View</c>.</summary>
    /// <param name="ids">Методології, які цікавлять.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// Три осі версійності не змішуються (ФВ-13.2): версія визначення, вікно
    /// дії і версія даних. Остання тут не з'являється взагалі — вона живе в
    /// довідниках.
    /// </remarks>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<MethodologyDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<MethodologyDto>>> List(
        [FromQuery] int[] ids, CancellationToken ct)
        => Ok(await listMethodologies.HandleAsync(ids ?? [], ct).ConfigureAwait(false));

    /// <summary>
    /// Усі версії методології, включно з чернетками. Право <c>Calculation.View</c>.
    /// </summary>
    /// <param name="id">Методологія.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// ⚠ Саме тут видно те, чого не показує <c>GET /methodologies</c>: версію,
    /// яку ще не опублікували. Доти кнопка «Опублікувати» стояла в переліку
    /// для версій, яких той перелік не містив за побудовою.
    /// </remarks>
    [HttpGet("{id:int}/versions")]
    [ProducesResponseType<IReadOnlyList<MethodologyDraftVersionDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<MethodologyDraftVersionDto>>> Versions(
        int id, CancellationToken ct)
        => Ok(await listVersions.HandleAsync(id, ct).ConfigureAwait(false));

    /// <summary>
    /// Створює версію-чернетку. Право <c>Calculation.EditFormula</c>.
    /// </summary>
    /// <param name="id">Методологія.</param>
    /// <param name="request">Номер версії і, за потреби, версія-джерело.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// ⛔ **Єдиний спосіб змінити опубліковану версію** (ФВ-9.1): вона
    /// незмінна, бо на її числа посилаються вже подані форми. «Зміна» — це
    /// клон із власним вікном дії, і саме тому клонування є частиною
    /// редагування, а не зручністю.
    /// </remarks>
    [HttpPost("{id:int}/versions")]
    [ProducesResponseType<MethodologyDraftVersionDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<MethodologyDraftVersionDto>> CreateVersion(
        int id, [FromBody] CreateMethodologyVersionRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Ok(await createVersion
            .HandleAsync(id, request.VersionNumber, request.CopyFromVersionId, request.Level, ct)
            .ConfigureAwait(false));
    }

    /// <summary>Формули версії. Право <c>Calculation.View</c>.</summary>
    /// <param name="id">Методологія.</param>
    /// <param name="vid">Версія.</param>
    /// <param name="ct">Токен скасування.</param>
    [HttpGet("{id:int}/versions/{vid:int}/formulas")]
    [ProducesResponseType<IReadOnlyList<MethodologyFormulaDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<MethodologyFormulaDto>>> Formulas(
        int id, int vid, CancellationToken ct)
        => Ok(await listFormulas.HandleAsync(vid, ct).ConfigureAwait(false));

    /// <summary>
    /// Записує формулу версії-чернетки. Право <c>Calculation.EditFormula</c>.
    /// </summary>
    /// <param name="id">Методологія.</param>
    /// <param name="vid">Версія-чернетка.</param>
    /// <param name="code">Код формули.</param>
    /// <param name="request">Вираз, тип результату й одиниця.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// ⚠ <c>PUT</c>, а не <c>POST</c>: адресою формули є її **код** у межах
    /// версії, і повторний запит із тим самим тілом має дати той самий стан.
    /// Створення й зміна — та сама дія рівно тому, що код задає викликач.
    ///
    /// ⛔ Стан версії перевіряє домен, а не ця дія: опублікована версія
    /// відхиляє правку сама (<c>ECR-CALC-0409</c>, ФВ-13.2).
    /// </remarks>
    [HttpPut("{id:int}/versions/{vid:int}/formulas/{code}")]
    [ProducesResponseType<MethodologyFormulaDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<MethodologyFormulaDto>> SaveFormula(
        int id, int vid, string code, [FromBody] SaveMethodologyFormulaRequest request,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Ok(await saveFormula
            .HandleAsync(
                vid, code, request.Expression, request.ResultType, request.OutputUnitId,
                request.ArgumentsCsv, ct)
            .ConfigureAwait(false));
    }

    /// <summary>Константи версії. Право <c>Calculation.View</c>.</summary>
    /// <param name="id">Методологія.</param>
    /// <param name="vid">Версія.</param>
    /// <param name="ct">Токен скасування.</param>
    [HttpGet("{id:int}/versions/{vid:int}/constants")]
    [ProducesResponseType<IReadOnlyList<MethodologyConstantDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<MethodologyConstantDto>>> Constants(
        int id, int vid, CancellationToken ct)
        => Ok(await listConstants.HandleAsync(vid, ct).ConfigureAwait(false));

    /// <summary>
    /// Записує константу версії-чернетки. Право <c>Calculation.EditConstant</c>.
    /// </summary>
    /// <param name="id">Методологія.</param>
    /// <param name="vid">Версія-чернетка.</param>
    /// <param name="code">Код константи — те, що стоїть після <c>CST.</c>.</param>
    /// <param name="request">Значення, одиниця, вид, вікно чинності, звуження.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// ⛔ Доти константа потрапляла у версію рівно одним шляхом — копіюванням при
    /// клонуванні. Первісну завести не було чим, тобто методологія, створена з
    /// нуля, рахувала правильними виразами по порожніх коефіцієнтах.
    ///
    /// ⚠ <c>ValidTo</c> — перший НЕчинний день (виключна межа): коефіцієнт,
    /// чинний увесь 2024 рік, має тут <c>2025-01-01</c>.
    /// </remarks>
    [HttpPut("{id:int}/versions/{vid:int}/constants/{code}")]
    [ProducesResponseType<MethodologyConstantDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<MethodologyConstantDto>> SaveConstant(
        int id, int vid, string code, [FromBody] SaveMethodologyConstantRequest request,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Ok(await saveConstant
            .HandleAsync(
                vid,
                code,
                new SaveMethodologyConstant(
                    request.Kind,
                    request.Value,
                    request.UnitId,
                    request.TextValue,
                    request.ValidFrom,
                    request.ValidTo,
                    request.Category,
                    request.SubstanceEntryId,
                    request.Source),
                ct)
            .ConfigureAwait(false));
    }

    /// <summary>Правила відбору рядків версії. Право <c>Calculation.View</c>.</summary>
    /// <param name="id">Методологія.</param>
    /// <param name="vid">Версія.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// ⚠ Віддаються і ВИМКНЕНІ правила, на відміну від того, що бачить прогін:
    /// вимкнене правило, невидиме в редакторі, неможливо ні ввімкнути назад, ні
    /// назвати причиною порожнього розрахунку.
    /// </remarks>
    [HttpGet("{id:int}/versions/{vid:int}/rules")]
    [ProducesResponseType<IReadOnlyList<MethodologyRuleDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<MethodologyRuleDto>>> Rules(
        int id, int vid, CancellationToken ct)
        => Ok(await listRules.HandleAsync(vid, ct).ConfigureAwait(false));

    /// <summary>
    /// Записує правило відбору рядків. Право <c>Calculation.EditRule</c>.
    /// </summary>
    /// <param name="id">Методологія.</param>
    /// <param name="vid">Версія-чернетка.</param>
    /// <param name="code">Код правила.</param>
    /// <param name="request">Предикат, пріоритет, активність.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// ⛔ Правило і прив'язка — РІЗНІ речі й обидві обов'язкові. Прив'язка
    /// (<c>PUT …/bindings/…</c>) каже, ЯКА таблиця і в яку колонку лягає число;
    /// правило — ЯКІ рядки цієї таблиці рахувати. Без правил
    /// <c>MethodologyResolver</c> не зіставляє жодного рядка, і перерахунок
    /// завершується успіхом, не порахувавши нічого.
    ///
    /// ⚠ Предикат СТРУКТУРОВАНИЙ, а не вираз (<c>Q-026</c>): діалект методологій
    /// посилань на комірки документів не має за побудовою, а третій діалект не
    /// створюється (<c>D-92</c>). «Уся таблиця» пишеться як <c>{}</c>.
    /// </remarks>
    [HttpPut("{id:int}/versions/{vid:int}/rules/{code}")]
    [ProducesResponseType<MethodologyRuleDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<MethodologyRuleDto>> SaveRule(
        int id, int vid, string code, [FromBody] SaveMethodologyRuleRequest request,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Ok(await saveRule
            .HandleAsync(vid, code, request.MatchJson, request.Priority, request.IsActive, ct)
            .ConfigureAwait(false));
    }

    /// <summary>Оголошені виходи версії. Право <c>Calculation.View</c>.</summary>
    /// <param name="id">Методологія.</param>
    /// <param name="vid">Версія.</param>
    /// <param name="ct">Токен скасування.</param>
    [HttpGet("{id:int}/versions/{vid:int}/outputs")]
    [ProducesResponseType<IReadOnlyList<MethodologyOutputDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<MethodologyOutputDto>>> Outputs(
        int id, int vid, CancellationToken ct)
        => Ok(await listOutputs.HandleAsync(vid, ct).ConfigureAwait(false));

    /// <summary>
    /// Оголошує вихід версії. Право <c>Calculation.EditFormula</c>.
    /// </summary>
    /// <param name="id">Методологія.</param>
    /// <param name="vid">Версія-чернетка.</param>
    /// <param name="code">Код виходу — адреса, на яку посилається прив'язка.</param>
    /// <param name="request">Одиниця результату й порядок.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// ⛔ Без жодного виходу модуль рахує всі формули і не записує НІЧОГО: цикл
    /// запису йде по оголошених виходах, а не по формулах. Методологія з
    /// правильними виразами дає при цьому порожній результат, і жодна перевірка
    /// публікації цього не називає.
    /// </remarks>
    [HttpPut("{id:int}/versions/{vid:int}/outputs/{code}")]
    [ProducesResponseType<MethodologyOutputDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<MethodologyOutputDto>> SaveOutput(
        int id, int vid, string code, [FromBody] SaveMethodologyOutputRequest request,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Ok(await saveOutput
            .HandleAsync(vid, code, request.UnitId, request.Ordinal, ct)
            .ConfigureAwait(false));
    }

    /// <summary>Золотий набір версії. Право <c>Calculation.View</c>.</summary>
    /// <param name="id">Методологія.</param>
    /// <param name="vid">Версія.</param>
    /// <param name="ct">Токен скасування.</param>
    [HttpGet("{id:int}/versions/{vid:int}/tests")]
    [ProducesResponseType<IReadOnlyList<MethodologyTestCaseDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<MethodologyTestCaseDto>>> Tests(
        int id, int vid, CancellationToken ct)
        => Ok(await listTests.HandleAsync(vid, ct).ConfigureAwait(false));

    /// <summary>
    /// Записує тест золотого набору. Право <c>Calculation.EditFormula</c>.
    /// </summary>
    /// <param name="id">Методологія.</param>
    /// <param name="vid">Версія-чернетка.</param>
    /// <param name="code">Код тесту.</param>
    /// <param name="request">Вхід, очікувані виходи, допуск.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// ⛔ Не «тести заради тестів», а умова публікації (ФВ-9.12): порожній набір
    /// НЕ зелений, і версія без нього не публікується взагалі. Доки набір не було
    /// чим заповнити, опублікувати заведену з нуля методологію було неможливо в
    /// принципі — правило працювало, користі з нього не було.
    /// </remarks>
    [HttpPut("{id:int}/versions/{vid:int}/tests/{code}")]
    [ProducesResponseType<MethodologyTestCaseDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<MethodologyTestCaseDto>> SaveTest(
        int id, int vid, string code, [FromBody] SaveMethodologyTestCaseRequest request,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Ok(await saveTest
            .HandleAsync(vid, code, request.InputJson, request.ExpectedJson, request.Tolerance, ct)
            .ConfigureAwait(false));
    }

    /// <summary>
    /// Задає режими обчислення версії-чернетки. Право <c>Calculation.EditFormula</c>.
    /// </summary>
    /// <param name="id">Методологія.</param>
    /// <param name="vid">Версія-чернетка.</param>
    /// <param name="request">Арифметика, календарна конвенція, обсяг журналу.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// ⛔ Доти <c>SetModes</c> кликав лише <c>CloneAsDraft</c>, який ПЕРЕНОСИТЬ
    /// режими джерела: конструктор ставить <c>Legacy</c>/<c>Actual</c>, клон
    /// переносить, третього шляху не було. Тобто <c>NumericMode.Strict</c>
    /// увімкнути було неможливо в принципі — при тому, що саме він відрізняє
    /// <c>null</c> від тихого нуля при діленні на нуль (ФВ-9.14).
    ///
    /// ⚠ Обидва перші режими тихо змінюють УСІ числа версії, не змінивши жодної
    /// формули. Тому вони обов'язкові в diff публікації (<c>D-78</c>), а сама
    /// зміна — окрема дія, а не поле у створенні версії.
    /// </remarks>
    [HttpPut("{id:int}/versions/{vid:int}/modes")]
    [ProducesResponseType<MethodologyDraftVersionDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<MethodologyDraftVersionDto>> SaveModes(
        int id, int vid, [FromBody] SetMethodologyModesRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Ok(await setModes
            .HandleAsync(vid, request.NumericMode, request.CalendarMode, request.TraceLevel, ct)
            .ConfigureAwait(false));
    }

    /// <summary>Прив'язки методології до колонок. Право <c>Calculation.View</c>.</summary>
    /// <param name="id">Методологія.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// ⚠ Прив'язка живе на МЕТОДОЛОГІЇ, а не на версії: вона переживає всі її
    /// версії одразу і клонуванням не копіюється. Тому маршрут без <c>vid</c>.
    /// </remarks>
    [HttpGet("{id:int}/bindings")]
    [ProducesResponseType<IReadOnlyList<CalculationBindingDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<CalculationBindingDto>>> Bindings(
        int id, CancellationToken ct)
        => Ok(await listBindings.HandleAsync(id, ct).ConfigureAwait(false));

    /// <summary>
    /// Прив'язує вихід методології до колонки документа. Право <c>Calculation.EditRule</c>.
    /// </summary>
    /// <param name="id">Методологія-джерело.</param>
    /// <param name="columnDefId">Колонка-приймач.</param>
    /// <param name="outputCode">Який вихід методології лягає в колонку.</param>
    /// <param name="request">Предикат звуження рядків і активність.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// ⛔ <b>Головний блокер розрахунку.</b> <c>cfg.CalculationBinding</c> не
    /// створювало НІЩО — ні обробник, ні тест, — і наслідок був повністю
    /// мовчазний: <c>RecalculationJob</c> віддавав порожній перелік прив'язок,
    /// оркестратор одразу повертав порожній профіль, задача завершувалася
    /// <c>Succeeded</c> і не рахувала нічого. Порожній набір прив'язок помилкою
    /// не є, тож ані стан задачі, ані журнал про це не казали.
    ///
    /// ⛔ Адреса — трійка <c>(колонка, методологія, код виходу)</c>
    /// (<c>UQ_CalculationBinding</c>), тому дія одна: <c>PUT</c> створює і
    /// змінює. <c>TableDefId</c> у запиті НЕМАЄ — він виводиться з колонки: два
    /// поля про те саме розходяться мовчки, а прив'язка з чужою таблицею просто
    /// не спрацьовує.
    /// </remarks>
    [HttpPut("{id:int}/bindings/{columnDefId:int}/{outputCode}")]
    [ProducesResponseType<CalculationBindingDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<CalculationBindingDto>> SaveBinding(
        int id, int columnDefId, string outputCode,
        [FromBody] SaveCalculationBindingRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Ok(await saveBinding
            .HandleAsync(id, columnDefId, outputCode, request.MatchJson, request.IsActive, ct)
            .ConfigureAwait(false));
    }

    /// <summary>
    /// Прибирає формулу з версії-чернетки. Право <c>Calculation.EditFormula</c>.
    /// </summary>
    /// <param name="id">Методологія.</param>
    /// <param name="vid">Версія-чернетка.</param>
    /// <param name="code">Код формули.</param>
    /// <param name="ct">Токен скасування.</param>
    [HttpDelete("{id:int}/versions/{vid:int}/formulas/{code}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> DeleteFormula(int id, int vid, string code, CancellationToken ct)
    {
        await deleteFormula.HandleAsync(vid, code, ct).ConfigureAwait(false);

        return NoContent();
    }

    /// <summary>
    /// Публікує версію методології. Право <c>Calculation.Publish</c>.
    /// </summary>
    /// <param name="id">Методологія.</param>
    /// <param name="vid">Версія.</param>
    /// <param name="request">Причина зміни і дата набуття чинності.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// ⚠ <b>Найнебезпечніша операція в системі</b> (ФВ-9.6): вона тихо змінює
    /// числа у вже поданих формах. Тому обов'язкові: причина зміни (ФВ-14.7),
    /// зелений тест (ФВ-9.12, інакше <c>ECR-CALC-0422</c>) і правило чотирьох
    /// очей — публікувати власну правку заборонено системно
    /// (<c>D-40</c>, <c>ECR-CALC-0409</c>).
    /// </remarks>
    [HttpPost("{id:int}/versions/{vid:int}/publish")]
    [ProducesResponseType<MethodologyPublicationDiff>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<MethodologyPublicationDiff>> Publish(
        int id, int vid, [FromBody] PublishMethodologyRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Diff РЕЗУЛЬТАТІВ повертається клієнтові, а не лише пишеться в журнал:
        // той, хто щойно опублікував, має побачити, що саме змінилося в числах.
        var diff = await publish
            .HandleAsync(vid, request.ChangeReason, request.EffectiveFrom, ct)
            .ConfigureAwait(false);

        return Ok(diff);
    }

    /// <summary>
    /// Прогін методології <b>без запису</b>. Право <c>Calculation.View</c>.
    /// </summary>
    /// <param name="id">Методологія.</param>
    /// <param name="request">Версія і період.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// Показує, що вийде, якщо опублікувати (ФВ-13.5). Нічого не зберігає:
    /// саме тому доступний із правом на перегляд, а не на публікацію.
    /// </remarks>
    [HttpPost("{id:int}/simulate")]
    [ProducesResponseType<SimulationResultDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<SimulationResultDto>> Simulate(
        int id, [FromBody] SimulateMethodologyRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Ok(await simulate
            .HandleAsync(request.MethodologyVersionId, request.PeriodKey, ct)
            .ConfigureAwait(false));
    }
}

/// <summary>Запит на публікацію версії методології.</summary>
/// <param name="ChangeReason">Причина зміни; обов'язкова і непорожня (ФВ-14.7).</param>
/// <param name="EffectiveFrom">
/// Дата набуття чинності. Обов'язкова: без неї невідомо, які періоди рахувати
/// цією версією, а які — попередньою.
/// </param>
public sealed record PublishMethodologyRequest(string ChangeReason, DateOnly? EffectiveFrom);

/// <summary>Запит на створення версії-чернетки.</summary>
/// <param name="VersionNumber">
/// Номер нової версії; унікальний у межах методології (<c>UQ_MethodologyVersion</c>).
/// </param>
/// <param name="CopyFromVersionId">
/// Версія-джерело. <c>null</c> — порожня чернетка; це законно лише для
/// методології, у якій ще нічого немає.
/// </param>
/// <param name="Level">
/// Рівень драбини виразності для ПОРОЖНЬОЇ чернетки. Клон бере рівень із
/// джерела: версія, що змінила рівень, — уже інша методологія, а не її нова
/// редакція (ФВ-9.2).
/// </param>
public sealed record CreateMethodologyVersionRequest(
    string VersionNumber, int? CopyFromVersionId, CalculationLevel Level);

/// <summary>Запит на запис формули версії-чернетки.</summary>
/// <param name="Expression">Вираз діалекту методологій.</param>
/// <param name="ResultType">
/// Що формула повертає: число чи текст. Оголошується явно — у корпусі формула
/// повертає <c>'Сверхнорматив'</c> як ЗНАЧЕННЯ, і без типу воно пішло б у
/// числову колонку результату.
/// </param>
/// <param name="OutputUnitId">
/// Одиниця результату; <c>null</c> — зняти. На текстовому результаті одиниця
/// відхиляється: вимір — властивість числа (ФВ-16.6).
/// </param>
/// <param name="ArgumentsCsv">
/// Оголошені аргументи — <c>;</c>-список, як у <c>FInfo_Arguments</c>;
/// <c>null</c> — списку немає. ⛔ Це **джерело істини про аргументи**, а не
/// текст виразу (директива ПК-1 №05 §7, пастка 2): збірка підставляє рівно
/// перелічене, і токен поза списком у вираз не потрапляє — формула рахується з
/// невизначеним параметром і повертає правдоподібне число, а не помилку.
/// ⚠ <c>null</c> і порожній рядок — різні стани: перший глушить звірку
/// (<c>ECR-CALC-0432</c>), другий оголошує «нуль аргументів».
/// </param>
public sealed record SaveMethodologyFormulaRequest(
    string Expression, FormulaResultType ResultType, int? OutputUnitId, string? ArgumentsCsv);

/// <summary>Запит на заведення методології-контейнера.</summary>
/// <param name="Code">
/// Код, унікальний у системі: те, чим на методологію посилаються імпорти
/// (<c>!Name</c> через <c>calc.MethodologyImport</c>) і прив'язки.
/// </param>
/// <param name="NameL10n">Назва мовами каталогу.</param>
/// <param name="Kind">
/// Природа методології. ⛔ Не описове поле: <c>Bespoke</c>-модулі правила
/// прив'язки не мають у принципі, а <c>Library</c> не рахує ні для кого — і саме
/// на цьому тримається перевірка публікації, яка доти була недосяжна.
/// </param>
/// <param name="Group">Група в переліку; <c>null</c> — поза групами.</param>
public sealed record CreateMethodologyRequest(
    string Code,
    Dictionary<string, string> NameL10n,
    MethodologyKind Kind,
    string? Group);

/// <summary>Запит на запис константи версії-чернетки.</summary>
/// <param name="Kind">
/// Число, текст або мітка категорії. ⛔ Константа не завжди число: з 6507
/// констант корпусу 108 нечислові, і ~90 із них ужиті у виразах операндом
/// порівняння (поправка 2-біс директиви ПК-1 №05).
/// </param>
/// <param name="Value">Число; обов'язкове для <c>Numeric</c>.</param>
/// <param name="UnitId">Одиниця; для числа обов'язкова, для тексту знімається (ФВ-16.1).</param>
/// <param name="TextValue">Текст; обов'язковий для нечислових видів.</param>
/// <param name="ValidFrom">Перший чинний день; <c>null</c> — від початку.</param>
/// <param name="ValidTo">
/// Перший НЕчинний день, **виключно**: коефіцієнт, чинний увесь 2024 рік, має
/// тут <c>2025-01-01</c>.
/// </param>
/// <param name="Category">Категорія звуження; <c>null</c> — константа спільна.</param>
/// <param name="SubstanceEntryId">Речовина звуження; <c>null</c> — спільна.</param>
/// <param name="Source">Звідки взято значення: наказ, паспорт установки, вимірювання.</param>
public sealed record SaveMethodologyConstantRequest(
    ConstantKind Kind,
    decimal? Value,
    int? UnitId,
    string? TextValue,
    DateOnly? ValidFrom,
    DateOnly? ValidTo,
    string? Category,
    long? SubstanceEntryId,
    string? Source);

/// <summary>Запит на запис правила відбору рядків.</summary>
/// <param name="MatchJson">
/// Структурований предикат «колонка → очікуване значення»; <c>{}</c> — уся
/// таблиця. ⚠ Не вираз: діалект методологій посилань на комірки не має
/// (<c>Q-026</c>).
/// </param>
/// <param name="Priority">Менше значення — вищий пріоритет; перший збіг виграє (ФВ-13.4).</param>
/// <param name="IsActive">Вимкнене правило не бере участі ні в зіставленні, ні в матриці покриття.</param>
public sealed record SaveMethodologyRuleRequest(string MatchJson, int Priority, bool IsActive);

/// <summary>Запит на оголошення виходу версії.</summary>
/// <param name="UnitId">Одиниця результату; обов'язкова — на ній тримається перевірка розмірностей.</param>
/// <param name="Ordinal">Порядок у переліку виходів.</param>
public sealed record SaveMethodologyOutputRequest(int UnitId, int Ordinal);

/// <summary>Запит на запис тесту золотого набору.</summary>
/// <param name="InputJson">Вхід прогону у формі <c>CalculationInput</c>.</param>
/// <param name="ExpectedJson">Очікувані виходи: <c>{"tons":12.5}</c>.</param>
/// <param name="Tolerance">
/// Допуск порівняння; нуль — точна рівність. ⚠ Потрібен саме тому, що числа
/// рахуються з округленням: очікувати побітової рівності означало б червоний
/// тест від зміни порядку доданків.
/// </param>
public sealed record SaveMethodologyTestCaseRequest(
    string InputJson, string ExpectedJson, decimal Tolerance);

/// <summary>Запит на зміну режимів обчислення версії-чернетки.</summary>
/// <param name="NumericMode">
/// Арифметика: <c>Legacy</c> відтворює числа чинної системи, <c>Strict</c>
/// віддає <c>null</c> там, де та мовчки давала нуль (ФВ-9.9, ФВ-9.14).
/// </param>
/// <param name="CalendarMode">Джерело тривалості періоду (ФВ-16.11).</param>
/// <param name="TraceLevel">Обсяг журналу обчислення (ФВ-9.13).</param>
public sealed record SetMethodologyModesRequest(
    NumericMode NumericMode, CalendarMode CalendarMode, TraceLevel TraceLevel);

/// <summary>Запит на прив'язку виходу методології до колонки документа.</summary>
/// <param name="MatchJson">
/// Як звузити рядки таблиці; <c>{}</c> — усі. ⚠ Порожній рядок відхиляється: він
/// не збігається з жодним рядком, і прив'язка мовчки не спрацьовувала б.
/// </param>
/// <param name="IsActive">
/// Вимкнена прив'язка не бере участі в прогоні. Законна одразу: її заводять
/// наперед, поки методологію ще правлять.
/// </param>
public sealed record SaveCalculationBindingRequest(string MatchJson, bool IsActive);

/// <summary>Запит на симуляцію.</summary>
/// <param name="MethodologyVersionId">Версія, яку проганяємо.</param>
/// <param name="PeriodKey">Період, на даних якого проганяємо.</param>
public sealed record SimulateMethodologyRequest(int MethodologyVersionId, int PeriodKey);
