using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ecr.MethodologyImport.Analysis;
using Ecr.MethodologyImport.Model;

namespace Ecr.MethodologyImport.Export;

/// <summary>Формула в пакеті. Дати — рядки AF як є (без часового поясу й перерахунку).</summary>
public sealed record PackageFormula(
    string Name,
    string Version,
    string Arguments,
    string Text,
    string StartDate,
    string EndDate,
    bool IsAvailable,
    string Report,
    string ResultType = "Number",
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Scope = null);

public sealed record PackageConstantValue(
    string Category,
    string Version,
    string Value,
    string StartDate,
    string EndDate);

public sealed record PackageConstant(
    string Name,
    string Parameter,
    string Unit,
    IReadOnlyList<PackageConstantValue> Values);

public sealed record PackageVersion(
    string Version,
    IReadOnlyList<PackageFormula> Formulas,
    IReadOnlyList<PackageConstant> Constants,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PackageCategoryRule? CategoryRule = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Level = null);

/// <summary>Правило категорії версії (вираз діалекту Methodology); у пакеті — вузол <c>categoryRule</c>.</summary>
public sealed record PackageCategoryRule(string Expression);

public sealed record PackageMethodology(string Name, IReadOnlyList<PackageVersion> Versions);

/// <summary>
/// Пакет <c>ecr-methodology-package</c> v1 — вхід майбутнього <c>POST /methodologies/import?dryRun</c>
/// (лише чернетки). ⚠ Схема введена цим інструментом (у документах її не було) і документується в
/// FEATURE-HSE301-VIEW §11.6; ендпоінта-споживача в продукті ще немає. Непорожній <see cref="Blockers"/> =
/// імпортер ЗОБОВ'ЯЗАНИЙ відмовити. Без часу створення й шляхів середовища: той самий вхід → той самий файл.
/// </summary>
public sealed record MethodologyPackage(
    string Format,
    int Version,
    string Library,
    IReadOnlyList<PackageMethodology> Methodologies,
    IReadOnlyList<string> Blockers)
{
    public const string FormatName = "ecr-methodology-package";
    public const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>Row-формули Land: категорію рядка задають три формули; автовизначення за замиканням правила немає.</summary>
    public const string RowScope = "Row";

    /// <summary>Методологія-модуль (рівень Module): HSE400 рахує <c>MonthColumnArgumentsModule</c>, а не правила рядків; решта AF - Configuration.</summary>
    public const string ModuleMethodology = "HSE400";

    public const string ModuleLevel = "Module";

    public const string CategoryRuleFormula = "ECW_Location";

    /// <summary>Формула, що дає ключ категорії там, де <c>ECW_Location</c> немає (G12).</summary>
    public const string CategoryFormula = "ECW_Category";

    private static readonly HashSet<string> RowScopeFormulas =
        new(["ECW_Location", "ECW_RepairStatus", "ECW_Category"], StringComparer.Ordinal);

    public static MethodologyPackage From(MethodologyModel model, AnalysisReport report, string library)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(report);

        var shapes = FormulaTypeInference.Infer(model, library);
        var formulas = model.Formulas.ToLookup(f => (f.Methodology, f.MethodologyVersion));
        var constants = model.Constants.ToLookup(c => (c.Methodology, c.MethodologyVersion));
        var keys = formulas.Select(g => g.Key).Concat(constants.Select(g => g.Key)).Distinct().ToList();

        var methodologies = keys
            .GroupBy(k => k.Item1, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new PackageMethodology(
                g.Key,
                g.Select(k => k.Item2).Distinct(StringComparer.Ordinal).OrderBy(v => v, StringComparer.Ordinal)
                    .Select(v => new PackageVersion(
                        v,
                        formulas[(g.Key, v)]
                            .OrderBy(f => f.Name, StringComparer.Ordinal).ThenBy(f => f.Version, StringComparer.Ordinal)
                            .Select(f => new PackageFormula(
                                f.Name, f.Version, f.Arguments, f.Text, f.StartDate, f.EndDate, f.Available, f.Report,
                                shapes[f.Key].Kind == FormulaResultKind.Text ? "Text" : "Number",
                                RowScopeFormulas.Contains(f.Name) ? RowScope : null))
                            .ToList(),
                        constants[(g.Key, v)]
                            .GroupBy(c => c.Name, StringComparer.Ordinal)
                            .OrderBy(cg => cg.Key, StringComparer.Ordinal)
                            .Select(cg => new PackageConstant(
                                cg.Key,
                                cg.Select(c => c.Parameter).FirstOrDefault(p => p.Length > 0) ?? string.Empty,
                                CanonicalUnit(cg.Select(c => c.Unit).FirstOrDefault(u => u.Length > 0)),
                                CollapseRedundantCategories(
                                    cg.Where(c => c.HasValue)
                                        .OrderBy(c => c.Category, StringComparer.Ordinal).ThenBy(c => c.Version, StringComparer.Ordinal)
                                        .Select(c => new PackageConstantValue(c.Category, c.Version, c.Value, c.StartDate, c.EndDate))
                                        .ToList(),
                                    library,
                                    isLibrary: string.Equals(g.Key, library, StringComparison.Ordinal))))
                            .ToList(),
                        CategoryRuleOf(
                            formulas[(g.Key, v)],
                            hasCategoryConstants: constants[(g.Key, v)]
                                .Any(c => c.Category.Length > 0 && !string.Equals(c.Category, library, StringComparison.Ordinal))),
                        string.Equals(g.Key, ModuleMethodology, StringComparison.Ordinal) ? ModuleLevel : null))
                    .ToList()))
            .ToList();

        return new MethodologyPackage(FormatName, CurrentVersion, library, methodologies, report.Blockers);
    }

    /// <summary>
    /// Бібліотека: значення константи в категоріях, що дослівно збігаються зі значенням спільної категорії
    /// (<paramref name="library"/>) на ті самі дати й версію AF, зайві й прибираються.
    /// </summary>
    /// <remarks>
    /// ⚠ Лише для самої бібліотеки й лише коли ВСІ значення групи однакові: тоді кожен вибір категорії
    /// (точний збіг, а за його відсутності — запасна спільна категорія, <c>ConstantResolver</c>) дає те саме число,
    /// тож результат не змінюється. Без згортання версія бібліотеки з кількома однаковими категоріями (приклад — константа
    /// складу газу факелів: шість категорій з одним значенням) на запуску без категорії давала <c>constantAmbiguous</c>
    /// і не публікувалася. Група, де значення розрізняються, лишається як є — вибір між ними рішення методолога.
    /// </remarks>
    public static IReadOnlyList<PackageConstantValue> CollapseRedundantCategories(
        IReadOnlyList<PackageConstantValue> values, string library, bool isLibrary)
    {
        if (!isLibrary || values.Count < 2)
        {
            return values;
        }

        var result = new List<PackageConstantValue>(values.Count);
        var collapsed = false;
        foreach (var group in values.GroupBy(v => (v.Version, v.StartDate, v.EndDate)))
        {
            var items = group.ToList();
            var common = items.FirstOrDefault(v => string.Equals(v.Category, library, StringComparison.Ordinal));
            var identical = common is not null
                && items.All(v => string.Equals(v.Value, common.Value, StringComparison.Ordinal));
            if (identical && items.Count > 1)
            {
                result.Add(common!);
                collapsed = true;
            }
            else
            {
                result.AddRange(items);
            }
        }

        return !collapsed
            ? values
            : result.OrderBy(v => v.Category, StringComparer.Ordinal).ThenBy(v => v.Version, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Правило категорії версії: <c>!ECW_Location</c>, якщо є така доступна формула (5.1), інакше <c>!ECW_Category</c>
    /// (5.2, 7.16, 7.21 — категорію дає сама формула <c>ECW_Category</c>); без обох — правила немає.
    /// </summary>
    /// <remarks>
    /// ⛔ G12 (Land): правило <c>!ECW_Category</c> для 5.2/7.21 раніше задавали руками після імпорту, а для 7.16
    /// (константи за категоріями Summer/Winter, ключ — формула <c>ECW_Category</c>) його не було взагалі — версія з
    /// категорійними константами без правила не резолвить категорію й не публікується з коректним золотим набором.
    /// Правила за довідником (<c>REGFIELD(@Lookup,'NAME')</c> у 5.4/6.1/7.7/7.8) з AF не виводяться — це рішення
    /// про шаблон, а не властивість методології: їх задає той, хто знає, яка колонка шаблону дає категорію.
    /// </remarks>
    /// <remarks>
    /// ⚠ Гілка <c>ECW_Category</c> — лише для версій, що мають константи НЕ в спільній категорії бібліотеки
    /// (<paramref name="hasCategoryConstants"/>): формула <c>ECW_Category</c> без категорійних констант не має чого
    /// резолвити, і правило <c>!ECW_Category</c> там лише додало б версії вимогу, якої методологія не ставить.
    /// </remarks>
    private static PackageCategoryRule? CategoryRuleOf(IEnumerable<FormulaDef> versionFormulas, bool hasCategoryConstants)
    {
        var names = versionFormulas.Where(f => f.Available).Select(f => f.Name).ToHashSet(StringComparer.Ordinal);

        if (names.Contains(CategoryRuleFormula))
        {
            return new PackageCategoryRule("!" + CategoryRuleFormula);
        }

        return hasCategoryConstants && names.Contains(CategoryFormula) ? new PackageCategoryRule("!" + CategoryFormula) : null;
    }

    /// <summary>Код каталогу ECR, якщо одиницю AF зведено; інакше рядок AF як є (нерезолвну звітує analyze, сервер — <c>unitUnknown</c>).</summary>
    private static string CanonicalUnit(string? raw)
    {
        var r = UnitCanonicalizer.Resolve(raw);
        return r.Code ?? (raw ?? string.Empty);
    }

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions).ReplaceLineEndings("\n");
}
