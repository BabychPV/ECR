using System.Text.Json;
using System.Text.RegularExpressions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// L1 плану стилів комірок (.sync-local/reports/CELL-STYLE-PLAN-2026-10-12.md): модель шаблону Land несе ролі стилів
/// Excel-шаблону (кольори заливки з xlsm), ширини й формати чисел колонок, а умовне форматування «баланс ≠ 0 / = 0»
/// лежить окремо, з прив'язкою до рядків. Тест читає лише файли репозиторію: кольори закріплені значеннями з
/// <c>styles.xml</c> xlsm (indexed 42 = CCFFCC, 47 = FFCC99, 13 = FFFF00, rgb FFFFFF99 тощо), а не відтворюються з коду.
/// Без перегенерації моделі (<c>tools/land/model-gen/run-model-gen.ps1</c>) тест червоний: ключа <c>styles</c> немає.
/// </summary>
public sealed partial class LandCellStyleRolesModelTests
{
    private static JsonDocument Model()
        => JsonDocument.Parse(File.ReadAllText(Path.Combine(
            SourceTree.Root, "docs", "delivery", "reference-data", "land-template", "land-template.model.json")));

    private static JsonElement Table(JsonElement root, string sheetCode, string tableCode)
        => root.GetProperty("sheets").EnumerateArray()
            .Single(s => s.GetProperty("code").GetString() == sheetCode)
            .GetProperty("tables").EnumerateArray()
            .Single(t => t.GetProperty("code").GetString() == tableCode);

    private static JsonElement Column(JsonElement table, string code)
        => table.GetProperty("columns").EnumerateArray().Single(c => c.GetProperty("code").GetString() == code);

