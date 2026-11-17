using Ecr.Application.Calculations;
using Ecr.Domain.ValueObjects;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>
/// Позначка «перераховано після подання»: ОДНЕ правило на картку документа (<c>sheets[].recalculatedAfterSubmission</c>)
/// і на чергу «До погодження» (✎ B79, Q13).
/// </summary>
/// <remarks>
/// <para>
/// Правило: рядок стану аркуша (аркуш × копія × період) ПОДАНИЙ, і в журналі змін є запис
/// <c>SheetRecalc / RecalculatedAfterSubmission</c> з кореляцією саме цього аркуша, копії й періоду
/// (<see cref="RecalculationSkips.AuditCorrelation"/>), ПІЗНІШИЙ за останнє подання (<c>SubmittedAt</c>). Окремого
/// стовпця немає навмисно: повторне подання зсуває <c>SubmittedAt</c> (позначка зникає сама), затвердження знімає стан
/// <c>Submitted</c>.
/// </para>
/// <para>
/// ⚠ Умова — один фрагмент SQL (<see cref="Rule"/>) на обидва запити: картка питає за документом і періодом, черга —
/// за набором Id станів сторінки (один запит на сторінку, не на рядок). Копія аркуша — свій рядок стану й своя
/// кореляція (<c>…:{аркуш}#{копія}:{період}</c>).
/// </para>
/// </remarks>
internal static class RecalculatedAfterSubmissionMark
{
    /// <summary>Умова позначки над рядком <c>wf.ApprovalState a</c>; параметри — <see cref="Parameters"/>.</summary>
    private const string Rule = """
        a.Status = 1 AND a.SubmittedAt IS NOT NULL
        AND EXISTS (SELECT 1
                      FROM aud.StructureChange c
                     WHERE c.EntityType = @entityType AND c.Operation = @operation
                       AND c.ChangedAt > a.SubmittedAt
                       AND c.CorrelationId = CONCAT(@prefix, CAST(a.DocumentId AS nvarchar(20)), N':',
                           CAST(a.SheetDefId AS nvarchar(12)),
                           CASE WHEN a.SheetCopyId IS NULL THEN N''
                                ELSE CONCAT(@separator, CAST(a.SheetCopyId AS nvarchar(20))) END,
                           N':', CAST(a.PeriodKey AS nvarchar(12))))
        """;

    /// <summary>Запит картки документа: ключі стану позначених аркушів документа за період.</summary>
    private const string StateKeysSql = """
        SELECT CASE WHEN a.SheetCopyId IS NULL THEN sd.Code
                    ELSE CONCAT(sd.Code, @separator, CAST(a.SheetCopyId AS nvarchar(20))) END AS Value
          FROM wf.ApprovalState a
          JOIN cfg.SheetDef sd ON sd.Id = a.SheetDefId
         WHERE a.DocumentId = @documentId AND a.PeriodKey = @periodKey
           AND
        """ + " " + Rule;

    /// <summary>Запит черги: позначені рядки стану серед наведених Id (JSON-масив у <c>@ids</c>).</summary>
    private const string StateIdsSql = """
        SELECT a.Id AS Value
          FROM wf.ApprovalState a
         WHERE a.Id IN (SELECT CAST(j.[value] AS bigint) FROM OPENJSON(@ids) j)
           AND
        """ + " " + Rule;

    /// <summary>
    /// Ключі стану («КОД» оригіналу, «КОД#sheetCopyId» копії) поданих аркушів документа за період, що мають позначку.
    /// </summary>
    /// <param name="db">Контекст.</param>
    /// <param name="documentId">Документ.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="ct">Токен скасування.</param>
    public static async Task<List<string>> SheetStateKeysAsync(
        EcrDbContext db, long documentId, int periodKey, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);

        return await db.Database
            .SqlQueryRaw<string>(
                StateKeysSql,
                [.. Parameters(), new SqlParameter("@documentId", documentId), new SqlParameter("@periodKey", periodKey)])
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <summary>Які з наведених рядків стану (<c>wf.ApprovalState.Id</c>) мають позначку — одним запитом на набір.</summary>
    /// <param name="db">Контекст.</param>
    /// <param name="stateIds">Id станів (сторінка черги «До погодження»).</param>
    /// <param name="ct">Токен скасування.</param>
    public static async Task<HashSet<long>> StateIdsAsync(
        EcrDbContext db, IReadOnlyCollection<long> stateIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(stateIds);

        // Порожній набір сюди не доходить (обробник черги для порожньої сторінки не питає); якщо дійде — запит
        // просто нічого не поверне (`OPENJSON('[]')`).
        var marked = await db.Database
            .SqlQueryRaw<long>(
                StateIdsSql,
                [.. Parameters(), new SqlParameter("@ids", System.Text.Json.JsonSerializer.Serialize(stateIds))])
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return [.. marked];
    }

    /// <summary>Параметри <see cref="Rule"/> — щоразу нові (параметр не можна вжити у двох командах).</summary>
    private static object[] Parameters() =>
    [
        new SqlParameter("@entityType", RecalculationSkips.AuditEntityType),
        new SqlParameter("@operation", RecalculationSkips.AuditRecalculatedAfterSubmission),
        new SqlParameter("@prefix", RecalculationSkips.AuditCorrelationPrefix),
        new SqlParameter("@separator", SheetCopyNaming.StateKeySeparator.ToString()),
    ];
}
