using System.Text.Json;
using System.Text.RegularExpressions;
using Ecr.MethodologyImport.Model;

namespace Ecr.MethodologyImport.Export;

/// <summary>Вузол <c>categoryRule</c> версії пакета: вираз діалекту Methodology над рядком документа, результат — текст (ключ категорії).</summary>
public sealed record PackageCategoryRule(string Expression);

/// <summary>Що зроблено з правилом категорії для методології: <see cref="Issue"/> = null — правило потрапляє в пакет.</summary>
public sealed record CategoryRuleStatus(string Methodology, string Version, string Expression, string? Issue);

/// <summary>
/// Правило категорії констант (L-2) для методологій AF. ⚠ В AF правила категорії НЕМАЄ: категорію ставить C#-клас
/// методології (<c>DESIGN-category-rule.md</c> §1), тому вузол <c>categoryRule</c> експортер ДОПИСУЄ з таблиці
/// (<see cref="LandDefaults"/>) або з файла <c>--category-rules</c> (<c>{"ECW_C05_01":"!ECW_Location"}</c>), а не виводить із XML.
/// Правило з <c>!Формула</c>, якої немає у версії, НЕ потрапляє в пакет (сервер усе одно відмовив би) і виходить у звіт.
/// Діалект Methodology не має <c>Contains</c>/<c>Concat</c>/<c>switch</c>, а <c>+</c> на тексті у виразі не вживається:
/// рівність і вкладені <c>if</c>.
/// </summary>
public static partial class CategoryRules
{
    // Значення Land_* — двомовні рядки словників DropdownList (LAND-RULES-SPEC-AF §2). ⚠ Не звірено зі стендом: у реєстрах ECR
    // колонка може віддавати код запису, а не підпис; тоді вираз правиться файлом --category-rules без перезбірки.
    private const string Diesel = "Diesel - Дизель";
    private const string Gasoline = "Gasoline - Бензин";
    private const string Kerosene54 = "Kerosene (Jet fuel) - Керосин";
    private const string JetFuel = "Jet fuel/Avgas/Kerosene - Авиационное топливо/Керосин";

    /// <summary>Таблиця для Land (ECW_C05/C06/C07). Ключ — ім'я методології в AF без регістру.</summary>
    public static IReadOnlyDictionary<string, string> LandDefaults { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // 5.1: ECW_Location = ECW_RepairStatus_ECW_Category (формула є в AF: Location=Repair_Category).
            ["ECW_C05_01"] = "!ECW_Location",
            // 5.2, 7.16, 7.21: категорію дає формула ECW_Category (Summer/Winter за місяцем; 7.21 — за регіоном і паливом).
            ["ECW_C05_02"] = "!ECW_Category",
            ["ECW_C07_16"] = "!ECW_Category",
            ["ECW_C07_21"] = "!ECW_Category",
            // 5.4: у рулах лише Diesel і Kerosene.
            ["ECW_C05_04"] = $"if(@Land_TypeFuel = '{Diesel}', 'Diesel', 'Kerosene')",
            // 6.1: два рули (Diesel, Gasoline); Gas (natural gas) у AF-рулах немає.
            ["ECW_C06_01"] = $"if(@Land_TypeFuel = '{Diesel}', 'Diesel', if(@Land_TypeFuel = '{Gasoline}', 'Gasoline', ''))",
            // 7.7: 7 руливих матеріалів → Base/Kerosene/Diesel/Gasoline/Recovered (Fuel oil у рулах немає).
            ["ECW_C07_07"] = "if(@Land_TypeStorMater = 'Bilge water - Трюмные (подсланевые) воды', 'Base', "
                + $"if(@Land_TypeStorMater = '{Diesel}', 'Diesel', "
                + $"if(@Land_TypeStorMater = '{Gasoline}', 'Gasoline', "
                + $"if(@Land_TypeStorMater = '{JetFuel}', 'Kerosene', "
                + "if(@Land_TypeStorMater = 'Lubricant/Mineral oil - Смазочные материалы/Mинеральные масла', 'Base', "
                + "if(@Land_TypeStorMater = 'Recovered oil - Восстановленное масло', 'Recovered', "
                + "if(@Land_TypeStorMater = 'Solvent/Kerosene - Растворитель/Керосин', 'Kerosene', '')))))))",
            // 7.7.1: соляна кислота двох концентрацій.
            ["ECW_C07_07_01"] = "if(@Land_TypeStorMater = 'Hydrogen chloride 15% - Водород хлористый 15%', 'HCL15', "
                + "if(@Land_TypeStorMater = 'Hydrogen chloride 36.6% - Водород хлористый 36.6%', 'HCL36', ''))",
            // 7.8: рідина, що перекачується.
            ["ECW_C07_08"] = $"if(@Land_TypePumpedLiquid = '{Diesel}', 'Diesel', "
                + $"if(@Land_TypePumpedLiquid = '{Gasoline}', 'Gasoline', "
                + $"if(@Land_TypePumpedLiquid = '{JetFuel}', 'Kerosene', "
                + "if(@Land_TypePumpedLiquid = 'Lubricant/mineral oil - Смазочные материалы/Mинеральные масла', 'MineralOil', ''))))",
        };

