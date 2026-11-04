using Ecr.Application.Ports;

namespace Ecr.Application.Recalculation;

/// <summary>
/// RS (копії аркушів): «вид» документа для одного прогону перерахунку — таблиці аркуша-копії беруться з екземплярів
/// ЦІЄЇ копії, усі інші таблиці — з оригіналів.
/// </summary>
/// <param name="CopyId">Копія аркуша (<c>doc.DocumentSheet.Id</c>).</param>
/// <param name="SheetDefId">Аркуш шаблону, копією якого вона є.</param>
/// <param name="TableDefIds">Таблиці цього аркуша у версії шаблону прогону.</param>
/// <remarks>
/// ⛔ Навіщо. Формула копії читає СВОЇ комірки (самопосилання аркуша), а все інше — оригінал: копія не змішується ні
/// з оригіналом, ні з іншими копіями (рішення людини 03.11, без агрегації). Прогін оригіналу бачить лише оригінали.
/// Той самий вид застосовується до ІНШИХ періодів (<c>[Period:-1]</c>): копія лишається тією самою копією.
/// </remarks>
internal sealed record CopyScope(long CopyId, int SheetDefId, IReadOnlySet<int> TableDefIds)
{
    /// <summary>Екземпляри виду: для оригіналу (<paramref name="scope"/> = <c>null</c>) — лише оригінали.</summary>
    public static IReadOnlyList<TableInstanceRef> Apply(IReadOnlyList<TableInstanceRef> all, CopyScope? scope)
    {
        if (all.All(i => i.SheetCopyId is null) && scope is null)
        {
            return all;
        }

        return
        [
            .. all.Where(i => i.SheetCopyId is null
                ? scope is null || !scope.TableDefIds.Contains(i.TableDefId)
                : scope is not null && i.SheetCopyId == scope.CopyId),
        ];
    }
}

/// <summary>Які «види» (оригінал, копії) перераховує повний прогін документа.</summary>
/// <param name="Original">Оригінал.</param>
/// <param name="CopyId">Лише ця копія (разом з <paramref name="AllCopies"/> = <c>false</c>).</param>
/// <param name="AllCopies">Усі копії документа.</param>
internal sealed record ScopeFilter(bool Original, long? CopyId, bool AllCopies)
{
    /// <summary>Оригінал і всі копії.</summary>
    public static readonly ScopeFilter All = new(true, null, true);

    /// <summary>Лише оригінал.</summary>
    public static readonly ScopeFilter OriginalOnly = new(true, null, false);

    /// <summary>Лише одна копія.</summary>
    public static ScopeFilter ForCopy(long copyId) => new(false, copyId, false);

    /// <summary>Чи входить екземпляр-представник у прогін.</summary>
    public bool Includes(long? instanceCopyId)
        => instanceCopyId is null ? Original : AllCopies || instanceCopyId == CopyId;

    /// <summary>Фільтр за викликачем: <c>sheetCopyId</c> задано — копія, інакше — оригінал.</summary>
    public static ScopeFilter Of(long? sheetCopyId) => sheetCopyId is { } id ? ForCopy(id) : OriginalOnly;
}
