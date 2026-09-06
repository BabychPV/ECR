using System.Text.Json;
using Ecr.Api.Middleware;
using Ecr.Application.Errors;
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
            Title = code,
            Detail = message,
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

        context.Response.Clear();
        context.Response.StatusCode = status;
        context.Response.ContentType = ProblemJson;

        await JsonSerializer.SerializeAsync(
            context.Response.Body,
            problem,
            SerializerOptions,
            context.RequestAborted).ConfigureAwait(false);
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
        // і три коди виходять за межі 422. Розбирати номер із рядка було б
        // спритно і крихко: `4223` — це 422, а `0503` — 503, і одна помилка в
        // правилі розбору тихо переназначила б статус усьому каталогу.
        BusinessRuleException e when e.ErrorCode == ErrorCodes.PasswordChangeRequired =>
            (StatusCodes.Status428PreconditionRequired, e.ErrorCode, e.Message, e.Details),

        BusinessRuleException e when e.ErrorCode == ErrorCodes.AccountLocked =>
            (StatusCodes.Status423Locked, e.ErrorCode, e.Message, e.Details),

        BusinessRuleException e when e.ErrorCode is ErrorCodes.Archiving or ErrorCodes.SourceUnavailable =>
            (StatusCodes.Status503ServiceUnavailable, e.ErrorCode, e.Message, e.Details),

        BusinessRuleException e =>
            (StatusCodes.Status422UnprocessableEntity, e.ErrorCode, e.Message, e.Details),

        DomainException e =>
            (StatusCodes.Status422UnprocessableEntity, e.ErrorCode, e.Message, null),

        _ => (StatusCodes.Status500InternalServerError, ErrorCodes.Internal,
              "Внутрішня помилка. Зверніться до адміністратора з ідентифікатором кореляції.", null),
    };
}
