using System.Text.Json;
using System.Text.RegularExpressions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// D-429: годинна/календарна верхня межа DV (28..31, 672, 696, 720, 744) не стоїть на колонках, де одиниця не години й не
/// дні (kg/h, Sm3, т, ppm, густина). У xlsm це суцільний діапазон DV, що накрив чужі колонки. Правила OperatingTime (h),
/// днів (d) і прихованих службових колонок лишаються.
/// </summary>
/// <remarks>
/// Мутаційний доказ (лише цей клас): у gen_ecr_model.py прибрати виклик <c>is_foreign_time_bound</c> і перегенерувати
/// модель (run-model-gen.ps1 -Work) - <see cref="Часова_межа_не_стоїть_на_колонці_без_годин_і_днів"/> і
/// <see cref="Перелік_колонок_без_правила_і_без_dv"/> червоніють.
/// </remarks>
public sealed class LandForeignTimeBoundTests
{
    private static readonly HashSet<double> Bounds = [28, 29, 30, 31, 672, 696, 720, 744];

    private static readonly Regex Le = new(@"^SUM\(\[(?<c>.+)\]\) <= (?<n>\d+(\.\d+)?)$", RegexOptions.Compiled);

    private static readonly Regex Days = new(@"\bdays?\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static IEnumerable<JsonElement> Tables()
    {
        var path = Path.Combine(
            SourceTree.Root, "docs", "delivery", "reference-data", "land-template", "land-template.model.json");
        var model = JsonDocument.Parse(File.ReadAllText(path)).RootElement.Clone();
        foreach (var sheet in model.GetProperty("sheets").EnumerateArray())
        {
            foreach (var t in sheet.GetProperty("tables").EnumerateArray())
            {
                yield return t;
            }
        }
    }

    // код таблиці в моделі може повторюватись (T7_00): беремо першу
    private static Dictionary<string, JsonElement> ByCode() =>
        Tables().GroupBy(t => t.GetProperty("code").GetString()!).ToDictionary(g => g.Key, g => g.First());

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool IsTrue(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    /// <summary>(таблиця, колонка, межа, одиниця, прихована, заголовок) для кожного правила «SUM([c]) &lt;= N» із часовою межею.</summary>
    private static IEnumerable<(string Table, string Col, double Bound, string? Unit, bool Hidden, string Header)> TimeBoundRules()
    {
        foreach (var t in Tables())
        {
            var cols = t.GetProperty("columns").EnumerateArray().ToDictionary(c => c.GetProperty("code").GetString()!);
            foreach (var r in t.GetProperty("rules").EnumerateArray())
            {
                var m = Le.Match(r.GetProperty("expression").GetString()!);
                if (!m.Success || !Bounds.Contains(double.Parse(m.Groups["n"].Value, System.Globalization.CultureInfo.InvariantCulture)))
                {
                    continue;
                }

                var c = cols[m.Groups["c"].Value];
                yield return (
                    t.GetProperty("code").GetString()!,
                    m.Groups["c"].Value,
                    double.Parse(m.Groups["n"].Value, System.Globalization.CultureInfo.InvariantCulture),
                    Str(c, "unit"),
                    IsTrue(c, "hidden"),
                    Str(c, "headerEn") ?? string.Empty);
            }
        }
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Часова_межа_не_стоїть_на_колонці_без_годин_і_днів()
    {
        var bad = TimeBoundRules()
            .Where(x => x.Unit is not ("h" or "d") && !x.Hidden && !Days.IsMatch(x.Header))
            .Select(x => $"{x.Table}.{x.Col} <= {x.Bound} (unit={x.Unit ?? "-"})")
            .ToList();
        Assert.True(bad.Count == 0, string.Join('\n', bad));

        // залишились правила на годинах і днях (і на прихованих службових колонках)
        var all = TimeBoundRules().ToList();
        Assert.Contains(all, x => x.Unit == "h");
        Assert.Contains(all, x => x.Unit == "d");
        Assert.Contains(all, x => x.Hidden);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Перелік_колонок_без_правила_і_без_dv()
    {
        // 13 груп, 121 правило: (таблиця, база коду колонки, кількість правил до D-429)
        (string Table, string Base, int Was)[] removed =
        [
            ("T10_04", "FuelConsumption", 1),
            ("T5_26", "FlaringUnitGasConsumptionUnderStanda", 12),
            ("T6_11", "FlowRateOfSulfur", 12),
            ("T9_04", "SubstanceConsumption", 12),
            ("T9_04", "SubstanceConsumption20Dgr1atm", 12),
            ("T9_06", "SubstanceConsumption", 12),
            ("T9a_16", "SubstanceConsumption", 12),
            ("T9a_19", "DensityAt200C", 12),
            ("T9a_19", "HydrogenSulfidePpmWt", 12),
            ("T9a_19", "ReidVapourPressureKPa", 12),
            ("T9a_19", "SumMethylEthylMercaptansPpmWt", 12),
        ];
        Assert.Equal(121, removed.Sum(x => x.Was));

        var tables = ByCode();
        foreach (var (table, @base, was) in removed)
        {
            var t = tables[table];
            var cols = t.GetProperty("columns").EnumerateArray()
                .Where(c => Regex.IsMatch(c.GetProperty("code").GetString()!, "^" + Regex.Escape(@base) + @"(_M\d\d)?$"))
                .ToList();
            Assert.True(cols.Count >= (was == 1 ? 1 : 12), $"{table}.{@base}: колонок {cols.Count}");
            foreach (var c in cols)
            {
                // інші DV (>= 0 тощо) можуть бути; часової межі <= N не лишилось
                if (c.TryGetProperty("dv", out var dv) && dv.ValueKind == JsonValueKind.Object)
                {
                    var f1 = Str(dv, "f1");
                    Assert.False(
                        Str(dv, "op") == "lessThanOrEqual" && f1 is not null
                        && Bounds.Contains(double.Parse(f1, System.Globalization.CultureInfo.InvariantCulture)),
                        $"{table}.{c.GetProperty("code").GetString()}: dv <= {f1} лишився");
                }

                var code = "DV_" + c.GetProperty("code").GetString();
                Assert.DoesNotContain(
                    t.GetProperty("rules").EnumerateArray(),
                    r => r.GetProperty("code").GetString() == code
                         && Le.IsMatch(r.GetProperty("expression").GetString()!));
            }
        }

        // Усього правил DV у моделі: 1344 до D-429, 1223 після (-121)
        Assert.Equal(1223, Tables().Sum(t => t.GetProperty("rules").GetArrayLength()));
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Годинні_й_денні_колонки_зберегли_правила()
    {
        var tables = ByCode();

        // OperatingTime (h): по правилу на кожен місяць
        foreach (var table in new[] { "T6_11", "T9_05", "T9_06", "T9_08", "T9_09", "T9_10", "T9_13", "T9a_17", "T9a_22", "T9a_23" })
        {
            var rules = tables[table].GetProperty("rules").EnumerateArray()
                .Select(r => r.GetProperty("code").GetString()!).ToHashSet();
            for (var m = 1; m <= 12; m++)
            {
                Assert.Contains($"DV_OperatingTime_M{m:00}", rules);
            }
        }

        // дні (d) і «кількість днів зберігання» без одиниці, але з «days» у заголовку
        var t1102 = tables["T11_02"].GetProperty("rules").EnumerateArray().Select(r => r.GetProperty("code").GetString()!).ToList();
        Assert.Equal(12, t1102.Count(c => c.StartsWith("DV_MaterialResidenceTimeInProcessUnits_M", StringComparison.Ordinal)));
        var t606 = tables["T6_06"].GetProperty("rules").EnumerateArray().Select(r => r.GetProperty("code").GetString()!).ToList();
        Assert.True(t606.Count(c => c.StartsWith("DV_NumberOfDaysInStorage_M", StringComparison.Ordinal)) >= 11);
    }
}
