// src/Ecr.Application/Ports/IUnitCatalog.cs

namespace Ecr.Application.Ports;

/// <summary>
/// Довідник одиниць <c>uom.*</c> у формі, потрібній перевірці публікації.
/// </summary>
/// <remarks>
/// ⚠ Порт віддає **знімок цілком**, а не відповідає на питання по одному.
/// Перевірка публікації обходить кожну формулу версії, а їх у шаблоні
/// 2 658 — запит на кожну одиницю перетворив би публікацію на тисячі
/// звернень до бази. Одиниць у системі десятки, і знімок коштує один запит.
/// </remarks>
public interface IUnitCatalog
{
    /// <summary>Читає весь довідник одиниць.</summary>
    public Task<UnitCatalogSnapshot> GetAsync(CancellationToken ct);
}

/// <summary>Знімок довідника одиниць.</summary>
/// <param name="Units">Одиниці за кодом.</param>
/// <param name="Derived">Похідні: <c>чисельник|знаменник</c> → одиниця.</param>
public sealed record UnitCatalogSnapshot(
    IReadOnlyDictionary<string, UnitRef> Units,
    IReadOnlyDictionary<string, int> Derived)
{
    /// <summary>Порожній довідник — коли одиниці ще не заведені.</summary>
    public static UnitCatalogSnapshot Empty { get; } = new(
        new Dictionary<string, UnitRef>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, int>(StringComparer.Ordinal));

    /// <summary>Розмірність одиниці; нуль — одиниці немає.</summary>
    /// <param name="unitId">Ідентифікатор одиниці.</param>
    public byte DimensionOf(int unitId)
        => Units.Values.FirstOrDefault(u => u.Id == unitId)?.DimensionId ?? 0;
}

/// <summary>Одиниця довідника.</summary>
/// <param name="Id">Ідентифікатор.</param>
/// <param name="Code">Код: <c>kg</c>, <c>t</c>, <c>m3</c>.</param>
/// <param name="DimensionId">Розмірність; конверсія можлива лише в її межах.</param>
/// <param name="FactorToBase">Множник переходу до базової одиниці розмірності.</param>
/// <param name="OffsetToBase">Зсув до базової; ненульовий лише в температури.</param>
/// <remarks>
/// ⚠ Множник і зсув входять у знімок, а не читаються окремо. Без них
/// <c>CONVERT</c> у рантаймі множив би на одиницю і мовчки повертав те саме
/// число: тонни лишалися б тоннами під виглядом кілограмів.
/// </remarks>
public sealed record UnitRef(
    int Id, string Code, byte DimensionId, decimal FactorToBase = 1m, decimal OffsetToBase = 0m);
