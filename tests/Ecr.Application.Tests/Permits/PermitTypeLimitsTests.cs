// tests/Ecr.Application.Tests/Permits/PermitTypeLimitsTests.cs
using Ecr.Application.Errors;
using Ecr.Application.Permits;
using Ecr.Application.Permits.Dto;
using Xunit;

namespace Ecr.Application.Tests.Permits;

/// <summary>
/// Межі розміру визначення типу дозволу (аудит, прохід A, PA-A-06): межа проходить, межа + 1 - 422 <c>ECR-PRM-0422</c>.
/// </summary>
/// <remarks>
/// ⛔ Межі тут - ЯВНІ числа, а не <see cref="PermitTypeLimits"/>: з константою «межа + 1» попливла б разом із нею, і
/// піднята межа лишалась би зеленою. Мутаційний доказ: підняти будь-яку з констант - червоніє відповідний «+1»-тест
/// (а <c>Межі_збігаються_з_задокументованими</c> - завжди).
/// </remarks>
public sealed class PermitTypeLimitsTests
{
    [Fact]
    public void Межі_збігаються_з_задокументованими()
    {
        Assert.Equal(50, PermitTypeLimits.MaxTables);
        Assert.Equal(100, PermitTypeLimits.MaxColumnsPerTable);
        Assert.Equal(200, PermitTypeLimits.MaxColumnsTotal);
        Assert.Equal(256, PermitTypeLimits.MaxNameLength);
        Assert.Equal(1024 * 1024, PermitTypeLimits.MaxRequestBodyBytes);
    }

    [Fact]
    public void Рівно_50_таблиць_проходить_а_51_відхиляється()
    {
        Validate(Content(Tables(50, 1)));
        Refused("tooManyTables", Content(Tables(51, 1)));
    }

    [Fact]
    public void Рівно_100_стовпців_у_таблиці_проходить_а_101_відхиляється()
    {
        Validate(Content(Table("T", Columns(100))));

        var error = Refused("tooManyColumns", Content(Table("T", Columns(101))));
        Assert.Equal("T", error.Details!["tableCode"]);
        Assert.Equal("100", error.Details["max"]);
    }

    [Fact]
    public void Рівно_200_стовпців_у_типі_проходить_а_201_відхиляється()
    {
        Validate(Content(Table("A", Columns(100)), Table("B", Columns(100))));
        Refused("tooManyColumnsTotal", Content(Table("A", Columns(100)), Table("B", Columns(100)), Table("C", Columns(1))));
    }

    [Fact]
    public void Склад_на_десятки_тисяч_стовпців_відсікається_межею_до_будь_якого_обходу()
    {
        // Коди стовпців недопустимі: якби спершу йшов обхід, відмовою було б columnCodeInvalid, а не межа.
        var huge = Enumerable.Range(0, 20_000).Select(i => Col("1x" + i)).ToArray();

        Refused("tooManyColumns", Content(Table("T", huge)));
    }

    [Theory]
    [InlineData("type")]
    [InlineData("table")]
    [InlineData("column")]
    [InlineData("rule")]
    public void Назва_у_256_символів_проходить_а_у_257_відхиляється(string place)
    {
        Validate(WithLongName(place, 256));

        var error = Refused("textTooLong", WithLongName(place, 257));
        Assert.Equal("256", error.Details!["max"]);
    }

    [Fact]
    public void Довга_назва_будь_якою_мовою_відхиляється()
    {
        var content = Content(Table("T", Key("A"))) with
        {
            NameL10n = new() { ["en"] = "ok", ["kz"] = new string('к', 257) },
        };

        Refused("textTooLong", content);
        Assert.Equal(
            "err.ECR-PRM-0422.textTooLong",
            Assert.Throws<BusinessRuleException>(
                () => PermitTypeDefinitionValidator.ValidateDetails(content.NameL10n, "Air")).Details!["messageKey"]);
    }

    private static PermitTypeContent WithLongName(string place, int length)
    {
        var name = new Dictionary<string, string> { ["en"] = new string('N', length) };
        var content = Content(Table("T", Key("A")));
        return place switch
        {
            "type" => content with { NameL10n = name },
            "table" => content with { Tables = [content.Tables[0] with { NameL10n = name }] },
            "column" => content with { Tables = [Table("T", Key("A") with { NameL10n = name })] },
            _ => content with { Rules = [new PermitTypeRuleDto("R", name, "[A] > 0", "Warn")] },
        };
    }

    private static void Validate(PermitTypeContent content)
        => PermitTypeDefinitionValidator.Validate("AIR", content, null);

    private static BusinessRuleException Refused(string key, PermitTypeContent content)
    {
        var error = Assert.Throws<BusinessRuleException>(() => Validate(content));
        Assert.Equal("ECR-PRM-0422", error.ErrorCode);
        Assert.Equal("err.ECR-PRM-0422." + key, error.Details!["messageKey"]);
        return error;
    }

    private static PermitTypeContent Content(params PermitTypeTableDto[] tables)
        => new(Name("Air"), "Air", tables, []);

    private static PermitTypeTableDto[] Tables(int count, int columns)
        => [.. Enumerable.Range(0, count).Select(i => Table("T" + i, Columns(columns)))];

    /// <summary>Ключ <c>K</c> і ще <c>count - 1</c> звичайних стовпців.</summary>
    private static PermitTypeColumnDto[] Columns(int count)
        => [Key("K"), .. Enumerable.Range(1, count - 1).Select(i => Col("C" + i))];

    private static PermitTypeTableDto Table(string code, params PermitTypeColumnDto[] columns)
        => new(code, Name(code), 1, columns, []);

    private static PermitTypeColumnDto Key(string code) => Col(code) with { Key = true, Required = true };

    private static PermitTypeColumnDto Col(string code)
        => new(code, Name(code), "String", null, false, false, null, null, null, null, null, null, null, 1);

    private static Dictionary<string, string> Name(string en) => new() { ["en"] = en };
}
