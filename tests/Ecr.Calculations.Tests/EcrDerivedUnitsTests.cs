using System.Globalization;
using System.Text.RegularExpressions;
using Ecr.Calculations;
using Ecr.Expressions;
using Ecr.Expressions.Evaluation;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Calculations.Tests;

/// <summary>
/// Похідні одиниці секції <c>-- UNITS:ecr-derived</c> сіду (<c>mg_per_Sm3</c>,
/// <c>Sm3_per_day</c>): їх назвали сухі прогони bootstrap-excel на книгах
/// замовника.
/// </summary>
/// <remarks>
/// ⛔ Мутаційні точки: зміни множник <c>mg_per_Sm3</c> у сіді (напр. на 0.001) —
/// червоніє <see cref="Сід_кладе_обидві_одиниці_у_свої_розмірності"/>; прибери
/// рядок з <c>UnitTable.Seed</c> — червоніє <see cref="Метричні_конверсії_у_межах_розмірності"/>
/// і загальна звірка дзеркала в <c>Hse301UnitsTests</c>; віднеси одиницю в
/// розмірність 11 (<c>MassPerVolume</c>) — червоніє
/// <see cref="Робочий_і_стандартний_кубометр_не_змішуються"/>.
/// </remarks>
public sealed class EcrDerivedUnitsTests
{
    private const string SeedPath = "src/Ecr.Infrastructure/Persistence/Sql/09-seed.sql";
    private const string SectionStart = "-- UNITS:ecr-derived ── похідні";
    private const string SectionEnd = "-- UNITS:ecr-derived ── кінець секції";

    private static readonly UnitTable Mirror = UnitTable.Seed();

    private static readonly string[] ExpectedCodes = ["Sm3_per_day", "mg_per_Sm3"];

    /// <summary><c>(N'mg_per_Sm3', 16, N'mg', N'Sm3', 0.000001)</c> — похідна посиланнями.</summary>
    private static readonly Regex DerivedRow = new(
        @"\(N'(?<code>[^']+)',\s*(?<dim>\d+),\s*N'(?<num>[^']+)',\s*N'(?<den>[^']+)',\s*(?<factor>\d+(?:\.\d+)?)\)",
        RegexOptions.CultureInvariant);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "ФВ-16.2")]
    public void Сід_кладе_обидві_одиниці_у_свої_розмірності()
    {
        var rows = SectionRows();

        // Рівно дві одиниці: жодного «заодно» — коефіцієнт умов приведення невідомий.
        Assert.Equal(ExpectedCodes, rows.Keys.Order(StringComparer.Ordinal).ToArray());

        // 16 — MassPerStdVolume (Mass / StdVolume), 13 — StdVolumeFlow (StdVolume / Time).
        Assert.Equal((16, "mg", "Sm3"), (rows["mg_per_Sm3"].Dimension, rows["mg_per_Sm3"].Num, rows["mg_per_Sm3"].Den));
        Assert.Equal((13, "Sm3", "day"), (rows["Sm3_per_day"].Dimension, rows["Sm3_per_day"].Num, rows["Sm3_per_day"].Den));

        // Метричний множник, без жодного припущення про умови приведення об'єму.
        Assert.Equal(0.000001m, rows["mg_per_Sm3"].Factor);

        // 1/86400, округлене до 18 знаків так, як зберігає decimal(38,18).
        Assert.Equal(Math.Round(1m / 86_400m, 18, MidpointRounding.AwayFromZero), rows["Sm3_per_day"].Factor);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "ФВ-16.2")]
    public void Метричні_конверсії_у_межах_розмірності()
    {
        // mg/Sm3 ↔ kg/Sm3: лише префікс «мілі», точно.
        Assert.Equal(0.000001m, Convert(1m, "mg_per_Sm3", "kg_per_Sm3"));
        Assert.Equal(1_000_000m, Convert(1m, "kg_per_Sm3", "mg_per_Sm3"));

        // Sm3/h → Sm3/day: 24 год у добі. 1/3600 і 1/86400 у decimal(38,18)
        // округлені, тож рівність — з допуском на це округлення (≤ 1e-9 відносно).
        Assert.InRange(Convert(24m, "Sm3_per_h", "Sm3_per_day"), 576m - 0.000000001m, 576m + 0.000000001m);
        Assert.InRange(Convert(1m, "Sm3_per_s", "Sm3_per_day"), 86_400m - 0.000000001m, 86_400m + 0.000000001m);

        // Sm3/day · day = Sm3 з точністю до округлення 1/86400 (6.4e-15).
        var perDay = Mirror.Convert(ExpressionValue.Number(1m), "Sm3_per_day", "Sm3_per_s").AsNumber()!.Value;
        Assert.True(Math.Abs((perDay * 86_400m) - 1m) <= 0.00000000000001m, $"Sm3/day · 86400 s = {perDay * 86_400m}.");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "ФВ-16.3")]
    public void Робочий_і_стандартний_кубометр_не_змішуються()
    {
        // ⛔ V-12: множника між Sm3 і m3 каталог не має (потрібні тиск і
        // температура) — «mg/Nm3» не стає «mg/m3» мовчки.
        Assert.Equal(ExpressionErrors.BadUnit, ErrorOf("mg_per_Sm3", "mg_per_m3"));
        Assert.Equal(ExpressionErrors.BadUnit, ErrorOf("mg_per_m3", "mg_per_Sm3"));

        // Потік — не об'єм і не концентрація.
        Assert.Equal(ExpressionErrors.BadUnit, ErrorOf("Sm3_per_day", "Sm3"));
        Assert.Equal(ExpressionErrors.BadUnit, ErrorOf("Sm3_per_day", "mg_per_Sm3"));
    }

    private static decimal Convert(decimal value, string from, string to)
    {
        var result = Mirror.Convert(ExpressionValue.Number(value), from, to);
        Assert.False(result.IsError, $"CONVERT({value}, '{from}', '{to}') = {result.ErrorCode}.");
        return result.AsNumber()!.Value;
    }

    private static string? ErrorOf(string from, string to)
        => Mirror.Convert(ExpressionValue.Number(1m), from, to).ErrorCode;

    private static Dictionary<string, Row> SectionRows()
    {
        var text = File.ReadAllText(Path.Combine(SolutionRoot(), SeedPath.Replace('/', Path.DirectorySeparatorChar)));
        var start = text.IndexOf(SectionStart, StringComparison.Ordinal);
        var end = text.IndexOf(SectionEnd, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "Секції `-- UNITS:ecr-derived` у 09-seed.sql немає.");

        // Коментарі секції згадують коди й числа — беремо лише текст запиту.
        var body = text[start..end];
        var rows = new Dictionary<string, Row>(StringComparer.Ordinal);

        foreach (Match row in DerivedRow.Matches(body))
        {
            var code = row.Groups["code"].Value;
            Assert.True(
                rows.TryAdd(
                    code,
                    new Row(
                        int.Parse(row.Groups["dim"].Value, CultureInfo.InvariantCulture),
                        row.Groups["num"].Value,
                        row.Groups["den"].Value,
                        Math.Round(
                            decimal.Parse(row.Groups["factor"].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture),
                            18,
                            MidpointRounding.AwayFromZero))),
                $"Одиниця {code} у секції двічі.");
        }

        Assert.NotEmpty(rows);
        return rows;
    }

    private static string SolutionRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Ecr.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Немає Ecr.sln.");
    }

    private sealed record Row(int Dimension, string Num, string Den, decimal Factor);
}
