// tests/Ecr.Application.Tests/Registries/FlertCompositionMappingTests.cs
using Ecr.Application.Ports;
using Ecr.Application.Registries.Flert;
using Xunit;

namespace Ecr.Application.Tests.Registries;

/// <summary>
/// Зіставлення знімка FLERT <c>usp_GetAllStreamDataFlattened</c> із дзеркалом STREAM → STREAM_CASE → GAS_COMPOSITION
/// (<c>D-476</c>, план FLERT F1): NULL/0 компонента, Σ = 0, пари дублів, версії й вікна, дрейф схеми, дублі номерів.
/// </summary>
/// <remarks>
/// Мутаційні докази (локально, не в коміті): (1) у <c>Components</c> взяти довгу назву першою
/// (<c>longValue ?? shortValue</c>) → <c>Пара_дублів_коротка_назва_виграє_конфлікт_попередження</c> червоний;
/// (2) прибрати <c>Filled</c> (нуль пише рядок) → <c>NULL_і_нуль_компонента_рядка_не_дають</c> червоний;
/// (3) у <c>FindByNumber</c> повертати першого кандидата → <c>Дубль_номера_потоку_розводиться_сушею_і_кейсом_інакше_неоднозначність</c> червоний.
/// </remarks>
public sealed class FlertCompositionMappingTests
{
    private static readonly string[] ExpectedRows = ["C10_20220101_H2S", "C10_20220101_Methane"];

    private static readonly string[] OnlyMethane = ["Methane"];

    private static readonly string[] ExpectedVersions = ["C10_20220101", "C10_20260615", "C20_20250301"];

    private static readonly string[] ExpectedAmbiguous = ["C10_20220101", "C30_20220101"];

    private static readonly string[] StreamValidity = ["Stream_ValidFrom", "Stream_ValidTo"];

    private static readonly string[] TypeValidity = ["StreamType_ValidFrom", "StreamType_ValidTo"];

    private static readonly DateTime Since2022 = new(2022, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);

    [Fact]
    [Trait("Requirement", "D-476")]
    public void Рядок_дає_потік_кейс_і_склад_з_кодами_і_властивостями()
    {
        var mirror = FlertCompositionMapping.Map(Table(Row(1, 10, "370 Winter", number: "1D-2", c: new()
        {
            ["Methane"] = 90m,
            ["H2S"] = 10m,
        }, temperature: 0m, density: 0m, mw: 23.05m)));

        Assert.False(mirror.HasErrors);
        var stream = Assert.Single(mirror.Streams);
        Assert.Equal("S1", stream.Code);
        Assert.Equal(1, stream.LegacyId);
        Assert.Equal("1D-2", stream.Number);
        Assert.True(stream.IsOnshore);

        var @case = Assert.Single(mirror.Cases);
        Assert.Equal("C10_20220101", @case.Code);
        Assert.Equal("S1", @case.StreamCode);
        Assert.Equal("370 Winter", @case.CaseName);
        Assert.Equal(new DateOnly(2022, 1, 1), @case.ValidFrom);
        Assert.Null(@case.ValidTo);
        Assert.Equal(0m, @case.TemperatureC);   // 0 °C — значення, а не «немає»
        Assert.Null(@case.DensityOp);           // 0 густини HYSYS — «немає», ніколи 0
        Assert.Equal(23.05m, @case.MwHysys);
        Assert.False(@case.NoComposition);
        Assert.Equal(100m, @case.MolPctSum);

        Assert.Equal(ExpectedRows, mirror.Rows.Select(r => r.Code));
        Assert.All(mirror.Rows, r => Assert.Equal("C10_20220101", r.CaseCode));
    }

    [Fact]
    [Trait("Requirement", "D-476")]
    public void NULL_і_нуль_компонента_рядка_не_дають()
    {
        var mirror = FlertCompositionMapping.Map(Table(Row(1, 10, "370 Winter", c: new()
        {
            ["Methane"] = 100m,
            ["Ethane"] = 0m,
            ["Propane"] = null,
        })));

        Assert.Equal(OnlyMethane, mirror.Rows.Select(r => r.Component));
    }

    [Fact]
    [Trait("Requirement", "D-476")]
    public void Сума_нуль_дає_кейс_без_складу_з_позначкою_і_зауваженням_а_не_нулі()
    {
        var mirror = FlertCompositionMapping.Map(Table(Row(1, 11, "No composition", c: new())));

        Assert.False(mirror.HasErrors);
        var @case = Assert.Single(mirror.Cases);
        Assert.True(@case.NoComposition);
        Assert.Equal(0m, @case.MolPctSum);
        Assert.Empty(mirror.Rows);
        var issue = Assert.Single(mirror.Issues);
        Assert.Equal(FlertCompositionMapping.NoComposition, issue.Kind);
        Assert.Equal(FlertIssueSeverity.Warning, issue.Severity);
        Assert.Equal("C11_20220101", issue.Subject);
    }

