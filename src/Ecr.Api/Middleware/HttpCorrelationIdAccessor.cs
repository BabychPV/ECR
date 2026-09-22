using Ecr.Application.Ports;

namespace Ecr.Api.Middleware;

/// <summary>
/// Кореляція з <see cref="HttpContext.Items"/>, куди її кладе
/// <see cref="CorrelationIdMiddleware"/>; поза запитом — <c>null</c>.
/// </summary>
public sealed class HttpCorrelationIdAccessor(IHttpContextAccessor accessor) : ICorrelationIdAccessor
{
    /// <inheritdoc />
    public string? CorrelationId
        => accessor.HttpContext?.Items.TryGetValue(CorrelationIdMiddleware.ItemKey, out var raw) == true
           && raw is string id
            ? id
            : null;
}
