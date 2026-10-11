using System.Globalization;
using System.Security.Claims;
using Ecr.Api.Auth;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

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
    /// <param name="simulation">Сервіс сеансів симуляції (Scoped, тож параметром, а не конструктором).</param>
    /// <remarks>
    /// ⛔ S1-03 (аудит 5): сеанс закрито НА СЕРВЕРІ (завершено з іншого клієнта того самого актора), а cookie з
    /// заявкою <c>ecr:sim</c> лишилась. Профіль для такого сеансу вже звичайний
    /// (<c>SimulationAwareAccessDecisionService</c>), банера немає, а тут кожен запис давав <c>403 ECR-SIM-0403</c> —
    /// користувач без видимої причини не міг нічого змінити до виходу. Тепер «лише читання» діє, поки сеанс
    /// ВІДКРИТИЙ на сервері; закритий — заявку знімаємо з cookie й запит іде як звичайний. Перевірка — лише для
    /// небезпечних методів під заявкою (симуляція рідкісна), відмова бази не відкриває запис (виняток іде вгору).
    /// </remarks>
    public async Task InvokeAsync(HttpContext context, ISimulationService simulation)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(simulation);

        if (context.User is { Identity.IsAuthenticated: true }
            && context.User.HasClaim(c => c.Type == AuthenticationSetup.SimulationSessionClaim)
            && !IsSafe(context.Request.Method)
            && !Allowed.Contains(context.Request.Path.Value ?? string.Empty, StringComparer.OrdinalIgnoreCase))
        {
            if (await IsSessionOpenAsync(context, simulation).ConfigureAwait(false))
            {
                throw new AccessDeniedException(
                    "ECR-SIM-0403",
                    "Сеанс симуляції — лише читання: змінювати дані не можна.",
                    new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["messageKey"] = "err.ECR-SIM-0403.readOnly",
                    });
            }

            await DropStaleClaimAsync(context).ConfigureAwait(false);
        }

        await next(context).ConfigureAwait(false);
    }

    /// <summary>Чи сеанс із заявки ще відкритий на сервері. Нерозбірна заявка — «відкритий» (відмова безпечна).</summary>
    private static async Task<bool> IsSessionOpenAsync(
        HttpContext context, ISimulationService simulation)
    {
        var raw = context.User.FindFirst(AuthenticationSetup.SimulationSessionClaim)?.Value;
        if (!long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var sessionId))
        {
            return true;
        }

        return await simulation.GetActorAsync(sessionId, context.RequestAborted).ConfigureAwait(false) is not null;
    }

    /// <summary>Перевидає cookie без заявки закритого сеансу й знімає її з принципала цього запиту.</summary>
    private static async Task DropStaleClaimAsync(HttpContext context)
    {
        var claims = context.User.Claims
            .Where(c => c.Type != AuthenticationSetup.SimulationSessionClaim)
            .Select(c => new Claim(c.Type, c.Value))
            .ToList();

        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            claims, CookieAuthenticationDefaults.AuthenticationScheme));

        await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal)
            .ConfigureAwait(false);

        context.User = principal;
    }

    private static bool IsSafe(string method)
        => HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method);
}
