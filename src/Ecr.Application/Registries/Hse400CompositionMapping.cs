using System.Globalization;
using System.Text.RegularExpressions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Registries;

/// <summary>Числове значення документа HSE400: комірка вводу або результат розрахункового рядка.</summary>
/// <param name="Year">Рік документа.</param>
/// <param name="Month">Місяць колонки, 1..12.</param>
/// <param name="Stream">Код таблиці = префікс потоку (<c>FG_Internal</c>).</param>
/// <param name="RowKey">Ключ рядка: код компонента або <c>F_&lt;X&gt;</c> для розрахункового.</param>
/// <param name="Value">Значення.</param>
public sealed record Hse400DocumentCell(int Year, int Month, string Stream, string RowKey, decimal Value);

/// <summary>Рядок складу газу у формі довідника <c>LAND_HSE400_COMPOSITION</c>.</summary>
/// <param name="Year">Рік.</param>
/// <param name="Month">Місяць, 1..12.</param>
/// <param name="Stream">Префікс потоку.</param>
/// <param name="Indicator">Показник у канонічному імені Land.</param>
/// <param name="Value">Значення.</param>
public sealed record Hse400CompositionRow(int Year, int Month, string Stream, string Indicator, decimal Value);

/// <summary>
/// Відображення документа HSE400 у рядки складу газу, які читають методології Land через довідник
/// <c>LAND_HSE400_COMPOSITION</c> (рішення людини 11.11: довідник не дублює документ, Land читає документ).
/// </summary>
/// <remarks>
/// ⛔ Правила канонічних імен тут ті самі, що в імпорті зі старої бази
/// (<c>tools/land/LandHse400Composition.Lib.ps1</c>, <c>ConvertTo-LandHse400CompositionPlan</c>), у тому самому
/// порядку. «Колонка існує» імпорту тут — «рядок із таким показником є в таблиці шаблону»: шаблон HSE400 збудовано
/// з тих самих колонок старих таблиць, тож перевірка існування точна, а не наближена.
///
/// Чиста логіка без БД: читання документа — <c>Hse400CompositionSnapshotLoader</c> (Infrastructure).
/// </remarks>
public static partial class Hse400CompositionMapping
{
    /// <summary>Код довідника складу газу.</summary>
    public const string RegistryCode = "LAND_HSE400_COMPOSITION";

    /// <summary>Код шаблону HSE400.</summary>
    public const string TemplateCode = "HSE400";

    /// <summary>
    /// Код проєкту золотих тестів методології HSE400 (<c>tools/hse400/Build-Hse400Template.ps1</c>, <c>$goldCode</c>):
    /// його документ має синтетичний ввід і НІКОЛИ не є джерелом складу для Land.
    /// </summary>
    public const string GoldProjectCode = TemplateCode + "_GOLD";

    /// <summary>Префікс ключа розрахункового рядка (<c>F_&lt;X&gt;</c> — формула <c>HSE400_&lt;таблиця&gt;_&lt;X&gt;</c>).</summary>
    public const string CalculatedPrefix = "F_";

    private static readonly string[] MassAliases = ["C1_C5", "C6_C10", "C12_C19"];

    /// <summary>Шість таблиць аркуша GasComposition; код таблиці = префікс потоку.</summary>
    public static IReadOnlyList<string> Streams { get; } =
        ["FG_Makat", "FG_HMB", "LPG_HMB", "FG_Export", "FG_Internal", "LPG_Internal"];

    /// <summary>
    /// Ім'я показника старої бази для кожного рядка таблиці: рядок вводу <c>X</c> — <c>X</c>; розрахунковий
    /// <c>F_X</c> — <c>X</c>, а коли в тій самій таблиці є рядок вводу <c>X</c>, — <c>X</c> із суфіксом версії.
    /// </summary>
    /// <param name="stream">Код таблиці.</param>
    /// <param name="rowKeys">Ключі рядків таблиці шаблону.</param>
    /// <returns>Ключ рядка → ім'я показника старої бази.</returns>
    /// <remarks>
    /// Суфікс версії у старій базі різний: <c>_V2</c> у таблицях <c>*_Internal</c> (<c>H2S_V2</c>), <c>V2</c> у решті
    /// (<c>NitrogenV2</c>).
    /// </remarks>
    public static IReadOnlyDictionary<string, string> RawIndicators(string stream, IEnumerable<string> rowKeys)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(rowKeys);

