// src/Ecr.Application/Permits/AirLimits/AirTypeSystemProvisioner.cs
using Ecr.Application.Common;
using Ecr.Application.Registries.Nae;

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
public sealed class AirTypeSystemProvisioner(
    ProvisionAirLimitsHandler provision,
    NaeTreeProvisioner naeTree,
    AirLimitsSettings settings,
    JobActorScope actorScope)
{
    /// <summary>
    /// ✎ AIRSYS02 (live-copy check 02.11: live has no NAE tree → the AIR prerequisite refused on every start): the NAE tree registry
    /// the AIR tables point at (<see cref="AirLimitsSettings.NaeRegistryCode"/>) is created EMPTY as the system when missing —
    /// definition, fields, business key and the five levels, no entries. An existing registry is left untouched. Runs BEFORE
    /// <see cref="RunAsync"/>.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The registry code and whether this call created it.</returns>
    public async Task<(string RegistryCode, bool Created)> EnsureNaeTreeAsync(CancellationToken ct)
    {
        var actor = PermitTypeActor.System();
        using var _ = actorScope.Enter(new JobActor(PermitTypeActor.SystemUserId, "system", "en", [], actor.CorrelationId));
        var created = await naeTree.EnsureAsSystemAsync(settings.NaeRegistryCode, actor, ct).ConfigureAwait(false);
        return (settings.NaeRegistryCode, created);
    }

    /// <summary>Provisions as the system.</summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task<AirLimitsProvisionReport> RunAsync(CancellationToken ct)
    {
        var actor = PermitTypeActor.System();
        using var _ = actorScope.Enter(new JobActor(PermitTypeActor.SystemUserId, "system", "en", [], actor.CorrelationId));
        return await provision.ProvisionAsync(actor, ct).ConfigureAwait(false);
    }
}
