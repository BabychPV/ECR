using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Workflow;

namespace Ecr.Infrastructure.Persistence;

/// <summary>
/// RS (копії аркушів, рішення людини 03.11): відбір оригіналів серед екземплярів таблиць і станів аркушів.
/// </summary>
/// <remarks>
/// ⛔ Інтеграція, матеріалізація зібраних даних, події джерел і рядки з дозволу пишуть ЛИШЕ в оригінал (Q5): копія —
/// порожня форма, яку заповнює людина. Без цього відбору запис за <c>(документ, таблиця, період)</c> після появи копії
/// або падав би на дублі ключа (<c>ToDictionary</c>), або мовчки потрапляв у копію. Сторож
/// <c>SheetCopyQuerySitesTests</c> (Architecture) тримає перелік місць, що читають ці таблиці.
/// </remarks>
public static class SheetCopyQueries
{
    /// <summary>Лише екземпляри оригінальних аркушів (<c>SheetCopyId IS NULL</c>).</summary>
    /// <param name="source">Запит до <c>doc.TableInstance</c>.</param>
    /// <returns>Звужений запит.</returns>
    public static IQueryable<TableInstance> OriginalOnly(this IQueryable<TableInstance> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return source.Where(t => t.SheetCopyId == null);
    }

    /// <summary>Лише стани оригінальних аркушів (<c>SheetCopyId IS NULL</c>).</summary>
    /// <param name="source">Запит до <c>wf.ApprovalState</c>.</param>
    /// <returns>Звужений запит.</returns>
    public static IQueryable<ApprovalState> OriginalOnly(this IQueryable<ApprovalState> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return source.Where(s => s.SheetCopyId == null);
    }
}
