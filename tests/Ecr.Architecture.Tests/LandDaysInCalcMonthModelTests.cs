using System.Text.Json;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// Паритет Land 12.10 (трек C): у старій системі аргумент <c>@DaysInCalcMonth</c> методології 7.7 дорівнює
/// <c>Land_Measure_OperTimeDays</c> (відпрацьовані дні), а не календарним дням періоду: при 0 днів результат 0 (гілка
/// <c>if(@DaysInCalcMonth=0,0,...)</c>), tons рахуються від <c>Stor/(365/Days)</c>. Модель ECR задавала
/// <c>[Period].Days</c> (31 для січня): 60 клітинок C07_07 на LAND-PROD-1..3 мали нуль у старій і не нуль в ECR.
/// Тест читає лише файл моделі; без перегенерації (<c>tools/land/model-gen/run-model-gen.ps1</c>) він червоний.
/// </summary>
public sealed class LandDaysInCalcMonthModelTests
{
    private static JsonDocument Model()
        => JsonDocument.Parse(File.ReadAllText(Path.Combine(
            SourceTree.Root, "docs", "delivery", "reference-data", "land-template", "land-template.model.json")));

    private static IEnumerable<JsonElement> Columns(JsonElement root, string code)
        => root.GetProperty("sheets").EnumerateArray()
            .SelectMany(s => s.GetProperty("tables").EnumerateArray())
            .SelectMany(t => t.GetProperty("columns").EnumerateArray())
            .Where(c => c.GetProperty("code").GetString() == code);

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void DaysInCalcMonth_береться_з_відпрацьованих_днів_поточного_періоду_а_не_з_календаря()
    {
        using var doc = Model();
        var columns = Columns(doc.RootElement, "DaysInCalcMonth").ToList();
        Assert.NotEmpty(columns);

        foreach (var column in columns)
        {
            var expression = column.GetProperty("expression").GetString()!;
            Assert.DoesNotContain("[Period].Days", expression, StringComparison.Ordinal);
            Assert.Contains("[Period].Sequence", expression, StringComparison.Ordinal);
            for (var month = 1; month <= 12; month++)
            {
                Assert.Contains($"[Land_Measure_OperTimeDays_M{month:00}]", expression, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void DaysInCalcYear_не_змінився()
    {
        using var doc = Model();
        // колонка є й у мостах аргументів таблиць 6, 9-11 (LP-10) - вираз у всіх однаковий
        var year = Columns(doc.RootElement, "DaysInCalcYear").Select(c => c.GetProperty("expression").GetString()).Distinct().Single();
        Assert.Equal("([Period].Year % 4 = 0) ? 366 : 365", year);
    }
}