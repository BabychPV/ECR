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
/// <param name="DimensionCode">
/// Код розмірності з довідника <c>uom.Dimension</c> (<c>kg</c> → <c>Mass</c>).
/// </param>
/// <param name="SymbolL10n">
/// Позначення мовами каталогу (UI-21: колонка «Unit» переліку без N+1 по <c>GET /units/{id}</c>);
/// <c>null</c> — тест-дублер не задав.
/// </param>
/// <param name="NameL10n">Назва мовами каталогу; <c>null</c> — не задано.</param>
/// <param name="IsBase">Базова одиниця розмірності (колонка «Base unit»).</param>
/// <param name="UsedIn">
/// Скільки колонок шаблонів і полів довідників тримає одиницю (колонка «Used in»). Лише в
/// <c>GET /units</c> і лише для того, хто має <c>Uom.EditCatalog</c>; інакше <c>null</c> (не нуль:
/// «не знаю» не те саме, що «ніде»). Знімок каталогу його не несе.
/// </param>
/// <remarks>
/// ⚠ Множник і зсув входять у знімок, а не читаються окремо. Без них
/// <c>CONVERT</c> у рантаймі множив би на одиницю і мовчки повертав те саме
/// число: тонни лишалися б тоннами під виглядом кілограмів.
///
/// ⛔ Q-297: <c>DimensionCode</c> — за замовчуванням порожній рядок, бо
/// більшість викликів (конверсія, обхід формул) звіряють <c>DimensionId</c> і
/// їм людський код розмірності не потрібен. Заповнює його лише
/// <c>UnitCatalog</c> (реальний довідник); клієнт API отримує це поле
/// заповненим, а тест-дублери, що не задають його явно, — порожнім, і це
/// навмисно не ламає їх.
/// </remarks>
public sealed record UnitRef(
    int Id, string Code, byte DimensionId, decimal FactorToBase = 1m, decimal OffsetToBase = 0m,
    string DimensionCode = "",
    IReadOnlyDictionary<string, string>? SymbolL10n = null,
    IReadOnlyDictionary<string, string>? NameL10n = null,
    bool IsBase = false,
    int? UsedIn = null);
