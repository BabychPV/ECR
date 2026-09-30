using Ecr.Api.Auth;
using Ecr.Application.Errors;

namespace Ecr.Api.Middleware;

/// <summary>
/// Сеанс симуляції «очима користувача» — <b>лише читання</b> (<c>ФВ-6.16a</c>,
/// <c>D-96</c>, V-06).
/// </summary>
/// <remarks>
/// ⛔ Одна точка на ВЕСЬ API, а не перевірка в кожному обробнику запису.
/// <c>EditRules</c> відхиляє запис комірок (<c>SimulationReadOnly</c>), але
/// обробників запису — сотні, і більшість питає лише функціональне право:
/// права суб'єкта з <c>Manage</c> дозволили б адміністраторові, що «лише
/// дивиться», змінювати дані від чужого імені. Тут кожен небезпечний метод
/// відхиляється ДО обробника.
///
/// ⚠ Дозволено рівно те, без чого з сеансу не вийти: завершити його, вийти з
/// системи, увійти наново (новий вхід видає cookie без сеансу).
///
/// ⚠ Ціна названа: POST-ендпоінти, що лише читають (перевірка виразу,
/// попередній перегляд), під симуляцією теж відхиляються. Це свідомо: список
/// «безпечних POST» довелося б тримати в синхроні з кожним новим ендпоінтом, і
/// забутий запис став би дірою саме в режимі, де запис заборонено.
/// </remarks>
public sealed class SimulationReadOnlyMiddleware(RequestDelegate next)
{
    /// <summary>Шляхи, дозволені під симуляцією для будь-якого методу.</summary>
    private static readonly string[] Allowed =
    [
        "/api/v1/security/simulation",
        "/api/v1/logout",
        "/api/v1/login/local",
        "/api/v1/login/windows",
    ];

    /// <summary>Перевіряє запит.</summary>
    /// <param name="context">Контекст запиту.</param>
    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.User is { Identity.IsAuthenticated: true }
            && context.User.HasClaim(c => c.Type == AuthenticationSetup.SimulationSessionClaim)
            && !IsSafe(context.Request.Method)
            && !Allowed.Contains(context.Request.Path.Value ?? string.Empty, StringComparer.OrdinalIgnoreCase))
        {
            throw new AccessDeniedException(
                "ECR-SIM-0403",
                "Сеанс симуляції — лише читання: змінювати дані не можна.",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["messageKey"] = "err.ECR-SIM-0403.readOnly",
                });
        }

        return next(context);
    }

    private static bool IsSafe(string method)
        => HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method);
}
