using Ecr.Application.Common;

namespace Ecr.Api.Auth;

/// <summary>
/// Реалізація <see cref="ICurrentUser"/> поверх <c>HttpContext</c>.
/// </summary>
/// <remarks>
/// Нижче рівня входу не видно, **як** користувач увійшов — доменний він чи
/// локальний (ФВ-6.2). Обидва провайдери дають одну cookie і один
/// <see cref="ICurrentUser"/>, тому use-cases не мають жодного розгалуження
/// за способом автентифікації.
/// </remarks>
public sealed class CurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    /// <inheritdoc />
    public int? UserId
        => throw new NotImplementedException(
            "TODO: узяти claim із cookie-принципала; анонімний запит → null. " +
            "SID доменного користувача НЕ підставляти — в аудиті має бути наш UserId (ФВ-6.2).");

    /// <inheritdoc />
    public string? UserName
        => throw new NotImplementedException(
            "TODO: claim ім'я користувача; використовується в аудиті і повідомленнях.");

    /// <inheritdoc />
    public string CorrelationId
        => throw new NotImplementedException(
            "TODO: узяти з HttpContext.Items, куди його кладе CorrelationIdMiddleware; " +
            "якщо його там немає — це дефект конвеєра, а не привід згенерувати новий.");

    /// <inheritdoc />
    public string Language
        => throw new NotImplementedException(
            "TODO: мова з профілю користувача, далі Accept-Language, далі мова за замовчуванням. " +
            "Тексти беруться з IUiStringCatalog, а не хардкодом (D-95).");
}