        var keys = rowKeys.ToList();
        var inputs = new HashSet<string>(
            keys.Where(k => !IsCalculated(k)), StringComparer.OrdinalIgnoreCase);
        var suffix = stream.EndsWith("_Internal", StringComparison.OrdinalIgnoreCase) ? "_V2" : "V2";

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in keys)
        {
            if (!IsCalculated(key))
            {
                map.TryAdd(key, key);
                continue;
            }

            var name = key[CalculatedPrefix.Length..];
            map.TryAdd(key, inputs.Contains(name) ? name + suffix : name);
        }

        return map;
    }

    /// <summary>Чи це ключ розрахункового рядка.</summary>
    /// <param name="rowKey">Ключ рядка.</param>
    /// <returns><c>true</c> — рядок <c>F_&lt;X&gt;</c>.</returns>
    public static bool IsCalculated(string rowKey)
        => rowKey.StartsWith(CalculatedPrefix, StringComparison.Ordinal);

    /// <summary>Перетворює значення документа на рядки складу в канонічних іменах Land.</summary>
    /// <param name="cells">Значення в порядку рядків таблиці (за збігу канонічних імен перемагає перше).</param>
    /// <param name="rawIndicatorsByStream">Результат <see cref="RawIndicators"/> для кожної таблиці.</param>
    /// <returns>Рядки складу; ключ (рік, місяць, потік, показник) унікальний.</returns>
    public static IReadOnlyList<Hse400CompositionRow> ToCompositionRows(
        IEnumerable<Hse400DocumentCell> cells,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> rawIndicatorsByStream)
    {
        ArgumentNullException.ThrowIfNull(cells);
        ArgumentNullException.ThrowIfNull(rawIndicatorsByStream);

        var result = new List<Hse400CompositionRow>();
        var groups = cells
            .Where(c => c.Month is >= 1 and <= 12)
            .GroupBy(c => (c.Year, c.Month, Stream: c.Stream.ToUpperInvariant()));

        foreach (var group in groups)
        {
            var stream = group.First().Stream;
            if (!rawIndicatorsByStream.TryGetValue(stream, out var raw))
            {
                continue;
            }

            var known = new HashSet<string>(raw.Values, StringComparer.OrdinalIgnoreCase);
            var order = new List<string>();
            var values = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);

            void Put(string indicator, decimal value)
            {
                if (!values.ContainsKey(indicator))
                {
                    order.Add(indicator);
                }

                values[indicator] = value;
            }

            foreach (var cell in group)
            {
                if (!raw.TryGetValue(cell.RowKey, out var name))
                {
                    continue;
                }

                var indicator = Canonical(name);
                if (MassAliases.Contains(indicator, StringComparer.OrdinalIgnoreCase))
                {
                    // Власна колонка «…_mass» старої таблиці перемагає псевдонім — за самим існуванням, не за значенням.
                    if (known.Contains(name + "_mass"))
                    {
                        continue;
                    }

                    indicator += "_mass";
                }

                if (!values.ContainsKey(indicator))
                {
                    Put(indicator, cell.Value);
                }
            }

            var hmb = stream.Contains("HMB", StringComparison.OrdinalIgnoreCase);
            if (hmb && known.Contains("EMercaptan_mass"))
            {
                if (values.TryGetValue("EMercaptanV2_mass", out var v2))
                {
                    Put("EMercaptan_mass", v2);
                }
                else if (values.Remove("EMercaptan_mass"))
                {
                    order.RemoveAll(i => string.Equals(i, "EMercaptan_mass", StringComparison.OrdinalIgnoreCase));
                }
            }

            if (hmb && values.TryGetValue("TotalSulphurContentWithoutH2S", out var sulphur))
            {
                Put("MercaptanSulphurContent", sulphur);
            }

            if (!values.ContainsKey("C6_C10_mass") && values.TryGetValue("C6_1_mass", out var hexane))
            {
                Put("C6_C10_mass", hexane);
            }

            result.AddRange(order.Select(i => new Hse400CompositionRow(group.Key.Year, group.Key.Month, stream, i, values[i])));
        }

        return result;
    }

    /// <summary>
    /// Підміняє в джерелі знімка рядки довідника складу рядками документа — ЦІЛИМИ РОКАМИ: рік, за який є документ
    /// HSE400, береться з документа повністю, роки без документа лишаються з довідника.
    /// </summary>
    /// <param name="inner">Джерело, прочитане з <c>dic.*</c>.</param>
    /// <param name="registryCode">Код довідника складу.</param>
    /// <param name="rows">Рядки документа.</param>
    /// <param name="documentYears">Роки, за які є документ (і порожній документ витісняє довідник за свій рік).</param>
    /// <returns>Нове джерело; <paramref name="inner"/> — якщо довідника чи одного з його п'яти полів немає.</returns>
    /// <remarks>
    /// Змішувати джерела в межах року не можна: місяць, якого в документі ще не заповнили, інакше мовчки рахувався б
    /// за старим імпортом, і користувач не мав би як це побачити.
    /// </remarks>
    public static RegistrySnapshotSource Overlay(
        RegistrySnapshotSource inner,
        string registryCode,
        IReadOnlyList<Hse400CompositionRow> rows,
        IReadOnlyCollection<int> documentYears)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(documentYears);

        var registry = inner.Registries.FirstOrDefault(
            r => string.Equals(r.Code, registryCode, StringComparison.OrdinalIgnoreCase));
        if (registry is null || documentYears.Count == 0)
        {
            return inner;
        }

        var year = Field(registry, "YEAR");
        var month = Field(registry, "MONTH");
        var stream = Field(registry, "STREAM");
        var indicator = Field(registry, "INDICATOR");
        var value = Field(registry, "VALUE");
        if (year is null || month is null || stream is null || indicator is null || value is null)
        {
            return inner;
        }

        var ownEntries = inner.Entries.Where(e => e.RegistryDefId == registry.Id).Select(e => e.Id).ToHashSet();
        var dropped = inner.Values
            .Where(v => v.FieldDefId == year.Id && v.Numeric is { } y && ownEntries.Contains(v.EntryId)
                        && y == decimal.Truncate(y) && documentYears.Contains((int)y))
            .Select(v => v.EntryId)
            .ToHashSet();

        var entries = inner.Entries.Where(e => !dropped.Contains(e.Id)).ToList();
        var values = inner.Values.Where(v => !dropped.Contains(v.EntryId)).ToList();

        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var id = -(i + 1L);
            var code = EntryCode(row);
            entries.Add(RegistryEntry.ForSnapshot(
                id, registry.Id, EcrCode.Create(code), new LocalizedText(new Dictionary<string, string> { ["en"] = code }), i));
            values.Add(new RegistrySnapshotValue(id, year.Id, row.Year, null, null, null, null, null, null));
            values.Add(new RegistrySnapshotValue(id, month.Id, row.Month, null, null, null, null, null, null));
            values.Add(new RegistrySnapshotValue(id, stream.Id, null, row.Stream, null, null, null, null, null));
            values.Add(new RegistrySnapshotValue(id, indicator.Id, null, row.Indicator, null, null, null, null, null));
            values.Add(new RegistrySnapshotValue(id, value.Id, row.Value, null, null, null, null, null, null));
        }

        return new RegistrySnapshotSource(inner.Registries, inner.PrimaryKeys, entries, values);
    }

    /// <summary>Код запису — як у імпорті: <c>H{рік}{місяць}_{потік}_{показник}</c>, не довший за 64.</summary>
    internal static string EntryCode(Hse400CompositionRow row)
    {
        var code = string.Create(
            CultureInfo.InvariantCulture,
            $"H{row.Year:D4}{row.Month:D2}_{row.Stream}_{NonCode().Replace(row.Indicator, "_")}");
        code = NonCode().Replace(code, "_");
        return code.Length > 64 ? code[..64] : code;
    }

    /// <summary>Канонічне ім'я показника: <c>Density_20</c> → <c>Density20</c>, <c>X_0</c> → <c>X0</c>.</summary>
    internal static string Canonical(string name)
        => Zero().Replace(Twenty().Replace(name, "20"), "0");

    private static RegistryFieldDef? Field(RegistryDef registry, string code)
        => registry.Fields.FirstOrDefault(f => string.Equals(f.Code, code, StringComparison.OrdinalIgnoreCase));

    [GeneratedRegex("_20(?=$|_)")]
    private static partial Regex Twenty();

    [GeneratedRegex("_0(?=$|_)")]
    private static partial Regex Zero();

    [GeneratedRegex("[^A-Za-z0-9_]")]
    private static partial Regex NonCode();
}
