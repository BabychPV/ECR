namespace Ecr.Api.Middleware;

/// <summary>
/// Наскрізний ідентифікатор запиту. Потрапляє в логи, метрики, аудит і тіло
/// помилки — без нього звірити скаргу користувача з логом неможливо.
/// </summary>
public sealed class CorrelationIdMiddleware(RequestDelegate next)
{
    /// <summary>Заголовок кореляції.</summary>
    public const string HeaderName = "X-Correlation-Id";

    /// <summary>Ключ у <see cref="HttpContext.Items"/>.</summary>
    public const string ItemKey = "Ecr.CorrelationId";

    /// <summary>Обробляє запит.</summary>
    public async Task InvokeAsync(HttpContext context, ILogger<CorrelationIdMiddleware> logger)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(logger);

        // Ідентифікатор із запиту, якщо він є: коли скарга приходить від
        // клієнта, який уже щось логував у себе, склеїти два журнали можна
        // лише за спільним значенням.
        var incoming = context.Request.Headers[HeaderName].ToString();
        var correlationId = string.IsNullOrWhiteSpace(incoming)
            ? Guid.NewGuid().ToString("N")
            : Sanitize(incoming);

        context.Items[ItemKey] = correlationId;

        // Заголовок ставиться ДО виконання конвеєра, через OnStarting: якщо
        // писати його після next(), відповідь уже може бути надіслана, і
        // заголовок мовчки не потрапить у неї — саме в помилкових сценаріях,
        // де він найпотрібніший.
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = correlationId;
            return Task.CompletedTask;
        });

        using (logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId }))
        {
            await next(context).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Прибирає з чужого значення все, що не можна класти в заголовок і в лог.
    /// </summary>
    /// <remarks>
    /// Значення приходить ззовні, а потрапляє і у відповідь, і в журнал.
    /// Перенесення рядка в ньому дозволило б підробити рядок логу, тому
    /// лишаємо тільки безпечні символи й обмежуємо довжину.
    /// </remarks>
    private static string Sanitize(string value)
    {
        var clean = new string([.. value.Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')]);
        return clean.Length == 0 ? Guid.NewGuid().ToString("N")
             : clean.Length > 64 ? clean[..64]
             : clean;
    }
}
