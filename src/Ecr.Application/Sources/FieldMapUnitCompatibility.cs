// src/Ecr.Application/Sources/FieldMapUnitCompatibility.cs
using System.Globalization;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Errors;

namespace Ecr.Application.Sources;

/// <summary>
/// Сумісність одиниці джерела й цільової одиниці мапінгу за розмірністю (D-4 приймальної №8, ФВ-16.6/16.7).
/// </summary>
/// <remarks>
/// <para>
/// ⛔ Обидві одиниці відомі на момент налаштування - це поля самого мапінгу. Мапінг «MJ → kg» не переводиться
/// ніколи, тож приймати його значило б створити мапінг, який мовчить: перегляд давав порожнє значення без
/// причини, а нічне перенесення відмовляло лише на першому прогоні із зібраними даними.
/// </para>
/// <para>
/// ⚠ Інтеграл за часом не проходить перевірку розмірностей джерело/ціль: «швидкість × с» → величина
/// (<c>Sm3/h</c> → <c>Sm3</c>) - законний перехід між розмірностями. Його перевіряє конверсія на межі
/// (<see cref="BoundaryUnitConversion"/>) - і ТЕ САМЕ правило виконується тут при налаштуванні (Z2-04), а не лише
/// в нічному перенесенні.
/// </para>
/// <para>
/// ⚠ Невідома одиниця (немає в знімку) несумісністю НЕ вважається: її відсутність ловить існування одиниці
/// (<c>ECR-UOM-0404</c>) або конверсія на межі - тут вигадана відмова була б хибною.
/// </para>
/// </remarks>
public static class FieldMapUnitCompatibility
{
    /// <summary>Ключ каталогу відмови «розмірності одиниць мапінгу різні».</summary>
    public const string MismatchKey = "err.ECR-UOM-0422.fieldMapUnitDimensions";

    /// <summary>Чи несумісні одиниці джерела й цілі за розмірністю.</summary>
    /// <param name="catalog">Знімок довідника одиниць.</param>
    /// <param name="sourceUnitId">Одиниця джерела; <c>null</c> - не оголошена.</param>
    /// <param name="targetUnitId">Цільова одиниця; <c>null</c> - не оголошена.</param>
    /// <param name="aggregation">Згортка; інтеграл за часом пропускається.</param>
    public static bool IsIncompatible(
        UnitCatalogSnapshot catalog, int? sourceUnitId, int? targetUnitId, AggregationKind? aggregation)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        if (aggregation == AggregationKind.TimeIntegral
            || sourceUnitId is not { } source
            || targetUnitId is not { } target
            || source == target)
        {
            return false;
        }

        var sourceDimension = catalog.DimensionOf(source);
        var targetDimension = catalog.DimensionOf(target);

        return sourceDimension != 0 && targetDimension != 0 && sourceDimension != targetDimension;
    }

    /// <summary>Відмовляє <c>422 ECR-UOM-0422</c>, якщо одиниці мапінгу різної розмірності.</summary>
    /// <param name="units">Довідник одиниць; <c>null</c> - перевірку не виконано (тести без довідника).</param>
    /// <param name="sourceField">Поле джерела - для тексту відмови.</param>
    /// <param name="sourceUnitId">Одиниця джерела.</param>
    /// <param name="targetUnitId">Цільова одиниця.</param>
    /// <param name="aggregation">Згортка мапінгу.</param>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="BusinessRuleException"><c>ECR-UOM-0422</c> <c>fieldMapUnitDimensions</c>.</exception>
    public static async Task EnsureAsync(
        IUnitCatalog? units, string sourceField, int? sourceUnitId, int? targetUnitId,
        AggregationKind? aggregation, CancellationToken ct)
    {
        if (units is null)
        {
            return;
        }

        // ⛔ Z2-04: інтеграл за часом не звільнений від перевірки, а перевіряється ІНШИМ правилом - тим самим, що
        // виконує нічне перенесення (`BoundaryUnitConversion.ConvertFolded`): обидві одиниці оголошено, джерело - швидкість
        // «величина / час», чисельник сумісний з ціллю. Раніше мапінг «MJ → kg» з інтегралом приймався, а задача
        // відмовляла щоночі на кожному періоді.
        if (aggregation == AggregationKind.TimeIntegral)
        {
            await EnsureIntegralAsync(units, sourceUnitId, targetUnitId, ct).ConfigureAwait(false);
            return;
        }

        if (sourceUnitId is null || targetUnitId is null || sourceUnitId == targetUnitId)
        {
            return;
        }

        var catalog = await units.GetAsync(ct).ConfigureAwait(false);
        if (!IsIncompatible(catalog, sourceUnitId, targetUnitId, aggregation))
        {
            return;
        }

        var from = CodeOf(catalog, sourceUnitId.Value);
        var to = CodeOf(catalog, targetUnitId.Value);

        throw new BusinessRuleException(
            ErrorCodes.UnitDimensionMismatch,
            $"Одиниця «{from}» не переводиться в «{to}»: вони вимірюють різні величини, тож мапінг поля "
            + $"«{sourceField}» ніколи не запише значення. Оберіть одиниці однієї розмірності.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = MismatchKey,
                ["from"] = from,
                ["to"] = to,
                ["sourceField"] = sourceField,
                ["sourceUnitId"] = sourceUnitId.Value.ToString(CultureInfo.InvariantCulture),
                ["targetUnitId"] = targetUnitId.Value.ToString(CultureInfo.InvariantCulture),
            });
    }

    /// <summary>
    /// Пробна конверсія інтеграла: ті самі відмови, що дала б задача на першому прогоні, - але при налаштуванні.
    /// </summary>
    /// <remarks>
    /// ⚠ Одиниця, якої немає в знімку, відмовою тут не стає (її ловить існування одиниці й конверсія на межі) -
    /// як і в <see cref="IsIncompatible"/>.
    /// </remarks>
    private static async Task EnsureIntegralAsync(
        IUnitCatalog units, int? sourceUnitId, int? targetUnitId, CancellationToken ct)
    {
        var catalog = await units.GetAsync(ct).ConfigureAwait(false);

        if ((sourceUnitId is { } s && catalog.DimensionOf(s) == 0)
            || (targetUnitId is { } t && catalog.DimensionOf(t) == 0))
        {
            return;
        }

        // Значення не важливе: відмова залежить лише від одиниць.
        _ = BoundaryUnitConversion.ConvertFolded(
            AggregationKind.TimeIntegral, 0m, sourceUnitId, targetUnitId, catalog);
    }

    private static string CodeOf(UnitCatalogSnapshot catalog, int unitId)
        => catalog.Units.Values.FirstOrDefault(u => u.Id == unitId)?.Code
           ?? unitId.ToString(CultureInfo.InvariantCulture);
}
