// src/Ecr.Application/Reporting/ReportSnapshotHandlers.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Application.Templates;
using Ecr.Domain.Errors;

namespace Ecr.Application.Reporting;

/// <summary>
/// Перелік побудованих зрізів. Право <c>Report.ViewRegulatory</c>.
/// </summary>
/// <remarks>
/// ⚠ Самих звітів у системі немає: звітність лишається в SSRS (D-52). Наша
/// межа — <c>rpt.*</c>, незмінний зріз, який SSRS читає.
/// </remarks>
public sealed class ListReportSnapshotsHandler(
    IReportSnapshotBuilder snapshots,
    IAccessDecisionService access,
    ICurrentUser currentUser)
{
    /// <summary>Право на перегляд регуляторної звітності (`02-contracts.md` §9).</summary>
    public const string Permission = "Report.ViewRegulatory";

    /// <summary>Віддає зрізи з часом побудови й контрольною сумою.</summary>
    /// <param name="projectId">Проєкт; <c>null</c> — усі.</param>
    /// <param name="periodKey">Період; <c>null</c> — усі.</param>
    /// <param name="ct">Скасування.</param>
    public async Task<IReadOnlyList<ReportSnapshotSummary>> HandleAsync(
        int? projectId, int? periodKey, CancellationToken ct)
    {
        await ListTemplatesHandler
            .RequireAsync(access, currentUser, Permission, ct)
            .ConfigureAwait(false);

        return await snapshots.ListAsync(projectId, periodKey, ct).ConfigureAwait(false);
    }
}

/// <summary>
/// Побудова зрізу. Право <c>Report.BuildSnapshot</c>.
/// </summary>
/// <remarks>
/// Зріз **незмінний**: повторна побудова створює новий, а не переписує
/// старий. Інакше звіт, роздрукований учора, і той самий звіт сьогодні давали
/// б різні числа без жодного сліду.
/// </remarks>
public sealed class BuildReportSnapshotHandler(
    IReportDefinitionStore definitions,
    IBackgroundJobScheduler jobs,
    IAccessDecisionService access,
    ICurrentUser currentUser)
{
    /// <summary>Право на побудову зрізу (`02-contracts.md` §9).</summary>
    public const string Permission = "Report.BuildSnapshot";

    /// <summary>Ставить побудову в чергу.</summary>
    /// <param name="code">Код звіту.</param>
    /// <param name="projectId">Проєкт.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="ct">Скасування.</param>
    /// <returns>Ідентифікатор задачі.</returns>
    /// <exception cref="NotFoundException">Звіту з таким кодом немає.</exception>
    public async Task<string> HandleAsync(string code, int projectId, int periodKey, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);

        await ListTemplatesHandler
            .RequireAsync(access, currentUser, Permission, ct)
            .ConfigureAwait(false);

        // ⚠ Версія резолвиться ТУТ, а не в задачі. Невідомий код звіту має
        // дати 404 одразу, а не через хвилину у вигляді задачі, яка
        // «завершилася помилкою»: користувач не зрозуміє, що просто помилився
        // в коді.
        var versionId = await definitions.FindCurrentVersionIdAsync(code, ct).ConfigureAwait(false)
                        ?? throw new NotFoundException(
                            ErrorCodes.ReportNotFound,
                            $"Звіту «{code}» немає або в нього немає чинної версії.");

        return await jobs
            .EnqueueAsync<IReportSnapshotJob>(
                new ReportSnapshotTask(versionId, projectId, periodKey), ct)
            .ConfigureAwait(false);
    }
}

/// <summary>Завдання на побудову зрізу.</summary>
/// <param name="ReportVersionId">Версія звіту.</param>
/// <param name="ProjectId">Проєкт.</param>
/// <param name="PeriodKey">Період.</param>
public sealed record ReportSnapshotTask(int ReportVersionId, int ProjectId, int PeriodKey);
