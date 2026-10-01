using System.Globalization;
using System.Text.RegularExpressions;
using Ecr.Calculations;
using Ecr.Expressions;
using Ecr.Expressions.Evaluation;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Calculations.Tests;

/// <summary>
/// Одиниці методології <c>HSE301.FLARE</c> (FEATURE-HSE301-VIEW §11.2, крок F1).
/// </summary>
/// <remarks>
/// Дві половини, і жодна не зайва. Числа конверсій перевіряються на
/// <see cref="UnitTable.Seed"/> — там, де методологія рахує без бази. Сам сід
/// перевіряється по ТЕКСТУ <c>09-seed.sql</c>: воркер бере довідник із бази
/// (<c>GenericCalculationModule.UnitsAsync</c>), і множник, зламаний лише в
/// сіді, дав би в продуктиві інше число, ніж у симуляції, — тест на одному
/// дзеркалі цього не побачив би.
/// <para>
/// ⚠ Числа — <see cref="decimal"/>, порівняння точне: допуск <c>double</c>
/// сховав би саме ту похибку, про яку тут ідеться.
/// </para>
/// </remarks>
public sealed class Hse301UnitsTests
{
    private const string SeedPath = "src/Ecr.Infrastructure/Persistence/Sql/09-seed.sql";

    private static readonly UnitTable Mirror = UnitTable.Seed();

    /// <summary>Одиниці кроку й розмірність, у якій кожна мусить лежати.</summary>
    private static readonly Dictionary<string, string> F1Units = new(StringComparer.Ordinal)
    {
        ["Sm3"] = "StdVolume",
        ["Sm3_per_h"] = "StdVolumeFlow",
        ["Sm3_per_s"] = "StdVolumeFlow",
        ["Nm3"] = "StdVolume",
        ["Nm3_per_s"] = "StdVolumeFlow",
        ["Nm3_per_h"] = "StdVolumeFlow",
        ["Nm3_per_day"] = "StdVolumeFlow",
        ["mg_per_Nm3"] = "MassPerStdVolume",
        ["kt"] = "Mass",
        ["m_per_s"] = "Velocity",
        ["m2"] = "Area",
        ["pct_vol"] = "Dimensionless",
        ["pct_wt"] = "MassPerMass",
        ["MJ_per_Sm3"] = "EnergyPerStdVolume",
        ["MJ_per_kg"] = "EnergyPerMass",
        ["kg_per_Sm3"] = "MassPerStdVolume",
        ["t_per_t"] = "MassPerMass",
        ["kg_per_TJ"] = "MassPerEnergy",
        ["g_per_mol"] = "MassPerAmount",
        ["MJ"] = "Energy",
        ["TJ"] = "Energy",
    };

    private static readonly Regex UnitBlock = new(
        @"MERGE uom\.Unit AS t(?<body>.*?)\r?\nGO\b", RegexOptions.Singleline | RegexOptions.CultureInvariant);

    private static readonly Regex DimensionBlock = new(
        @"MERGE uom\.Dimension AS t(?<body>.*?)\r?\nGO\b", RegexOptions.Singleline | RegexOptions.CultureInvariant);

    /// <summary><c>(N'kg', 1, 1, 1.0, 0.0)</c> — код, розмірність, базова, множник, зсув.</summary>
    private static readonly Regex UnitRow = new(
        @"\(N'(?<code>[^']+)',\s*(?<dim>\d+),\s*[01],\s*(?<factor>\d+(?:\.\d+)?),\s*(?<offset>\d+(?:\.\d+)?)\)",
        RegexOptions.CultureInvariant);

    /// <summary><c>(N'g_per_s', 8, N'g', N's', 0.001)</c> — похідна посиланнями.</summary>
    private static readonly Regex DerivedRow = new(
        @"\(N'(?<code>[^']+)',\s*(?<dim>\d+),\s*N'(?<num>[^']+)',\s*N'(?<den>[^']+)',\s*(?<factor>\d+(?:\.\d+)?)\)",
        RegexOptions.CultureInvariant);

