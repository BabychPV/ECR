using Ecr.Application.Ports;

namespace Ecr.Calculations;

/// <summary>
/// Довідник одиниць, розкладений для рушіїв: таблиця конверсій (<see cref="UnitTable"/>), Id → код і код → Id.
/// </summary>
/// <remarks>
/// ⚠ Повторює <c>GenericCalculationModule.UnitsAsync</c> (старий модуль не чіпали): другий модуль (скрипт) мусить
/// конвертувати одиниці так само. Зміна в одному — зміна й тут.
/// </remarks>
internal sealed class UnitCatalogTables
{
    private UnitCatalogTables(UnitTable table, Dictionary<int, string> codes, Dictionary<string, int> ids)
    {
        Table = table;
        Codes = codes;
        Ids = ids;
    }

    /// <summary>Таблиця конверсій для <c>CONVERT</c>.</summary>
    public UnitTable Table { get; }

    /// <summary>Id одиниці → код.</summary>
    public Dictionary<int, string> Codes { get; }

    /// <summary>Код одиниці (без регістру) → Id.</summary>
    public Dictionary<string, int> Ids { get; }

    /// <summary>Будує таблиці зі знімка довідника одиниць.</summary>
    /// <param name="snapshot">Знімок довідника.</param>
    public static UnitCatalogTables Build(UnitCatalogSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var table = new UnitTable();
        var codes = new Dictionary<int, string>();
        var ids = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var unit in snapshot.Units.Values)
        {
            table.Add(unit.Code, unit.DimensionId, unit.FactorToBase, unit.OffsetToBase);
            codes.TryAdd(unit.Id, unit.Code);
            ids.TryAdd(unit.Code, unit.Id);
        }

        // ⛔ Z2-05: похідні одиниці (швидкості) — зі своїми чисельником і знаменником, щоб `CONVERT` швидкості у
        // швидкість не ішов через заокруглений `FactorToBase` (`Sm3_per_h` = 1/3600 до 18 знаків).
        foreach (var (key, unitId) in snapshot.Derived)
        {
            var bar = key.IndexOf('|', StringComparison.Ordinal);
            if (bar > 0
                && int.TryParse(key.AsSpan(0, bar), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var numeratorId)
                && int.TryParse(key.AsSpan(bar + 1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var denominatorId)
                && codes.TryGetValue(unitId, out var rateCode)
                && codes.TryGetValue(numeratorId, out var numeratorCode)
                && codes.TryGetValue(denominatorId, out var denominatorCode))
            {
                table.AddRate(rateCode, numeratorCode, denominatorCode);
            }
        }

        return new UnitCatalogTables(table, codes, ids);
    }
}
