using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Ecr.Application.Documents;
using Ecr.Application.Documents.Dto;
using Ecr.Application.Errors;
using Ecr.Application.Localization;
using Ecr.Application.Workflow;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Localization;

/// <summary>
/// Рішення людини 2026-09-29: число, набране людиною ТЕКСТОМ, читається за
/// культурою мови користувача (en → en-US, ru → ru-RU, kz → kk-KZ), без мови —
/// en-US; неоднозначне — відмова з підказкою.
/// </summary>
/// <remarks>
/// ⛔ Мутації, від яких червоніє цей файл:
/// <list type="bullet">
/// <item><c>NumberCulture.ForLanguage</c> завжди віддає Invariant/en-US — падають
/// усі рядки ru/kk («1,234» → 1.234, «1 234,5» → 1234.5);</item>
/// <item><c>CellValueReader</c>/<c>HeaderValueReader</c> ігнорують культуру
/// (голий <c>NumberStyles.Float</c>) — «1 234,5» і «1,234.5» стають відмовою;</item>
/// <item>змішані роздільники береться за клієнтським правилом «останній —
/// десятковий» — «1.234,5» в en-US стає 1234.5 замість відмови.</item>
/// </list>
/// </remarks>
public sealed class CultureNumberReaderTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    /// <summary>Нерозривний пробіл — так ru/kk Excel і ОС ставлять розряди.</summary>
    private static readonly string Nbsp = ((char)0xA0).ToString();

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [InlineData("en", "en-US")]
    [InlineData("ru", "ru-RU")]
    [InlineData("kz", "kk-KZ")]
    [InlineData("kk-KZ", "kk-KZ")]
    [InlineData("ru-RU", "ru-RU")]
    [InlineData(null, "en-US")]
    [InlineData("", "en-US")]
    public void Мова_користувача_дає_культуру_розбору(string? language, string expected)
        => Assert.Equal(expected, NumberCulture.ForLanguage(language).Name);

    public static TheoryData<string, string, string> Accepted() => new()
    {
        // en-US: кома — розряди, крапка — десятковий.
        { "en", "1,234.5", "1234.5" },
        { "en", "1234.5", "1234.5" },
        { "en", "1,234,567", "1234567" },
        { "en", "12,5", "12.5" },
        { "en", "1E-05", "0.00001" },

        // ru: кома — десятковий, розряди — пробіл (зокрема нерозривний).
        { "ru", "1 234,5", "1234.5" },
        { "ru", "1234,5", "1234.5" },
        { "ru", "1" + Nbsp + "234,5", "1234.5" },
        { "ru", "1,234", "1.234" },
        { "ru", "1234.5", "1234.5" },
        { "ru", "1.234", "1.234" },

        // kk — як ru.
        { "kz", "1 234,5", "1234.5" },
        { "kz", "1234,5", "1234.5" },
        { "kz", "1,234", "1.234" },
        { "kz", "-0,5", "-0.5" },
    };

    public static TheoryData<string, string, NumberTextKind> Refused() => new()
    {
        // Неоднозначне: кома в en-US — і розряди, і дріб.
        { "en", "1,234", NumberTextKind.Ambiguous },
        { "en", "-12,500", NumberTextKind.Ambiguous },

        // Змішані роздільники, що не відповідають культурі.
        { "en", "1.234,5", NumberTextKind.NotNumber },
        { "ru", "1,234.5", NumberTextKind.NotNumber },
        { "ru", "1.234,5", NumberTextKind.NotNumber },
        { "kz", "1,234.5", NumberTextKind.NotNumber },

        // Не числа за будь-якої культури.
        { "en", "1,23,4", NumberTextKind.NotNumber },
        { "ru", "abc", NumberTextKind.NotNumber },
        { "ru", "1,", NumberTextKind.NotNumber },
    };

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [MemberData(nameof(Accepted))]
    public void Однозначний_текст_читається_за_культурою_мови(string language, string text, string expected)
    {
        var reading = CultureNumberReader.Read(text, NumberCulture.ForLanguage(language));

        Assert.Equal(NumberTextKind.Number, reading.Kind);
        Assert.Equal(decimal.Parse(expected, CultureInfo.InvariantCulture), reading.Value);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [MemberData(nameof(Refused))]
    public void Неоднозначне_і_змішане_не_вгадується(string language, string text, NumberTextKind kind)
        => Assert.Equal(kind, CultureNumberReader.Read(text, NumberCulture.ForLanguage(language)).Kind);

    /// <summary>
    /// Шлях PATCH комірки: текстове значення числової колонки з JSON читається
    /// за мовою користувача.
    /// </summary>
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [MemberData(nameof(Accepted))]
    public void Комірка_з_текстом_числа_читається_за_мовою(string language, string text, string expected)
    {
        var data = CellValueReader.Read(FromWire(text), Column(), NumberCulture.ForLanguage(language));

        Assert.NotNull(data);
        Assert.Equal(decimal.Parse(expected, CultureInfo.InvariantCulture), data.ValueNumeric);
    }

    /// <summary>
    /// Неоднозначне — відмова наявним кодом і ключем, з підказкою: причина й
    /// обидва прочитання.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Неоднозначна_комірка_відмовляє_з_обома_прочитаннями()
    {
        var error = Assert.Throws<BusinessRuleException>(
            () => CellValueReader.Read(FromWire("1,234"), Column(), NumberCulture.ForLanguage("en")));

        Assert.Equal(CellValueReader.TypeMismatch, error.ErrorCode);

        // Число є, лише двозначне: власний ключ, а не «очікує число».
        Assert.Equal("err.ECR-CELL-0422.ambiguousSeparator", error.Details?["messageKey"]);
        Assert.Equal("1,234", error.Details?["value"]);
        Assert.Equal(CellValueReader.AmbiguousSeparator, error.Details?["reason"]);
        Assert.Equal("1234", error.Details?["asGroup"]);
        Assert.Equal("1.234", error.Details?["asDecimal"]);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [InlineData("en", "1.234,5")]
    [InlineData("ru", "1,234.5")]
    public void Змішані_роздільники_в_комірці_відмовляють(string language, string text)
    {
        var error = Assert.Throws<BusinessRuleException>(
            () => CellValueReader.Read(FromWire(text), Column(), NumberCulture.ForLanguage(language)));

        Assert.Equal("err.ECR-CELL-0422.expectsNumber", error.Details?["messageKey"]);
        Assert.False(error.Details!.ContainsKey("reason"));
    }

    /// <summary>Шлях шапки документа — той самий розбір.</summary>
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [InlineData("ru", "1 234,5", "1234.5")]
    [InlineData("kz", "1,234", "1.234")]
    [InlineData("en", "1,234.5", "1234.5")]
    public void Поле_шапки_з_текстом_числа_читається_за_мовою(string language, string text, string expected)
    {
        var data = HeaderValueReader.Read(text, HeaderField(), NumberCulture.ForLanguage(language));

        Assert.NotNull(data);
        Assert.Equal(decimal.Parse(expected, CultureInfo.InvariantCulture), data.ValueNumeric);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Неоднозначне_поле_шапки_відмовляє_з_підказкою()
    {
        var error = Assert.Throws<BusinessRuleException>(
            () => HeaderValueReader.Read("1,234", HeaderField(), NumberCulture.ForLanguage("en")));

        Assert.Equal("err.ECR-HDR-0422.ambiguousSeparator", error.Details?["messageKey"]);
        Assert.Equal("1,234", error.Details?["value"]);
        Assert.Equal(CellValueReader.AmbiguousSeparator, error.Details?["reason"]);
        Assert.Equal("1234", error.Details?["asGroup"]);
        Assert.Equal("1.234", error.Details?["asDecimal"]);
    }

    /// <summary>Справді не число в шапці — старий ключ «очікує число», без причини.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Не_число_в_полі_шапки_лишає_ключ_очікує_число()
    {
        var error = Assert.Throws<BusinessRuleException>(
            () => HeaderValueReader.Read("1.234,5", HeaderField(), NumberCulture.ForLanguage("en")));

        Assert.Equal("err.ECR-HDR-0422.expectsNumber", error.Details?["messageKey"]);
        Assert.False(error.Details!.ContainsKey("reason"));
    }

    /// <summary>
    /// Борг `C1` (DocumentVersionHandlers): порівняння версій бачить зміну
    /// «12,5» → «125», а не вважає їх рівними через <c>AllowThousands</c>.
    /// </summary>
    /// <remarks>⛔ Мутація: повернути <c>NumberStyles.Number</c> у <c>SameUntyped</c>.</remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait("Finding", "C1")]
    public void Порівняння_версій_не_викидає_кому_як_розряди()
    {
        var same = typeof(CompareDocumentVersionsHandler).GetMethod(
            "SameValue",
            BindingFlags.NonPublic | BindingFlags.Static,
            [typeof(SubmissionPayloadHeaderValue), typeof(SubmissionPayloadHeaderValue)])!;

        bool Same(string a, string b)
            => (bool)same.Invoke(null, [new SubmissionPayloadHeaderValue(a, null), new SubmissionPayloadHeaderValue(b, null)])!;

        Assert.False(Same("12,5", "125"));
        Assert.True(Same("1.50", "1.5"));
    }

    private static object? FromWire(string value)
    {
        var request = new PatchCellsRequest(
            1, 202603, "UserEdit", [new PatchRow("R1", "0x01", [new PatchCell("C1", value)])]);

        var json = JsonSerializer.Serialize(request, Web);
        var wire = JsonSerializer.Deserialize<PatchCellsRequest>(json, Web)!.Rows[0].Cells[0].Value;

        Assert.IsType<JsonElement>(wire);

        return wire;
    }

    private static ColumnDef Column()
        => new(tableDefId: 1, EcrCode.Create("C1"), new LocalizedText(), ordinal: 1, CellDataType.Decimal);

    private static HeaderFieldDef HeaderField()
        => new(templateVersionId: 1, EcrCode.Create("QTY"), new LocalizedText(), 1, CellDataType.Decimal);
}