    private static readonly Regex DimensionRow = new(
        @"\((?<id>\d+),\s*N'(?<code>[^']+)',\s*(?<derived>[01]),\s*(?<num>NULL|\d+),\s*(?<den>NULL|\d+)\)",
        RegexOptions.CultureInvariant);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "ФВ-16.2")]
    public void Маса_і_час_конвертуються_точно()
    {
        // §6.3 №2, приклад A: 269.258 Sm3 · 0.9589 kg/Sm3 = 258.1914962 kg.
        Assert.Equal(0.2581914962m, Convert(258.1914962m, "kg", "t"));

        // №14: tons[SO2] = 0.0891735 t → g.
        Assert.Equal(89_173.5m, Convert(0.0891735m, "t", "g"));

        // №10–11: CONVERT(!M_t, 't', 'kt').
        Assert.Equal(0.0002581914962m, Convert(0.2581914962m, "t", "kt"));

        // №1, приклад B: пілот за січень — 2 678 400 s = 744 h рівно.
        Assert.Equal(744m, Convert(2_678_400m, "s", "h"));

        // 930 s = 31/120 h: частка нескінченна, але це ДІЛЕННЯ на точні 3600 —
        // правильно округлене до 28 знаків, а не добуток на округлене 1/3600.
        Assert.Equal(0.2583333333333333333333333333m, Convert(930m, "s", "h"));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "ФВ-16.2")]
    public void Потік_Sm3_за_годину_на_930_секунд_дає_стандартні_кубометри()
    {
        // Шлях формули V_Sm3 (§6.3 №1): потік × CONVERT(тривалість, 's', 'h').
        // Приклад B — без жодного округлення.
        Assert.Equal(7_216.8m, 9.7m * Convert(2_678_400m, "s", "h"));

        // Подія 930 s: похибка частки ≤ 1e-27, тож на шкалі комірки
        // (decimal(34,16)) об'єм точний: 3.6 · 930 / 3600 = 0.93.
        Assert.Equal(0.93m, Math.Round(3.6m * Convert(930m, "s", "h"), 16));

        // Шлях межі (§4.2): Sm3/h → база Sm3/s → × s. База StdVolume — Sm3,
        // база Time — s, тож добуток уже в Sm3.
        // ⚠ НЕ 0.93: 1/3600 у decimal(38,18) = 0.000277777777777778, і похибка
        // 7.44e-16 видна на шкалі комірки. Тому інтеграл на межі (F3) — через
        // знаменник h, а не через цей множник.
        var viaBase = Convert(3.6m, "Sm3_per_h", "Sm3_per_s") * 930m;
        Assert.Equal(0.930000000000000744m, viaBase);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "ФВ-16.3")]
    public void Стандартний_і_робочий_кубометр_не_конвертуються()
    {
        // ⛔ V-12. Множник між ними залежить від тиску й температури, тож
        // конверсія «1 до 1» дала б правдоподібне й хибне число.
        Assert.Equal(ExpressionErrors.BadUnit, ErrorOf("Sm3", "m3"));
        Assert.Equal(ExpressionErrors.BadUnit, ErrorOf("m3", "Sm3"));
        Assert.Equal(ExpressionErrors.BadUnit, ErrorOf("kg_per_Sm3", "kg_per_m3"));
        Assert.Equal(ExpressionErrors.BadUnit, ErrorOf("kg_per_m3", "kg_per_Sm3"));

        // Потік — не об'єм: множення на час робить формула, а не CONVERT.
        Assert.Equal(ExpressionErrors.BadUnit, ErrorOf("Sm3_per_h", "Sm3"));

        // Об.% ↔ мас.% без складу газу не перераховуються.
        Assert.Equal(ExpressionErrors.BadUnit, ErrorOf("pct_vol", "pct_wt"));
        Assert.Equal(ExpressionErrors.BadUnit, ErrorOf("pct_wt", "pct_vol"));

        // ⚠ Що Sm3 у дзеркалі взагалі є, CONVERT показати не може: він єдиний у
        // своїй розмірності, тож будь-яка конверсія з ним — або тотожність, або
        // #UNIT. Решту стереже звірка з сідом нижче.
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "ФВ-16.2")]
    public void Відсотки_і_енергія_конвертуються_у_своїх_розмірностях()
    {
        // S wt% прикладу A: 17.2965447 мас.% = 172.965447 кг/т.
        Assert.Equal(172.965447m, Convert(17.2965447m, "pct_wt", "kg_per_t"));

        // K_MASS[NO2] = 0.0024 t/t = 2.4 kg/t.
        Assert.Equal(2.4m, Convert(0.0024m, "t_per_t", "kg_per_t"));
        Assert.Equal(0.5m, Convert(50m, "pct_vol", "one"));

        // EF_CH4_KG_TJ: 1 kg/TJ = 1 g/GJ.
        Assert.Equal(1m, Convert(1m, "kg_per_TJ", "g_per_GJ"));
        Assert.Equal(1m, Convert(1_000m, "MJ", "GJ"));
        Assert.Equal(1_000m, Convert(1m, "TJ", "GJ"));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "ФВ-16.2")]
    public void Сід_кладе_одиниці_301_у_свої_розмірності()
    {
        var (units, dimensions) = LoadSeed();
        var byId = dimensions.Values.ToDictionary(d => d.Id);

        foreach (var code in (string[])
                 ["StdVolume", "StdVolumeFlow", "Velocity", "Area", "MassPerStdVolume",
                  "EnergyPerStdVolume", "EnergyPerMass", "MassPerEnergy"])
        {
            Assert.True(dimensions.ContainsKey(code), $"Розмірності {code} у сіді немає.");
        }

        foreach (var (code, dimension) in F1Units)
        {
            Assert.True(units.TryGetValue(code, out var unit), $"Одиниці {code} у сіді немає.");
            Assert.Equal(dimension, byId[unit!.Dimension].Code);
        }

        // ⛔ V-12: StdVolume — первинна і НЕ Volume.
        var std = dimensions["StdVolume"];
        Assert.False(std.IsDerived);
        Assert.NotEqual(dimensions["Volume"].Id, std.Id);

        // Потік — частка ст. об'єму й часу; щільність і теплота — на ст. м³.
        AssertRatio(dimensions["StdVolumeFlow"], std, dimensions["Time"]);
        AssertRatio(dimensions["MassPerStdVolume"], dimensions["Mass"], std);
        AssertRatio(dimensions["EnergyPerStdVolume"], dimensions["Energy"], std);
        AssertRatio(dimensions["EnergyPerMass"], dimensions["Energy"], dimensions["Mass"]);

        // Похідні складаються посиланнями (ФВ-16.2). Знаменник h — саме те, що
        // дає межі точний шлях інтеграла Sm3/h × s.
        Assert.Equal(("Sm3", "h"), (units["Sm3_per_h"].Numerator, units["Sm3_per_h"].Denominator));
        Assert.Equal(("MJ", "kg"), (units["MJ_per_kg"].Numerator, units["MJ_per_kg"].Denominator));
        Assert.Equal(("kg", "TJ"), (units["kg_per_TJ"].Numerator, units["kg_per_TJ"].Denominator));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "ФВ-16.2")]
    public void Множники_сіду_узгоджені_з_формулами_301()
    {
        var (units, _) = LoadSeed();
        decimal F(string code) => units[code].Factor;

        // №2 M_t: Sm3 · kg/Sm3 = kg.
        Assert.Equal(F("kg"), F("Sm3") * F("kg_per_Sm3"));

        // №3 NCV: (MJ/Sm3) / (kg/Sm3) = MJ/kg.
        Assert.Equal(F("MJ_per_kg"), F("MJ_per_Sm3") / F("kg_per_Sm3"));

        // №7 V_LIM: Sm3/s · s = Sm3.
        Assert.Equal(F("Sm3"), F("Sm3_per_s") * F("s"));

        // №10–11: kt · MJ/kg · kg/TJ = kg — ⛔ без жодного ·10ⁿ у формулі (D-74).
        Assert.Equal(F("kg"), F("kt") * F("MJ_per_kg") * F("kg_per_TJ"));

        // №14 gsec: g / s = g/s.
        Assert.Equal(F("g_per_s"), F("g") / F("s"));

        // №1 V_Sm3: Sm3/h · h = Sm3 — з точністю до округлення 1/3600 у
        // decimal(38,18). Множник 1/60 дав би тут 60.
        var hourly = F("Sm3_per_h") * F("h");
        Assert.True(Math.Abs(hourly - F("Sm3")) <= 0.000000000000001m, $"Sm3/h · h = {hourly} Sm3.");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "ФВ-16.2")]
    public void Дзеркало_в_памяті_збігається_з_сідом()
    {
        // ⚠ Порівнюється КОЖНА пара одиниць сіду, а не лише нові: дзеркало, яке
        // знає не всі одиниці, дає #UNIT у симуляції там, де продуктив рахує.
        var (units, _) = LoadSeed();
        var mismatches = new List<string>();

        foreach (var from in units.Values)
        {
            foreach (var to in units.Values)
            {
                if (from.Code == to.Code)
                {
                    continue;
                }

                var actual = Mirror.Convert(ExpressionValue.Number(1m), from.Code, to.Code);

                if (from.Dimension != to.Dimension)
                {
                    if (actual.ErrorCode != ExpressionErrors.BadUnit)
                    {
                        mismatches.Add($"{from.Code} → {to.Code}: у сіді різні розмірності, дзеркало дало {Show(actual)}");
                    }

                    continue;
                }

                var expected = ((1m * from.Factor) + from.Offset - to.Offset) / to.Factor;
                if (actual.AsNumber() != expected)
                {
                    mismatches.Add($"{from.Code} → {to.Code}: сід {expected}, дзеркало {Show(actual)}");
                }
            }
        }

        Assert.True(
            mismatches.Count == 0,
            "UnitTable.Seed розійшовся з 09-seed.sql:" + Environment.NewLine
            + string.Join(Environment.NewLine, mismatches.Take(20)));
    }

    private static decimal Convert(decimal value, string from, string to)
    {
        var result = Mirror.Convert(ExpressionValue.Number(value), from, to);
        Assert.False(result.IsError, $"CONVERT({value}, '{from}', '{to}') = {result.ErrorCode}.");
        return result.AsNumber()!.Value;
    }

    private static string? ErrorOf(string from, string to)
        => Mirror.Convert(ExpressionValue.Number(1m), from, to).ErrorCode;

    private static string Show(ExpressionValue value)
        => value.ErrorCode ?? value.AsNumber()?.ToString(CultureInfo.InvariantCulture) ?? "null";

    private static void AssertRatio(SeedDimension derived, SeedDimension numerator, SeedDimension denominator)
    {
        Assert.True(derived.IsDerived, $"{derived.Code} мусить бути похідною.");
        Assert.Equal((int?)numerator.Id, derived.Numerator);
        Assert.Equal((int?)denominator.Id, derived.Denominator);
    }

    /// <summary>Одиниці й розмірності з тексту сіду.</summary>
    /// <remarks>
    /// ⚠ Множник — такий, як його зберігає <c>decimal(38,18)</c>: SQL Server
    /// округлює довший літерал (<c>t_per_year</c>) половиною від нуля.
    /// </remarks>
    private static (Dictionary<string, SeedUnit> Units, Dictionary<string, SeedDimension> Dimensions) LoadSeed()
    {
        var text = File.ReadAllText(Path.Combine(SolutionRoot(), SeedPath.Replace('/', Path.DirectorySeparatorChar)));
        var units = new Dictionary<string, SeedUnit>(StringComparer.Ordinal);

        foreach (Match block in UnitBlock.Matches(text))
        {
            var body = block.Groups["body"].Value;

            foreach (Match row in UnitRow.Matches(body))
            {
                Add(units, new SeedUnit(
                    row.Groups["code"].Value, Int(row, "dim"), Stored(row, "factor"), Stored(row, "offset"), null, null));
            }

            foreach (Match row in DerivedRow.Matches(body))
            {
                Add(units, new SeedUnit(
                    row.Groups["code"].Value, Int(row, "dim"), Stored(row, "factor"), 0m,
                    row.Groups["num"].Value, row.Groups["den"].Value));
            }
        }

        // ⚠ Контроль самого розбору: якби формат рядків сіду змінився, словник
        // був би порожнім, і звірка вище проходила б на будь-якому дзеркалі.
        Assert.True(units.Count > 30, $"У сіді розібрано лише {units.Count} одиниць — регулярка розійшлася з форматом.");

        var dimensions = new Dictionary<string, SeedDimension>(StringComparer.Ordinal);
        var ids = new HashSet<int>();

        foreach (Match block in DimensionBlock.Matches(text))
        {
            foreach (Match row in DimensionRow.Matches(block.Groups["body"].Value))
            {
                var dimension = new SeedDimension(
                    Int(row, "id"), row.Groups["code"].Value, row.Groups["derived"].Value == "1",
                    NullableInt(row, "num"), NullableInt(row, "den"));

                // MERGE ... ON t.Id мовчки пропустив би другий рядок з тим самим Id.
                Assert.True(ids.Add(dimension.Id), $"Розмірність {dimension.Id} у сіді двічі.");
                Assert.True(dimensions.TryAdd(dimension.Code, dimension), $"Розмірність {dimension.Code} у сіді двічі.");
            }
        }

        Assert.True(dimensions.Count > 10, $"У сіді розібрано лише {dimensions.Count} розмірностей.");
        return (units, dimensions);
    }

    private static void Add(Dictionary<string, SeedUnit> units, SeedUnit unit)
        => Assert.True(
            units.TryAdd(unit.Code, unit),
            $"Одиниця {unit.Code} у сіді двічі: MERGE ... ON t.Code мовчки пропустить другий рядок.");

    private static int Int(Match row, string group)
        => int.Parse(row.Groups[group].Value, CultureInfo.InvariantCulture);

    private static int? NullableInt(Match row, string group)
        => row.Groups[group].Value == "NULL" ? null : Int(row, group);

    private static decimal Stored(Match row, string group)
        => Math.Round(
            decimal.Parse(row.Groups[group].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture),
            18,
            MidpointRounding.AwayFromZero);

    private static string SolutionRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Ecr.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Немає Ecr.sln.");
    }

    private sealed record SeedUnit(
        string Code, int Dimension, decimal Factor, decimal Offset, string? Numerator, string? Denominator);

    private sealed record SeedDimension(int Id, string Code, bool IsDerived, int? Numerator, int? Denominator);
}