    [Fact]
    [Trait("Requirement", "D-476")]
    public void Пара_дублів_коротка_назва_виграє_конфлікт_попередження()
    {
        var mirror = FlertCompositionMapping.Map(Table(Row(1, 10, "370 Winter", c: new()
        {
            ["Methane"] = 97m,
            ["CO"] = 1m,
            ["Carbon_Monoxide"] = 2m,      // конфлікт → CO = 1, попередження
            ["Hydrogen"] = 1.5m,           // заповнена лише довга → H2 = 1.5, без попередження
            ["O2"] = 0.5m,
            ["Oxygen"] = 0.5m,             // однакові → без попередження
        })));

        var byComponent = mirror.Rows.ToDictionary(r => r.Component, r => r.MolPct);
        Assert.Equal(1m, byComponent["CO"]);
        Assert.Equal(1.5m, byComponent["H2"]);
        Assert.Equal(0.5m, byComponent["O2"]);
        Assert.DoesNotContain("Carbon_Monoxide", byComponent.Keys);
        Assert.DoesNotContain("Hydrogen", byComponent.Keys);

        var conflict = Assert.Single(mirror.Issues, i => i.Kind == FlertCompositionMapping.AliasConflict);
        Assert.Equal(FlertIssueSeverity.Warning, conflict.Severity);
        Assert.Equal("C10_20220101.CO", conflict.Subject);
    }

    [Fact]
    [Trait("Requirement", "D-476")]
    public void Невідома_колонка_попередження_відсутня_колонка_помилка_і_нічого_не_зіставлено()
    {
        var extra = TableWith(Row(1, 10, "370 Winter", c: new() { ["Methane"] = 100m }), extraColumn: "NewGas");
        var mirror = FlertCompositionMapping.Map(extra);
        Assert.False(mirror.HasErrors);
        var unknown = Assert.Single(mirror.Issues, i => i.Kind == FlertCompositionMapping.UnknownColumn);
        Assert.Equal("NewGas", unknown.Subject);
        Assert.Single(mirror.Cases);

        var missing = TableWith(Row(1, 10, "370 Winter", c: new() { ["Methane"] = 100m }), dropColumn: "Sulfur");
        var broken = FlertCompositionMapping.Map(missing);
        Assert.True(broken.HasErrors);
        var error = Assert.Single(broken.Issues, i => i.Kind == FlertCompositionMapping.MissingColumn);
        Assert.Equal("Sulfur", error.Subject);
        Assert.Empty(broken.Streams);
        Assert.Empty(broken.Cases);
        Assert.Empty(broken.Rows);
    }

    [Fact]
    [Trait("Requirement", "D-476")]
    public void Невідомі_колонки_вікон_потоку_й_кейсу_не_вимагаються_і_не_вважаються_невідомими()
    {
        var table = TableWith(Row(1, 10, "370 Winter", c: new() { ["Methane"] = 100m }), dropColumn: "Stream_ValidFrom");
        var mirror = FlertCompositionMapping.Map(table);
        Assert.Empty(mirror.Issues);

        var full = FlertCompositionMapping.Map(Table(Row(1, 10, "370 Winter", c: new() { ["Methane"] = 100m })));
        Assert.Empty(full.Issues);
    }

    [Fact]
    [Trait("Requirement", "D-476")]
    public void Версії_складу_дають_окремі_кейси_з_вікнами_UTC_і_кінцем_виключно()
    {
        var old = Row(1, 10, "370 Winter", c: new() { ["Methane"] = 100m },
            from: Since2022, to: new DateTime(2026, 6, 15, 0, 0, 0, DateTimeKind.Unspecified));
        var current = Row(1, 10, "370 Winter", c: new() { ["Methane"] = 99m, ["H2S"] = 1m },
            from: new DateTime(2026, 6, 15, 0, 0, 0, DateTimeKind.Unspecified), to: null);
        var midDay = Row(2, 20, "416 SOR", c: new() { ["Methane"] = 100m },
            from: new DateTime(2025, 3, 1, 10, 30, 0, DateTimeKind.Unspecified),
            to: new DateTime(2025, 3, 2, 10, 30, 0, DateTimeKind.Unspecified));

        var mirror = FlertCompositionMapping.Map(Table(old, current, midDay));

        Assert.Equal(ExpectedVersions, mirror.Cases.Select(c => c.Code));
        var closed = mirror.Cases[0];
        Assert.Equal(new DateOnly(2026, 6, 15), closed.ValidTo);
        Assert.True(closed.IsValidOn(new DateOnly(2026, 6, 14)));
        Assert.False(closed.IsValidOn(new DateOnly(2026, 6, 15)));
        Assert.True(mirror.Cases[1].IsValidOn(new DateOnly(2026, 6, 15)));

        // Час доби: початок — вниз, кінець — вгору.
        Assert.Equal(new DateOnly(2025, 3, 1), mirror.Cases[2].ValidFrom);
        Assert.Equal(new DateOnly(2025, 3, 3), mirror.Cases[2].ValidTo);

        Assert.Single(mirror.Streams, s => s.Code == "S1");
    }

