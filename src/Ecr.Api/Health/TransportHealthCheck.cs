using System.Globalization;
using Ecr.Api.Startup;
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Ecr.Api.Health;

/// <summary>
/// Перевірка <c>transport</c>: чи не працює Production по HTTP із cookie сеансу
/// без <c>Secure</c> і чи не спливає сертифікат HTTPS.
/// </summary>
/// <remarks>
/// ⛔ Предмет (<c>D14-08</c>). Вибір «HTTP» на майданчику — рішення адміністратора
/// (<c>deploy-ecr.ps1 -AllowHttp</c>, стенд), але воно не має стати невидимим
/// постійним: без цієї перевірки Production без <c>Secure</c>-cookie нічим не
/// відрізнявся б від нормального. Тепер його видно на <c>/admin/health</c> і в
/// <c>/health/ready</c> жовтим.
///
/// ⚠ Жовтий лише в Production: Development і тести (усі фікстури вимикають
/// <c>Auth:RequireHttps</c>) інакше світилися б жовтим ЗАВЖДИ, а моніторинг, що
/// світиться жовтим завжди, навчають ігнорувати (див. <see cref="JobsHealthCheck"/>).
///
/// ⚠ Що перевірка НЕ знає: чи стоїть перед Kestrel зворотний проксі, що
/// завершує TLS (<c>deploy-ecr.ps1 -BehindHttpsProxy</c>) — тоді
/// <c>Auth:RequireHttps</c> лишається <c>true</c>, і стан зелений: cookie
/// <c>Secure</c>, а браузер говорить із проксі по HTTPS.
///
/// ⛔ Лише <c>Degraded</c>, ніколи <c>Unhealthy</c>: перевірка має тег <c>ready</c>,
/// і 503 вивів би з балансувальника працюючий Api через транспорт.
/// </remarks>
public sealed class TransportHealthCheck(
    IConfiguration configuration,
    IHostEnvironment environment,
    TransportState state,
    IUiStringCatalog catalog,
    ICurrentUser currentUser) : IHealthCheck
{
    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        var requireHttps = configuration.GetValue("Auth:RequireHttps", defaultValue: true);
        var data = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["requireHttps"] = requireHttps,
            ["httpsCertificate"] = state.HttpsCertificateLoaded,
        };

        if (!requireHttps && environment.IsProduction())
        {
            var insecure = await Text(
                "health.transport.httpNoSecureCookie",
                "Transport: HTTP - the session cookie is not Secure (Auth:RequireHttps = false). "
                + "Use this only on a stand; on a production site enable HTTPS (install guide, section HTTPS).",
                cancellationToken).ConfigureAwait(false);
            return HealthCheckResult.Degraded(insecure, data: data);
        }

        if (!requireHttps)
        {
            var outsideProduction = await Text(
                "health.transport.notProduction",
                "Auth:RequireHttps = false outside Production (development or test): the session cookie is not Secure.",
                cancellationToken).ConfigureAwait(false);
            return HealthCheckResult.Healthy(outsideProduction, data);
        }

        if (state.CertificateNotAfter is { } notAfter)
        {
            var days = (int)Math.Floor((notAfter - TimeProvider.System.GetUtcNow()).TotalDays);
            data["certificateNotAfter"] = notAfter.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            data["certificateDaysLeft"] = days;

            var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["days"] = days.ToString(CultureInfo.InvariantCulture),
                ["date"] = (string)data["certificateNotAfter"],
            };

            if (days < 0)
            {
                var expired = await Text(
                    "health.transport.certificateExpired",
                    "The HTTPS certificate expired on {date}: browsers refuse to open the application. "
                    + "Install a renewed certificate and repeat deploy-ecr.ps1 with the new -HttpsThumbprint.",
                    cancellationToken, parameters).ConfigureAwait(false);
                return HealthCheckResult.Degraded(expired, data: data);
            }

            if (days < HttpsTransport.ExpiryWarningDays)
            {
                var soon = await Text(
                    "health.transport.certificateExpiresSoon",
                    "The HTTPS certificate expires in {days} day(s) ({date}). "
                    + "Install a renewed certificate and repeat deploy-ecr.ps1 with the new -HttpsThumbprint.",
                    cancellationToken, parameters).ConfigureAwait(false);
                return HealthCheckResult.Degraded(soon, data: data);
            }
        }

        var ok = await Text(
            "health.transport.secure",
            "The session cookie requires HTTPS (Auth:RequireHttps = true).",
            cancellationToken).ConfigureAwait(false);
        return HealthCheckResult.Healthy(ok, data);
    }

    private Task<string> Text(
        string key, string fallback, CancellationToken ct, IReadOnlyDictionary<string, string>? parameters = null)
        => HealthCatalogText.ResolveAsync(catalog, currentUser, key, fallback, parameters, ct);
}