    private static string? Str(JsonElement element, string name)
        => element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static readonly string[] NumericTypes = ["Decimal", "Int", "Formula", "Calculated"];

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]{0,63}\\z")]
    private static partial Regex EcrCode();

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Ролі_стилів_мають_кольори_Excel_шаблону_і_відповідний_ARGB()
    {
        using var doc = Model();
        var styles = doc.RootElement.GetProperty("styles").EnumerateArray()
            .ToDictionary(s => s.GetProperty("code").GetString()!);

        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["LAND_HEADER"] = "#CCFFCC",        // шапка статичних колонок (light green, indexed 42)
            ["LAND_HEADER_MONTH"] = "#FFCC99",  // шапка місячних колонок (peach, indexed 47)
            ["LAND_INPUT"] = "#FFFFFF",         // вхідні комірки
            ["LAND_AUTO"] = "#CCFFCC",          // авто-поля (No, Назва)
            ["LAND_SERVICE"] = "#FFFF00",       // службові поля (yellow, indexed 13)
            ["LAND_TAG"] = "#FFFF99",           // Tags/Attributes/Measurements
        };

        foreach (var (code, hex) in expected)
        {
            Assert.True(styles.TryGetValue(code, out var style), $"Немає ролі {code}.");
            Assert.Equal(hex, style.GetProperty("backgroundHex").GetString());

            // ARGB — знакове int з альфою FF: те, що читає XLColor.FromArgb і argbOfHex клієнта.
            var rgb = Convert.ToInt32(hex[1..], 16);
            Assert.Equal(unchecked((int)(0xFF000000u | (uint)rgb)), style.GetProperty("backgroundArgb").GetInt32());
        }

        // Шапка в Excel — по центру, з переносом (з xlsm, а не припущення).
        Assert.True(styles["LAND_HEADER"].GetProperty("wrapText").GetBoolean());
        Assert.Equal(1, styles["LAND_HEADER"].GetProperty("horizontalAlign").GetInt32());
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Кожна_роль_стилю_колонки_і_шапки_таблиці_розв_язується_а_код_валідний_для_EcrCode()
    {
        using var doc = Model();
        var codes = doc.RootElement.GetProperty("styles").EnumerateArray()
            .Select(s => s.GetProperty("code").GetString()!).ToList();
        Assert.Equal(codes.Count, codes.Distinct(StringComparer.Ordinal).Count());
        Assert.All(codes, c => Assert.Matches(EcrCode(), c));

        var known = codes.ToHashSet(StringComparer.Ordinal);
        var usedByColumns = 0;
        foreach (var sheet in doc.RootElement.GetProperty("sheets").EnumerateArray())
        {
            foreach (var table in sheet.GetProperty("tables").EnumerateArray())
            {
                var header = Str(table, "headerStyleRole");
                Assert.True(header is null || known.Contains(header), $"{table.GetProperty("code")}: шапка {header}");

                foreach (var column in table.GetProperty("columns").EnumerateArray())
                {
                    foreach (var key in new[] { "styleRole", "headerStyleRole" })
                    {
                        var role = Str(column, key);
                        Assert.True(role is null || known.Contains(role), $"{column.GetProperty("code")}: {key} {role}");
                        usedByColumns += key == "styleRole" && role is not null ? 1 : 0;
                    }
                }
            }
        }

        Assert.True(usedByColumns > 1000, $"Ролей на колонках лише {usedByColumns}: перегенеруйте модель.");
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Колонки_Land_3_00_мають_ролі_шапок_ширини_і_формат_числа_з_Excel()
    {
        using var doc = Model();
        var table = Table(doc.RootElement, "S03_STATIONARY_EQUIPMENT", "T3_00");

        // Службова колонка A (Excel row id): жовта; «No» — авто-поле; статус/виробник — вхідні (білі).
        Assert.Equal("LAND_SERVICE", Str(Column(table, "ExcelRowId"), "styleRole"));
        Assert.Equal("LAND_AUTO", Str(Column(table, "Land_No"), "styleRole"));
        Assert.Equal("LAND_INPUT", Str(Column(table, "Land_Status"), "styleRole"));
        Assert.Equal("LAND_INPUT", Str(Column(table, "Land_Subcontractor"), "styleRole"));

        // Місячна колонка: персикова шапка, формат 0.000000, ширина з <col width> (~14.5 симв. * 7 px).
        var month = Column(table, "Land_RunningHoursMonthly_M01");
        Assert.Equal("LAND_HEADER_MONTH", Str(month, "headerStyleRole"));
        Assert.Equal("0.000000", Str(month, "displayFormat"));
        var width = month.GetProperty("widthPx").GetInt32();
        Assert.InRange(width, 90, 110);

        // Вузька «No» (2,7 символа ≈ 24 px) підтягнута до мінімуму ColumnDef (40).
        Assert.Equal(40, Column(table, "Land_No").GetProperty("widthPx").GetInt32());

        // Шапка таблиці — зелена роль (CCFFCC), а не місячна: місячні шапки не задають шапку таблиці.
        Assert.StartsWith("LAND_HEADER", Str(table, "headerStyleRole"), StringComparison.Ordinal);
        Assert.NotEqual("LAND_HEADER_MONTH", Str(table, "headerStyleRole"));
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Ширини_у_межах_ColumnDef_а_формат_лише_для_числових_колонок_і_не_довший_за_50()
    {
        using var doc = Model();
        var withWidth = 0;
        var withFormat = 0;
        foreach (var sheet in doc.RootElement.GetProperty("sheets").EnumerateArray())
        {
            foreach (var table in sheet.GetProperty("tables").EnumerateArray())
            {
                foreach (var column in table.GetProperty("columns").EnumerateArray())
                {
                    if (column.TryGetProperty("widthPx", out var w))
                    {
                        withWidth++;
                        Assert.InRange(w.GetInt32(), 40, 800);          // CK_ColumnDef_WidthPx
                        Assert.False(column.GetProperty("hidden").GetBoolean(), "Ширина прихованої колонки не потрібна.");
                    }

                    var format = Str(column, "displayFormat");
                    if (format is not null)
                    {
                        withFormat++;
                        Assert.True(format.Length <= 50, $"{column.GetProperty("code")}: формат довший за 50.");
                        Assert.Contains(column.GetProperty("dataType").GetString(), NumericTypes);
                    }
                }
            }
        }

        Assert.True(withWidth > 3000, $"Ширин лише {withWidth}.");
        Assert.True(withFormat > 500, $"Форматів чисел лише {withFormat}.");
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Баланс_не_нуль_червоний_нуль_зелений_лежить_row_scoped_а_колонкових_правил_немає()
    {
        using var doc = Model();
        var root = doc.RootElement;

        // Правило колонки ECR діє на ВСІ рядки колонки з цим кодом по всій версії; у Excel ці правила стоять на
        // окремих рядках балансу (C20:AN20 …). Колонкове правило пофарбувало б усі вхідні комірки - тому порожньо.
        Assert.Empty(root.GetProperty("conditionalFormats").EnumerateArray());

        var scoped = root.GetProperty("conditionalFormatsRowScoped").EnumerateArray().ToList();
        var waste = scoped.Where(r => r.GetProperty("sheet").GetString() == "S08_WASTE_REPORT").ToList();
        Assert.Contains(waste, r => r.GetProperty("operator").GetString() == "ne"
            && r.GetProperty("value").GetString() == "0" && r.GetProperty("backgroundHex").GetString() == "#FF0000");
        Assert.Contains(waste, r => r.GetProperty("operator").GetString() == "eq"
            && r.GetProperty("value").GetString() == "0" && r.GetProperty("backgroundHex").GetString() == "#00FF00");

        var table = Table(root, "S08_WASTE_REPORT", "T8_00");
        var rows = table.GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("key").GetString()!).ToHashSet();
        var columns = table.GetProperty("columns").EnumerateArray().Select(c => c.GetProperty("code").GetString()!).ToHashSet();
        foreach (var rule in waste)
        {
            var keys = rule.GetProperty("rowKeys").EnumerateArray().Select(k => k.GetString()!).ToList();
            Assert.NotEmpty(keys);
            Assert.All(keys, k => Assert.Contains(k, rows));
            Assert.All(rule.GetProperty("columns").EnumerateArray(), c => Assert.Contains(c.GetString()!, columns));
        }

        // Водний баланс 7. Water Report: ті самі кольори на рядках, а не на колонках місяців.
        var water = scoped.Where(r => r.GetProperty("sheet").GetString() == "S07_WATER_REPORT").ToList();
        Assert.Contains(water, r => r.GetProperty("operator").GetString() == "ne"
            && r.GetProperty("backgroundHex").GetString() == "#FF0000");
        Assert.Contains(water, r => r.GetProperty("operator").GetString() == "eq"
            && r.GetProperty("backgroundHex").GetString() == "#00FF00");
    }
}
