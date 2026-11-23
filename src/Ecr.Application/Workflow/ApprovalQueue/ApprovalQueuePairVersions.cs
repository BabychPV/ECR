using Ecr.Application.Ports;

namespace Ecr.Application.Workflow.ApprovalQueue;

/// <summary>
/// ✎ B74 P3 (D-576): версія пари проєкт × шаблон за період для черг «До погодження» й «Повернені» — документ іншого
/// (не основного) шаблону проєкту бере знімок і маршрут СВОЄЇ версії. Один запит на (проєкт, шаблон, період) за прохід.
/// </summary>
public static class ApprovalQueuePairVersions
{
    /// <summary>Версія пари; <c>null</c> — пари немає (тоді викликач лишає версію проєкту).</summary>
    /// <param name="store">Сховище черги.</param>
    /// <param name="cache">Кеш проходу.</param>
    /// <param name="projectId">Проєкт.</param>
    /// <param name="templateId">Шаблон документа.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="ct">Токен скасування.</param>
    public static async Task<int?> ResolveAsync(
        IApprovalQueueStore store,
        Dictionary<(int ProjectId, int TemplateId, int PeriodKey), int?> cache,
        int projectId, int templateId, int periodKey, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(cache);

        if (!cache.TryGetValue((projectId, templateId, periodKey), out var version))
        {
            version = await store.TemplateVersionOfAsync(projectId, templateId, periodKey, ct).ConfigureAwait(false);
            cache[(projectId, templateId, periodKey)] = version;
        }

        return version;
    }
}
