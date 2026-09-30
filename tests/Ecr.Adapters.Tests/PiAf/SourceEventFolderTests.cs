using Ecr.Adapters.PiAf;
using Ecr.Application.Ports;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Adapters.Tests.PiAf;

/// <summary>
/// Згортка довгої форми «рядок на атрибут» у події (HSE301 F4e, §4.7.2):
/// групування за ідентифікатором, незакрита подія, область атрибута, текст
/// значення, стеля.
/// </summary>
public sealed class SourceEventFolderTests
{
    private const string Template = "FlareEvent";

    private static readonly DateTime Start = new(2026, 1, 28, 9, 9, 20, DateTimeKind.Utc);

    private static readonly DateTime End = new(2026, 1, 28, 9, 24, 50, DateTimeKind.Utc);

    private static SourceEventRow Row(
        string id,
        string? attr,
        object? value = null,
        string? name = "Flare 1",
        DateTime? end = null,
        string? scope = "E",
        string? uom = null)
        => new(id, name, null, Start, end ?? End, null, @"\\AF\Plant\HP", null, scope, attr, value, uom);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Подія_з_трьома_атрибутами_це_одна_подія_в_порядку_рядків()
    {
        var result = SourceEventFolder.Fold(
            [
                Row("EF-1", "Category", "V8"),
                Row("EF-1", "Volume", "269.258", uom: "Sm3"),
                Row("EF-1", "Season", "Winter", scope: "P"),
            ],
            Template,
            10);

        var single = Assert.Single(result.Events);
        Assert.False(result.Truncated);
        Assert.Null(result.ErrorCode);
        Assert.Equal("EF-1", single.EventId);
        Assert.Equal(Template, single.TemplateName);
        Assert.Equal(Start, single.StartUtc);
        Assert.Equal(End, single.EndUtc);
        Assert.Equal(@"\\AF\Plant\HP", single.PrimaryElementPath);

        (string, SourceEventAttributeScope, decimal?, string?, string?)[] expected =
        [
            ("Category", SourceEventAttributeScope.Event, null, "V8", null),
            ("Volume", SourceEventAttributeScope.Event, 269.258m, null, "Sm3"),
            ("Season", SourceEventAttributeScope.PrimaryElement, null, "Winter", null),
        ];
        Assert.Equal(
            expected,
            single.Attributes.Select(a => (a.Name, a.Scope, a.ValueNumeric, a.ValueString, a.SourceUnitSymbol)));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Дві_події_з_однаковою_назвою_але_різними_ID_це_дві_події()
    {
        var result = SourceEventFolder.Fold(
            [
                Row("EF-1", "Category", "V8", name: "Flare"),
                Row("EF-2", "Category", "V9", name: "Flare"),
                Row("EF-1", "Volume", "1"),
            ],
            Template,
            10);

        Assert.Equal(["EF-1", "EF-2"], result.Events.Select(e => e.EventId));
        Assert.Equal(["Category", "Volume"], result.Events[0].Attributes.Select(a => a.Name));
        Assert.Equal("V9", Assert.Single(result.Events[1].Attributes).ValueString);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [InlineData(null)]
    [InlineData("9999-12-31T23:59:59")]
    [InlineData("9999-01-01T00:00:00")]
    public void Незакрита_подія_NULL_чи_сторожова_дата_дає_EndUtc_null(string? rawEnd)
    {
        var end = rawEnd is null
            ? (DateTime?)null
            : DateTime.SpecifyKind(DateTime.Parse(rawEnd, System.Globalization.CultureInfo.InvariantCulture), DateTimeKind.Utc);

        var row = new SourceEventRow("EF-1", "Flare", null, Start, end, null, null, null, null, null, null, null);

        var single = Assert.Single(SourceEventFolder.Fold([row], Template, 10).Events);

        Assert.Null(single.EndUtc);
        Assert.Empty(single.Attributes);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Кінець_перед_сторожовою_датою_лишається_кінцем()
    {
        var late = new DateTime(9998, 12, 31, 23, 59, 59, DateTimeKind.Utc);

        Assert.Equal(late, SourceEventFolder.OpenEnd(late));
        Assert.Equal(End, SourceEventFolder.OpenEnd(End));
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [InlineData("1.5", "1.5", null)]
    [InlineData(" -2.5E1 ", "-25", null)]
    [InlineData("1,5", null, "1,5")]
    [InlineData("1,500.25", null, "1,500.25")]
    [InlineData("V8", null, "V8")]
    [InlineData("370 Winter", null, "370 Winter")]
    [InlineData("NaN", null, "NaN")]
    public void Текст_атрибута_розбирається_інваріантною_культурою_інакше_лишається_текстом(
        string raw, string? numeric, string? text)
    {
        var (value, rest) = SourceEventFolder.Value(raw);

        decimal? expected = numeric is null
            ? null
            : decimal.Parse(numeric, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(expected, value);
        Assert.Equal(text, rest);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Розбір_тексту_не_залежить_від_культури_потоку()
    {
        var before = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            // У uk-UA десятковий роздільник — кома: з поточною культурою "1,5"
            // стало б числом, а "1.5" — текстом.
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("uk-UA");

            Assert.Equal(((decimal?)1.5m, (string?)null), SourceEventFolder.Value("1.5"));
            Assert.Equal(((decimal?)null, (string?)"1,5"), SourceEventFolder.Value("1,5"));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = before;
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Типізоване_значення_йде_правилом_точок_порожній_текст_це_нічого()
    {
        Assert.Equal(((decimal?)12.5m, (string?)null), SourceEventFolder.Value(12.5d));
        Assert.Equal(((decimal?)null, (string?)"true"), SourceEventFolder.Value(true));
        Assert.Equal(((decimal?)null, (string?)null), SourceEventFolder.Value("  "));
        Assert.Equal(((decimal?)null, (string?)null), SourceEventFolder.Value(null));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Стеля_подій_понад_неї_Truncated_рівно_стеля_ні()
    {
        SourceEventRow[] rows =
        [
            Row("EF-1", "A", "1"), Row("EF-1", "B", "2"),
            Row("EF-2", "A", "3"), Row("EF-2", "B", "4"),
            Row("EF-3", "A", "5"),
        ];

        var cut = SourceEventFolder.Fold(rows, Template, 2);
        var exact = SourceEventFolder.Fold(rows[..4], Template, 2);

        Assert.True(cut.Truncated);
        Assert.Equal(["EF-1", "EF-2"], cut.Events.Select(e => e.EventId));

        // Стеля рахує події, не рядки: чотири рядки — дві повні події.
        Assert.All(cut.Events, e => Assert.Equal(2, e.Attributes.Count));

        Assert.False(exact.Truncated);
        Assert.Equal(2, exact.Events.Count);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Фільтр_атрибутів_розводить_область_і_не_зважає_на_регістр_імені()
    {
        var result = SourceEventFolder.Fold(
            [
                Row("EF-1", "Category", "V8", scope: "E"),
                Row("EF-1", "Category", "V6", scope: "P"),
                Row("EF-1", "Noise", "x"),
            ],
            Template,
            10,
            [new SourceEventAttributeRef("category", SourceEventAttributeScope.PrimaryElement)]);

        var attribute = Assert.Single(Assert.Single(result.Events).Attributes);
        Assert.Equal(("Category", SourceEventAttributeScope.PrimaryElement, "V6"), (attribute.Name, attribute.Scope, attribute.ValueString));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Повтор_атрибута_перший_виграє_шаблон_рядка_переважає_шаблон_запиту()
    {
        var own = new SourceEventRow("EF-1", null, "OwnTemplate", Start, End, null, null, "EF-0", "E", "A", "1", null);

        var single = Assert.Single(SourceEventFolder.Fold([own, own with { AttrValue = "2" }], Template, 10).Events);

        Assert.Equal("OwnTemplate", single.TemplateName);
        Assert.Equal("EF-0", single.ParentId);
        Assert.Equal(1m, Assert.Single(single.Attributes).ValueNumeric);
    }
}
