using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Domain.Tests.Documents;

/// <summary>
/// Значення поля шапки документа — той самий Apply/ToData контракт, що
/// <see cref="CellValueTests"/>, але без періоду й рядка таблиці: ключ
/// <c>(DocumentId, HeaderFieldDefId)</c>.
/// </summary>
public sealed class DocumentHeaderValueTests
{
    private static HeaderFieldDef Field(CellDataType type, string code = "Area")
        => new(
            templateVersionId: 1, EcrCode.Create(code),
            new LocalizedText(new Dictionary<string, string> { ["en"] = code }), ordinal: 1, type);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Apply_замінює_значення_і_тип()
    {
        var value = new DocumentHeaderValue(
            documentId: 501, headerFieldDefId: 3, new DocumentHeaderValueData { ValueNumeric = 12500m });
        Assert.Equal(12500m, value.ValueNumeric);

        value.Apply(new DocumentHeaderValueData { ValueString = "Онтустiк" });

        Assert.Equal("Онтустiк", value.ValueString);
        Assert.Null(value.ValueNumeric);
        Assert.True(value.ToData().IsWellFormed());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void ToData_віддає_рівно_те_що_записано()
    {
        var written = new DocumentHeaderValueData { ValueDate = new DateTime(2026, 3, 1) };
        var value = new DocumentHeaderValue(501, 3, written);

        var read = value.ToData();

        Assert.Equal(written, read);
        Assert.Equal(new DateTime(2026, 3, 1), read.ValueDate);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait("Requirement", "ФВ-3.0")]
    public void Явна_порожнеча_і_відсутнє_значення_це_різні_стани()
    {
        var explicitlyEmpty = new DocumentHeaderValue(501, 3, DocumentHeaderValueData.Empty);

        Assert.True(explicitlyEmpty.IsEmpty);
        Assert.True(explicitlyEmpty.ToData().IsWellFormed());
        Assert.Null(explicitlyEmpty.ValueString);

        // «Значення немає» — відсутність рядка doc.DocumentHeaderValue
        // узагалі, той самий контракт, що й doc.CellValue (ФВ-3.8).
        DocumentHeaderValue? absent = null;
        Assert.Null(absent);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Ключ_складається_з_документа_і_поля_а_не_з_періоду_чи_рядка()
    {
        var value = new DocumentHeaderValue(501, 3, new DocumentHeaderValueData { ValueBool = true });

        Assert.Equal(501, value.DocumentId);
        Assert.Equal(3, value.HeaderFieldDefId);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Значення_узгоджується_з_типом_поля_через_ValidateValue()
    {
        var lookup = new DocumentHeaderValue(501, 3, new DocumentHeaderValueData { ValueRegistryEntryId = 9 });

        Assert.Null(Field(CellDataType.Lookup, "Facility").ValidateValue(lookup.ToData()));
        Assert.Equal("ECR-HDR-0422", Field(CellDataType.Decimal, "Amount").ValidateValue(lookup.ToData()));
    }
}
