// src/Ecr.Application/Documents/CalcStatus/GetDocumentCalcStatusHandler.cs
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Documents.CalcStatus;

/// <summary>
/// ✎ D-577 (L3): стан розрахунку документа для чипа рядка стану — <c>GET /documents/{id}/calc-status?periodKey=</c>.
/// Право й видимість — ті самі, що в «Перерахувати» (<see cref="RecalculateDocumentHandler"/>): <c>Document.View</c> у проєкті
/// документа і видимий документ (інакше 404).
/// </summary>
/// <remarks>
/// ⛔ BUG-3/BUG-4 (UX-огляд): стан перерахунку жив у пам'яті вкладки — перезавантаження, інша вкладка, чужа чи нічна
/// задача давали «Recalculate», хоча задача вже йде, а провал був лише тостом. Тепер стан — із сервера.
/// <para>
/// ⚠ Видимість застарілості — як у картці (R-7, D-446): читач, якому в проєкті документа сховано аркуш, таблицю чи
/// колонку, застарілості не отримує (<c>resultsStale = null</c>, причина <c>PartiallyHidden</c>), а імен авторів задач —
/// теж. Стан задач (у черзі, рахується, провал) лишається: він нічого схованого не розкриває.
/// </para>
/// <para>
/// ⚠ Ціна: без застарілості й задач — 3 вузькі запити (застарілі місяці, задачі документа за фільтрованим індексом
/// <c>IX_JobProgress_Document_Active</c>, актуальні прогони за <c>IX_CalculationRun</c>…Current) + провал за добу
/// (<c>IX_JobProgress_State_UpdatedAt</c>, лише <c>Failed</c>). Стани періодів і аркушів — лише коли є застарілий місяць.
/// Клієнт опитує цей ресурс, поки є задача в черзі чи в роботі.
/// </para>
/// </remarks>
public sealed class GetDocumentCalcStatusHandler(
    IAccessDecisionService access,
    ICurrentUser currentUser,
    IDocumentCalcStatusStore store,
    DocumentStaleMonths staleMonths,
    IClock clock,
    IJobQueuePosition? queuePosition = null,
    ITemplateLayoutRegistry? layouts = null,
    IProjectVersionTimeline? timeline = null)
{
    /// <summary>Право — як у перерахунку свого документа.</summary>
    public const string Permission = RecalculateDocumentHandler.Permission;

    /// <summary>Наскільки давній провал ще показується (новіший успішний прогін знімає його раніше).</summary>
    public static readonly TimeSpan FailureWindow = TimeSpan.FromHours(24);

    /// <summary>Стан розрахунку документа.</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="periodKey">Період на екрані.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="Errors.NotFoundException"><c>ECR-DOC-0404</c> — документ невидимий чи не існує.</exception>
    public async Task<DocumentCalcStatusDto> HandleAsync(long documentId, int periodKey, CancellationToken ct)
    {
        var key = PeriodKey.Parse(periodKey);
        var profile = await PermissionCheck
            .RequireInAnyProjectAsync(access, currentUser, Permission, ct)
            .ConfigureAwait(false);
        await DocumentVisibility.RequireVisibleAsync(access, profile, documentId, Permission, ct).ConfigureAwait(false);

        var projectId = await access.DocumentProjectIdAsync(documentId, ct).ConfigureAwait(false)
                        ?? throw DocumentVisibility.NotFound(documentId);

        var hidden = DocumentSheetVisibility.HasRestrictions(profile)
                     && DocumentSheetVisibility.IsProjectNarrowed(
                         (await access.ReadScopeAsync(profile, documentId, ct).ConfigureAwait(false)).InPeriod(key));

        var year = await YearOfAsync(projectId, key, ct).ConfigureAwait(false);

        IReadOnlyList<CalcStaleMonthDto> months = hidden
            ? []
            : await staleMonths.ClassifyAsync(documentId, projectId, key.Value, year, ct).ConfigureAwait(false);
        var recalculable = months.Where(m => m.Recalculable).ToList();

        var me = currentUser.UserId;
        var active = new List<CalcActiveJobDto>();
        foreach (var job in (await store.ActiveAsync(documentId, ct).ConfigureAwait(false))
                     .OrderBy(j => j.State == "Running" ? 0 : 1)
                     .ThenBy(j => j.QueuedAt))
        {
            var position = job.State == "Queued" && queuePosition is not null
                ? await queuePosition.GetAsync(job.JobId, ct).ConfigureAwait(false)
                : null;
            active.Add(new CalcActiveJobDto(
                job.JobId, job.State, CalcJobKinds.Of(job.TargetKey ?? job.JobId, job.CreatedByUserId), job.Percent,
                position?.Position, position?.EstimatedStartAt, job.StepDone, job.StepTotal, job.CurrentPeriodKey,
                Utc(job.QueuedAt), Utc(job.StartedAt), Own: me is not null && job.CreatedByUserId == me,
                StartedByDisplayName: hidden ? null : job.CreatedByDisplayName));
        }

        int[] runPeriods = year is { } y
            ? [.. Enumerable.Range(1, 12).Select(m => (y * 100) + m)]
            : [key.Value];
        var calculatedAt = Utc(await store.CalculatedAtAsync(documentId, projectId, runPeriods, ct).ConfigureAwait(false));

        var floor = clock.UtcNow - FailureWindow;
        var failure = await store
            .LastFailureAsync(documentId, calculatedAt is { } at && at > floor ? at : floor, ct)
            .ConfigureAwait(false);

        return new DocumentCalcStatusDto(
            documentId,
            key.Value,
            year,
            ResultsStale: hidden ? null : recalculable.Count > 0,
            ResultsStaleSince: hidden ? null : recalculable.Min(m => m.Since),
            ResultsStaleUndetermined: hidden ? ResultsStaleUndeterminedReason.PartiallyHidden : null,
            months,
            active,
            calculatedAt,
            failure is null
                ? null
                : new CalcFailureDto(
                    failure.JobId, Utc(failure.FailedAt)!.Value, failure.ErrorCode, failure.CorrelationId,
                    CalcJobKinds.Of(failure.TargetKey ?? failure.JobId, failure.CreatedByUserId),
                    Own: me is not null && failure.CreatedByUserId == me));
    }

    /// <summary>
    /// Рік, якщо версія ПЕРІОДУ має річну розкладку — те саме правило, що в «Перерахувати» (<see cref="RecalculateDocumentHandler"/>):
    /// натискання на такому документі рахує місяці року, тож і стан — за місяцями року.
    /// </summary>
    private async Task<int?> YearOfAsync(int projectId, PeriodKey key, CancellationToken ct)
        => layouts is { HasAny: true } && timeline is not null
           && layouts.IsYearVersion(await timeline.VersionOfAsync(projectId, key.Value, ct).ConfigureAwait(false))
            ? key.Year
            : null;

    private static DateTime? Utc(DateTime? value)
        => value is { } v ? DateTime.SpecifyKind(v, DateTimeKind.Utc) : null;
}
