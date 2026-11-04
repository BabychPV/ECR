using System.Globalization;
using Ecr.Application.RegistryImport;
using Xunit;

namespace Ecr.Application.Tests.RegistryImport;

/// <summary>Parsers of the generic import engine: every rule with the edge cases seen in the ERA table 3.3 file.</summary>
public sealed class ImportFieldReaderTests
{
    private static ImportFieldReader Reader(ImportParser parser, Action<ImportFieldSpecBuilder>? tweak = null)
    {
        var b = new ImportFieldSpecBuilder { Parser = parser };
        tweak?.Invoke(b);
        return new ImportFieldReader(b.Build());
    }

    private sealed class ImportFieldSpecBuilder
    {
        public ImportParser Parser { get; set; }

        public int Pad { get; set; } = 4;

        public decimal? Factor { get; set; }

        public bool NonNegative { get; set; }

        public Dictionary<string, string> Choices { get; } = [];

        public Dictionary<string, string> Units { get; } = [];

        public ImportFieldSpec Build() => new()
        {
            Name = "f",
            Patterns = ["x"],
            Parser = Parser,
            Pad = Pad,
            Factor = Factor,
            NonNegative = NonNegative,
            Choices = Choices,
            Units = Units,
        };
    }

    // ---- numbers ----

    public static TheoryData<object?, string?> Decimals => new()
    {
        { 131d, "131" },
        { 0.6d, "0.6" },
        { "131", "131" },
        { "0.6", "0.6" },
        { "0,6", "0.6" },
        { " 1 234,5 ", "1234.5" },
        { "1 234,5", "1234.5" },
        { "1,234.5", "1234.5" },
        { "1.234,5", "1234.5" },
        { "1,234,567", "1234567" },
        { "1.234.567", "1234567" },
        { "1.4e-06", "0.0000014" },
        { "1.4E-6", "0.0000014" },
        { 3.5e-12d, "0.0000000000035" },
        { 3.1e-13d, "0.00000000000031" },
        { 1.6e-07d, "0.00000016" },
        { "-2,5", "-2.5" },
        { "−2.5", "-2.5" },
        { "+7", "7" },
        { 218552.850105d, "218552.850105" },
        { "5.", "5" },
        { ".5", "0.5" },
    };

    [Theory]
    [MemberData(nameof(Decimals))]
    public void Decimal_reads_numbers_and_text_numbers(object? raw, string? expected)
    {
        var result = Reader(ImportParser.Decimal).Read(raw);

        Assert.Null(result.ErrorKey);
        Assert.Equal(decimal.Parse(expected!, CultureInfo.InvariantCulture), Assert.IsType<decimal>(result.Value));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("1,2,3")]
    [InlineData("1.23.4")]
    [InlineData("12,34.567,8")]
    [InlineData("1e")]
    [InlineData("--5")]
    [InlineData("1.2.3,4")]
    [InlineData("#ERR")]
    public void Decimal_refuses_unreadable_text(string raw)
    {
        var result = Reader(ImportParser.Decimal).Read(raw);

        Assert.Equal(RegistryImportKeys.ValueInvalid, result.ErrorKey);
    }

    [Fact]
    public void Decimal_refuses_a_double_that_does_not_fit_instead_of_writing_zero()
    {
        // 1e-30 does not fit decimal (28 digits); a silent 0 would be a wrong emission limit.
        Assert.Equal(RegistryImportKeys.ValueInvalid, Reader(ImportParser.Decimal).Read(1e-30d).ErrorKey);
        Assert.Equal(RegistryImportKeys.ValueInvalid, Reader(ImportParser.Decimal).Read(double.NaN).ErrorKey);
    }

    [Fact]
    public void Decimal_keeps_the_exact_digits_of_tiny_values()
    {
        // 3.5e-12 must survive: the legacy file holds values down to 3.1e-13.
        var value = Assert.IsType<decimal>(Reader(ImportParser.Decimal).Read(3.5e-12d).Value);

        Assert.Equal(0.0000000000035m, value);
    }

    [Theory]
    [InlineData("1,234", true)]
    [InlineData("12,345", true)]
    [InlineData("0,333", false)]
    [InlineData("1.234", false)]
    [InlineData("1,23", false)]
    [InlineData("1,2345", false)]
    public void A_single_comma_with_three_digits_is_read_as_decimal_and_flagged(string raw, bool ambiguous)
    {
        var result = Reader(ImportParser.Decimal).Read(raw);

        Assert.Null(result.ErrorKey);
        Assert.Equal(ambiguous ? RegistryImportKeys.ValueAmbiguous : null, result.WarningKey);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("-")]
    [InlineData("–")]
    [InlineData("N/A")]
    [InlineData("n/a")]
    [InlineData("#N/A")]
    [InlineData("н/д")]
    [InlineData("нет")]
    public void Empty_cells_and_no_value_markers_read_as_null(string raw)
    {
        var result = Reader(ImportParser.Decimal).Read(raw);

        Assert.True(result.Empty);
        Assert.Null(result.Value);
        Assert.Null(result.ErrorKey);
    }

    [Fact]
    public void Null_cell_is_empty()
        => Assert.True(Reader(ImportParser.Decimal).Read(null).Empty);

    [Fact]
    public void A_dash_is_text_for_the_text_parser_and_a_no_value_for_numbers()
    {
        Assert.Equal("-", Reader(ImportParser.Text).Read("-").Value);
        Assert.True(Reader(ImportParser.Decimal).Read("-").Empty);
    }

