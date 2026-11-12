// src/Ecr.Application/Integration/FlertSyncHandlers.cs
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Application.Security;

namespace Ecr.Application.Integration;

/// <summary>
/// «Синхронізувати зараз» — позачерговий синк складу газу FLERT (<c>D-476</c>, крок F3). Право <c>Integration.Manage</c>
/// (те саме, що в «Зібрати» сутності джерела).
/// </summary>
/// <remarks>
/// ⚠ Злита постановка (<c>EnqueueCoalescedAsync</c>, одна ціль): повторні натискання, поки синк у черзі, не множать прогонів.
/// Задача та сама, що й щоденна; вимкнений синк (<c>Flert:Sync:Enabled=false</c>) і тут лише пише «вимкнено».
/// </remarks>
public sealed class SyncFlertNowHandler(IBackgroundJobScheduler jobs, IAccessDecisionService access, ICurrentUser currentUser)
{
    /// <summary>Ціль злиття постановок.</summary>
    public const string Target = "flert-composition-sync";

    /// <summary>Ставить синк у чергу.</summary>
    /// <param name="ct">Скасування.</param>
    /// <returns>Ідентифікатор задачі.</returns>
    public async Task<string> HandleAsync(CancellationToken ct)
    {
        var profile = await PermissionCheck
            .RequireAsync(access, currentUser, CollectFromSourceHandler.Permission, ct)
            .ConfigureAwait(false);

        return await jobs
            .EnqueueCoalescedAsync<IFlertCompositionSyncJob>(Target, null, ct, profile.UserId)
            .ConfigureAwait(false);
    }
}

/// <summary>Стан синку FLERT. Право <c>Integration.View</c> або <c>Integration.Manage</c> (як перелік сутностей збору).</summary>
public sealed class GetFlertSyncStatusHandler(IFlertSyncStatusReader reader, IAccessDecisionService access, ICurrentUser currentUser)
{
    /// <summary>Читає стан.</summary>
    /// <param name="ct">Скасування.</param>
    public async Task<FlertSyncStatusDto> HandleAsync(CancellationToken ct)
    {
        _ = await PermissionCheck
            .RequireAnyAsync(access, currentUser, [ListSourceEntitiesHandler.ViewPermission, CollectFromSourceHandler.Permission], ct)
            .ConfigureAwait(false);

        return await reader.ReadAsync(ct).ConfigureAwait(false);
    }
}
