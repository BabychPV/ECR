using Ecr.Application.Errors;
using Ecr.Domain.Abstractions;

namespace Ecr.Api.Errors;

/// <summary>
/// Перетворює винятки на <see cref="EcrProblemDetails"/>.
/// </summary>
/// <remarks>
/// Клієнт має розрізняти причини **за кодом**, а не парсити текст: саме тому
/// код стабільний, а повідомлення локалізоване і може змінюватися.
/// </remarks>
public sealed class ExceptionHandlingMiddleware(RequestDelegate next, ILogger<ExceptionHandlingMiddleware> logger)
{
    /// <summary>Обробляє запит.</summary>
    public Task InvokeAsync(HttpContext context)
        => throw new NotImplementedException(
            "TODO: try/catch навколо next(context); мапінг:\n" +
            "  NotFoundException            → 404\n" +
            "  AccessDeniedException        → 403, у Extensions2.reason — EditDenyReason\n" +
            "  ConcurrencyConflictException → 409, у Extensions2.conflicts — перелік CellConflictDto\n" +
            "  BusinessRuleException        → 422, у Extensions2 — деталі правила\n" +
            "  DomainException              → 422 з ErrorCode\n" +
            "  решта                        → 500 ECR-SYS-0500\n" +
            "⚠ У відповідь на 500 НЕ включати текст винятку і стек: у логи — так, клієнту — " +
            "лише CorrelationId. Пароль і секрети не логуються ніколи (ФВ-6.11) — " +
            "це перевіряється окремим тестом.");
}
