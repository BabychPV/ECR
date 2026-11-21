// src/Ecr.Infrastructure/Jobs/NightlyInputChangeQuery.cs
using System.Text.Json;
using Ecr.Application.Calculations;
using Ecr.Application.Documents;
using Ecr.Domain.Enums;
using Ecr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Jobs;

/// <summary>✎ PSS-15: що перевірити для документа — межа <c>since</c> і набори, від яких вона залежить.</summary>
/// <param name="ProjectId">Проєкт документа.</param>
/// <param name="DocumentId">Документ.</param>
/// <param name="Since">Межа (UTC): найстарший момент входів актуальних прогонів документа мінус запас.</param>
/// <param name="TemplateVersionIds">Версії шаблону документа і версія проєкту (записи журналу структури).</param>
/// <param name="SourceDocumentIds">Документи-джерела (B179), чиї зміни — вхід документа-споживача; порожньо — не споживач.</param>
internal sealed record NightlyInputProbe(
    int ProjectId, long DocumentId, DateTime Since, int[] TemplateVersionIds, long[] SourceDocumentIds);

/// <summary>
/// ✎ PSS-15 (<c>D-562</c>): чи змінився хоч один вхід документа після межі <c>since</c> — і який саме (перший за
/// <see cref="NightlyReason"/>).
/// </summary>
/// <remarks>
/// <para>
/// ⚠ Не <c>StaleResultsQuery</c>/<c>StaleResultsByIdsQuery</c>, хоч правки комірок і довідники — ті самі джерела: «застарілі
/// результати» картки — питання про РЕЗУЛЬТАТИ МЕТОДОЛОГІЙ за один період (документ без результатів у карту не потрапляє
/// зовсім, правки комірок міряються від <c>StartedAt</c>, довідник — лише ті, що їх читали результати прогону). Пропуск
/// нічного перерахунку — ширше питання «чи дасть перерахунок ДОКУМЕНТА щось нове», і помилка тут ховає перерахунок, а не лише
/// позначку. Тому межа одна на всі входи — <c>COALESCE(InputsAsOfUtc, StartedAt)</c> найстаршого прогону мінус запас, — правки
/// комірок рахуються в будь-якому періоді документа (формули читають інші періоди), а довідники — будь-які (використання
/// транзитивне: правила довідників, скрипти, зв'язки джерел). Це надмножина «застарілості»: усе, що карта назвала б
/// застарілим, тут — зміна.
/// </para>
/// <para>
/// Входи (кожен — <c>EXISTS</c> з межею <c>&gt;= since</c>; гілки незалежні, порядок — лише для причини):
/// <list type="number">
///   <item>правка комірки документа будь-якого походження, крім <c>Recalculation</c>/<c>CalcError</c>/<c>CalcErrorResolved</c>
///   (людина, імпорт Excel, інтеграція PI й синк подій, дозволи, ручне значення формули), і додавання/видалення рядка
///   (<c>aud.RowChange</c>);</item>
///   <item>екземпляри таблиць документа, створені чи змінені після межі (перенос версії шаблону, нова таблиця, новий період);</item>
///   <item>дані будь-якого довідника (<c>cfg.RegistryDef.DataChangedAt</c>; зміна лише опису мітки не дає, D-163);</item>
///   <item>публікація версії шаблону чи методології, перемикання рушія скрипта (<c>aud.PublicationEvent</c>);</item>
///   <item>журнал структури (<c>aud.StructureChange</c>) версій документа, версії проєкту і глобальний (<c>TemplateVersionId = 0</c>:
///   прив'язки методологій, пакети, довідники): формули, колонки, правила, періоди, відкриття. Крім власних записів перерахунку —
///   <c>SheetRecalc</c> (журнал пропущених/перерахованих аркушів) і <c>ReportSnapshot</c> (зріз застарів), — і блокувань рядків
///   (<c>RowLock</c>: не вхід розрахунку): інакше нічний прогін сам собі давав би «зміну» щоночі;</item>
///   <item>перехід стану періоду проєкту (<c>doc.Period.StateChangedAt</c>, зокрема Reopen) і виняток «місяць відкрито для
///   документа» (відкрито чи закрито);</item>
///   <item>повернення затвердженого аркуша документа (<c>wf.ApprovalEvent.FromStatus = Approved</c>): поки аркуш був затверджений,
///   перерахунок його пропускав (D-493/D-510) — тепер його треба порахувати. Саме затвердження входом не є;</item>
///   <item>документ-джерело (B179): правка комірки, новий актуальний прогін (результати методологій джерела), будь-яка подія
///   погодження (стан-ворота <c>ApprovedOnly</c>).</item>
/// </list>
/// </para>
/// <para>
/// ⚠ Індекси: правки комірок — <c>IX_CellChange_DocPeriodChanged</c> (документ, далі межа на вирівняних по <c>ChangedAt</c>
/// партиціях — лише партиції після межі); <c>aud.StructureChange</c>/<c>PublicationEvent</c> — кластерний (<c>ChangedAt</c>,
/// діапазон останньої доби); екземпляри — з ключем партиції (<c>PeriodKey IN</c> періодів проєкту, як
/// <c>RecalculationJob.DocumentInstancesQuery</c>). Запит один на документ за ніч.
/// </para>
/// </remarks>
internal static class NightlyInputChangeQuery
{
    /// <summary>Типи записів журналу структури, які пише сам перерахунок чи які не є входом розрахунку.</summary>
    internal static readonly string[] IgnoredStructureEntityTypes =
    [
        RecalculationSkips.AuditEntityType,
        RunCalculationHandler.SnapshotAuditEntityType,
        Ecr.Application.Documents.RowLocks.RowLockRules.AuditEntityType,
    ];

