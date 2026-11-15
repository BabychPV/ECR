using Ecr.Domain.Entities.Documents;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>
/// ⛔ B56: «живі» записи історії дозволів (j) для лічильників «де використовується» — дозволу (<c>PermitStore</c>) і запису
/// довідника (<c>RegistryStore.FindEntryDocumentsAsync</c>, C-13). Одне правило на всіх споживачів.
/// </summary>
/// <remarks>
/// <para>
/// Логічне видалення рядка (<c>TableRow.SoftDelete</c>) сегменти й частини НЕ прибирає, а видалений рядок — не використання
/// (як <c>where !r.IsDeleted</c> у комірках). Тому ціль-рядок вимагає невидалений рядок того ж документа, таблиці, копії
/// аркуша й ключа: для сегмента (він логічний, без періоду) — у будь-якому періоді, для частини — у її місяці. Сегмент поля
/// шапки (<c>HeaderFieldDefId != null</c>) — як є: шапка не видаляється логічно.
/// </para>
/// <para>
/// Корельований <c>EXISTS</c> іде від <c>doc.TableInstance</c> документа (одиниці-десятки на документ) до рядка за ключем —
/// без N+1: усе в тому ж SQL-запиті споживача.
/// </para>
/// </remarks>
internal static class PermitHistoryUsage
{
    /// <summary>Сегменти, чия ціль жива: поле шапки або невидалений рядок.</summary>
    /// <remarks>
    /// ⛔ Дві непересічні гілки через <c>UNION ALL</c>, а не <c>HeaderFieldDefId IS NOT NULL OR EXISTS (…)</c>: OR над
    /// корельованим підзапитом оптимізатор не розкладає на seek-и (F13 p227, сторож <c>RegistryEntryUsageTests</c>).
    /// </remarks>
    public static IQueryable<PermitSegment> LiveSegments(EcrDbContext db)
        => db.PermitSegments.AsNoTracking().Where(s => s.HeaderFieldDefId != null)
            .Concat(db.PermitSegments.AsNoTracking().Where(s =>
                s.HeaderFieldDefId == null
                && db.TableInstances.Any(ti =>
                    ti.DocumentId == s.DocumentId && ti.TableDefId == s.TableDefId && ti.SheetCopyId == s.SheetCopyId
                    && db.TableRows.Any(r =>
                        r.PeriodKeyValue == ti.PeriodKeyValue && r.TableInstanceId == ti.Id
                        && r.RowKeyValue == s.RowKey && !r.IsDeleted))));

    /// <summary>
    /// Сегменти, чия ціль жива САМЕ в періоді <paramref name="periodKey"/>: поле шапки або рядок, невидалений у цьому
    /// періоді (як комірки під фільтром періоду). Рядок, видалений лише в цьому періоді, але живий в іншому, тут не рахується;
    /// без фільтра періоду — <see cref="LiveSegments"/> («бодай в одному періоді»).
    /// </summary>
    public static IQueryable<PermitSegment> LiveSegmentsIn(EcrDbContext db, int periodKey)
        => db.PermitSegments.AsNoTracking().Where(s => s.HeaderFieldDefId != null)
            .Concat(db.PermitSegments.AsNoTracking().Where(s =>
                s.HeaderFieldDefId == null
                && db.TableInstances.Any(ti =>
                    ti.PeriodKeyValue == periodKey
                    && ti.DocumentId == s.DocumentId && ti.TableDefId == s.TableDefId && ti.SheetCopyId == s.SheetCopyId
                    && db.TableRows.Any(r =>
                        r.PeriodKeyValue == ti.PeriodKeyValue && r.TableInstanceId == ti.Id
                        && r.RowKeyValue == s.RowKey && !r.IsDeleted))));

    /// <summary>Частини поділених місяців, чий рядок у цьому місяці не видалено.</summary>
    public static IQueryable<CellPermitPart> LiveParts(EcrDbContext db)
        => db.CellPermitParts.AsNoTracking().Where(p =>
            db.TableInstances.Any(ti =>
                ti.PeriodKeyValue == p.PeriodKeyValue && ti.DocumentId == p.DocumentId && ti.TableDefId == p.TableDefId
                && ti.SheetCopyId == p.SheetCopyId
                && db.TableRows.Any(r =>
                    r.PeriodKeyValue == ti.PeriodKeyValue && r.TableInstanceId == ti.Id
                    && r.RowKeyValue == p.RowKey && !r.IsDeleted)));
}
