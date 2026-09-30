// src/Ecr.Application/Integration/SourceEvents/SyncSourceEventsHandler.cs
using System.Globalization;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
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
/// </remarks>
public sealed class SyncSourceEventsHandler(
    ICollectionStore sources,
    ISourceEventMapStore store,
    IBackgroundJobScheduler jobs,
    IAccessDecisionService access,
    ICurrentUser currentUser)
{
    /// <summary>Ставить синхронізацію; повертає ідентифікатор задачі (наявної чи нової).</summary>
    /// <param name="sourceEntityId">Сутність-шаблон подій.</param>
    /// <param name="ct">Скасування.</param>
    public async Task<string> HandleAsync(int sourceEntityId, CancellationToken ct)
    {
        await PermissionCheck
            .RequireAsync(access, currentUser, SaveDataSourceHandler.Permission, ct)
            .ConfigureAwait(false);

        _ = await sources.FindSourceEntityAsync(sourceEntityId, ct).ConfigureAwait(false)
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

        return await jobs
            .EnqueueCoalescedAsync<ISourceEventSyncJob>(
                SourceEventSyncTarget.Of(sourceEntityId),
                new SourceEventSyncRequest(sourceEntityId),
                ct,
                currentUser.UserId)
            .ConfigureAwait(false);
    }
}
