using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ecr.Application.Calculations.Scripting.Migration;

/// <summary>
/// Відбиток джерела (план, розд. 8.1, 8.5): SHA-256 канонічного, не залежного від порядку рядків JSON СТАРОГО вмісту версії.
/// </summary>
/// <remarks>
/// Канон (<see cref="Canonical"/>): UTF-8 без BOM, один рядок JSON; ключі об'єктів — у фіксованому порядку; масиви
/// впорядковано за канонічним текстом елемента (порядок рядків у БД не важить); числа — інваріантно, без хвостових нулів;
/// дати — <c>yyyy-MM-dd</c>; Id БД не входять — лише КОДИ (одиниць, довідників, записів, речовин, колонок).
/// Склад: вид методології, режими (числовий, календарний), <c>RowActivityFilter</c>, правило категорії, категорії, речовини,
/// формули (код, вираз, область, видимість, тип результату, код одиниці, порядок), виходи, константи (усі вікна чинності),
/// активні правила відбору рядків (ключі — коди колонок), імпорти (код бібліотеки + її члени).
/// Не входять: дати життя версії, прив'язки таблиць, тестові випадки.
/// </remarks>
public static class Fingerprint
{
    /// <summary>Версія формату канону: змінюється лише разом зі зміною складу чи форми.</summary>
    public const int FormatVersion = 1;

    private static readonly JsonSerializerOptions Options = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>SHA-256 канонічного подання.</summary>
    /// <param name="source">Старий вміст версії.</param>
    public static byte[] Compute(VersionSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return SHA256.HashData(new UTF8Encoding(false).GetBytes(Canonical(source)));
    }

    /// <summary>Відбиток шістнадцятковим рядком (малі літери).</summary>
    /// <param name="source">Старий вміст версії.</param>
    public static string ComputeHex(VersionSource source) => Convert.ToHexStringLower(Compute(source));

    /// <summary>Канонічний JSON.</summary>
    /// <param name="source">Старий вміст версії.</param>
    public static string Canonical(VersionSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var root = new JsonObject
        {
            ["format"] = FormatVersion,
            ["kind"] = source.IsLibrary ? "Library" : source.IsModuleLevel ? "Module" : "Methodology",
            ["numericMode"] = source.NumericMode.ToString(),
            ["calendarMode"] = source.CalendarMode.ToString(),
            ["rowActivityFilter"] = source.RowActivityFilter,
            ["categoryRule"] = string.IsNullOrWhiteSpace(source.CategoryRule) ? null : source.CategoryRule,
            ["categories"] = Sorted(source.Categories.Select(c => (JsonNode?)JsonValue.Create(c))),
            ["substances"] = Sorted(source.Substances.Select(c => (JsonNode?)JsonValue.Create(c))),
            ["formulas"] = Sorted(source.Formulas.Select(f => (JsonNode?)new JsonObject
            {
                ["code"] = f.Code,
                ["expression"] = f.Expression,
                ["scope"] = f.Scope.ToString(),
                ["visible"] = f.IsVisible,
                ["resultType"] = f.ResultType.ToString(),
                ["unit"] = f.UnitCode,
                ["order"] = f.EvaluationOrder,
            })),
            ["outputs"] = Sorted(source.Outputs.Select(o => (JsonNode?)new JsonObject
            {
                ["code"] = o.Code,
                ["unit"] = o.UnitCode,
                ["perSubstance"] = o.IsPerSubstance,
                ["ordinal"] = o.Ordinal,
            })),
            ["constants"] = Sorted(source.Constants.Select(c => (JsonNode?)new JsonObject
            {
                ["code"] = c.Code,
                ["kind"] = c.Kind.ToString(),
                ["number"] = c.Number is { } n ? Number(n) : null,
                ["text"] = c.Text,
                ["unit"] = c.UnitCode,
                ["category"] = c.Category,
                ["substance"] = c.SubstanceCode,
                ["validFrom"] = Date(c.ValidFrom),
                ["validTo"] = Date(c.ValidTo),
            })),
            ["rules"] = Sorted(source.Rules.Select(r => (JsonNode?)new JsonObject
            {
                ["pairs"] = Sorted(r.Pairs.Select(p => (JsonNode?)new JsonObject
                {
                    ["column"] = p.ColumnCode,
                    ["text"] = p.Value.Text,
                    ["registry"] = p.Value.RegistryCode,
                    ["entry"] = p.Value.EntryCode,
                })),
            })),
            ["imports"] = Sorted(source.Imports.Select(i => (JsonNode?)new JsonObject
            {
                ["library"] = i.LibraryCode,
                ["members"] = Sorted(i.Members.Select(m => (JsonNode?)JsonValue.Create(m))),
            })),
        };

        return root.ToJsonString(Options);
    }

    private static JsonArray Sorted(IEnumerable<JsonNode?> items)
    {
        var texts = items.Select(i => i?.ToJsonString(Options) ?? "null").OrderBy(t => t, StringComparer.Ordinal).ToList();
        var array = new JsonArray();
        foreach (var text in texts)
        {
            array.Add(JsonNode.Parse(text));
        }

        return array;
    }

    private static string Number(decimal value) => value.ToString("0.############################", CultureInfo.InvariantCulture);

    private static string? Date(DateOnly? value) => value?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
