using System.Text.Json;
using Ecr.Api.Middleware;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Localization;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Errors;
using Microsoft.AspNetCore.Mvc;

namespace Ecr.Api.Errors;

/// <summary>
/// Перетворює винятки на <see cref="EcrProblemDetails"/>.
/// </summary>
/// <remarks>
/// Клієнт має розрізняти причини **за кодом**, а не парсити текст: саме тому
/// код стабільний, а повідомлення локалізоване і може змінюватися.
/// </remarks>
public sealed partial class ExceptionHandlingMiddleware(
    RequestDelegate next, ILogger<ExceptionHandlingMiddleware> logger)
{
    private const string ProblemJson = "application/problem+json";

    /// <summary>Налаштування серіалізації, спільні на весь застосунок.</summary>
    /// <remarks>
    /// <see cref="JsonSerializerOptions"/> кешує метадані типів усередині
    /// себе. Створювати його на кожну помилку означає щоразу будувати цей кеш
    /// наново — а помилки трапляються саме тоді, коли система під навантаженням.
    /// </remarks>
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    [LoggerMessage(Level = LogLevel.Error, Message = "Необроблений виняток. CorrelationId={CorrelationId}")]
    private partial void LogUnhandled(Exception exception, string correlationId);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Запит відхилено: {Code} ({Status}). CorrelationId={CorrelationId}")]
    private partial void LogRejected(string code, int status, string correlationId);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Відповідь уже почалася, ProblemDetails не надіслано. CorrelationId={CorrelationId}")]
    private partial void LogTooLate(string correlationId);

    /// <summary>Обробляє запит.</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        try
        {
            await next(context).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // Клієнт відвалився. Тіла відповіді ніхто не прочитає, а новий код
            // помилки заради цього заводити не можна: кожен код каталогу
            // звіряється з `02-contracts.md` §7 в обидва боки, і код, якого
            // ніхто не побачить, лишився б там назавжди як мертвий рядок.
            context.Response.StatusCode = StatusCodes.Status499ClientClosedRequest;
        }
        catch (Exception ex)
        {
            await WriteAsync(context, ex).ConfigureAwait(false);
        }
    }

    private async Task WriteAsync(HttpContext context, Exception exception)
    {
        var correlationId = context.Items.TryGetValue(CorrelationIdMiddleware.ItemKey, out var raw)
            ? raw as string ?? string.Empty
            : string.Empty;

        var (status, code, message, details) = Map(exception);

        if (status >= StatusCodes.Status500InternalServerError)
        {
            // Стек іде В ЛОГ, і тільки туди. Клієнт отримує CorrelationId —
            // цього досить, щоб знайти цей самий запис.
            LogUnhandled(exception, correlationId);
        }
        else
        {
            LogRejected(code, status, correlationId);
        }

        if (context.Response.HasStarted)
        {
            // Відповідь уже пішла — переписати її неможливо. Мовчки це
            // проковтнути гірше, ніж лишити слід у журналі.
            LogTooLate(correlationId);
            return;
        }

        var problem = new EcrProblemDetails
        {
            Status = status,
            Title = await LocalizedTitleAsync(context, code).ConfigureAwait(false),
            Detail = await LocalizedDetailAsync(context, code, message, details).ConfigureAwait(false),
            Type = $"https://ecr.ncoc.kz/errors/{code}",
            Instance = context.Request.Path,
            ErrorCode = code,
            CorrelationId = correlationId,
            Extensions2 = details,
        };

        // Розширення дублюються в стандартний словник ProblemDetails: саме
        // його бачить клієнт у JSON, окреме поле Extensions2 потрібне лише
        // для типізованого доступу з коду.
        problem.Extensions["errorCode"] = code;
        problem.Extensions["correlationId"] = correlationId;
        if (details is not null)
        {
            foreach (var (key, value) in details)
            {
                problem.Extensions[key] = value;
            }
        }

        // ⛔ `Response.Clear()` стирає ВСІ заголовки, включно з `Set-Cookie`,
        // який міг лишити обробник ВИЩЕ по конвеєру перед тим, як кинути
        // виняток (наприклад, `SecurityStampMiddleware.SignOutAsync` при
        // відкликаній ролі). Без збереження цього заголовка клієнт носив би
        // мертву cookie до кінця її строку — і отримував 401 на КОЖЕН запит,
        // разом з публічними (`GET /ui-strings`) і навіть на сам `/logout`,
        // без жодного способу вийти з цього стану інакше, ніж вручну стерти
        // cookie в браузері.
        var setCookie = context.Response.Headers.SetCookie;

        context.Response.Clear();
        context.Response.StatusCode = status;
        context.Response.ContentType = ProblemJson;
        if (setCookie.Count > 0)
        {
            context.Response.Headers.SetCookie = setCookie;
        }

        await JsonSerializer.SerializeAsync(
            context.Response.Body,
            problem,
            SerializerOptions,
            context.RequestAborted).ConfigureAwait(false);
    }

    /// <summary>
    /// Заголовок відповіді: текст із каталогу за ключем <c>err.&lt;код&gt;</c>,
    /// інакше сам код (ФВ-14.9a, <c>D-111</c>).
    /// </summary>
    /// <remarks>
    /// ⛔ Механізм локалізації один, не два. ФВ-14.9a каже прямо: повідомлення
    /// каталогу помилок — це записи того самого <c>sys.UiString</c> із
    /// префіксом <c>err.</c>, і <b>резолвить їх сервер</b>. Обидві половини
    /// механізму вже існували — п'ять ключів <c>err.ECR-…</c> у seed і
    /// <c>UiStringResolver.ResolveError</c>, — і не була написана рівно одна
    /// сполучна ланка: ця. Тому ключі не читав НІХТО, а заголовком помилки
    /// їхав сам код: користувач бачив «ECR-AUTH-0423» замість «обліковий запис
    /// заблоковано», причому будь-якою мовою однаково.
    ///
    /// ⚠ Замінюється <c>Title</c>, а не <c>Detail</c>. <c>Detail</c> несе
    /// конкретику сервера («Проєкт 42 не знайдено») — саме її забороняє
    /// втратити <c>07-checkpoints</c> Етап 6 («щось пішло не так» заборонено).
    /// Каталог дає постійний текст на код, і разом вони читаються як заголовок
    /// плюс подробиця.
    ///
    /// ⚠ Ключа немає — повертається сам код, а не рядок «err.ECR-…»:
    /// <c>ResolveError</c> підставляє ключ, і показати його користувачеві було
    /// б гірше за код, який принаймні названий у контракті.
    ///
    /// ⛔ Будь-який збій каталогу ковтається. Це обробник ПОМИЛОК: якщо база
    /// недоступна (а 500 масово трапляються саме тоді), похід за перекладом
    /// кине вдруге — уже поза <c>try</c> конвеєра, і клієнт замість
    /// <c>problem+json</c> отримав би обірване з'єднання.
    /// </remarks>
    private static async Task<string> LocalizedTitleAsync(HttpContext context, string code)
    {
        try
        {
            var catalog = context.RequestServices.GetService<IUiStringCatalog>();
            var currentUser = context.RequestServices.GetService<ICurrentUser>();

            if (catalog is null || currentUser is null)
            {
                return code;
            }

            var strings = await catalog
                .GetAsync(currentUser.Language, context.RequestAborted)
                .ConfigureAwait(false);

            var text = UiStringResolver.ResolveError(strings, code);

            return string.Equals(text, UiStringResolver.ErrorKeyPrefix + code, StringComparison.Ordinal)
                ? code
                : text;
        }
#pragma warning disable CA1031 // Причина — у ⛔ вище: помилка в обробнику помилок не має права дійти до клієнта.
        catch (Exception)
#pragma warning restore CA1031
        {
            return code;
        }
    }

    /// <summary>Ключ каталогу для підпису «Потрібне право» перед кодом права.</summary>
    private const string RequiresPermissionKey = "err.ECR-AUTH-0403.requiresPermission";

    /// <summary>
    /// Клієнтська <c>Detail</c>: здебільшого — те саме `message`, яке вже
    /// написане людською мовою прямо в обробнику. Виняток — коди, де виняток
    /// несе СТРУКТУРОВАНУ подробицю замість готового речення (сьогодні лише
    /// <c>ECR-AUTH-0403</c>: код права з <c>PermissionCheck</c>, не текст) —
    /// для них речення будується тут із каталогу, тим самим механізмом, що й
    /// <see cref="LocalizedTitleAsync"/> для заголовка.
    /// </summary>
    /// <remarks>
    /// ⛔ Без цього подробиця `ECR-AUTH-0403` доїжджала клієнту сирим
    /// українським реченням незалежно від мови інтерфейсу користувача —
    /// текст, написаний розробником обробника для СЕРВЕРНОГО боку, а не для
    /// показу (виявлено реальним входом у застосунок під час аудиту, не
    /// прогоном тестів: `Title` уже читався каталогом за `D-95`, а `Detail`
    /// поруч — ні, і речення виходило двомовним).
    /// </remarks>
    private static async Task<string> LocalizedDetailAsync(
        HttpContext context, string code, string message, IReadOnlyDictionary<string, object?>? details)
    {
        if (!string.Equals(code, ErrorCodes.Forbidden, StringComparison.Ordinal)
            || details is null
            || !details.TryGetValue("permission", out var permissionValue)
            || permissionValue is not string permission)
        {
            return message;
        }

        try
        {
            var catalog = context.RequestServices.GetService<IUiStringCatalog>();
            var currentUser = context.RequestServices.GetService<ICurrentUser>();

            if (catalog is null || currentUser is null)
            {
                return message;
            }

            var strings = await catalog
                .GetAsync(currentUser.Language, context.RequestAborted)
                .ConfigureAwait(false);

            var label = UiStringResolver.Resolve(strings, RequiresPermissionKey);

            // Ключа немає в каталозі — краще сире (українське) речення, ніж
            // сам ключ, конкатенований із кодом права.
            return string.Equals(label, RequiresPermissionKey, StringComparison.Ordinal)
                ? message
                : $"{label} {permission}";
        }
#pragma warning disable CA1031 // Причина — та сама, що й у LocalizedTitleAsync: обробник помилок не падає вдруге.
        catch (Exception)
#pragma warning restore CA1031
        {
            return message;
        }
    }

    /// <summary>Виняток → код відповіді, код помилки, повідомлення, подробиці.</summary>
    /// <remarks>
    /// ⚠ Для 500 повідомлення **стале і беззмістовне** навмисно: текст
    /// винятку може містити імена об'єктів БД, фрагменти запитів, а в
    /// найгіршому разі — значення параметрів. Це поверхня для розвідки, і
    /// клієнту вона не потрібна (ФВ-6.11).
    /// </remarks>
    private static (int Status, string Code, string Message, IReadOnlyDictionary<string, object?>? Details) Map(
        Exception exception) => exception switch
    {
        NotFoundException e =>
            (StatusCodes.Status404NotFound, e.ErrorCode, e.Message, null),

        // 401 і 403 розрізняє КОД, а не тип винятку: «не увійшов» і «увійшов,
        // але не має права» — різні відповіді, і клієнт мусить їх розрізняти,
        // бо на першу він показує форму входу, а на другу — повідомлення.
        AccessDeniedException e when e.ErrorCode == ErrorCodes.Unauthorized =>
            (StatusCodes.Status401Unauthorized, e.ErrorCode, e.Message, e.Details),

        AccessDeniedException e =>
            (StatusCodes.Status403Forbidden, e.ErrorCode, e.Message, e.Details),

        ConcurrencyConflictException e =>
            (StatusCodes.Status409Conflict, e.ErrorCode, e.Message, e.Details),

        // ⚠ Каталог кодує HTTP у самому коді (`ECR-<ДОМЕН>-<HTTP><порядковий>`),
        // і чотири коди виходять за межі 422. Розбирати номер із рядка було б
        // спритно і крихко: `4223` — це 422, а `0503` — 503, і одна помилка в
        // правилі розбору тихо переназначила б статус усьому каталогу.
        BusinessRuleException e when e.ErrorCode == ErrorCodes.PasswordChangeRequired =>
            (StatusCodes.Status428PreconditionRequired, e.ErrorCode, e.Message, e.Details),

        BusinessRuleException e when e.ErrorCode == ErrorCodes.AccountLocked =>
            (StatusCodes.Status423Locked, e.ErrorCode, e.Message, e.Details),

        BusinessRuleException e when e.ErrorCode is ErrorCodes.Archiving or ErrorCodes.SourceUnavailable =>
            (StatusCodes.Status503ServiceUnavailable, e.ErrorCode, e.Message, e.Details),

        // ⛔ `ECR-ROW-0409` доїжджав клієнтові як 422: код називає конфлікт
        // ключа рядка (уже існує, вичерпано межу, таблиця не приймає нових
        // рядків), а загальний арм нижче віддавав його як помилку введення.
        // Клієнт, що читає HTTP-статус раніше за код (типовий шаблон обробки
        // помилок сітки), бачив «дані невірні» замість «спробуйте інший ключ».
        BusinessRuleException e when e.ErrorCode == ErrorCodes.RowDuplicate =>
            (StatusCodes.Status409Conflict, e.ErrorCode, e.Message, e.Details),

        // ⚠ Відмова джерела в автентифікації сьогодні доїжджає лише у фонову
        // задачу (збір ставиться в чергу, `202`), і до HTTP не доходить. Арм
        // усе одно є: без нього той самий виняток, кинутий із синхронного
        // шляху, дав би `500` з беззмістовним текстом — тобто найгіршу з
        // можливих відповідей саме там, де причина відома точно.
        //
        // ⛔ `502`, а не `503`, і цифри коду це повторюють (`ECR-INT-0502`).
        // 503 обіцяє «спробуйте пізніше» — а відмова в автентифікації від
        // повторення не минає. 401 сказав би клієнтові «увійдіть», хоча
        // не пускають не його, а нас.
        SourceAuthenticationException e =>
            (StatusCodes.Status502BadGateway, e.ErrorCode, e.Message, e.Details),

        // ⛔ Той самий клас розбіжності, що й `ECR-ROW-0409` вище (`D2-294`),
        // і та сама причина: цифри коду — це НАШ статус відповіді
        // (`ECR-<ДОМЕН>-<HTTP>`). `ECR-RPT-0409` каже 409 і доїжджав як 422
        // обома шляхами: зайнятий код опису звіту (`BusinessRuleException`) і
        // спроба опублікувати вже опубліковану версію (`DomainException` із
        // самої сутності). Клієнт, що читає статус раніше за код, показував
        // «дані невірні» там, де правильна відповідь — «цей звіт уже є» і
        // «цю версію вже опубліковано».
        BusinessRuleException e when e.ErrorCode == ErrorCodes.ReportDefDuplicate =>
            (StatusCodes.Status409Conflict, e.ErrorCode, e.Message, e.Details),

        // ⛔ Той самий клас розбіжності, що й `ECR-RPT-4091` вище: цифри коду
        // означають наш HTTP-статус, і «цей довідник уже є» — конфлікт, а не
        // помилка введення. Клієнт, що читає статус раніше за код, показав би
        // «дані невірні» там, де правильна відповідь — «код уже зайнято».
        BusinessRuleException e when e.ErrorCode == ErrorCodes.RegistryDefDuplicate =>
            (StatusCodes.Status409Conflict, e.ErrorCode, e.Message, e.Details),

        // Той самий клас, що й `ECR-RPT-4091` вище: код політики періодів —
        // адреса вибору у формі створення проєкту (T6/#37), і дублікат має
        // читатися як «цей код зайнятий», а не як помилка введення.
        BusinessRuleException e when e.ErrorCode == ErrorCodes.PeriodPolicyDuplicate =>
            (StatusCodes.Status409Conflict, e.ErrorCode, e.Message, e.Details),

        // ⚠ Той самий клас, що й `ECR-ROW-0409`/`ECR-RPT-0409` вище: задача не
        // в стані `Failed` — це конфлікт стану на дії, а не невірні дані
        // запиту (директива №11, T10 #40).
        BusinessRuleException e when e.ErrorCode == Ecr.Application.Integration.RestartJobHandler.NotFailedErrorCode =>
            (StatusCodes.Status409Conflict, e.ErrorCode, e.Message, e.Details),

        BusinessRuleException e =>
            (StatusCodes.Status422UnprocessableEntity, e.ErrorCode, e.Message, e.Details),

        DomainException e when e.ErrorCode == ErrorCodes.ReportImmutable =>
            (StatusCodes.Status409Conflict, e.ErrorCode, e.Message, null),

        DomainException e =>
            (StatusCodes.Status422UnprocessableEntity, e.ErrorCode, e.Message, null),

        _ => (StatusCodes.Status500InternalServerError, ErrorCodes.Internal,
              "Внутрішня помилка. Зверніться до адміністратора з ідентифікатором кореляції.", null),
    };
}
