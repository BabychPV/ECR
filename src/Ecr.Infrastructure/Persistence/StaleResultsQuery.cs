using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>Документ із застарілими результатами методологій і момент, відколи вони застарілі.</summary>
/// <param name="DocumentId">Документ.</param>
/// <param name="Since">Найраніша зміна входів після початку актуального прогону (UTC, без <c>Kind</c>).</param>
public sealed record StaleDocumentRow(long DocumentId, DateTime? Since);

/// <summary>
/// ЄДИНЕ місце, де живе предикат «результати документа застаріли» для ПЕРЕЛІКУ
/// (перелік, фільтр <c>resultsStale</c> і лічильники <c>/documents/summary</c> компонують САМЕ цей запит).
/// </summary>
/// <remarks>
/// ⚠ Те саме правило, що й <c>MethodologyStore.GetCalculationFreshnessAsync</c> (панель результатів документа),
/// але пакетом: застарілість ВИВОДИТЬСЯ, а не зберігається (міграції немає). Актуальний прогін документа —
/// найновіший <c>Current</c>-прогін періоду, у якому є числа цього документа; вхід змінено, коли в
/// <c>aud.CellChange</c> є правка комірки цього документа за період ПІСЛЯ початку прогону, крім
/// <c>Recalculation</c> (формули шаблону пишуть свої комірки всередині прогону).
///
/// ⚠ Правка довідника, який читає методологія (RT-25), теж рахується — але НЕ при фільтрі за автором
/// (<c>byUserId</c>): довідник змінила не людина з <c>aud.CellChange</c>.
///
/// ⛔ Видимості тут НЕМАЄ — вона в обробнику: читач, чий доступ звужено нижче проєкту, не отримує
/// значення взагалі (<c>null</c>, поза фільтром і лічильником), бо «застаріло за видимими входами» не
/// доводить, що застарів видимий вихід — це оракул про схований аркуш. Викликач виключає такі проєкти.
///
/// ⚠ Сирий SQL: <c>aud.*</c> поза моделлю EF. Результат лишається <c>IQueryable</c> — компонується в
/// підзапит <c>IN</c> до <c>Take</c> (фільтр) або звужується до сторінки (позначка).
/// </remarks>
internal static class StaleResultsQuery
{
    /// <summary>Документи періоду із застарілими результатами.</summary>
    /// <param name="db">Контекст.</param>
    /// <param name="periodKey">Період (обов'язковий: прогін і правки належать періоду).</param>
    /// <param name="byUserId">Лише правки цього користувача (<c>ChangedByUserId</c>); <c>null</c> — будь-які.</param>
    public static IQueryable<StaleDocumentRow> Documents(EcrDbContext db, int periodKey, int? byUserId = null)
    {
        ArgumentNullException.ThrowIfNull(db);

        var hasBy = byUserId is null ? 0 : 1;
        var by = byUserId ?? 0;

        return db.Database
            .SqlQuery<StaleDocumentRow>($"""
                SELECT x.DocumentId AS DocumentId, MIN(t.At) AS Since
                  FROM (SELECT cr.DocumentId AS DocumentId, MAX(r0.Id) AS RunId
                          FROM calc.CalculationResult AS cr
                          JOIN calc.CalculationRun AS r0 ON r0.Id = cr.CalculationRunId
                         WHERE cr.PeriodKey = {periodKey}
                           AND r0.PeriodKey = {periodKey}
                           AND r0.Status = N'Current'
                         GROUP BY cr.DocumentId) AS x
                  JOIN calc.CalculationRun AS r ON r.Id = x.RunId
                 CROSS APPLY (
                       SELECT c.ChangedAt AS At
                         FROM aud.CellChange AS c
                        WHERE c.DocumentId = x.DocumentId
                          AND c.PeriodKey = {periodKey}
                          AND c.ChangedAt > r.StartedAt
                          AND c.Origin <> N'Recalculation'
                          AND ({hasBy} = 0 OR c.ChangedByUserId = {by})
                       UNION ALL
                       SELECT rd.DataChangedAt AS At
                         FROM cfg.RegistryDef AS rd
                        WHERE {hasBy} = 0
                          AND rd.DataChangedAt > r.StartedAt
                          AND EXISTS (SELECT 1
                                        FROM cfg.RegistryUse AS u
                                        JOIN calc.CalculationResult AS cr2 ON cr2.MethodologyVersionId = u.SourceId
                                       WHERE u.RegistryDefId = rd.Id
                                         AND u.SourceKind = 1
                                         AND cr2.CalculationRunId = x.RunId
                                         AND cr2.PeriodKey = {periodKey}
                                         AND cr2.DocumentId = x.DocumentId)
                       ) AS t
                 GROUP BY x.DocumentId
                """);
    }
}
