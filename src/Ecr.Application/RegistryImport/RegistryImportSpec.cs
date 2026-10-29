using System.Text.Json;
using System.Text.Json.Serialization;
using Ecr.Application.Errors;

namespace Ecr.Application.RegistryImport;

/// <summary>
/// Layout and mapping of an Excel file onto a two-level target (parent rows with child rows),
/// stored as <c>cfg.RegistryImportProfile.SpecJson</c> (FEATURE-REGISTRY-TABLES 4.6, NAE/permits plan N4).
/// </summary>
/// <remarks>
/// Generic on purpose: nothing here knows about permits or NAE. A parent is a row whose
/// <see cref="ParentKeyField"/> cell is filled (e.g. an emission source), its children are the rows
/// that carry a <see cref="ChildKeyField"/> (e.g. substances); the first child sits on the parent row.
/// </remarks>
public sealed class RegistryImportSpec
{
    /// <summary>Worksheet name; <c>null</c> - the first worksheet.</summary>
    public string? Sheet { get; init; }

    /// <summary>Header block position; auto-detected by default.</summary>
    public HeaderSpec Header { get; init; } = new();

    /// <summary>Explicit first data row; <c>null</c> - right after the header (and the column-number row).</summary>
    public int? DataStartRow { get; init; }

    /// <summary>Group-row rule; <c>null</c> - group rows are not recognised (they are skipped as non-data).</summary>
    public GroupRowSpec? GroupRows { get; init; }

    /// <summary>Name of the field whose filled cell starts a new parent.</summary>
    public string ParentKeyField { get; init; } = "";

    /// <summary>Name of the field whose filled cell starts a child row.</summary>
    public string ChildKeyField { get; init; } = "";

    /// <summary>Texts treated as "no value" for numbers and dates (compared folded); <c>null</c> - the defaults.</summary>
    public IReadOnlyList<string>? NaMarkers { get; init; }

    /// <summary>Field mapping.</summary>
    public IReadOnlyList<ImportFieldSpec> Fields { get; init; } = [];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
    };

    /// <summary>Default "no value" markers.</summary>
    public static readonly IReadOnlyList<string> DefaultNaMarkers = ["-", "–", "—", "n/a", "#n/a", "н/д", "н.д.", "нет"];

    /// <summary>Parses and validates a spec.</summary>
    /// <exception cref="BusinessRuleException"><c>ECR-IMP-0422</c> <c>regSpecInvalid</c>.</exception>
    public static RegistryImportSpec Parse(string json)
    {
        RegistryImportSpec? spec;

        try
        {
            spec = JsonSerializer.Deserialize<RegistryImportSpec>(json, Json);
        }
        catch (JsonException ex)
        {
            throw Invalid("json", ex.Message);
        }

        return (spec ?? throw Invalid("json", "empty")).Validated();
    }

    /// <summary>Serialises the spec the way <see cref="Parse"/> reads it.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>Checks the spec is self-consistent.</summary>
    /// <returns>The same spec.</returns>
    /// <exception cref="BusinessRuleException"><c>ECR-IMP-0422</c> <c>regSpecInvalid</c>.</exception>
    public RegistryImportSpec Validated()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (var field in Fields)
        {
            if (string.IsNullOrWhiteSpace(field.Name) || !names.Add(field.Name))
            {
                throw Invalid("fields", $"duplicate or empty field name '{field.Name}'");
            }

            if (field.Column is null && field.Patterns.Count == 0)
            {
                throw Invalid(field.Name, "a field needs header patterns or an explicit column");
            }

            if (field.Column is { } letter && ColumnLetters.ToIndex(letter) is null)
            {
                throw Invalid(field.Name, $"'{letter}' is not a column");
            }

            if (field.Parser == ImportParser.Choice && field.Choices.Count == 0)
            {
                throw Invalid(field.Name, "Choice parser needs choices");
            }

            if (field.Parser == ImportParser.Unit && field.Units.Count == 0)
            {
                throw Invalid(field.Name, "Unit parser needs units");
            }

            if (field.Factor is { } factor && factor == 0m)
            {
                throw Invalid(field.Name, "factor must not be zero");
            }
        }

        var parent = Fields.FirstOrDefault(f => string.Equals(f.Name, ParentKeyField, StringComparison.Ordinal));
        var child = Fields.FirstOrDefault(f => string.Equals(f.Name, ChildKeyField, StringComparison.Ordinal));

        if (parent is not { Level: ImportLevel.Parent })
        {
            throw Invalid(nameof(ParentKeyField), "must name a Parent-level field");
        }

        if (child is not { Level: ImportLevel.Child })
        {
            throw Invalid(nameof(ChildKeyField), "must name a Child-level field");
        }

        if (!parent.Required || !child.Required)
        {
            throw Invalid("keys", "key fields must be Required (their columns must be found)");
        }

        return this;
    }

    internal static BusinessRuleException Invalid(string where, string reason)
        => new(
            "ECR-IMP-0422",
            $"The import profile is invalid ({where}): {reason}.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = RegistryImportKeys.SpecInvalid,
                ["where"] = where,
                ["reason"] = reason,
            });
}

/// <summary>Where the header block is.</summary>
public sealed class HeaderSpec
{
    /// <summary>First header row; <c>null</c> - detected from the column-number row.</summary>
    public int? FromRow { get; init; }

