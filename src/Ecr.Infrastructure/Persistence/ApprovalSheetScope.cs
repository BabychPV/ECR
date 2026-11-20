// src/Ecr.Infrastructure/Persistence/ApprovalSheetScope.cs
using Ecr.Domain.Entities.Documents;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>Рядок складу документа, що входить до «усіх аркушів» періоду.</summary>
/// <param name="Id">Рядок складу (для копії — <c>SheetCopyId</c> її стану).</param>
/// <param name="DocumentId">Документ.</param>
/// <param name="SheetDefId">Аркуш шаблону.</param>
/// <param name="SheetCode">Код аркуша.</param>
/// <param name="CopyOfId">Оригінал, копією якого є рядок; <c>null</c> — оригінал.</param>
internal sealed record ApprovalSheetRow(long Id, long DocumentId, int SheetDefId, string SheetCode, long? CopyOfId);

/// <summary>
/// D-1: ЄДИНЕ правило «які аркуші документа затверджує процес затвердження» — склад документа (<c>doc.DocumentSheet</c>,
/// <c>IsIncluded</c>), як у <c>WorkflowStore.AllSheetsApprovedAsync</c>, переліку станів аркушів і смузі документа. Видимість аркуша
/// (<c>SheetDef.IsVisible</c>) — презентаційна й затвердження не обмежує: прихований аркуш затверджують так само, тож він
/// входить. Логічно видалений аркуш шаблону (<c>IsDeleted</c>) затвердити вже не можна — він не блокує «усі затверджені».
/// </summary>
internal static class ApprovalSheetScope
{
    /// <summary>
    /// Рядки складу, що входять до «усіх аркушів»: включені, аркуш шаблону не видалений логічно. ЄДИНЕ місце правила —
    /// його беруть оцінка впливу, <c>WorkflowStore.AllSheetsApprovedAsync</c>, перелік станів аркушів і фільтр/смуга стану документа.
    /// </summary>
    /// <param name="db">Контекст (підзапит до <c>cfg.SheetDef</c> перекладається в SQL).</param>
    public static IQueryable<DocumentSheet> InScope(EcrDbContext db)
        => db.DocumentSheets.Where(s => s.IsIncluded && !db.SheetDefs.Any(sd => sd.Id == s.SheetDefId && sd.IsDeleted));

    /// <summary>Склад документів за правилом затвердження; за зростанням документа, порядку аркуша, ідентифікатора.</summary>
    /// <param name="db">Контекст.</param>
    /// <param name="documentIds">Документи.</param>
    /// <param name="ct">Токен скасування.</param>
    public static async Task<List<ApprovalSheetRow>> RowsAsync(EcrDbContext db, IReadOnlyCollection<long> documentIds, CancellationToken ct)
    {
        var ids = documentIds.ToList();
        return await (
                from s in InScope(db).AsNoTracking()
                join sd in db.SheetDefs.AsNoTracking() on s.SheetDefId equals sd.Id
                where ids.Contains(s.DocumentId)
                orderby s.DocumentId, sd.Ordinal, sd.Id, s.CopyOrdinal
                select new ApprovalSheetRow(s.Id, s.DocumentId, sd.Id, sd.Code, s.CopyOfId))
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }
}