    [Fact]
    [Trait("Requirement", "D-476")]
    public void Порожнє_вікно_і_дубль_версії_того_самого_дня_дають_зауваження()
    {
        var empty = Row(1, 10, "370 Winter", c: new() { ["Methane"] = 100m },
            from: new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Unspecified),
            to: new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Unspecified));
        var first = Row(2, 20, "416 SOR", c: new() { ["Methane"] = 100m },
            from: new DateTime(2025, 5, 1, 8, 0, 0, DateTimeKind.Unspecified));
        var later = Row(2, 20, "416 SOR", c: new() { ["Methane"] = 50m, ["Ethane"] = 50m },
            from: new DateTime(2025, 5, 1, 9, 0, 0, DateTimeKind.Unspecified));

        var mirror = FlertCompositionMapping.Map(Table(empty, first, later));

        Assert.Contains(mirror.Issues, i => i.Kind == FlertCompositionMapping.EmptyValidity);
        Assert.Contains(mirror.Issues, i => i.Kind == FlertCompositionMapping.DuplicateVersion && i.Subject == "C20_20250501");
        var @case = Assert.Single(mirror.Cases);
        Assert.Equal("C20_20250501", @case.Code);
        Assert.Equal(2, mirror.Rows.Count);   // лишилась пізніша версія (Methane + Ethane)
    }

    [Fact]
    [Trait("Requirement", "D-476")]
    public void Дубль_номера_потоку_розводиться_сушею_і_кейсом_інакше_неоднозначність()
    {
        var on = new DateOnly(2026, 3, 1);
        var onshoreSor = Row(1, 10, "Units 331-332 - SOR Rev D04", number: "416", onshore: true, c: new() { ["Methane"] = 100m });
        var onshoreEor = Row(1, 11, "Units 331-332 - EOR Rev D04", number: "416", onshore: true, c: new() { ["Methane"] = 100m });
        var offshoreSor = Row(2, 20, "Units 331-332 - SOR Rev D04", number: "416", onshore: false, c: new() { ["Methane"] = 100m });
        var otherOnshoreSor = Row(3, 30, "Train 2 SOR", number: "416", onshore: true, c: new() { ["Methane"] = 100m });

        var unique = FlertCompositionMapping.Map(Table(onshoreSor, onshoreEor, offshoreSor));
        var found = FlertCompositionMapping.FindByNumber(unique, "416", isOnshore: true, "sor", on);
        Assert.Equal(FlertCaseMatchStatus.Found, found.Status);
        Assert.Equal("C10_20220101", found.Case!.Code);

        var offshore = FlertCompositionMapping.FindByNumber(unique, "416", isOnshore: false, "SOR", on);
        Assert.Equal("C20_20220101", offshore.Case!.Code);

        Assert.Equal(FlertCaseMatchStatus.NotFound, FlertCompositionMapping.FindByNumber(unique, "417", true, "SOR", on).Status);
        Assert.Equal(FlertCaseMatchStatus.NotFound,
            FlertCompositionMapping.FindByNumber(unique, "416", true, "SOR", new DateOnly(2021, 12, 31)).Status);

        var duplicated = FlertCompositionMapping.Map(Table(onshoreSor, onshoreEor, offshoreSor, otherOnshoreSor));
        var ambiguous = FlertCompositionMapping.FindByNumber(duplicated, "416", isOnshore: true, "SOR", on);
        Assert.Equal(FlertCaseMatchStatus.Ambiguous, ambiguous.Status);
        Assert.Null(ambiguous.Case);
        Assert.Equal(ExpectedAmbiguous, ambiguous.Candidates.Order(StringComparer.Ordinal));
    }

    [Fact]
    [Trait("Requirement", "D-476")]
    public void Значення_що_не_читається_помилка_рядок_без_ключа_попередження()
    {
        var bad = Row(1, 10, "370 Winter", c: new() { ["Methane"] = 100m });
        bad["H2S"] = new object();
        var mirror = FlertCompositionMapping.Map(Table(bad));
        Assert.True(mirror.HasErrors);
        Assert.Contains(mirror.Issues, i => i.Kind == FlertCompositionMapping.UnreadableValue && i.Subject == "H2S@1");

        var keyless = Row(1, 10, "  ", c: new() { ["Methane"] = 100m });
        var skipped = FlertCompositionMapping.Map(Table(keyless));
        Assert.False(skipped.HasErrors);
        Assert.Empty(skipped.Cases);
        Assert.Contains(skipped.Issues, i => i.Kind == FlertCompositionMapping.RowWithoutKey);
    }

    [Fact]
    [Trait("Requirement", "D-476")]
    public void Атрибути_потоку_беруться_з_чинної_версії()
    {
        var historic = Row(1, 10, "370 Winter", name: "Old name", c: new() { ["Methane"] = 100m },
            from: Since2022, to: new DateTime(2026, 6, 15, 0, 0, 0, DateTimeKind.Unspecified));
        var current = Row(1, 10, "370 Winter", name: "New name", c: new() { ["Methane"] = 100m },
            from: new DateTime(2026, 6, 15, 0, 0, 0, DateTimeKind.Unspecified));

        Assert.Equal("New name", Assert.Single(FlertCompositionMapping.Map(Table(current, historic)).Streams).Name);
        Assert.Equal("New name", Assert.Single(FlertCompositionMapping.Map(Table(historic, current)).Streams).Name);
    }

    [Fact]
    [Trait("Requirement", "D-476")]
    public void Схема_має_46_компонентів_і_49_колонок_складу()
    {
        Assert.Equal(49, FlertCompositionSchema.ComponentColumns.Count);
        Assert.Equal(46, FlertCompositionSchema.Components.Count);
        Assert.True(FlertCompositionSchema.IsLongAlias("Carbon_Monoxide"));
        Assert.False(FlertCompositionSchema.IsLongAlias("CO"));
    }

    // ─── Фікстура ───────────────────────────────────────────────────────────

    private static Dictionary<string, object?> Row(
        int streamId,
        int typeId,
        string caseName,
        Dictionary<string, decimal?> c,
        string? number = null,
        bool onshore = true,
        string? name = null,
        DateTime? from = null,
        DateTime? to = null,
        decimal? temperature = 20m,
        decimal? density = null,
        decimal? mw = null)
    {
        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Stream_ID"] = streamId,
            ["StreamNumber"] = number ?? $"N{streamId}",
            ["Stream_Name"] = name ?? $"Stream {streamId}",
            ["isOnshore"] = onshore,
            ["isSour"] = false,
            ["streamHMB"] = "370",
            ["excludeInListBox"] = false,
            ["StreamGroup"] = 1,
            ["isHMB"] = true,
            ["Stream_ValidFrom"] = Since2022,
            ["Stream_ValidTo"] = null,
            ["StreamType_ID"] = typeId,
            ["StreamType"] = caseName,
            ["RuleParse"] = null,
            ["StreamType_ValidFrom"] = Since2022,
            ["StreamType_ValidTo"] = null,
            ["GasComposition_ID"] = typeId * 10,
            ["GasComposition_ValidFrom"] = from ?? Since2022,
            ["GasComposition_ValidTo"] = to,
            ["Temperature"] = temperature,
            ["Density"] = density,
            ["Molecular_Weight"] = mw,
            ["Z_CompressibilityFactor"] = null,
        };

        foreach (var (column, value) in c)
        {
            row[column] = value;
        }

        return row;
    }

    private static ExternalTable Table(params Dictionary<string, object?>[] rows) => Table(rows, null, null);

    private static ExternalTable TableWith(Dictionary<string, object?> row, string? extraColumn = null, string? dropColumn = null)
        => Table(new[] { row }, extraColumn, dropColumn);

    private static ExternalTable Table(Dictionary<string, object?>[] rows, string? extraColumn, string? dropColumn)
    {
        var columns = FlertCompositionSchema.StreamColumns
            .Concat(StreamValidity)
            .Concat(FlertCompositionSchema.CaseColumns)
            .Concat(TypeValidity)
            .Concat(FlertCompositionSchema.PropertyColumns)
            .Concat(FlertCompositionSchema.ComponentColumns.Select(x => x.Column))
            .Concat(extraColumn is null ? Array.Empty<string>() : new[] { extraColumn })
            .Where(x => !string.Equals(x, dropColumn, StringComparison.Ordinal))
            .ToList();

        return new ExternalTable(
            columns,
            [.. rows.Select(r => (IReadOnlyList<object?>)[.. columns.Select(col => r.TryGetValue(col, out var v) ? v : null)])],
            Truncated: false);
    }
}