    /// <summary>Last header row; <c>null</c> - detected.</summary>
    public int? ToRow { get; init; }
}

/// <summary>A group row is a data-area row with no keys whose only content is a label in one column.</summary>
public sealed class GroupRowSpec
{
    /// <summary>1-based column that holds the label.</summary>
    public int Column { get; init; } = 1;

    /// <summary>A label counts as a group when its cell is merged across at least this many columns, or when it is the only filled cell in the row.</summary>
    public int MinMergedColumns { get; init; } = 10;
}

/// <summary>One mapped field.</summary>
public sealed class ImportFieldSpec
{
    /// <summary>Unique field name (the target property).</summary>
    public string Name { get; init; } = "";

    /// <summary>Parent (source) or child (substance) level.</summary>
    public ImportLevel Level { get; init; } = ImportLevel.Parent;

    /// <summary>
    /// Header variants (any may match). Folded (case, Latin/Cyrillic look-alikes, spaces, hyphens, punctuation ignored);
    /// <c>a...b</c> or <c>a*b</c> means "a, then b later in the combined header".
    /// </summary>
    public IReadOnlyList<string> Patterns { get; init; } = [];

    /// <summary>The text of the LAST header row must equal one of these (e.g. the unit row <c>г/с</c>); empty - not checked.</summary>
    public IReadOnlyList<string> UnitQualifier { get; init; } = [];

    /// <summary>Explicit column letter that bypasses header matching.</summary>
    public string? Column { get; init; }

    /// <summary>The column must be found in the header.</summary>
    public bool Required { get; init; }

    /// <summary>Every row must carry a value.</summary>
    public bool NonEmpty { get; init; }

    /// <summary>How a cell is read.</summary>
    public ImportParser Parser { get; init; } = ImportParser.Text;

    /// <summary>How an empty cell is filled.</summary>
    public FillDown FillDown { get; init; } = FillDown.None;

    /// <summary>Reference kind to validate against the target registry (<c>nae</c>, <c>substance</c>); <c>null</c> - plain value.</summary>
    public string? Reference { get; init; }

    /// <summary>Code width for <see cref="ImportParser.Code"/> (numeric codes are left-padded with zeros).</summary>
    public int Pad { get; init; } = 4;

    /// <summary>Multiplier applied to <see cref="ImportParser.Decimal"/> values (unit conversion).</summary>
    public decimal? Factor { get; init; }

    /// <summary>A negative <see cref="ImportParser.Decimal"/> is an error.</summary>
    public bool NonNegative { get; init; }

    /// <summary>Allowed values for <see cref="ImportParser.Choice"/>: file text (folded) to canonical value.</summary>
    public IReadOnlyDictionary<string, string> Choices { get; init; } = new Dictionary<string, string>();

    /// <summary>Unit aliases for <see cref="ImportParser.Unit"/>: file text (folded) to canonical unit code.</summary>
    public IReadOnlyDictionary<string, string> Units { get; init; } = new Dictionary<string, string>();
}

/// <summary>Field level.</summary>
public enum ImportLevel
{
    /// <summary>Source-level attribute: read once per parent.</summary>
    Parent,

    /// <summary>Per-child attribute.</summary>
    Child,
}

/// <summary>Cell parsers.</summary>
public enum ImportParser
{
    /// <summary>Trimmed text, NBSP to space, runs of spaces collapsed.</summary>
    Text,

    /// <summary>Invariant decimal from a number or text; scientific notation kept, thousands separators tolerated.</summary>
    Decimal,

    /// <summary>Whole number.</summary>
    Integer,

    /// <summary>Business code: numeric (number or text) padded to <see cref="ImportFieldSpec.Pad"/>, <c>NN_suffix</c> pads the numeric part.</summary>
    Code,

    /// <summary>One of <see cref="ImportFieldSpec.Choices"/>.</summary>
    Choice,

    /// <summary>A date (Excel date or unambiguous text).</summary>
    Date,

    /// <summary>A unit label mapped through <see cref="ImportFieldSpec.Units"/>.</summary>
    Unit,
}

/// <summary>Empty-cell fill rule.</summary>
public enum FillDown
{
    /// <summary>Never.</summary>
    None,

    /// <summary>From the anchor of the merged range the cell belongs to (and only that).</summary>
    Merge,

    /// <summary>From the last filled cell above in the same column.</summary>
    Blank,
}

/// <summary>Excel column letters.</summary>
public static class ColumnLetters
{
    /// <summary>"AB" to 28; <c>null</c> when not a column.</summary>
    public static int? ToIndex(string? letters)
    {
        if (string.IsNullOrWhiteSpace(letters))
        {
            return null;
        }

        var n = 0;
        foreach (var ch in letters.Trim().ToUpperInvariant())
        {
            if (ch is < 'A' or > 'Z')
            {
                return null;
            }

            n = checked((n * 26) + (ch - 'A' + 1));
        }

        return n is > 0 and <= 16384 ? n : null;
    }

    /// <summary>28 to "AB".</summary>
    public static string ToLetters(int index)
    {
        var s = "";
        while (index > 0)
        {
            index--;
            s = (char)('A' + (index % 26)) + s;
            index /= 26;
        }

        return s;
    }
}