    [GeneratedRegex(@"(?<![\w!])!([A-Za-z_]\w*)", RegexOptions.CultureInvariant)]
    private static partial Regex FormulaRefRegex();

    /// <summary>Файл-доповнення: JSON-об'єкт «методологія → вираз». Порожній вираз або не-рядок — помилка.</summary>
    public static IReadOnlyDictionary<string, string> ParseFile(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException("Файл правил категорій має бути JSON-об'єктом {\"методологія\": \"вираз\"}.");
        }

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in doc.RootElement.EnumerateObject())
        {
            var expression = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString()!.Trim() : string.Empty;
            if (expression.Length == 0)
            {
                throw new FormatException($"Правило категорії для «{property.Name}» порожнє або не є рядком.");
            }

            result[property.Name.Trim()] = expression;
        }

        return result;
    }

    /// <summary>Правила для версій, які є в моделі; методологія з таблиці, якої немає в AF, іде окремим рядком з порожньою версією.</summary>
    public static IReadOnlyList<CategoryRuleStatus> Resolve(MethodologyModel model, IReadOnlyDictionary<string, string>? rules)
    {
        ArgumentNullException.ThrowIfNull(model);
        rules ??= LandDefaults;
        var result = new List<CategoryRuleStatus>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var versions = model.Formulas.Select(f => (f.Methodology, f.MethodologyVersion))
            .Concat(model.Constants.Select(c => (c.Methodology, c.MethodologyVersion)))
            .Distinct()
            .OrderBy(v => v.Methodology, StringComparer.Ordinal)
            .ThenBy(v => v.MethodologyVersion, StringComparer.Ordinal);

        foreach (var (methodology, version) in versions)
        {
            if (!rules.TryGetValue(methodology, out var expression))
            {
                continue;
            }

            seen.Add(methodology);
            var names = model.Formulas
                .Where(f => f.Methodology == methodology && f.MethodologyVersion == version)
                .Select(f => f.Name)
                .ToHashSet(StringComparer.Ordinal);
            var missing = FormulaRefRegex().Matches(expression).Select(m => m.Groups[1].Value).FirstOrDefault(n => !names.Contains(n));
            result.Add(new CategoryRuleStatus(
                methodology, version, expression, missing is null ? null : $"формули !{missing} немає у версії — правило не експортовано"));
        }

        foreach (var name in rules.Keys.Where(k => !seen.Contains(k)).Order(StringComparer.Ordinal))
        {
            result.Add(new CategoryRuleStatus(name, string.Empty, rules[name], "методології немає в AF XML — правило не експортовано"));
        }

        return result;
    }
}
