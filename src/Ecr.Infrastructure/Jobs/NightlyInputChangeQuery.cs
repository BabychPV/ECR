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
/// <param name="TemplateVersionIds">Версії шаблону документа за його екземплярами (записи журналу структури).</param>
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
/// Входи (кожен — <c>EXISTS</c> з межею <c>&gt;= since</c>; перевіряються по черзі до першої знайденої, порядок — і є причина):
/// <list type="number">
///   <item>правка комірки документа будь-якого походження, крім <c>Recalculation</c>/<c>CalcError</c>/<c>CalcErrorResolved</c>
///   (людина, імпорт Excel, інтеграція PI й синк подій, дозволи, ручне значення формули), і додавання/видалення рядка
///   (<c>aud.RowChange</c>);</item>
///   <item>екземпляри таблиць документа, створені чи змінені після межі (перенос версії шаблону, нова таблиця, новий період);</item>
///   <item>дані будь-якого довідника (<c>cfg.RegistryDef.DataChangedAt</c>; зміна лише опису мітки не дає, D-163). ✎ Огляд
///   M2(b): БУДЬ-ЯКОГО, а не лише тих, що їх читає документ за <c>cfg.RegistryUse</c>, — цей граф неповний для рішення
///   «пропустити»: <c>REGFIELD(@Arg, …)</c> вибирає довідник за значенням Lookup-колонки під час розрахунку (D-556, ребра
///   немає), скрипт читає довідники через бібліотеки й імпорти (<c>calc.MethodologyImport</c>), правило довідника читає інший
///   довідник (ребра <c>SourceKind = 2</c>, ланцюги), а <c>RegistryImpactStore</c> бере лише ребра версій методологій
///   (<c>SourceKind = 1</c>). Хибне звуження сховало б перерахунок; ціна глобальності — одна повна ніч після правки довідника;</item>
///   <item>публікація версії шаблону чи методології, перемикання рушія скрипта (<c>aud.PublicationEvent</c>);</item>
///   <item>журнал структури (<c>aud.StructureChange</c>) версій документа (формули, колонки, правила) і глобальний
///   (<c>TemplateVersionId = 0</c>) — лише типи <see cref="GlobalStructureEntityTypes"/> (огляд M2(a): прив'язки, пакети,
///   довідники; маршрути погодження, звіти, дозволи, інтеграція — ні). Записи періодів (під версією проєкту) не потрібні — їх
///   перехід ловить гілка станів періодів нижче (і B74 не дає читати <c>Project.TemplateVersionId</c>). Крім власних записів перерахунку —
///   <c>SheetRecalc</c> (журнал пропущених/перерахованих аркушів) і <c>ReportSnapshot</c> (зріз застарів), — і блокувань рядків
///   (<c>RowLock</c>: не вхід розрахунку): інакше нічний прогін сам собі давав би «зміну» щоночі;</item>
///   <item>перехід стану періоду проєкту (<c>doc.Period.StateChangedAt</c>, зокрема Reopen) і виняток «місяць відкрито для
///   документа» (відкрито чи закрито);</item>
///   <item>повернення затвердженого аркуша документа (<c>wf.ApprovalEvent.FromStatus = Approved</c>): поки аркуш був затверджений,
///   перерахунок його пропускав (D-493/D-510) — тепер його треба порахувати. Саме затвердження входом не є;</item>
///   <item>документ-джерело (B179): правка комірки, новий актуальний прогін (результати методологій джерела), будь-яка подія
///   погодження (стан-ворота <c>ApprovedOnly</c>);</item>
///   <item>✎ огляд H1: дотик документа (<c>doc.Document.ModifiedAt</c>, <c>IDocumentStore.TouchAsync</c>) — зміни, яких немає
///   ні в <c>aud.CellChange</c>, ні в <c>aud.RowChange</c> (лише <c>aud.SecurityEvent</c>): шапка документа (<c>HDR.*</c>,
///   <c>DocumentHeaderHandlers</c>), додавання/перейменування/видалення копії аркуша (<c>DocumentSheetCopyHandler</c> — видаляє
///   екземпляри, рядки й комірки без журналу правок), видалення рядка події синком (<c>SourceEventSyncJob</c>). Перерахунок
///   документ не торкає (перевіряє інтеграційний тест «без самопробудження»).</item>
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
    /// <summary>Типи записів журналу структури ВЕРСІЙ ДОКУМЕНТА, які пише сам перерахунок чи які не є входом розрахунку.</summary>
    internal static readonly string[] IgnoredStructureEntityTypes =
    [
        RecalculationSkips.AuditEntityType,
        RunCalculationHandler.SnapshotAuditEntityType,
        Ecr.Application.Documents.RowLocks.RowLockRules.AuditEntityType,
    ];

    /// <summary>
    /// ✎ Огляд M2(a): ГЛОБАЛЬНИЙ журнал структури (<c>TemplateVersionId = 0</c>) будить документи лише цими типами — тими, що
    /// змінюють розрахунок: прив'язки методологій до колонок, імпорт пакета методологій, опис і записи довідників (правила,
    /// ієрархія, зовнішні ключі синку).
    /// </summary>
    /// <remarks>
    /// Поза переліком — свідомо, бо до розрахунку не доходять: маршрути погодження (<c>wf.ApprovalRoute</c>), версії звітів
    /// (<c>ReportVersion</c>), адміністрування дозволів (<c>cfg.PermitTypeDef</c>, <c>cfg.PermitGroup</c> — значення дозволів
    /// доходять у комірки записом <c>Permit</c>, гілка правок), налаштування інтеграції й політика реєстру джерел (збір пише
    /// комірки — гілка правок), політика періодів проєкту (переходи станів — гілка періодів), чернетки опису довідника
    /// (<c>cfg.RegistryDefinitionDraft</c>, до публікації не діють), видалення рядка синком подій (<c>ext.SourceEventLink</c> —
    /// синк торкається документа, гілка «документ»).
    /// </remarks>
    internal static readonly string[] GlobalStructureEntityTypes =
    [
        "cfg.CalculationBinding",
        "calc.MethodologyPackage",
        "cfg.RegistryDef",
        "dic.RegistryEntry",
        "dic.RegistryExternalKey",
    ];

    /// <summary>Типи публікацій, що змінюють розрахунок.</summary>
    internal static readonly string[] PublicationEntityTypes = ["TemplateVersion", "calc.MethodologyVersion"];

    /// <summary>Перша знайдена зміна входу після межі; <c>null</c> — нічого не змінилось.</summary>
    /// <param name="db">Контекст.</param>
    /// <param name="probe">Документ і межа.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// ✎ Огляд L4: один <c>CASE WHEN EXISTS … WHEN EXISTS …</c> — гілки перевіряються по черзі й зупиняються на першій
    /// знайденій (доти <c>UNION ALL</c> рахував усі). Причина — номер першої гілки, що спрацювала (<see cref="NightlyReason"/>).
    /// Актуальний прогін документа-джерела шукається через його проєкт — префікс фільтрованого
    /// <c>UX_CalculationRun_Current (ProjectId, PeriodKey, DocumentId) WHERE Status = 'Current'</c>, а не за самим
    /// <c>DocumentId</c>, з якого не починається жоден індекс.
    /// </remarks>
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
        var (global0, global1, global2, global3, global4) = (
            GlobalStructureEntityTypes[0], GlobalStructureEntityTypes[1], GlobalStructureEntityTypes[2],
            GlobalStructureEntityTypes[3], GlobalStructureEntityTypes[4]);
        var (published0, published1) = (PublicationEntityTypes[0], PublicationEntityTypes[1]);
        var (recalc, calcError, calcResolved) =
            (CellChangeOrigins.Recalculation, CellChangeOrigins.CalcError, CellChangeOrigins.CalcErrorResolved);

        var codes = await db.Database
            .SqlQuery<int>($"""
                /* ecr:nightly-input-change */
                SELECT CASE
                  WHEN EXISTS (SELECT 1 FROM aud.CellChange AS c
                                WHERE c.DocumentId = {probe.DocumentId} AND c.ChangedAt >= {since}
                                  AND c.Origin NOT IN ({recalc}, {calcError}, {calcResolved}))
                    OR EXISTS (SELECT 1 FROM aud.RowChange AS rc
                                WHERE rc.DocumentId = {probe.DocumentId} AND rc.ChangedAt >= {since})
                    THEN 1
                  WHEN EXISTS (SELECT 1 FROM doc.TableInstance AS ti
                                WHERE ti.PeriodKey IN (SELECT p.PeriodKey FROM doc.Period AS p WHERE p.ProjectId = {probe.ProjectId})
                                  AND ti.DocumentId = {probe.DocumentId}
                                  AND (ti.CreatedAt >= {since} OR ti.ModifiedAt >= {since}))
                    THEN 2
                  WHEN EXISTS (SELECT 1 FROM cfg.RegistryDef AS rd WHERE rd.DataChangedAt >= {since})
                    THEN 3
                  WHEN EXISTS (SELECT 1 FROM aud.PublicationEvent AS pe
                                WHERE pe.ChangedAt >= {since} AND pe.EntityType IN ({published0}, {published1}))
                    THEN 4
                  WHEN EXISTS (SELECT 1 FROM aud.StructureChange AS sc
                                WHERE sc.ChangedAt >= {since}
                                  AND ((sc.TemplateVersionId = 0
                                        AND sc.EntityType IN ({global0}, {global1}, {global2}, {global3}, {global4}))
                                       OR (sc.TemplateVersionId IN (SELECT CAST([value] AS int) FROM OPENJSON({versions}))
                                           AND sc.EntityType NOT IN ({ignored0}, {ignored1}, {ignored2}))))
                    THEN 5
                  WHEN EXISTS (SELECT 1 FROM doc.Period AS pp
                                WHERE pp.ProjectId = {probe.ProjectId} AND pp.StateChangedAt >= {since})
                    OR EXISTS (SELECT 1 FROM doc.DocumentPeriodReopen AS dr
                                WHERE dr.DocumentId = {probe.DocumentId}
                                  AND (dr.OpenedAt >= {since} OR dr.ClosedAt >= {since}))
                    THEN 6
                  WHEN EXISTS (SELECT 1 FROM wf.ApprovalEvent AS e
                                WHERE e.DocumentId = {probe.DocumentId} AND e.At >= {since} AND e.FromStatus = {approved})
                    THEN 7
                  WHEN EXISTS (SELECT 1
                                 FROM OPENJSON({sources}) AS s
                                 JOIN doc.Document AS sd ON sd.Id = CAST(s.[value] AS bigint)
                                WHERE EXISTS (SELECT 1 FROM aud.CellChange AS sc2
                                               WHERE sc2.DocumentId = sd.Id AND sc2.ChangedAt >= {since}
                                                 AND sc2.Origin NOT IN ({recalc}, {calcError}, {calcResolved}))
                                   OR EXISTS (SELECT 1 FROM calc.CalculationRun AS sr
                                               WHERE sr.ProjectId = sd.ProjectId AND sr.DocumentId = sd.Id
                                                 AND sr.Status = N'Current' AND sr.StartedAt >= {since})
                                   OR EXISTS (SELECT 1 FROM wf.ApprovalEvent AS se
                                               WHERE se.DocumentId = sd.Id AND se.At >= {since}))
                    THEN 8
                  WHEN EXISTS (SELECT 1 FROM doc.Document AS dm
                                WHERE dm.Id = {probe.DocumentId} AND dm.ModifiedAt >= {since})
                    THEN 9
                  ELSE 0
                END AS Value
                """)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return codes.Count == 0 || codes[0] == 0 ? null : (NightlyReason)codes[0];
    }
}
