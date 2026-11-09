using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Ecr.Api.Errors;

/// <summary>
/// ✎ debt-1109 x3: PUT/POST без тіла (і без <c>Content-Type</c>) чи з чужим типом вмісту MVC відхиляв ще до дії —
/// <see cref="UnsupportedMediaTypeResult"/>, який <c>[ApiController]</c> перетворював на типовий
/// <c>ProblemDetails</c> 415 без <c>errorCode</c> і <c>messageKey</c>. Клієнт (<c>problemOf</c>) розрізняє відмови лише
/// за <c>errorCode</c>, тож це читалося як «HTTP 415» і нічого більше.
/// </summary>
/// <remarks>
/// ⚠ Одна точка для всіх контролерів, а не правка кожного ендпоінта. Кидок, а не зібраний результат — як і
/// <c>InvalidModelStateResponseFactory</c> у <c>Program.cs</c>: виняток доїжджає до
/// <see cref="ExceptionHandlingMiddleware"/> і отримує ту саму форму й локалізацію (415, наявний
/// <c>ECR-REQ-0422</c>, <c>err.ECR-REQ-0422.malformedRequest</c>).
/// ⚠ Лише <see cref="UnsupportedMediaTypeResult"/>: власний <c>StatusCode(415)</c> контролера (звіти CSP) не чіпається.
/// ⚠ <see cref="IAlwaysRunResultFilter"/> з порядком раніше за <c>ClientErrorResultFilter</c> (-2000): так він бачить і
/// відмову <c>UnsupportedContentTypeFilter</c> (дія), і <c>[Consumes]</c> (ресурсний фільтр).
/// </remarks>
internal sealed class UnsupportedMediaTypeRefusalFilter : IAlwaysRunResultFilter, IOrderedFilter
{
    /// <inheritdoc />
    public int Order => -3000;

    /// <inheritdoc />
    public void OnResultExecuting(ResultExecutingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // ⚠ Шаблон властивості, а не `context` + `.Result`: сторож LayerRulesTests (правило 5) шукає блокувальні виклики
        // за текстом.
        if (context is { Result: UnsupportedMediaTypeResult })
        {
            throw new BadHttpRequestException(
                "Request body is missing or is not JSON.", StatusCodes.Status415UnsupportedMediaType);
        }
    }

    /// <inheritdoc />
    public void OnResultExecuted(ResultExecutedContext context)
    {
    }
}
