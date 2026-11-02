// src/Ecr.Application/Permits/AirLimits/AirTypeSystemProvisioner.cs
using Ecr.Application.Common;

namespace Ecr.Application.Permits.AirLimits;

/// <summary>
/// ✎ AIRSYS01 (coordinator 02.11): provisioning of the AIR type's sources tables and the Area registry as the SYSTEM — a deploy-time
/// action like the seed. No user permission check; the same domain validation, history and audit, with the author recorded as
/// system (NULL in <c>cfg.PermitTypeDef</c>, 0 «system» in the journals).
/// </summary>
/// <remarks>
/// ⛔ Only the startup step (<c>Ecr.Api.Startup.AirTypeStartupStep</c>) calls this. No controller may take it (guard —
/// <c>AirTypeStartupTests.No_controller_reaches_the_system_provisioner</c>); the HTTP fallback is
/// <c>POST /api/v1/air-limits/provision</c>, which keeps <c>Permit.EditType</c> + <c>Permit.PublishType</c>.
/// ⚠ The scope's actor is the system (Id 0, the job convention of <c>PeriodStateJob.SystemUserId</c>): code below that reads
/// <see cref="ICurrentUser"/> (correlation, registry history) has no HTTP request at start and would otherwise throw.
/// </remarks>
public sealed class AirTypeSystemProvisioner(ProvisionAirLimitsHandler provision, JobActorScope actorScope)
{
    /// <summary>Provisions as the system.</summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task<AirLimitsProvisionReport> RunAsync(CancellationToken ct)
    {
        var actor = PermitTypeActor.System();
        using var _ = actorScope.Enter(new JobActor(PermitTypeActor.SystemUserId, "system", "en", [], actor.CorrelationId));
        return await provision.ProvisionAsync(actor, ct).ConfigureAwait(false);
    }
}
