using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Services;

namespace Ecr.Adapters.PiAf;

/// <summary>
/// Конверсія одиниць на межі інтеграції.
/// </summary>
/// <remarks>
/// Атрибути PI AF мають власний UOM, і це **найчастіше джерело мовчазних
/// розбіжностей у числах**. Тому мапінг зберігає <c>SourceUnitId</c> і
/// <c>TargetUnitId</c> явно, а несподівана зміна одиниці в джерелі **зупиняє
/// збір**, а не конвертує «як здається» (ФВ-16.9).
/// </remarks>
public sealed class SourceUnitConverter(UnitConverter converter, IUnitCatalog catalog)
{
    /// <summary>Одиниця джерела не та, що оголошена в мапінгу.</summary>
    public const string UnitChangedCode = "ECR-INT-0422";

    /// <summary>Знімок довідника; читається один раз на прогін.</summary>
    private UnitCatalogSnapshot? units;

    /// <summary>Довідник одиниць для цього прогону.</summary>
    /// <param name="ct">Скасування.</param>
    /// <remarks>
    /// ⚠ Читається знімком і один раз. Точок у батчі тисячі, і запит на
    /// одиницю кожної перетворив би збір на тисячі звернень до бази —
    /// повільно рівно настільки, щоб збір перестали запускати.
    /// </remarks>
    public async Task<UnitCatalogSnapshot> UnitsAsync(CancellationToken ct)
        => units ??= await catalog.GetAsync(ct).ConfigureAwait(false);

    /// <summary>
    /// Перевіряє, що джерело віддає одиницю, яку оголошено в мапінгу.
    /// </summary>
    /// <param name="declaredSourceUnitId">Одиниця з мапінгу; <c>null</c> — не оголошена.</param>
    /// <param name="actualSourceUnitCode">Одиниця, яку фактично повернуло джерело.</param>
    /// <param name="catalogSnapshot">Знімок довідника.</param>
    /// <param name="sourcePath">Атрибут — щоб у повідомленні було видно, який саме.</param>
    /// <exception cref="BusinessRuleException">
    /// Фактична одиниця не збігається з оголошеною — <c>ECR-INT-0422</c>.
    /// </exception>
    /// <remarks>
    /// ⛔ Це **зупинка збору**, а не попередження. Мовчазна конверсія «як
    /// здається» тут гірша за зупинку: вона дає правдоподібні числа, помилку
    /// в яких знайдуть через місяць на звірці — коли звіт уже подано.
    /// </remarks>
    public static void EnsureDeclaredUnit(
        int? declaredSourceUnitId,
        string? actualSourceUnitCode,
        UnitCatalogSnapshot catalogSnapshot,
        string sourcePath)
    {
        ArgumentNullException.ThrowIfNull(catalogSnapshot);

        // Джерело не повідомило одиниці — порівнювати нема з чим. Це не
        // «збіглося»: безрозмірність тут не підставляється, значення просто
        // лягає в одиниці, оголошеній у мапінгу (ФВ-16.12).
        if (string.IsNullOrWhiteSpace(actualSourceUnitCode) || declaredSourceUnitId is not { } declared)
        {
            return;
        }

        if (catalogSnapshot.Units.TryGetValue(actualSourceUnitCode, out var actual) && actual.Id == declared)
        {
            return;
        }

        throw new BusinessRuleException(
            UnitChangedCode,
            $"Атрибут «{sourcePath}» повертає одиницю «{actualSourceUnitCode}», "
            + $"а в мапінгу оголошено одиницю {declared}. Збір зупинено.",
            new Dictionary<string, object?>
            {
                ["sourcePath"] = sourcePath,
                ["declaredUnitId"] = declared,
                ["actualUnitCode"] = actualSourceUnitCode,
            });
    }

    /// <summary>Конвертує значення на межі.</summary>
    /// <param name="value">Значення в одиниці джерела.</param>
    /// <param name="declaredSourceUnitId">Одиниця, оголошена в мапінгу.</param>
    /// <param name="actualSourceUnitCode">Одиниця, яку фактично повернуло джерело.</param>
    /// <param name="targetUnitId">Цільова одиниця.</param>
    /// <param name="catalogSnapshot">Знімок довідника.</param>
    /// <exception cref="BusinessRuleException">
    /// Фактична одиниця не збігається з оголошеною — <c>ECR-INT-0422</c>.
    /// </exception>
    /// <remarks>
    /// ⚠ Явні конверсії <c>uom.Conversion</c> сюди не доходять: знімок
    /// довідника їх не несе, і маршрут іде через базову одиницю. Для межі
    /// інтеграції це правильно — <c>LegacyPinned</c> існує заради збігу з
    /// числом чинної системи в **поданому звіті**, а не заради сирої точки.
    /// </remarks>
    public decimal Convert(
        decimal value,
        int declaredSourceUnitId,
        string? actualSourceUnitCode,
        int targetUnitId,
        UnitCatalogSnapshot catalogSnapshot)
    {
        ArgumentNullException.ThrowIfNull(catalogSnapshot);

        EnsureDeclaredUnit(declaredSourceUnitId, actualSourceUnitCode, catalogSnapshot, "—");

        if (declaredSourceUnitId == targetUnitId)
        {
            return value;
        }

        // ⚠ Сама арифметика — в доменному UnitConverter, а не тут. Другий
        // множник у другому місці розійшовся б із першим тихо: обидва дають
        // число, і жодне не падає.
        return converter.Convert(
            value,
            Spec(catalogSnapshot, declaredSourceUnitId),
            Spec(catalogSnapshot, targetUnitId),
            explicitConversion: null);
    }

    /// <summary>Одиниця довідника у формі, потрібній конверсії.</summary>
    private static UnitSpec Spec(UnitCatalogSnapshot catalogSnapshot, int unitId)
    {
        var unit = catalogSnapshot.Units.Values.FirstOrDefault(u => u.Id == unitId)
                   ?? throw new BusinessRuleException(
                       UnitChangedCode,
                       $"Одиниці {unitId} немає в довіднику: конверсія на межі неможлива.",
                       new Dictionary<string, object?> { ["unitId"] = unitId });

        return new UnitSpec(unit.Id, unit.Code, unit.DimensionId, unit.FactorToBase, unit.OffsetToBase);
    }
}
