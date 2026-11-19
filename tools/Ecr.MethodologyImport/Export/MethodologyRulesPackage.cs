using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ecr.MethodologyImport.Model;

namespace Ecr.MethodologyImport.Export;

/// <summary>Умова правила в пакеті: колонка таблиці старої системи і текст значення як у старій системі.</summary>
public sealed record PackageRuleCondition(string Parameter, string Value);

/// <summary>Правило рядків версії методології. Дати — UTC-рядки AF, як у формулах.</summary>
public sealed record PackageRule(
    string Code,
    string RuleVersion,
    string UniqueName,
    string Table,
    IReadOnlyList<PackageRuleCondition> Conditions,
    bool CatchAll,
    bool IsAvailable,
    string StartDate,
    string EndDate);

public sealed record PackageRuleVersion(string Version, IReadOnlyList<string> Tables, IReadOnlyList<PackageRule> Rules);

public sealed record PackageRuleMethodology(string Name, IReadOnlyList<PackageRuleVersion> Versions);

/// <summary>
/// Проблема правила, яку імпорт не вирішує сам: <c>Kind</c> — <c>malformed</c> (рядок не розібрано; правило в пакет
/// не потрапляє), <c>methodologyNotImported</c> (у дзеркалах немає формул/констант цієї методології; правило в пакет не
/// потрапляє), <c>duplicateKey</c> (два рядки з однаковим ключем — береться останній), <c>catchAllInMixedScope</c>
/// (правило «уся таблиця» разом з умовними правилами тієї ж версії — див. примітку у <see cref="MethodologyRulesPackage"/>).
/// </summary>
public sealed record RulesProblem(string Kind, string Methodology, string MethodologyVersion, string Rule, string Detail);

/// <summary>Підсумок пакета правил — агреговані цифри (без значень умов).</summary>
public sealed record RulesTotals(int Rules, int Available, int Unavailable, int CatchAll, int Conditional, int Tables, int Methodologies, int Problems);

