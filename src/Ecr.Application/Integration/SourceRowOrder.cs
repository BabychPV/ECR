// src/Ecr.Application/Integration/SourceRowOrder.cs
using System.Globalization;
using Ecr.Application.Errors;
using Ecr.Application.Ports;

namespace Ecr.Application.Integration;

/// <summary>
/// Контракт порядку рядків запиту значень: мітки не спадають.
/// </summary>
/// <remarks>
/// ⛔ Хвіст обрізаного батча <c>[остання мітка, ToUtc)</c> правдивий лише для
/// впорядкованого результату: запит без <c>ORDER BY</c> записав би покриття за
/// інтервал, у якому точки раніше хвоста пропущено. Аудит A7 додав перевірку в
/// <c>SqlDataSource</c>, а <c>PiSqlClientDataSource</c> лишився без неї (L3-10);
/// тепер обидва адаптери кличуть цей один помічник, і логіка не розходиться.
/// Рівні мітки дозволені.
/// </remarks>
public static class SourceRowOrder
{
    /// <summary>Відмова <c>ECR-INT-0422</c> <c>.timestampsOutOfOrder</c>, якщо мітка менша за попередню.</summary>
    /// <param name="previous">Попередня мітка; <c>null</c> — рядок перший.</param>
    /// <param name="current">Поточна мітка.</param>
    /// <param name="dataSource">Код джерела — для тексту відмови.</param>
    /// <param name="sourcePath">Шлях сутності — для тексту відмови.</param>
    public static void EnsureOrdered(DateTime? previous, DateTime current, string dataSource, string sourcePath)
    {
        if (previous is not { } before || current >= before)
        {
            return;
        }

        var earlier = before.ToString("O", CultureInfo.InvariantCulture);
        var later = current.ToString("O", CultureInfo.InvariantCulture);

        throw new BusinessRuleException(
            IExternalDataSource.QueryRefusedCode,
            $"Запит значень джерела {dataSource} для «{sourcePath}» повертає мітки не по черзі "
            + $"({later} після {earlier}): без ORDER BY за міткою хвіст обрізаного батча хибний.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-INT-0422.timestampsOutOfOrder",
                ["dataSource"] = dataSource,
                ["sourcePath"] = sourcePath,
                ["previous"] = earlier,
                ["current"] = later,
            });
    }
}
