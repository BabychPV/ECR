using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// Закомічені дані вкладки «2. Contract» шаблону Land
/// (<c>docs/delivery/reference-data/land-contract</c>) цілісні: кількості, коди, підписи,
/// кодування, маніфест шапки (RC15, L4 — сторож даних).
/// </summary>
/// <remarks>
/// ⛔ Це вхід для <c>tools/land/Import-ContractDictionaries.ps1</c> і <c>Apply-ContractHeader.ps1</c>:
/// зіпсований CSV (дубль коду, зсув кількості, не-UTF-8) мовчки дав би неповний довідник на
/// стенді замовника. Тому перевірка живе тут, у CI, а не лише в скрипті. Валідатор окремий від
/// файлової системи — негативні тести псують КОПІЮ в пам'яті й доводять, що сторож червоніє.
/// </remarks>
public sealed class ContractDictionaryDataTests
{
    private static readonly string DataDir = Path.Combine(
        SourceTree.Root, "docs", "delivery", "reference-data", "land-contract");

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [Trait("Requirement", "RC15-L4")]
    public void Закомічені_CSV_і_маніфест_проходять_усі_перевірки()
    {
        var problems = ContractDictionaryChecker.Validate(LoadFiles());

        Assert.True(problems.Count == 0, "Дані Contract зіпсовані:\n  " + string.Join("\n  ", problems));
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [Trait("Requirement", "RC15-L4")]
    public void Кількості_значень_збігаються_з_Excel()
    {
        // Незалежно від Validate: числа 25/60/2/71/2/10/9 — з аркуша DropdownList (D5:J75).
        var expected = new Dictionary<string, int>
        {
            ["area.csv"] = 25, ["contractor.csv"] = 60, ["region.csv"] = 2, ["location.csv"] = 71,
            ["onoffshore.csv"] = 2, ["activity.csv"] = 10, ["permit.csv"] = 9,
        };
        var files = LoadFiles();

        foreach (var (name, count) in expected)
        {
            var rows = ContractDictionaryChecker.ParseCsv(Encoding.UTF8.GetString(files[name]));
            Assert.Equal(count, rows.Count - 1);
        }
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [Trait("Requirement", "RC15-L4")]
    public void Дубль_коду_червоний()
    {
        var files = LoadFiles();
        var rows = ContractDictionaryChecker.ParseCsv(Encoding.UTF8.GetString(files["area.csv"]));
        rows[3][0] = rows[2][0];
        files["area.csv"] = Encoding.UTF8.GetBytes(ContractDictionaryChecker.ToCsv(rows));

        var problems = ContractDictionaryChecker.Validate(files);

        Assert.Contains(problems, p => p.Contains("area.csv", StringComparison.Ordinal) && p.Contains("дубль коду", StringComparison.Ordinal));
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [Trait("Requirement", "RC15-L4")]
    public void Втрачений_рядок_і_дубль_назви_червоні()
    {
        var files = LoadFiles();
        var rows = ContractDictionaryChecker.ParseCsv(Encoding.UTF8.GetString(files["contractor.csv"]));
        rows.RemoveAt(rows.Count - 1);
        rows[2][1] = rows[1][1];
        rows[2][2] = rows[1][2];
        files["contractor.csv"] = Encoding.UTF8.GetBytes(ContractDictionaryChecker.ToCsv(rows));

        var problems = ContractDictionaryChecker.Validate(files);

        Assert.Contains(problems, p => p.Contains("кількість", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("дубль NAME", StringComparison.Ordinal));
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [Trait("Requirement", "RC15-L4")]
    public void Недопустимий_код_розсинхрон_NAME_і_порожній_підпис_червоні()
    {
        var files = LoadFiles();
        var rows = ContractDictionaryChecker.ParseCsv(Encoding.UTF8.GetString(files["region.csv"]));
        rows[1][0] = "1_BAD CODE";
        rows[2][1] = "Інша назва";
        rows[2][3] = string.Empty;
        files["region.csv"] = Encoding.UTF8.GetBytes(ContractDictionaryChecker.ToCsv(rows));

        var problems = ContractDictionaryChecker.Validate(files);

        Assert.Contains(problems, p => p.Contains("недопустимий код", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("NAME не збігається", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("display_ru", StringComparison.Ordinal));
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [Trait("Requirement", "RC15-L4")]
    public void Файл_не_в_UTF8_червоний()
    {
        var files = LoadFiles();
        files["permit.csv"] = Encoding.GetEncoding(
            "iso-8859-1").GetBytes("code,NAME,display_en,display_ru,ordinal\r\nKZ1,\xE9\xE9,x,,1\r\n");

        var problems = ContractDictionaryChecker.Validate(files);

        Assert.Contains(problems, p => p.Contains("permit.csv", StringComparison.Ordinal) && p.Contains("UTF-8", StringComparison.Ordinal));
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [Trait("Requirement", "RC15-L4")]
    public void Маніфест_порядок_Lookup_і_обовязковість_Permit_охороняються()
    {
        var files = LoadFiles();
        var manifest = Encoding.UTF8.GetString(files["header-fields.json"]);

        // Порядок: міняємо місцями Area і Contractor.
        var swapped = manifest
            .Replace("\"key\": \"Area\"", "\"key\": \"TMP\"", StringComparison.Ordinal)
            .Replace("\"key\": \"Contractor\"", "\"key\": \"Area\"", StringComparison.Ordinal)
            .Replace("\"key\": \"TMP\"", "\"key\": \"Contractor\"", StringComparison.Ordinal);
        files["header-fields.json"] = Encoding.UTF8.GetBytes(swapped);
        Assert.Contains(ContractDictionaryChecker.Validate(files), p => p.Contains("порядок", StringComparison.Ordinal));

        // Permit перестає бути обов'язковим.
        files["header-fields.json"] = Encoding.UTF8.GetBytes(
            manifest.Replace(", \"isRequired\": true", string.Empty, StringComparison.Ordinal));
        Assert.Contains(ContractDictionaryChecker.Validate(files), p => p.Contains("Permit", StringComparison.Ordinal) && p.Contains("isRequired", StringComparison.Ordinal));

        // Lookup стає String: Lookup-ів уже не сім.
        files["header-fields.json"] = Encoding.UTF8.GetBytes(
            manifest.Replace("\"kind\": \"Lookup\", \"registry\": \"LAND_REGION\", \"csv\": \"region.csv\"", "\"kind\": \"String\"", StringComparison.Ordinal));
        Assert.Contains(ContractDictionaryChecker.Validate(files), p => p.Contains("Lookup", StringComparison.Ordinal));
    }

    private static Dictionary<string, byte[]> LoadFiles()
        => ContractDictionaryChecker.RequiredFiles.ToDictionary(
            f => f, f => File.ReadAllBytes(Path.Combine(DataDir, f)), StringComparer.Ordinal);
}

/// <summary>Чистий валідатор набору файлів Contract (без файлової системи).</summary>
internal static partial class ContractDictionaryChecker
{
    internal static readonly string[] RequiredFiles =
    [
        "area.csv", "contractor.csv", "region.csv", "location.csv", "onoffshore.csv", "activity.csv", "permit.csv",
        "header-fields.json",
    ];

    private static readonly string[] Header = ["code", "NAME", "display_en", "display_ru", "ordinal"];

    private static readonly Dictionary<string, int> Counts = new(StringComparer.Ordinal)
    {
        ["area.csv"] = 25, ["contractor.csv"] = 60, ["region.csv"] = 2, ["location.csv"] = 71,
        ["onoffshore.csv"] = 2, ["activity.csv"] = 10, ["permit.csv"] = 9,
    };

    // Порядок Excel (аркуш «2. Contract», B6..B18).
    private static readonly string[] ManifestOrder =
    [
        "Area", "Contractor", "Region", "Location", "OnOffshore", "FilledBy", "ContractHolder", "ContractNumber",
        "TypeOfActivity", "ProcessedOn", "FileNumber", "Permit", "Version",
    ];

    // Файли, де кожен підпис мусить мати RU-частину «EN - RU»: у них Excel двомовний.
    private static readonly HashSet<string> RuRequired = new(StringComparer.Ordinal)
    {
        "area.csv", "region.csv", "onoffshore.csv", "activity.csv",
    };

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]{0,63}$")]
    private static partial Regex CodePattern();

    internal static List<string> Validate(IReadOnlyDictionary<string, byte[]> files)
    {
        var problems = new List<string>();
        foreach (var (name, expected) in Counts)
        {
            ValidateCsv(name, expected, files, problems);
        }

        ValidateManifest(files, problems);
        return problems;
    }

    private static void ValidateCsv(
        string name, int expected, IReadOnlyDictionary<string, byte[]> files, List<string> problems)
    {
        if (!files.TryGetValue(name, out var bytes))
        {
            problems.Add($"{name}: файлу немає.");
            return;
        }

        string text;
        try
        {
            text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            problems.Add($"{name}: файл не в кодуванні UTF-8.");
            return;
        }

        if (text.Contains((char)0xFFFD, StringComparison.Ordinal))
        {
            problems.Add($"{name}: символ заміни U+FFFD — кодування вже зіпсовано.");
        }

        var rows = ParseCsv(text);
        if (rows.Count == 0 || !rows[0].SequenceEqual(Header))
        {
            problems.Add($"{name}: заголовок має бути {string.Join(',', Header)}.");
            return;
        }

        var data = rows.Skip(1).ToList();
        if (data.Count != expected)
        {
            problems.Add($"{name}: кількість {data.Count}, у Excel {expected}.");
        }

        var codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < data.Count; i++)
        {
            var row = data[i];
            var at = $"{name} рядок {i + 2}";
            if (row.Count != Header.Length)
            {
                problems.Add($"{at}: {row.Count} колонок замість {Header.Length}.");
                continue;
            }

            var code = row[0];
            var full = row[1];
            var en = row[2];
            var ru = row[3];

            if (!CodePattern().IsMatch(code))
            {
                problems.Add($"{at}: недопустимий код «{code}».");
            }
            else if (!codes.Add(code))
            {
                problems.Add($"{at}: дубль коду «{code}».");
            }

            if (string.IsNullOrWhiteSpace(full) || full.Length > 1000 || full != full.Trim())
            {
                problems.Add($"{at}: NAME порожній, довший за 1000 або з пробілами по краях.");
            }
            else if (!names.Add(full.Trim()))
            {
                problems.Add($"{at}: дубль NAME «{full}».");
            }

            if (string.IsNullOrWhiteSpace(en))
            {
                problems.Add($"{at}: порожній display_en.");
            }

            if (RuRequired.Contains(name) && string.IsNullOrWhiteSpace(ru))
            {
                problems.Add($"{at}: порожній display_ru (двомовний довідник).");
            }

            // NAME — повний рядок Excel: «EN - RU», а коли RU немає — рівно EN.
            var expectedName = string.IsNullOrEmpty(ru) ? en : $"{en} - {ru}";
            if (!string.Equals(full, expectedName, StringComparison.Ordinal))
            {
                problems.Add($"{at}: NAME не збігається з display_en/display_ru.");
            }

            if (!int.TryParse(row[4], NumberStyles.None, CultureInfo.InvariantCulture, out var ordinal) || ordinal != i + 1)
            {
                problems.Add($"{at}: ordinal має бути {i + 1} (порядок рядків Excel).");
            }

            if (name == "permit.csv" && !string.Equals(code, full.Replace('-', '_'), StringComparison.Ordinal))
            {
                problems.Add($"{at}: код Permit має бути номером з заміною «-» на «_».");
            }
        }
    }

    private static void ValidateManifest(IReadOnlyDictionary<string, byte[]> files, List<string> problems)
    {
        if (!files.TryGetValue("header-fields.json", out var bytes))
        {
            problems.Add("header-fields.json: файлу немає.");
            return;
        }

        JsonElement fields;
        try
        {
            using var doc = JsonDocument.Parse(bytes);
            fields = doc.RootElement.GetProperty("fields").Clone();
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            problems.Add("header-fields.json: не вдалося прочитати fields.");
            return;
        }

        var items = fields.EnumerateArray().ToList();
        if (items.Count != 13)
        {
            problems.Add($"header-fields.json: {items.Count} позицій замість 13.");
        }

        var keys = items.Select(i => i.GetProperty("key").GetString() ?? string.Empty).ToList();
        if (!keys.SequenceEqual(ManifestOrder))
        {
            problems.Add("header-fields.json: порядок полів не збігається з Excel: " + string.Join(", ", ManifestOrder));
        }

        var positions = items.Select(i => i.GetProperty("position").GetInt32()).ToList();
        if (!positions.SequenceEqual(Enumerable.Range(1, items.Count)))
        {
            problems.Add("header-fields.json: position має бути 1..13 без пропусків.");
        }

        var lookups = items.Where(i => i.GetProperty("kind").GetString() == "Lookup").ToList();
        if (lookups.Count != 7)
        {
            problems.Add($"header-fields.json: Lookup-полів {lookups.Count}, має бути 7.");
        }

        var services = items.Where(i => i.GetProperty("kind").GetString() == "Service")
            .Select(i => i.GetProperty("key").GetString()).ToList();
        if (!services.SequenceEqual(["FileNumber", "Version"]))
        {
            problems.Add("header-fields.json: службові (kind=Service) мають бути лише FileNumber і Version.");
        }

        var registries = new HashSet<string>(StringComparer.Ordinal);
        foreach (var l in lookups)
        {
            var key = l.GetProperty("key").GetString();
            var registry = l.TryGetProperty("registry", out var r) ? r.GetString() ?? string.Empty : string.Empty;
            var csv = l.TryGetProperty("csv", out var c) ? c.GetString() ?? string.Empty : string.Empty;
            if (!registry.StartsWith("LAND_", StringComparison.Ordinal) || !CodePattern().IsMatch(registry) || !registries.Add(registry))
            {
                problems.Add($"header-fields.json: {key}: registry «{registry}» має бути унікальним кодом LAND_*.");
            }

            if (!Counts.ContainsKey(csv))
            {
                problems.Add($"header-fields.json: {key}: csv «{csv}» не серед відомих файлів.");
            }
        }

        var permit = items.FirstOrDefault(i => i.GetProperty("key").GetString() == "Permit");
        if (permit.ValueKind == JsonValueKind.Object
            && !(permit.TryGetProperty("isRequired", out var req) && req.ValueKind == JsonValueKind.True))
        {
            problems.Add("header-fields.json: Permit має бути isRequired=true.");
        }
    }

    /// <summary>Мінімальний RFC 4180: лапки, подвоєні лапки, коми й переноси в лапках.</summary>
    internal static List<List<string>> ParseCsv(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (quoted)
            {
                if (ch == '"' && i + 1 < text.Length && text[i + 1] == '"')
                {
                    cell.Append('"');
                    i++;
                }
                else if (ch == '"')
                {
                    quoted = false;
                }
                else
                {
                    cell.Append(ch);
                }

                continue;
            }

            switch (ch)
            {
                case '"':
                    quoted = true;
                    break;
                case ',':
                    row.Add(cell.ToString());
                    cell.Clear();
                    break;
                case '\r':
                    break;
                case '\n':
                    row.Add(cell.ToString());
                    cell.Clear();
                    rows.Add(row);
                    row = [];
                    break;
                default:
                    cell.Append(ch);
                    break;
            }
        }

        if (cell.Length > 0 || row.Count > 0)
        {
            row.Add(cell.ToString());
            rows.Add(row);
        }

        return rows;
    }

    internal static string ToCsv(IEnumerable<List<string>> rows)
    {
        static string Q(string v) => v.IndexOfAny([',', '"', '\n', '\r']) >= 0
            ? "\"" + v.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
            : v;

        var sb = new StringBuilder();
        foreach (var r in rows)
        {
            sb.Append(string.Join(',', r.Select(Q))).Append("\r\n");
        }

        return sb.ToString();
    }
}