/// <summary>
/// Пакет <c>ecr-methodology-rules</c> v1 — правила рядків методологій із дзеркала <c>AF_EmissionCalculationWork_Rules</c>
/// (старі <c>RuleArg_Parameters / _Values / _TableLandFromDB</c>). Окремий від <c>ecr-methodology-package</c>: пакет
/// формул не знає ні таблиць, ні колонок шаблону, а правило без них не існує. Перетворення на
/// <c>MethodologyRule.MatchJson</c> (колонка → Id запису довідника) робить застосувальник проти конкретного шаблону
/// (<c>tools/land/Build-LandTemplate.ps1 -Stage LegacyRules</c>): лише він знає Id колонок і записів довідників.
/// </summary>
/// <remarks>
/// ⚠ У нашій моделі правило не прив'язане до таблиці (предикат по Id колонок; таблицю визначають прив'язки виходів), а у
/// старій — прив'язане (<c>RuleArg_TableLandFromDB</c>). Правило «уся таблиця» (<c>{}</c>) нової системи збігається з
/// рядками КОЖНОЇ таблиці, до якої прив'язана методологія, тому якщо та сама версія має і правила «уся таблиця», і умовні
/// правила (для тієї ж чи іншої таблиці), охоплення розходиться зі старою системою. Такі випадки не приховуються:
/// <c>catchAllInMixedScope</c>.
/// <para>Дані прода (значення умов) у пакет потрапляють як є — файл лишається поза репозиторієм.</para>
/// </remarks>
public sealed record MethodologyRulesPackage(
    string Format,
    int Version,
    IReadOnlyList<PackageRuleMethodology> Methodologies,
    RulesTotals Totals,
    IReadOnlyList<RulesProblem> Problems)
{
    public const string FormatName = "ecr-methodology-rules";
    public const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static MethodologyRulesPackage From(MethodologyModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        var imported = model.Formulas.Select(f => (f.Methodology, f.MethodologyVersion))
            .Concat(model.Constants.Select(c => (c.Methodology, c.MethodologyVersion)))
            .ToHashSet();

        var problems = new List<RulesProblem>();
        var accepted = new List<RuleDef>();
        foreach (var rule in model.Rules)
        {
            if (rule.Problem is not null)
            {
                problems.Add(new RulesProblem("malformed", rule.Methodology, rule.MethodologyVersion, RuleCode(rule), rule.Problem));
                continue;
            }

            if (!imported.Contains((rule.Methodology, rule.MethodologyVersion)))
            {
                problems.Add(new RulesProblem(
                    "methodologyNotImported", rule.Methodology, rule.MethodologyVersion, RuleCode(rule),
                    "у дзеркалах немає формул і констант цієї версії методології"));
                continue;
            }

            accepted.Add(rule);
        }

        // Двійник ключа (методологія, версія, код): у дзеркалі — лише Test_Rule (версії правила V1..V3 з різними датами);
        // код містить версію правила, тож двійники можливі лише при зламаних даних, і береться останній.
        var unique = new Dictionary<(string, string, string), RuleDef>();
        foreach (var rule in accepted)
        {
            var key = (rule.Methodology, rule.MethodologyVersion, RuleCode(rule));
            if (unique.ContainsKey(key))
            {
                problems.Add(new RulesProblem("duplicateKey", rule.Methodology, rule.MethodologyVersion, key.Item3, "двійник ключа: береться останній"));
            }

            unique[key] = rule;
        }

        var methodologies = unique.Values
            .GroupBy(r => r.Methodology, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new PackageRuleMethodology(
                g.Key,
                [.. g.GroupBy(r => r.MethodologyVersion, StringComparer.Ordinal)
                    .OrderBy(v => v.Key, StringComparer.Ordinal)
                    .Select(v =>
                    {
                        var rules = v.OrderBy(r => r.Name, StringComparer.Ordinal).ThenBy(r => r.Version, StringComparer.Ordinal).ToList();
                        var live = rules.Where(r => r.IsAvailable).ToList();
                        if (live.Any(r => r.CatchAll) && live.Any(r => !r.CatchAll))
                        {
                            var catchAllTables = live.Where(r => r.CatchAll).Select(r => r.Table).Distinct(StringComparer.Ordinal).Count();
                            problems.Add(new RulesProblem(
                                "catchAllInMixedScope", g.Key, v.Key, string.Empty,
                                string.Create(
                                    System.Globalization.CultureInfo.InvariantCulture,
                                    $"правил «уся таблиця» {live.Count(r => r.CatchAll)} (таблиць {catchAllTables}) разом з {live.Count(r => !r.CatchAll)} умовними")));
                        }

                        return new PackageRuleVersion(
                            v.Key,
                            [.. rules.Select(r => r.Table).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
                            [.. rules.Select(r => new PackageRule(
                                RuleCode(r), r.Version, r.UniqueName, r.Table,
                                [.. r.Conditions.Select(c => new PackageRuleCondition(c.Parameter, c.Value))],
                                r.CatchAll, r.IsAvailable, r.StartDate, r.EndDate))]);
                    })]))
            .ToList();

        var all = methodologies.SelectMany(m => m.Versions).SelectMany(v => v.Rules).ToList();
        var totals = new RulesTotals(
            all.Count,
            all.Count(r => r.IsAvailable),
            all.Count(r => !r.IsAvailable),
            all.Count(r => r.CatchAll),
            all.Count(r => !r.CatchAll),
            all.Select(r => r.Table).Distinct(StringComparer.Ordinal).Count(),
            methodologies.Count,
            problems.Count);

        return new MethodologyRulesPackage(
            FormatName,
            CurrentVersion,
            methodologies,
            totals,
            [.. problems
                .OrderBy(p => p.Methodology, StringComparer.Ordinal)
                .ThenBy(p => p.MethodologyVersion, StringComparer.Ordinal)
                .ThenBy(p => p.Kind, StringComparer.Ordinal)
                .ThenBy(p => p.Rule, StringComparer.Ordinal)]);
    }

    /// <summary>Код правила в нашій системі: ім'я правила старої (<c>Rule_006</c>); не-перша версія правила — суфікс версії.</summary>
    public static string RuleCode(RuleDef rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return string.Equals(rule.Version, "V1", StringComparison.Ordinal) ? rule.Name : rule.Name + "_" + rule.Version;
    }

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions).ReplaceLineEndings("\n");
}
