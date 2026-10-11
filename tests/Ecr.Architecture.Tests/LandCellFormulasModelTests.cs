using System.Text.Json;
using System.Text.RegularExpressions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// D1: модель шаблону Land несе 216 перехресних формул 7.0 ← 7. Water Report як формули РІВНЯ КОМІРКИ
/// (<c>cellFormulas</c>), а Build-LandTemplate.ps1 записує їх через <c>PUT …/formulas/cell/…</c>. Тест читає лише
/// файли репозиторію (без API): модель самодостатня, кожне посилання розв'язується в самій моделі.
/// </summary>
public sealed partial class LandCellFormulasModelTests
{
    private static readonly string[] Months =
    [
        "January", "February", "March", "April", "May", "June",
        "July", "August", "September", "October", "November", "December",
    ];

    private static JsonDocument Model()
        => JsonDocument.Parse(File.ReadAllText(Path.Combine(
            SourceTree.Root, "docs", "delivery", "reference-data", "land-template", "land-template.model.json")));

    private static JsonElement Sheet(JsonElement model, string excelNamePrefix)
        => model.GetProperty("sheets").EnumerateArray()
            .Single(s => s.GetProperty("excelName").GetString()!.StartsWith(excelNamePrefix, StringComparison.Ordinal));

    [GeneratedRegex(@"\[(?<s>[^\]]+)\]\.\[(?<t>[^\]]+)\]\.\[(?<r>[^\]]+)\]\.\[(?<c>[^\]]+)\]")]
    private static partial Regex Reference();

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Модель_7_0_має_216_комірочних_формул_18_рядків_на_12_місяців_і_нічого_нерозв_язаного()
    {
        using var doc = Model();
        var table = Sheet(doc.RootElement, "7.0").GetProperty("tables")[0];

        var formulas = table.GetProperty("cellFormulas").EnumerateArray().ToList();
        Assert.Equal(216, formulas.Count);
        Assert.Empty(table.GetProperty("cellFormulasUnresolved").EnumerateArray());

        var pairs = formulas.Select(f => (f.GetProperty("row").GetString(), f.GetProperty("column").GetString())).ToList();
        Assert.Equal(216, pairs.Distinct().Count());
        Assert.Equal(18, pairs.Select(p => p.Item1).Distinct().Count());
        Assert.Equal(Months.Order(), pairs.Select(p => p.Item2!).Distinct().Order());

        var rows = table.GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("key").GetString()).ToHashSet();
        Assert.All(pairs, p => Assert.Contains(p.Item1, rows));

        // Колонка з комірочною формулою обчислювана (інакше PUT дав би ECR-TMPL-4227).
        var typeByColumn = table.GetProperty("columns").EnumerateArray()
            .ToDictionary(c => c.GetProperty("code").GetString()!, c => c.GetProperty("dataType").GetString());
        Assert.All(Months, m => Assert.Equal("Formula", typeByColumn[m]));
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Кожне_посилання_формули_розв_язується_в_моделі_у_ту_саму_місячну_колонку_а_кількість_доданків_збігається_з_Excel()
    {
        using var doc = Model();
        var source = Sheet(doc.RootElement, "7. ");
        var sourceTable = source.GetProperty("tables")[0];
        var sourceSheetCode = source.GetProperty("code").GetString();
        var sourceTableCode = sourceTable.GetProperty("code").GetString();
        var sourceRows = sourceTable.GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("key").GetString()).ToHashSet();
        var sourceColumns = sourceTable.GetProperty("columns").EnumerateArray().Select(c => c.GetProperty("code").GetString()).ToHashSet();

        var target = Sheet(doc.RootElement, "7.0").GetProperty("tables")[0];
        var feeds = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var f in target.GetProperty("cellFormulas").EnumerateArray())
        {
            var row = f.GetProperty("row").GetString()!;
            var month = f.GetProperty("column").GetString()!;
            var expression = f.GetProperty("expression").GetString()!;
            var excel = f.GetProperty("excelFormula").GetString()!;

            var references = Reference().Matches(expression);
            Assert.NotEmpty(references);

            foreach (Match reference in references)
            {
                Assert.Equal(sourceSheetCode, reference.Groups["s"].Value);
                Assert.Equal(sourceTableCode, reference.Groups["t"].Value);
                Assert.Contains(reference.Groups["r"].Value, sourceRows);
                Assert.Contains(reference.Groups["c"].Value, sourceColumns);
                Assert.Equal(month, reference.Groups["c"].Value);

                if (!feeds.TryGetValue(reference.Groups["r"].Value, out var targets))
                {
                    feeds[reference.Groups["r"].Value] = targets = [];
                }

                targets.Add(row);
            }

            // (a+b+c)/1000 або a/1000: без діапазонів і функцій, ділення — у формулі шаблону, як в Excel.
            Assert.Matches(@"^\(?(\[[^\]]+\](\.\[[^\]]+\]){3}\+?)+\)?/1000$", expression);

            // Скільки джерельних клітинок в Excel — стільки посилань у виразі.
            Assert.Equal(Regex.Count(excel, "'7\\. Water Report'!"), references.Count);
        }

        // Багато-до-багатьох: джерельний рядок живить кілька цілей (7009016/17/18 → «Sulphur Pads (разом)» і №1/№2/№3).
        Assert.Contains(feeds, kv => kv.Value.Count >= 2);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Build_LandTemplate_записує_формули_комірок_через_PUT_formulas_cell_після_структури()
    {
        var script = File.ReadAllText(Path.Combine(SourceTree.Root, "tools", "land", "Build-LandTemplate.ps1"));

        Assert.Contains("/formulas/cell/", script, StringComparison.Ordinal);
        Assert.Contains("function Put-CellFormulas", script, StringComparison.Ordinal);

        // Формули комірок кладуться ПІСЛЯ структури (колонки, рядки, обидві таблиці-джерела мусять існувати).
        var structure = script.IndexOf("колонок $nCols, формул $nF", StringComparison.Ordinal);
        var cells = script.IndexOf("$nCell = Put-CellFormulas $vid", StringComparison.Ordinal);
        Assert.True(structure >= 0 && cells > structure, "Put-CellFormulas має викликатись після побудови структури.");
    }
}
