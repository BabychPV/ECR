// src/Ecr.Application/Integration/SourceEvents/SyncSourceEventsHandler.cs
using System.Globalization;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Application.Sources;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Errors;

namespace Ecr.Application.Integration.SourceEvents;

/// <summary>
/// «Отримати з PI зараз»: ставить синхронізацію подій сутності в чергу (HSE301 A6, §4.7.4). Право
/// <c>Integration.Manage</c>.
/// </summary>
/// <remarks>
/// ⚠ <see cref="IBackgroundJobScheduler.EnqueueCoalescedAsync{TJob}"/> з ціллю <see cref="SourceEventSyncTarget.Of"/>:
/// повторне натискання й прогін за розкладом зливаються в одну задачу, а виконувана не переривається. Сутність без
/// активного мапінгу подій відхиляється — задача завершилась би «нічого синхронізувати» без пояснення людині.
/// <para>
/// ⛔ D-464: підтвердження масового видалення (<c>confirmRemoval</c>) — дія з наслідком «видалити рядки документів»,
/// тож вона лишає слід у журналі структурних змін (<c>aud.StructureChange</c>, <c>ext.SourceEntity</c>,
/// <see cref="RemovalConfirmedOperation"/>): хто, коли, яка задача. Самі видалення задача журналює рядок за рядком.
/// </para>
/// </remarks>
public sealed class SyncSourceEventsHandler(
    ICollectionStore sources,
    ISourceEventMapStore store,
    IBackgroundJobScheduler jobs,
    IAccessDecisionService access,
    ICurrentUser currentUser,
    IAuditWriter audit,
    IClock clock)
{
    /// <summary>Операція журналу: масове видалення зниклих подій підтверджено вручну (D-464).</summary>
    public const string RemovalConfirmedOperation = "SourceEventRemovalConfirmed";

    /// <summary>Ставить синхронізацію; повертає ідентифікатор задачі (наявної чи нової).</summary>
    /// <param name="sourceEntityId">Сутність-шаблон подій.</param>
    /// <param name="ct">Скасування.</param>
    /// <param name="confirmRemovals">
    /// Підтвердити масове видалення зниклих подій, заблоковане лімітом «повної звірки». Право те саме
    /// (<c>Integration.Manage</c>); D-464: віддає контролер, слід — у журналі. ✎ Аудит 31.10 (G3): не голий прапор, а
    /// те, що людина бачила, — мапінг і кількість кандидатів (<c>eventRemovalBlocked.perMap</c>); порожньо — звичайний синк.
    /// </param>
    public async Task<string> HandleAsync(
        int sourceEntityId, CancellationToken ct, IReadOnlyList<SourceEventRemovalConfirmation>? confirmRemovals = null)
    {
        var confirmations = Confirmations(confirmRemovals);
        var confirmRemoval = confirmations.Count > 0;

        await PermissionCheck
            .RequireAsync(access, currentUser, SaveDataSourceHandler.Permission, ct)
            .ConfigureAwait(false);

        var entity = await sources.FindSourceEntityAsync(sourceEntityId, ct).ConfigureAwait(false)
            ?? throw new NotFoundException(
                ErrorCodes.SourceEntityNotFound,
                $"Сутності джерела {sourceEntityId} немає або вона вимкнена.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-INT-0404.sourceEntity",
                    ["id"] = sourceEntityId.ToString(CultureInfo.InvariantCulture),
                });

        if (!await store.HasActiveMapAsync(sourceEntityId, ct).ConfigureAwait(false))
        {
            throw new BusinessRuleException(
                "ECR-INT-0422",
                $"У сутності {sourceEntityId} немає активного мапінгу подій: синхронізувати нічого.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-INT-0422.eventSyncNoMap",
                    ["sourceEntityId"] = sourceEntityId.ToString(CultureInfo.InvariantCulture),
                });
        }

        var jobId = await jobs
            .EnqueueCoalescedAsync<ISourceEventSyncJob>(
                confirmRemoval ? SourceEventSyncTarget.OfConfirmed(sourceEntityId) : SourceEventSyncTarget.Of(sourceEntityId),
                new SourceEventSyncRequest(
                    sourceEntityId, ConfirmRemoval: confirmRemoval, ConfirmedRemovals: confirmRemoval ? confirmations : null),
                ct,
                currentUser.UserId)
            .ConfigureAwait(false);

        if (confirmRemoval)
        {
            // ⚠ Після постановки: запис називає задачу, яка видалятиме. Не вдалося поставити — і сліду немає
            // (підтвердження без задачі нічого не видалило б).
            await IntegrationConfigAudit
                .WriteAsync(
                    audit,
                    clock,
                    currentUser,
                    IntegrationConfigAudit.SourceEntityType,
                    sourceEntityId,
                    RemovalConfirmedOperation,
                    oldJson: null,
                    newJson: System.Text.Json.JsonSerializer.Serialize(
                        new
                        {
                            sourceEntityId,
                            entity = entity.Code,
                            jobId,
                            maps = confirmations.Select(c => new { mapId = c.MapId, candidates = c.Candidates }),
                        }),
                    IntegrationConfigAudit.Reason(
                        "integrationAudit.sourceEventRemovalConfirmed", ("entity", entity.Code), ("job", jobId)),
                    ct)
                .ConfigureAwait(false);
        }

        return jobId;
    }

    /// <summary>
    /// ✎ Аудит 31.10 (G3): перелік підтверджень — мапінг і кандидати &gt; 0, по одному на мапінг (найбільше з повторів
    /// НЕ береться — береться перше: людина підтверджує те, що бачила), не більше
    /// <see cref="SourceEventSyncOutcome.MaxConfirmations"/>; інакше <c>422 ECR-INT-0422.eventRemovalConfirmInvalid</c>.
    /// </summary>
    private static List<SourceEventRemovalConfirmation> Confirmations(IReadOnlyList<SourceEventRemovalConfirmation>? input)
    {
        if (input is not { Count: > 0 })
        {
            return [];
        }

        if (input.Count > SourceEventSyncOutcome.MaxConfirmations || input.Any(c => c is null || c.MapId <= 0 || c.Candidates <= 0))
        {
            throw new BusinessRuleException(
                "ECR-INT-0422",
                "Підтвердження масового видалення має називати мапінг і кількість кандидатів (> 0).",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-INT-0422.eventRemovalConfirmInvalid",
                    ["max"] = SourceEventSyncOutcome.MaxConfirmations.ToString(CultureInfo.InvariantCulture),
                });
        }

        return [.. input.GroupBy(c => c.MapId).Select(g => g.First())];
    }
}
