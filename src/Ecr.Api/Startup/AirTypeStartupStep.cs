using Ecr.Application.Errors;
using Ecr.Application.Permits.AirLimits;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Api.Startup;

/// <summary>
/// ✎ AIR-ON-AIR (coordinator 02.11: «No runtime provision step for users»): on every start the AIR permit type gets its sources tables
/// (AIR_SOURCES, AIR_SOURCE_SUBSTANCES) and the Area registry AIR_AREA (Offshore / Onshore) — the same idempotent provisioning as
/// <c>POST /api/v1/air-limits/provision</c>, which stays as the manual fallback.
/// </summary>
/// <remarks>
/// ⛔ AIRSYS01 (coordinator 02.11): a deploy-time action like the seed, done by the SYSTEM (<see cref="AirTypeSystemProvisioner"/>):
/// no user permission check, the normal domain validation, the type version recorded with no user (NULL → «system») and the
/// journals with 0 «system». It is never attributed to a real person, and no API endpoint reaches this path.
/// ✎ AIRSYS02: a missing NAE tree registry is created empty by the system first (<see cref="AirTypeSystemProvisioner.EnsureNaeTreeAsync"/>).
/// ⚠ Never stops the start: any refusal (missing substance registry, an NAE tree that could not be created, an open draft of the type, a concurrent node) is a
/// warning, the application starts as before. Twice in a row → the second run changes nothing.
/// </remarks>
internal static partial class AirTypeStartupStep
{
    public static async Task RunAsync(IServiceProvider services, ILogger logger, CancellationToken ct)
    {
        // ✎ AIRSYS02 (live-copy check 02.11): live had no NAE tree, so the prerequisite below refused on every start. First the
        // NAE tree registry is created EMPTY as the system when missing (own scope and transaction; an existing one is untouched),
        // then the AIR provisioning in the same start.
        await using (var naeScope = services.CreateAsyncScope())
        {
            try
            {
                var (registryCode, created) = await naeScope.ServiceProvider.GetRequiredService<AirTypeSystemProvisioner>()
                    .EnsureNaeTreeAsync(ct).ConfigureAwait(false);
                if (created)
                {
                    LogNaeCreated(logger, registryCode);
                }
            }
            catch (Exception ex) when (ex is BusinessRuleException or ConcurrencyConflictException or NotFoundException
                                           or DbUpdateException or Microsoft.Data.SqlClient.SqlException or InvalidOperationException)
            {
                LogNaeSkipped(logger, ex.GetType().Name, ex.Message);
            }
        }

        await using var scope = services.CreateAsyncScope();
        try
        {
            var report = await scope.ServiceProvider.GetRequiredService<AirTypeSystemProvisioner>().RunAsync(ct).ConfigureAwait(false);
            LogDone(logger, report.TypeCode, report.Version, report.Published);
        }
        catch (Exception ex) when (ex is BusinessRuleException or ConcurrencyConflictException or NotFoundException
                                       or DbUpdateException or Microsoft.Data.SqlClient.SqlException or InvalidOperationException)
        {
            LogSkipped(logger, ex.GetType().Name, ex.Message);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "NAE tree registry {RegistryCode} created empty on start (as system): definition, fields, business key and levels, no entries.")]
    private static partial void LogNaeCreated(ILogger logger, string registryCode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "NAE tree registry on start skipped ({Reason}): {Detail}. The application starts as before.")]
    private static partial void LogNaeSkipped(ILogger logger, string reason, string detail);

    [LoggerMessage(Level = LogLevel.Information, Message = "AIR provisioning on start (as system): type {TypeCode} version {Version}, published {Published}.")]
    private static partial void LogDone(ILogger logger, string typeCode, int version, bool published);

    [LoggerMessage(Level = LogLevel.Warning, Message = "AIR provisioning on start skipped ({Reason}): {Detail}. The application starts as before; POST /api/v1/air-limits/provision is the fallback.")]
    private static partial void LogSkipped(ILogger logger, string reason, string detail);
}
