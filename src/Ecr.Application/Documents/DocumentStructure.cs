using System.Globalization;
using Ecr.Application.Errors;
using Ecr.Domain.Errors;

namespace Ecr.Application.Documents;

/// <summary>
/// Звірка версії шаблону, за якою писар будував запит, із версією проєкту,
/// прочитаною під блокуванням структури документа (L6-02).
/// </summary>
/// <remarks>
/// ⛔ Що було. Запис комірок будував контекст (колонки, рядки, аркуші) за версією
/// проєкту ДО своєї транзакції. Перенос документа на іншу версію шаблону, що
/// зафіксувався між цим читанням і записом, лишав запис під старими
/// <c>ColumnDefId</c>/<c>SheetDefId</c> — значення, яких нова версія не бачить.
/// Тепер писар бере <see cref="Ports.ISheetEditGate.EnterStructureAsync"/> першою
/// дією транзакції і звіряє версію тут.
/// </remarks>
public static class DocumentStructure
{
    /// <summary>Ключ тексту відмови в каталозі.</summary>
    public const string StructureChangedKey = "err.ECR-DOC-4091.structureChanged";

    /// <summary>
    /// Кидає <c>409 ECR-DOC-4091</c>, якщо версія під блокуванням відрізняється від
    /// тієї, за якою будувався запит.
    /// </summary>
    /// <param name="locked">Версія, прочитана під блокуванням; <c>null</c> — документа немає (відмовить сам запис).</param>
    /// <param name="expected">Версія, за якою будувався запит.</param>
    /// <param name="documentId">Документ.</param>
    /// <exception cref="ConcurrencyConflictException">Версії розійшлися.</exception>
    public static void EnsureUnchanged(int? locked, int expected, long documentId)
    {
        if (locked is not { } current || current == expected)
        {
            return;
        }

        throw new ConcurrencyConflictException(
            ErrorCodes.SheetBusy,
            string.Create(
                CultureInfo.InvariantCulture,
                $"Документ {documentId} перенесено на версію шаблону {current}, поки готувався запис за версією {expected}: нічого не записано, оновіть сторінку."),
            new Dictionary<string, object?>
            {
                ["messageKey"] = StructureChangedKey,
                ["documentId"] = documentId.ToString(CultureInfo.InvariantCulture),
            });
    }
}
