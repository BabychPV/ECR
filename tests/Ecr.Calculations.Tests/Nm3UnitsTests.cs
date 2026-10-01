using System.Globalization;
using System.Text.RegularExpressions;
using Ecr.Calculations;
using Ecr.Expressions;
using Ecr.Expressions.Evaluation;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Calculations.Tests;

/// <summary>
/// Нормальний кубометр <c>Nm3</c> (0 °C, 1 атм) окремо від <c>Sm3</c> (20 °C, 1 атм): секція
/// <c>-- HSE301:NM3</c> сіду і дзеркало <see cref="UnitTable.Seed"/>.
/// </summary>
/// <remarks>
/// ПРИПУЩЕННЯ — замінити фактом замовника: 1 Nm3 = 293.15 / 273.15 Sm3 (ідеальний газ, однаковий тиск).
/// ⛔ Мутаційні точки: множник Nm3 у <c>UnitTable.Seed</c> або сіді → 1.0 — червоніють
/// <see cref="Nm3_у_Sm3_множиться_на_T20_до_T0"/> і <see cref="Сід_тримає_той_самий_фактор_що_й_дзеркало"/>.
/// </remarks>
public sealed class Nm3UnitsTests
{
    private const string SeedPath = "src/Ecr.Infrastructure/Persistence/Sql/09-seed.sql";
    private const string SectionStart = "-- HSE301:NM3 ──";
    private const string SectionEnd = "-- HSE301:NM3 ── кінець секції";

    private static readonly UnitTable Mirror = UnitTable.Seed();

    private static readonly decimal Factor = 293.15m / 273.15m;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "ФВ-16.2")]
    public void Nm3_у_Sm3_множиться_на_T20_до_T0()
    {
        AssertClose(Factor, Convert(1m, "Nm3", "Sm3"));
        AssertClose(Factor * 1000m, Convert(1000m, "Nm3", "Sm3"));
        AssertClose(1m / Factor, Convert(1m, "Sm3", "Nm3"));

        // 1 Nm3 більший за 1 Sm3: 7,32 %. Рівність 1:1 (мутація) тут червоніє.
        Assert.True(Convert(1m, "Nm3", "Sm3") > 1.0732m);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "ФВ-16.2")]
    public void Туди_й_назад_повертає_вихідне_число()
    {
        foreach (var value in new[] { 1m, 0.93m, 3348m, 12345.6789m })
        {
            var back = Convert(Convert(value, "Nm3", "Sm3"), "Sm3", "Nm3");
            AssertClose(value, back);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "ФВ-16.2")]
    public void Похідні_Nm3_узгоджені_з_Sm3()
    {
        // Потік 1 Nm3/h = F Sm3/h; питома концентрація: 1 mg/Nm3 = 1/F mg/Sm3.
        AssertClose(Factor, Convert(1m, "Nm3_per_h", "Sm3_per_h"));
        AssertClose(Factor, Convert(1m, "Nm3_per_day", "Sm3_per_day"));
        AssertClose(Factor, Convert(1m, "Nm3_per_s", "Sm3_per_s"));
        AssertClose(1m / Factor, Convert(1m, "mg_per_Nm3", "mg_per_Sm3"));
        AssertClose(24m, Convert(24m, "Nm3_per_h", "Nm3_per_h"));
        AssertClose(576m, Convert(24m, "Nm3_per_h", "Nm3_per_day") * 1m);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "ФВ-16.3")]
    public void Nm3_і_робочий_м3_не_конвертуються()
    {
        // ⛔ V-12: Nm3 — стандартний (розмірність 12), m3 — робочий (2); коефіцієнта без p, T немає.
        Assert.Equal(ExpressionErrors.BadUnit, ErrorOf("Nm3", "m3"));
        Assert.Equal(ExpressionErrors.BadUnit, ErrorOf("m3", "Nm3"));
        Assert.Equal(ExpressionErrors.BadUnit, ErrorOf("mg_per_Nm3", "mg_per_m3"));
        Assert.Equal(ExpressionErrors.BadUnit, ErrorOf("Nm3_per_day", "Nm3"));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "ФВ-16.2")]
    public void Приклад_930_секунд_у_Sm3_не_змінився()
    {
        // RowWindowFetchTests: 3.6 Sm3/h × 930 s = 0.93 Sm3; Sm3 лишається базою, множник 1.
        Assert.Equal(1m, Convert(1m, "Sm3", "Sm3"));
        Assert.Equal(0.93m, Math.Round(3.6m * Convert(930m, "s", "h"), 16));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "ФВ-16.2")]
    public void Сід_тримає_той_самий_фактор_що_й_дзеркало()
    {
        var text = File.ReadAllText(Path.Combine(SolutionRoot(), SeedPath.Replace('/', Path.DirectorySeparatorChar)));
        var start = text.IndexOf(SectionStart, StringComparison.Ordinal);
        var end = text.IndexOf(SectionEnd, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "Секції `-- HSE301:NM3` у 09-seed.sql немає.");
        var body = text[start..end];

        // (N'Nm3', 12, 0, 1.0732..., 0.0)
        var nm3 = Regex.Match(body, @"\(N'Nm3',\s*12,\s*0,\s*(?<f>\d+\.\d+),\s*0\.0\)", RegexOptions.CultureInvariant);
        Assert.True(nm3.Success, "Рядок Nm3 (розмірність 12, не база) у секції не знайдено.");
        var seedFactor = decimal.Parse(nm3.Groups["f"].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);

        AssertClose(Factor, seedFactor);
        AssertClose(seedFactor, Convert(1m, "Nm3", "Sm3"));

        // Похідні тягнуться від того самого фактора.
        foreach (var (code, expected) in new (string, decimal)[]
                 {
                     ("Nm3_per_s", Factor),
                     ("Nm3_per_h", Factor / 3600m),
                     ("Nm3_per_day", Factor / 86_400m),
                     ("mg_per_Nm3", 0.000001m / Factor),
                 })
        {
            var row = Regex.Match(
                body, @"\(N'" + code + @"',\s*\d+,\s*N'[^']+',\s*N'[^']+',\s*(?<f>\d+\.\d+)\)", RegexOptions.CultureInvariant);
            Assert.True(row.Success, $"Рядок {code} у секції не знайдено.");
            var value = decimal.Parse(row.Groups["f"].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
            AssertClose(expected, value);
        }
    }

    /// <summary>Відносний допуск 1e-9: похідні в decimal(38,18) округлені.</summary>
    private static void AssertClose(decimal expected, decimal actual)
        => Assert.True(
            Math.Abs(actual - expected) <= Math.Abs(expected) * 0.000000001m,
            $"Очікувалось {expected.ToString(CultureInfo.InvariantCulture)}, є {actual.ToString(CultureInfo.InvariantCulture)}.");

    private static decimal Convert(decimal value, string from, string to)
    {
        var result = Mirror.Convert(ExpressionValue.Number(value), from, to);
        Assert.False(result.IsError, $"CONVERT({value}, '{from}', '{to}') = {result.ErrorCode}.");
        return result.AsNumber()!.Value;
    }

    private static string? ErrorOf(string from, string to)
        => Mirror.Convert(ExpressionValue.Number(1m), from, to).ErrorCode;

    private static string SolutionRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Ecr.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Немає Ecr.sln.");
    }
}