    [Fact]
    public void Negative_is_refused_only_when_the_field_says_so()
    {
        Assert.Equal(-1m, Reader(ImportParser.Decimal).Read(-1d).Value);
        Assert.Equal(
            RegistryImportKeys.ValueNegative,
            Reader(ImportParser.Decimal, b => b.NonNegative = true).Read(-1d).ErrorKey);
        Assert.Equal(0m, Reader(ImportParser.Decimal, b => b.NonNegative = true).Read(0d).Value);
    }

    [Fact]
    public void Factor_converts_units_exactly()
    {
        var kgToTons = Reader(ImportParser.Decimal, b => b.Factor = 0.001m);

        Assert.Equal(1.234m, kgToTons.Read(1234d).Value);
        Assert.Equal(0.0000035m, kgToTons.Read(0.0035d).Value);
    }

    [Theory]
    [InlineData(2024d, 2024L)]
    [InlineData("2024", 2024L)]
    [InlineData("2 024", 2024L)]
    public void Integer_reads_whole_numbers(object raw, long expected)
        => Assert.Equal(expected, Reader(ImportParser.Integer).Read(raw).Value);

    [Theory]
    [InlineData(2024.5d)]
    [InlineData("12,5")]
    [InlineData("x")]
    public void Integer_refuses_fractions_and_text(object raw)
        => Assert.Equal(RegistryImportKeys.ValueInvalid, Reader(ImportParser.Integer).Read(raw).ErrorKey);

    // ---- codes ----

    public static TheoryData<object?, string?> Codes => new()
    {
        { 3d, "0003" },
        { 6001d, "6001" },
        { "0003", "0003" },
        { "3", "0003" },
        { "'3", "'3" },
        { " 333 ", "0333" },
        { 2735d, "2735" },
        { "2735", "2735" },
        { "131.0", "0131" },
        { "36_pod", "0036_pod" },
        { "0036_pod", "0036_pod" },
        { "12345", "12345" },
        { "A12", "A12" },
        { " 0333 ", "0333" },
    };

    [Theory]
    [MemberData(nameof(Codes))]
    public void Code_normalises_text_and_number_codes_the_same_way(object? raw, string? expected)
        => Assert.Equal(expected, Reader(ImportParser.Code).Read(raw).Value);

    [Theory]
    [InlineData(1.5d)]
    [InlineData(-3d)]
    [InlineData(double.PositiveInfinity)]
    public void Code_refuses_numbers_that_are_not_codes(double raw)
        => Assert.Equal(RegistryImportKeys.ValueInvalid, Reader(ImportParser.Code).Read(raw).ErrorKey);

    [Fact]
    public void Code_width_is_configurable()
        => Assert.Equal("00012", Reader(ImportParser.Code, b => b.Pad = 5).Read(12d).Value);

    // ---- text ----

    [Theory]
    [InlineData("  Hello   world ", "Hello world")]
    [InlineData("a b", "a b")]
    [InlineData("a\r\nb", "a b")]
    [InlineData("UNIT B4-100.  ОУС", "UNIT B4-100. ОУС")]
    public void Text_collapses_white_space(string raw, string expected)
        => Assert.Equal(expected, Reader(ImportParser.Text).Read(raw).Value);

    [Fact]
    public void Text_of_a_number_is_invariant()
        => Assert.Equal("12.5", Reader(ImportParser.Text).Read(12.5d).Value);

    // ---- choice and unit ----

    [Fact]
    public void Choice_matches_latin_and_cyrillic_look_alikes()
    {
        var reader = Reader(ImportParser.Choice, b =>
        {
            b.Choices["Т"] = "T";
            b.Choices["П1"] = "P1";
        });

        Assert.Equal("T", reader.Read("Т").Value);
        Assert.Equal("T", reader.Read("T").Value); // Latin T
        Assert.Equal("P1", reader.Read(" п1 ").Value);
        Assert.Equal(RegistryImportKeys.ValueInvalid, reader.Read("Z").ErrorKey);
    }

    [Fact]
    public void Unit_maps_aliases_ignoring_case_spaces_and_superscripts()
    {
        var reader = Reader(ImportParser.Unit, b =>
        {
            b.Units["мг/нм3"] = "mg/nm3";
            b.Units["г/с"] = "g/s";
        });

        Assert.Equal("mg/nm3", reader.Read("МГ / НМ³").Value);
        Assert.Equal("g/s", reader.Read("г/С").Value);
        Assert.Equal(RegistryImportKeys.ValueInvalid, reader.Read("kg").ErrorKey);
    }

    // ---- dates ----

    [Fact]
    public void Date_reads_excel_dates_and_unambiguous_text()
    {
        var reader = Reader(ImportParser.Date);

        Assert.Equal(new DateTime(2024, 4, 1), reader.Read(new DateTime(2024, 4, 1)).Value);
        Assert.Equal(new DateTime(2024, 4, 1), reader.Read("2024-04-01").Value);
        Assert.Equal(RegistryImportKeys.ValueInvalid, reader.Read("not a date").ErrorKey);
    }

    // ---- fold ----

    [Theory]
    [InlineData("Hаименование  вещества", "Наименование вещества")]
    [InlineData("Коэффи-циент", "Коэффициент")]
    [InlineData("мг/нм³", "мг/нм3")]
    [InlineData("Y1", "У1")]
    [InlineData("Х1", "X1")]
    public void Fold_ignores_what_legacy_files_get_wrong(string left, string right)
        => Assert.Equal(ImportText.Fold(left), ImportText.Fold(right));
}
