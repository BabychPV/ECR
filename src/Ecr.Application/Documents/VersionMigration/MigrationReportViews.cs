using Ecr.Application.Ports;
using Microsoft.Extensions.Logging;

namespace Ecr.Application.Documents.VersionMigration;

/// <summary>
/// V-5: стабільні вʼюхи SSRS після переносу, що зберіг нові аліаси колонок (<c>cfg.ColumnRename</c>): аліаси
/// вшиваються в текст вʼюхи при генерації. Після коміту переносу ставиться фонова задача <see cref="IReportViewsJob"/>
/// (<see cref="ReportViewsTask"/>, P-4b) — та сама, що й після публікації версії.
/// </summary>
/// <remarks>
/// ⛔ F13 RC #872 P2-1: НЕ генерувати в запиті переносу — на Land генерація триває хвилини (стеля команди 60/300 с), і
/// синхронний виклик падав на таймауті, лишаючи вʼюху з NULL. Задача ставиться ЗЛИТТЯМ на шаблон
/// (<see cref="ReportViewsTarget.Of"/>): публікація й перенос того самого шаблону дають один прогін.
/// ⚠ Збій ПОСТАНОВКИ не відкочує вже закомічений перенос: він на картці <c>reportviews</c> і в журналі; повтор —
/// <c>EXEC rpt.usp_GenerateStableViews</c> або перезапуск застосунку (старт ставить задачу «усі шаблони»).
/// </remarks>
public static partial class MigrationReportViews
{
    /// <summary>Ставить перегенерацію вʼюх шаблону, коли перенос зберіг пари перейменувань колонок; ніколи не кидає.</summary>
    /// <param name="jobs">Планувальник.</param>
    /// <param name="status">Картка <c>reportviews</c>; <c>null</c> — немає.</param>
    /// <param name="log">Журнал.</param>
    /// <param name="templateId">Шаблон цільової версії.</param>
    /// <param name="targetVersionId">Цільова версія переносу.</param>
    /// <param name="columnRenames">Скільки пар перейменувань колонок зберіг перенос.</param>
    /// <param name="userId">Ініціатор переносу (автор задачі).</param>
    /// <param name="ct">Токен скасування (викликач після коміту передає <see cref="CancellationToken.None"/>).</param>
    /// <returns><c>true</c> — задачу поставлено.</returns>
    public static async Task<bool> EnqueueAsync(
        IBackgroundJobScheduler jobs, IReportViewStatus? status, ILogger log, int templateId, int targetVersionId,
        int columnRenames, int userId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(log);
        if (columnRenames == 0)
        {
            return false;
        }

        // Версія — у набір, що чекає: злиття з іншою задачею шаблону її не загубить (як у публікації).
        var generation = status?.Pending(templateId, targetVersionId) ?? 0;
        try
        {
            await jobs
                .EnqueueCoalescedAsync<IReportViewsJob>(
                    ReportViewsTarget.Of(templateId), new ReportViewsTask(templateId, targetVersionId), ct, userId)
                .ConfigureAwait(false);
            LogQueued(log, templateId, targetVersionId, columnRenames);
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            status?.Settled(templateId, generation);
            status?.Failed(new ReportViewFailure(targetVersionId, 0, "Report view regeneration after version migration was not queued: " + ex.Message));
            LogFailed(log, ex, targetVersionId);
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Перенос версії: нові аліаси колонок ({Renames}); поставлено перегенерацію вʼюх rpt.* шаблону {TemplateId}, версія {TemplateVersionId}.")]
    private static partial void LogQueued(ILogger log, int templateId, int templateVersionId, int renames);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Перенос версії: перегенерацію вʼюх rpt.* шаблону версії {TemplateVersionId} не поставлено після нових аліасів cfg.ColumnRename; повтор — EXEC rpt.usp_GenerateStableViews або перезапуск застосунку.")]
    private static partial void LogFailed(ILogger log, Exception ex, int templateVersionId);
}
