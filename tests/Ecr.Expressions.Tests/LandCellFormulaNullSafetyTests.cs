using System.Text.Json;
using System.Text.RegularExpressions;
using Ecr.Expressions.Evaluation;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Expressions.Tests;

/// <summary>
/// LP-31: у ТЗ (<c>05-engines</c> «Семантика null», <c>02b</c> §6.2) бінарні операції ПОШИРЮЮТЬ null, а <c>SUM</c> його
/// поглинає (§6.1, «сумісність з Excel»). Формули 7.0 (7. Water Report) в Excel — <c>(a+b+c)/1000</c>, де порожня
/// клітинка = 0. Тому модель Land пише їх як <c>SUM(a,b,c)/1000</c>. Перші два тести фіксують норму ТЗ у рушії
/// (вони НЕ змінюються цим фіксом), третій — що формула з моделі дає число при відсутньому операнді (до фіксу моделі
/// давала null, тобто «немає значення»).
/// </summary>
public sealed partial class LandCellFormulaNullSafetyTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Сума_з_відсутнім_операндом_дає_null_за_ТЗ()
    {
        var context = new TestEvaluationContext();
        context.SetCell("S", "T", "R1", "Jan", ExpressionValue.Number(1m));
        context.SetCell("S", "T", "R3", "Jan", ExpressionValue.Number(3m));
        Rows(context, "S", "T", "R1", "R2", "R3");

        var value = Expr.Eval("[S].[T].[R1].[Jan]+[S].[T].[R2].[Jan]+[S].[T].[R3].[Jan]", context);

        Assert.True(value.IsNull);
        Assert.False(value.IsError);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void SUM_поглинає_відсутній_операнд_і_порожня_множина_дорівнює_нулю()
    {
        var context = new TestEvaluationContext();
        context.SetCell("S", "T", "R1", "Jan", ExpressionValue.Number(1m));
        context.SetCell("S", "T", "R3", "Jan", ExpressionValue.Number(3m));
        Rows(context, "S", "T", "R1", "R2", "R3");

        Assert.Equal(4m, Expr.Number("SUM([S].[T].[R1].[Jan],[S].[T].[R2].[Jan],[S].[T].[R3].[Jan])", context));
        Assert.Equal(0m, Expr.Number("SUM([S].[T].[R2].[Jan])/1000", context));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Усі_216_формул_7_0_з_моделі_дають_суму_а_не_null_коли_заповнено_лише_один_операнд()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            RepositoryRoot(), "docs", "delivery", "reference-data", "land-template", "land-template.model.json")));
        var table = doc.RootElement.GetProperty("sheets").EnumerateArray()
            .Single(s => s.GetProperty("excelName").GetString()!.StartsWith("7.0", StringComparison.Ordinal))
            .GetProperty("tables")[0];

        var formulas = table.GetProperty("cellFormulas").EnumerateArray().ToList();
        Assert.Equal(216, formulas.Count);

        var multiOperand = 0;
        foreach (var f in formulas)
        {
            var expression = f.GetProperty("expression").GetString()!;
            var refs = Reference().Matches(expression).ToList();
            Assert.NotEmpty(refs);

            // Заповнено лише ПЕРШИЙ операнд числом 5000; решта відсутні (порожні клітинки Water Report).
            var context = new TestEvaluationContext();
            foreach (var r in refs)
            {
                Rows(context, r.Groups["s"].Value, r.Groups["t"].Value, r.Groups["r"].Value);
            }

            var first = refs[0];
            context.SetCell(first.Groups["s"].Value, first.Groups["t"].Value, first.Groups["r"].Value, first.Groups["c"].Value,
                ExpressionValue.Number(5000m));
            if (refs.Count > 1)
            {
                multiOperand++;
            }

            var value = Expr.Eval(expression, context);
            Assert.False(value.IsNull, $"{f.GetProperty("excelCell").GetString()}: «немає значення» при одному заповненому операнді");
            Assert.Equal(5m, value.AsNumber());

            // Усі операнди відсутні — Excel дав би 0; null-безпечна форма теж 0.
            var empty = new TestEvaluationContext();
            foreach (var r in refs)
            {
                Rows(empty, r.Groups["s"].Value, r.Groups["t"].Value, r.Groups["r"].Value);
            }

            Assert.Equal(0m, Expr.Eval(expression, empty).AsNumber());
        }

        Assert.Equal(48, multiOperand); // 36 формул з 3 операндами + 12 з 4
    }

    // Рядки Fixed існують у таблиці, навіть коли комірка порожня (немає CellValue): це null, а не #REF.
    private static void Rows(TestEvaluationContext context, string sheet, string table, params string[] rows)
    {
        var key = $"{sheet}|{table}";
        var list = context.Rows.TryGetValue(key, out var l) ? l : context.Rows[key] = [];
        foreach (var r in rows.Where(r => !list.Contains(r, StringComparer.Ordinal)))
        {
            list.Add(r);
        }
    }

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Ecr.sln")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Не знайдено корінь репозиторію (Ecr.sln).");
    }

    [GeneratedRegex(@"\[(?<s>[^\]]+)\]\.\[(?<t>[^\]]+)\]\.\[(?<r>[^\]]+)\]\.\[(?<c>[^\]]+)\]")]
    private static partial Regex Reference();
}
