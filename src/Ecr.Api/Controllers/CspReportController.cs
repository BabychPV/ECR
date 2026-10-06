using Ecr.Api.Observability;
using Ecr.Api.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace Ecr.Api.Controllers;

/// <summary>Приймач звітів про порушення Content-Security-Policy (<c>S14</c>).</summary>
/// <remarks>
/// ⛔ Споживач цього ендпоінта — БРАУЗЕР, а не наш клієнт: адресу він бере із
/// заголовка політики (<c>report-uri</c>/<c>report-to</c>,
/// <see cref="SecurityHeadersMiddleware"/>). Тому в <c>src/Ecr.Web</c> її немає, а
/// сторож «кожна дія має споживача» тримає її в переліку виключень
/// (<c>ServerOnlyActions</c>) із цим самим поясненням.
///
/// ⛔ Ендпоінт АНОНІМНИЙ (браузер шле звіт без інтерактивної автентифікації,
/// а політика діє й на сторінці входу) і тому навмисно дешевий та безпечний:
/// <list type="bullet">
/// <item>тіло ≤ <see cref="MaxBodyBytes"/> — перевіряється і за
/// <c>Content-Length</c>, і за фактично прочитаним (chunked його не має);</item>
/// <item>у базу НЕ пише: лише рядок журналу й лічильник;</item>
/// <item>у журнал іде обрізане й очищене (див. <see cref="CspReportParser"/>) —
/// адреси без query/fragment, без персональних даних, без IP;</item>
/// <item>обмеження частоти за адресою — глобальний обмежувач
/// (<c>LoginRateLimiting</c>, ключ <c>Security:RateLimit:CspReportPermitPerMinute</c>);
/// відхилений запит отримує голе <c>429</c> без звернення до бази.</item>
/// </list>
///
/// ⚠ Тіла відповідей порожні навмисно: браузер їх не читає, а <c>problem+json</c>
/// із каталогу рядків означав би похід у базу на кожен анонімний звіт.
/// </remarks>
[ApiController]
[Route("api/v1/csp-report")]
public sealed partial class CspReportController(
    ILogger<CspReportController> logger,
    EcrMetrics metrics) : ControllerBase
{
    /// <summary>Шлях ендпоінта — для обмежувача частоти.</summary>
    /// <remarks>⚠ Дорівнює <c>[Route]</c> вище; це доводить тест, а не збіг у тексті.</remarks>
    public const string RoutePath = "/api/v1/csp-report";

    /// <summary>Найбільше тіло, яке приймається, байт.</summary>
    public const int MaxBodyBytes = 8 * 1024;

    /// <summary>Типи вмісту, які шлють браузери (і <c>application/json</c> — власні перевірки).</summary>
    private static readonly string[] AllowedContentTypes =
    [
        "application/csp-report",
        "application/reports+json",
        "application/json",
    ];

    /// <summary>Приймає звіт (чи масив звітів Reporting API).</summary>
    /// <param name="ct">Токен скасування.</param>
    [HttpPost]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(void), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(void), StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType(typeof(void), StatusCodes.Status415UnsupportedMediaType)]
    [ProducesResponseType(typeof(void), StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Report(CancellationToken ct)
    {
        if (!IsAllowedContentType(Request.ContentType))
        {
            return StatusCode(StatusCodes.Status415UnsupportedMediaType);
        }

        // ⚠ Дешева відмова до читання, коли розмір оголошено заздалегідь.
        if (Request.ContentLength is > MaxBodyBytes)
        {
            return StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        // ⚠ +1 байт: так відрізняється «рівно межа» від «більше межі» без
        // читання всього тіла (chunked не має Content-Length).
        var buffer = new byte[MaxBodyBytes + 1];
        var total = 0;

        try
        {
            while (total < buffer.Length)
            {
                var read = await Request.Body.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                total += read;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // A3-03: браузер закрив запит посеред тіла. Це не збій сервера — без цього
            // перехоплення виняток доходив до ExceptionHandlingMiddleware і потрапляв
            // у журнал як «Необроблений виняток» рівня Error. Відповідь ніхто не читає.
            return StatusCode(StatusCodes.Status499ClientClosedRequest);
        }
        catch (BadHttpRequestException)
        {
            // A3-03: «Unexpected end of request content» — обірване/некоректне тіло від
            // клієнта (ми не вичитали його до кінця), це 400, а не 500.
            return StatusCode(StatusCodes.Status400BadRequest);
        }

        if (total > MaxBodyBytes)
        {
            return StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        if (!CspReportParser.TryParse(buffer.AsMemory(0, total), out var violations))
        {
            return StatusCode(StatusCodes.Status400BadRequest);
        }

        foreach (var violation in violations)
        {
            LogViolation(
                logger,
                violation.Directive,
                violation.ViolatedDirective,
                violation.BlockedUri,
                violation.DocumentUri,
                violation.SourceFile,
                violation.LineNumber);

            metrics.RecordCspViolation(violation.Directive);
        }

        return NoContent();
    }

    private static bool IsAllowedContentType(string? contentType)
        => MediaTypeHeaderValue.TryParse(contentType, out var parsed)
           && parsed.MediaType.HasValue
           && AllowedContentTypes.Contains(parsed.MediaType.Value, StringComparer.OrdinalIgnoreCase);

    /// <summary>Один рядок журналу на звіт; усі поля вже очищені й обрізані.</summary>
    /// <remarks>
    /// ⚠ Рівень Information, а не Warning: Warning+ потрапляє в журнал подій
    /// Windows, і хвилинна межа звітів засмітила б його. Ні IP, ні cookie, ні
    /// заголовків запиту тут немає навмисно.
    /// </remarks>
    [LoggerMessage(
        EventId = 6001,
        Level = LogLevel.Information,
        Message = "CSP violation: directive={Directive} violated={ViolatedDirective} blocked={BlockedUri} "
                  + "document={DocumentUri} source={SourceFile} line={LineNumber}")]
    private static partial void LogViolation(
        ILogger logger,
        string directive,
        string violatedDirective,
        string blockedUri,
        string documentUri,
        string sourceFile,
        int? lineNumber);
}
