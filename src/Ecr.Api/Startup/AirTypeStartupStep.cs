using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Permits;
using Ecr.Application.Permits.AirLimits;
using Ecr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Api.Startup;

/// <summary>
/// ✎ AIR-ON-AIR (coordinator 02.11: «No runtime provision step for users»): on every start the AIR permit type gets its sources tables
/// (AIR_SOURCES, AIR_SOURCE_SUBSTANCES) and the Area registry AIR_AREA (Offshore / Onshore) — the same idempotent provisioning as
/// <c>POST /api/v1/air-limits/provision</c>, which stays as the manual fallback.
/// </summary>
/// <remarks>
/// ⛔ Authority: a type is published only by a user who may do it. The step acts as the first active user (lowest id) holding BOTH
/// <c>Permit.EditType</c> and <c>Permit.PublishType</c> through an unscoped role assignment, and goes through the very same permission
/// checks, history and audit as the endpoint — no privileged bypass. No such user yet (a fresh database before the bootstrap admin) →
/// skipped with a log line; the next start (or the endpoint) does it.
/// ⚠ Never stops the start: any refusal (missing NAE / substance registry, an open draft of the type, a concurrent node) is a
/// warning, the application starts as before. Twice in a row → the second run changes nothing (the type already has both tables).
/// </remarks>
internal static partial class AirTypeStartupStep
{
    public static async Task RunAsync(IServiceProvider services, ILogger logger, CancellationToken ct)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EcrDbContext>();

        var actor = await (
                from u in db.Users.AsNoTracking()
                where u.IsActive
                      && db.RoleAssignments.Any(a => a.UserId == u.Id && a.ScopeJson == null
                                                     && db.RolePermissions.Any(p => p.RoleId == a.RoleId && p.PermissionCode == CreatePermitTypeHandler.Permission))
                      && db.RoleAssignments.Any(a => a.UserId == u.Id && a.ScopeJson == null
                                                     && db.RolePermissions.Any(p => p.RoleId == a.RoleId && p.PermissionCode == PublishPermitTypeHandler.Permission))
                orderby u.Id
                select new { u.Id, u.UserName })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (actor is null)
        {
            LogNoPublisher(logger);
            return;
        }

        using var _ = scope.ServiceProvider.GetRequiredService<JobActorScope>()
            .Enter(new JobActor(actor.Id, actor.UserName, "en", [], Guid.NewGuid().ToString("N")));
        try
        {
            var report = await scope.ServiceProvider.GetRequiredService<ProvisionAirLimitsHandler>()
                .HandleAsync(ct).ConfigureAwait(false);
            LogDone(logger, report.TypeCode, report.Version, report.Published, actor.UserName);
        }
        catch (Exception ex) when (ex is BusinessRuleException or ConcurrencyConflictException or AccessDeniedException or NotFoundException
                                       or DbUpdateException or Microsoft.Data.SqlClient.SqlException)
        {
            LogSkipped(logger, ex.GetType().Name, ex.Message);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "AIR provisioning on start skipped: no active user holds Permit.EditType and Permit.PublishType (unscoped). It runs on the next start or through POST /api/v1/air-limits/provision.")]
    private static partial void LogNoPublisher(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "AIR provisioning on start: type {TypeCode} version {Version}, published {Published}, as {UserName}.")]
    private static partial void LogDone(ILogger logger, string typeCode, int version, bool published, string? userName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "AIR provisioning on start skipped ({Reason}): {Detail}. The application starts as before; POST /api/v1/air-limits/provision is the fallback.")]
    private static partial void LogSkipped(ILogger logger, string reason, string detail);
}
