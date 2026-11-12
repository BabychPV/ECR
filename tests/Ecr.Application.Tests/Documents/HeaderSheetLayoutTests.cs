// tests/Ecr.Application.Tests/Documents/HeaderSheetLayoutTests.cs
using Ecr.Application.Documents;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Documents;

/// <summary>
/// X-5 (аудит 2026-11, D-477): аркуш шапки xlsx у порядку й з підписами форми «2. Contract» Land — те саме правило,
/// що в картці документа (<c>contractSection.ts</c>).
/// </summary>
/// <remarks>
/// Мутації: прибрати сортування за <c>Rank</c> у <c>Order</c> — тест порядку червоний; прибрати умову «мови різні»
/// у <c>BilingualLabel</c> — тест однієї мови червоний.
/// </remarks>
public sealed class HeaderSheetLayoutTests
{
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [InlineData("Area", null, "area")]
    [InlineData("FilledBy", null, "filledInBy")]
    [InlineData("X1", "Filled in by, name, contact tel", null)]
    [InlineData("X2", "On/Offshore", "onshoreOffshore")]
    [InlineData("PERMIT", "Permit Number", "permitNumber")]
    [InlineData("NOTE", "Note", null)]
    public void ContractKeyOf_за_кодом_або_англійським_підписом(string code, string? english, string? expected)
        => Assert.Equal(expected, HeaderSheetLayout.ContractKeyOf(code, english));

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void BilingualLabel_EN_RU_лише_коли_обидві_мови_є_і_різні()
    {
        Assert.Equal("Area — Район работ", HeaderSheetLayout.BilingualLabel(Text(("en", "Area"), ("ru", "Район работ")), "Area"));
        Assert.Equal("Processed on", HeaderSheetLayout.BilingualLabel(Text(("en", "Processed on")), "Processed on"));
        Assert.Equal("Код", HeaderSheetLayout.BilingualLabel(Text(("en", "Код"), ("ru", "Код")), "Код"));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Order_секція_Contract_за_порядком_Land_далі_решта_далі_службові()
    {
        var rows = HeaderSheetLayout.Order(
            [Row("PERMIT", "permitNumber"), Row("AREA", "area"), Row("NOTE", null)],
            [Row("FileNumber", "fileNumber"), Row("Version", "version"), Row("Period", null)]);

        Assert.Equal(["AREA", "FileNumber", "PERMIT", "Version", "NOTE", "Period"], [.. rows.Select(r => r.Code)]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    public void Order_без_секції_службові_у_хвості_а_дубль_ключа_серед_решти()
    {
        var withoutSection = HeaderSheetLayout.Order(
            [Row("NOTE", null)],
            [Row("FileNumber", "fileNumber"), Row("Version", "version"), Row("Period", null)]);
        Assert.Equal(["NOTE", "FileNumber", "Version", "Period"], [.. withoutSection.Select(r => r.Code)]);

        var duplicate = HeaderSheetLayout.Order([Row("AREA2", "area"), Row("AREA", "area")], []);
        Assert.Equal(["AREA2", "AREA"], [.. duplicate.Select(r => r.Code)]);
    }

    private static HeaderExportRow Row(string code, string? contractKey)
        => new(code, code, null, null, IsNumeric: false, ContractKey: contractKey);

    private static LocalizedText Text(params (string Lang, string Value)[] values)
        => new(values.ToDictionary(v => v.Lang, v => v.Value));
}
