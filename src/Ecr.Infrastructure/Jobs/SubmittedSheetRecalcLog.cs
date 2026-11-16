using System.Text.Json;
using Ecr.Application.Calculations;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Jobs;

/// <summary>
/// ✎ B79: запис журналу «поданий аркуш перераховано після подання» для каскаду формул — та сама позначка, що її
/// ставить повний перерахунок (<c>RecalculationJob.SheetLogAsync</c>, B63/B83): <c>aud.StructureChange</c>,
/// <c>SheetRecalc / RecalculatedAfterSubmission</c>, кореляція <see cref="RecalculationSkips.AuditCorrelation"/>.
/// Картка документа читає її за аркушем і періодом (<c>DocumentStore.MarkRecalculatedAfterSubmissionAsync</c>).
/// </summary>
/// <remarks>
/// ⚠ Запис — на аркуш × копію × період, у який каскад СПРАВДІ записав (набір дає <c>RecalculationService</c> після
/// коміту). Автора немає: каскад — системна задача (<c>ChangedByUserId = 0</c>, як у B63). Затверджені аркуші каскад не
/// пише й не журналює: на відміну від повного перерахунку, він не має «області», з якої щось пропущено.
/// </remarks>
internal static class SubmittedSheetRecalcLog
{
    /// <summary>Пише по запису на кожен поданий аркуш (і копію), перерахований каскадом за період.</summary>
    /// <param name="db">Контекст — для коду аркуша й версії шаблону.</param>
    /// <param name="audit">Журнал; <c>null</c> — задачу зібрано вручну (тести), запису немає.</param>
    /// <param name="clock">Годинник; <c>null</c> — так само.</param>
    /// <param name="documentId">Документ.</param>
    /// <param name="periodKey">Період прогону.</param>
    /// <param name="sheets">Подані аркуші (аркуш × копія), у які записано.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Скільки записів зроблено.</returns>
    public static async Task<int> WriteAsync(
        EcrDbContext db, IAuditWriter? audit, IClock? clock, long documentId, int periodKey,
        IReadOnlyCollection<(int SheetDefId, long? CopyId)> sheets, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(sheets);

        if (audit is null || clock is null || sheets.Count == 0)
        {
            return 0;
        }

        var sheetIds = sheets.Select(s => s.SheetDefId).Distinct().ToList();
        var defs = await db.SheetDefs
            .AsNoTracking()
            .Where(s => sheetIds.Contains(s.Id))
            .Select(s => new { s.Id, s.TemplateVersionId, s.Code })
            .ToDictionaryAsync(s => s.Id, ct)
            .ConfigureAwait(false);

        var now = clock.UtcNow;
        var entries = new List<StructureChangeRecord>(sheets.Count);
        foreach (var (sheetDefId, copyId) in sheets.OrderBy(s => s.SheetDefId).ThenBy(s => s.CopyId ?? 0))
        {
            if (!defs.TryGetValue(sheetDefId, out var def))
            {
                continue;
            }

            entries.Add(new StructureChangeRecord(
                now,
                TemplateVersionId: def.TemplateVersionId,
                EntityType: RecalculationSkips.AuditEntityType,
                EntityId: sheetDefId,
                ChangeClass: Domain.Enums.ChangeClass.Safe,
                Operation: RecalculationSkips.AuditRecalculatedAfterSubmission,
                OldJson: null,
                NewJson: JsonSerializer.Serialize(new
                {
                    documentId,
                    sheetDefId,
                    sheetCopyId = copyId,
                    sheetCode = def.Code.Value,
                    periodKey,
                    state = nameof(Domain.Enums.DocumentStatus.Submitted),
                    reason = "recalculatedAfterSubmission",
                    source = "cascade",
                }),
                ChangeReason: "recalc-after-submission",
                ChangedByUserId: 0,
                CorrelationId: RecalculationSkips.AuditCorrelation(documentId, sheetDefId, periodKey, copyId)));
        }

        if (entries.Count > 0)
        {
            await audit.WriteStructureChangesAsync(entries, ct).ConfigureAwait(false);
        }

        return entries.Count;
    }
}
