namespace Ecr.Api.Auth;

/// <summary>
/// Перевіряє <c>SecurityStamp</c> на кожен запит.
/// </summary>
/// <remarks>
/// Сенс саме в негайності: відкликана роль має перестати діяти **до**
/// завершення сесії, а не після закінчення cookie (тест безпеки №2).
/// </remarks>
public sealed class SecurityStampMiddleware(RequestDelegate next)
{
    /// <summary>Обробляє запит.</summary>
    public Task InvokeAsync(HttpContext context, Ecr.Infrastructure.Security.SecurityStampValidator validator)
        => throw new NotImplementedException(
            "TODO: якщо користувач автентифікований — узяти userId і stamp із claims, " +
            "звірити через validator; розбіжність → SignOut і 401 з ECR-AUTH-0401. " +
            "Анонімні запити пропускати без перевірки.");
}
