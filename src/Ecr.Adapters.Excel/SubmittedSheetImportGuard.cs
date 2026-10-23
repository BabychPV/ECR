using Ecr.Application.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Adapters.Excel;

/// <summary>
/// ✎ D-442 (борг AN-129): Excel-імпорт ПОДАНИЙ аркуш не пише — і перегляд мусить казати те саме, що застосування.
/// </summary>
/// <remarks>
/// <para>
/// ⛔ Що було. Служба доступу дає погоджувачеві поточного кроку дозвіл на комірки поданого аркуша
/// (<see cref="EditDecision.ByApprover"/>) — для правки в сітці. Перегляд імпорту брав ті самі рішення
/// (<c>CanEditSlicesAsync</c>) і показував погоджувачеві зміни поданого аркуша як дозволені, а застосування під
/// блокуванням аркуша (книжковий шлях <c>PatchCellsHandler.HandleWorkbookAsync</c> → <c>CheckSheetStatus</c>)
/// відмовляло <see cref="EditDenyReason.DocumentSubmitted"/>: людина бачила «буде змінено N комірок», натискала
/// «Застосувати» і отримувала 403.
/// </para>
/// <para>
/// ⚠ Рішення — імпорт для поданого аркуша ЗАКРИТИЙ (так його свідомо лишила lane AN-129: «книжковий шлях не змінено
/// — поданий аркуш імпорт не пише»). D-442 дозволяє погоджувачеві правити значення в сітці, де кожна правка
/// перевіряється під блокуванням і йде в журнал як «правка погоджувача»; масовий запис книгою в аркуш, що чекає
/// погодження, D-442 не вводить. Тож перегляд перетворює дозвіл погоджувача на ту саму відмову
/// <see cref="EditDenyReason.DocumentSubmitted"/>, якою відповість застосування, — з тим самим ключем причини.
/// </para>
/// </remarks>
public static class SubmittedSheetImportGuard
{
    /// <summary>
    /// Рішення, у яких дозвіл дала САМЕ роль погоджувача поданого аркуша, замінено відмовою
    /// <see cref="EditDenyReason.DocumentSubmitted"/>; решта — без змін (той самий об'єкт, якщо замінювати нічого).
    /// </summary>
    /// <param name="decisions">Рішення служби доступу на зрізи книги.</param>
    /// <returns>Рішення для перегляду імпорту.</returns>
    public static IReadOnlyDictionary<long, IReadOnlyDictionary<CellAddress, EditDecision>> Close(
        IReadOnlyDictionary<long, IReadOnlyDictionary<CellAddress, EditDecision>> decisions)
    {
        ArgumentNullException.ThrowIfNull(decisions);

        if (!decisions.Values.Any(slice => slice.Values.Any(IsApproverEdit)))
        {
            return decisions;
        }

        return decisions.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.Values.Any(IsApproverEdit)
                ? (IReadOnlyDictionary<CellAddress, EditDecision>)pair.Value.ToDictionary(
                    cell => cell.Key,
                    cell => IsApproverEdit(cell.Value) ? EditDecision.Deny(EditDenyReason.DocumentSubmitted) : cell.Value)
                : pair.Value);
    }

    private static bool IsApproverEdit(EditDecision decision) => decision.IsAllowed && decision.ByApprover;
}