    /// <summary>Типи публікацій, що змінюють розрахунок.</summary>
    internal static readonly string[] PublicationEntityTypes = ["TemplateVersion", "calc.MethodologyVersion"];

    /// <summary>Перша знайдена зміна входу після межі; <c>null</c> — нічого не змінилось.</summary>
    /// <param name="db">Контекст.</param>
    /// <param name="probe">Документ і межа.</param>
    /// <param name="ct">Токен скасування.</param>
    public static async Task<NightlyReason?> FirstChangeAsync(EcrDbContext db, NightlyInputProbe probe, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(probe);

        var since = probe.Since.Kind == DateTimeKind.Utc ? probe.Since : DateTime.SpecifyKind(probe.Since, DateTimeKind.Utc);
        var versions = JsonSerializer.Serialize(probe.TemplateVersionIds);
        var sources = JsonSerializer.Serialize(probe.SourceDocumentIds);
        var approved = (byte)DocumentStatus.Approved;

        // ⚠ Переліки рядків — окремими параметрами, а не OPENJSON: значення OPENJSON мають колацію бази, і порівняння з
        // колонкою іншої колації дало б 468 замість відповіді.
        var (ignored0, ignored1, ignored2) =
            (IgnoredStructureEntityTypes[0], IgnoredStructureEntityTypes[1], IgnoredStructureEntityTypes[2]);
        var (published0, published1) = (PublicationEntityTypes[0], PublicationEntityTypes[1]);

        var codes = await db.Database
            .SqlQuery<int>($"""
                /* ecr:nightly-input-change */
                SELECT TOP (1) t.Reason AS Value
                  FROM (
                        SELECT 1 AS Reason
                         WHERE EXISTS (SELECT 1 FROM aud.CellChange AS c
                                        WHERE c.DocumentId = {probe.DocumentId} AND c.ChangedAt >= {since}
                                          AND c.Origin NOT IN ({CellChangeOrigins.Recalculation}, {CellChangeOrigins.CalcError}, {CellChangeOrigins.CalcErrorResolved}))
                            OR EXISTS (SELECT 1 FROM aud.RowChange AS rc
                                        WHERE rc.DocumentId = {probe.DocumentId} AND rc.ChangedAt >= {since})
                        UNION ALL
                        SELECT 2
                         WHERE EXISTS (SELECT 1 FROM doc.TableInstance AS ti
                                        WHERE ti.PeriodKey IN (SELECT p.PeriodKey FROM doc.Period AS p WHERE p.ProjectId = {probe.ProjectId})
                                          AND ti.DocumentId = {probe.DocumentId}
                                          AND (ti.CreatedAt >= {since} OR ti.ModifiedAt >= {since}))
                        UNION ALL
                        SELECT 3
                         WHERE EXISTS (SELECT 1 FROM cfg.RegistryDef AS rd WHERE rd.DataChangedAt >= {since})
                        UNION ALL
                        SELECT 4
                         WHERE EXISTS (SELECT 1 FROM aud.PublicationEvent AS pe
                                        WHERE pe.ChangedAt >= {since}
                                          AND pe.EntityType IN ({published0}, {published1}))
                        UNION ALL
                        SELECT 5
                         WHERE EXISTS (SELECT 1 FROM aud.StructureChange AS sc
                                        WHERE sc.ChangedAt >= {since}
                                          AND sc.EntityType NOT IN ({ignored0}, {ignored1}, {ignored2})
                                          AND (sc.TemplateVersionId = 0
                                               OR sc.TemplateVersionId IN (SELECT CAST([value] AS int) FROM OPENJSON({versions}))))
                        UNION ALL
                        SELECT 6
                         WHERE EXISTS (SELECT 1 FROM doc.Period AS pp
                                        WHERE pp.ProjectId = {probe.ProjectId} AND pp.StateChangedAt >= {since})
                            OR EXISTS (SELECT 1 FROM doc.DocumentPeriodReopen AS dr
                                        WHERE dr.DocumentId = {probe.DocumentId}
                                          AND (dr.OpenedAt >= {since} OR dr.ClosedAt >= {since}))
                        UNION ALL
                        SELECT 7
                         WHERE EXISTS (SELECT 1 FROM wf.ApprovalEvent AS e
                                        WHERE e.DocumentId = {probe.DocumentId} AND e.At >= {since} AND e.FromStatus = {approved})
                        UNION ALL
                        SELECT 8
                         WHERE EXISTS (SELECT 1 FROM OPENJSON({sources}) AS s
                                        WHERE EXISTS (SELECT 1 FROM aud.CellChange AS sc2
                                                       WHERE sc2.DocumentId = CAST(s.[value] AS bigint) AND sc2.ChangedAt >= {since}
                                                         AND sc2.Origin NOT IN ({CellChangeOrigins.Recalculation}, {CellChangeOrigins.CalcError}, {CellChangeOrigins.CalcErrorResolved}))
                                           OR EXISTS (SELECT 1 FROM calc.CalculationRun AS sr
                                                       WHERE sr.DocumentId = CAST(s.[value] AS bigint) AND sr.Status = N'Current'
                                                         AND sr.StartedAt >= {since})
                                           OR EXISTS (SELECT 1 FROM wf.ApprovalEvent AS se
                                                       WHERE se.DocumentId = CAST(s.[value] AS bigint) AND se.At >= {since}))
                       ) AS t
                 ORDER BY t.Reason
                """)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return codes.Count == 0 ? null : (NightlyReason)codes[0];
    }
}
