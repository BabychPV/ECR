namespace Ecr.Api.Middleware;

/// <summary>
/// Наскрізний ідентифікатор запиту. Потрапляє в логи, метрики, аудит і тіло
/// помилки — без нього звірити скаргу користувача з логом неможливо.
/// </summary>
public sealed class CorrelationIdMiddleware(RequestDelegate next)
{
    /// <summary>Заголовок кореляції.</summary>
    public const string HeaderName = "X-Correlation-Id";

    /// <summary>Обробляє запит.</summary>
    public Task InvokeAsync(HttpContext context)
        => throw new NotImplementedException(
            "TODO: узяти X-Correlation-Id з запиту або згенерувати; покласти в HttpContext.Items " +
            "і в logging scope; повернути в заголовку відповіді.");
}
