using System.Globalization;
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Ecr.Api.Health;

/// <summary>
/// Перевірка <c>tzdata</c>: чи знає база часових поясів цієї машини, що з
/// 2024-03-01 Казахстан на UTC+5 (F-4).
/// </summary>
/// <remarks>
/// ⛔ Лише <c>Degraded</c>, ніколи <c>Unhealthy</c>, і ніколи виняток: перевірка
/// має тег <c>ready</c>, а 503 вивів би з ротації сервер, який чудово приймає дані,
/// через те, що ОС не оновлювали. Це попередження оператору, а не відмова: межі
/// вже порахованих періодів не змінюються, а сервер із застарілою базою має
/// піднятися (див. <see cref="TimeZoneDatabaseStartup"/>).
///
/// ⚠ Що саме вважається застарілим — <see cref="KazakhstanTimeZoneReference"/>;
/// перевірка не читає БД: вона відповідає на питання про МАШИНУ, і збій бази не
/// повинен маскувати її відповідь.
/// </remarks>
public sealed class TimeZoneDatabaseHealthCheck(
    ITimeZoneOffsetProvider provider,
    IUiStringCatalog catalog,
    ICurrentUser currentUser) : IHealthCheck
{
    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<TimeZoneDrift> drift;

        try
        {
            drift = KazakhstanTimeZoneReference.FindDrift(provider);
        }
#pragma warning disable CA1031 // Збій самої перевірки — жовтий із причиною, а не виняток: інакше 503 (див. ⛔ вище).
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            var unavailable = await Text(
                "health.tzdata.unavailable", "The time zone database could not be checked.", null, cancellationToken)
                .ConfigureAwait(false);
            return HealthCheckResult.Degraded(unavailable, ex);
        }

        var data = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["expectedOffset"] = KazakhstanTimeZoneReference.FormatOffset(KazakhstanTimeZoneReference.ExpectedOffset),
            ["checkedZones"] = string.Join(", ", KazakhstanTimeZoneReference.Zones),
        };

        if (drift.Count == 0)
        {
            var ok = await Text(
                "health.tzdata.ok",
                "The time zone database knows that Kazakhstan has been on UTC+5 since 2024-03-01.",
                null, cancellationToken).ConfigureAwait(false);
            return HealthCheckResult.Healthy(ok, data);
        }

        var zones = string.Join(", ", drift.Select(d => d.Describe()));
        data["staleZones"] = zones;

        var stale = await Text(
            "health.tzdata.stale",
            "The operating system time zone database is out of date: {zones} (expected "
            + "+05:00 since 2024-03-01). Period boundaries and late-edit marks of projects in these zones "
            + "are shifted. Update tzdata (Linux) or install the Windows time zone update; "
            + "nothing is blocked and stored data is not changed.",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["zones"] = zones },
            cancellationToken).ConfigureAwait(false);
        return HealthCheckResult.Degraded(stale, data: data);
    }

    private Task<string> Text(
        string key, string fallback, IReadOnlyDictionary<string, string>? parameters, CancellationToken ct)
        => HealthCatalogText.ResolveAsync(catalog, currentUser, key, fallback, parameters, ct);
}
