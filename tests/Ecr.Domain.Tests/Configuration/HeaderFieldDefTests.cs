using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Domain.Tests.Configuration;

/// <summary>
/// Поле шапки документа: обов'язковість і тип за зразком
/// <see cref="ColumnDefValidationTests"/>, але на рівні документа, не рядка.
/// </summary>
public sealed class HeaderFieldDefTests
{
    private static HeaderFieldDef Field(CellDataType type, string code = "Area")
        => new(
            templateVersionId: 1, EcrCode.Create(code),
            new LocalizedText(new Dictionary<string, string> { ["en"] = code }), ordinal: 1, type);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Поле_типу_Formula_або_Calculated_відхиляється_на_створенні()
    {
        // ⛔ Шапка зберігає ВВЕДЕНЕ значення, а не обчислює його: на відміну
        // від ColumnDef, для HeaderFieldDef немає рушія, що міг би записати
        // обчислений результат, — тому дозволяти такий DataType узагалі не
        // можна, а не лише блокувати запис у нього (як робить
        // ColumnDef.ValidateValue для Formula/Calculated).
        foreach (var type in new[] { CellDataType.Formula, CellDataType.Calculated })
        {
            var ex = Assert.Throws<DomainException>(() => Field(type));
            Assert.Equal("ECR-TMPL-0422", ex.ErrorCode);
            Assert.Equal("err.ECR-TMPL-0422.headerFieldTypeNotAllowed", ex.Details!["messageKey"]);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Текст_у_числовому_полі_відхиляється_з_ECR_HDR_0422()
    {
        var field = Field(CellDataType.Decimal);

        Assert.Equal("ECR-HDR-0422", field.ValidateValue(new DocumentHeaderValueData { ValueString = "12500" }));
        Assert.Null(field.ValidateValue(new DocumentHeaderValueData { ValueNumeric = 12500m }));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Порожнє_значення_в_обовязковому_полі_відхиляється()
    {
        var optional = Field(CellDataType.String);
        var required = Field(CellDataType.String, "Contractor");
        required.SetRequired(true);

        Assert.Null(optional.ValidateValue(DocumentHeaderValueData.Empty));
        Assert.Equal("ECR-HDR-0422", required.ValidateValue(DocumentHeaderValueData.Empty));

        required.SetRequired(false);
        Assert.Null(required.ValidateValue(DocumentHeaderValueData.Empty));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Заповнене_двома_значеннями_одночасно_відхиляється()
    {
        var field = Field(CellDataType.Decimal);

        Assert.Equal(
            "ECR-HDR-0422",
            field.ValidateValue(new DocumentHeaderValueData { ValueNumeric = 1m, ValueString = "1" }));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait("Requirement", "ФВ-8.8")]
    public void Lookup_поле_вимагає_посилання_на_запис_реєстру_а_не_текст()
    {
        var field = Field(CellDataType.Lookup, "Facility");
        field.SetLookup(registryDefId: 3);

        Assert.Null(field.ValidateValue(new DocumentHeaderValueData { ValueRegistryEntryId = 77 }));
        Assert.Equal(
            "ECR-HDR-0422",
            field.ValidateValue(new DocumentHeaderValueData { ValueString = "Свердловина 7" }));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void SetLookup_на_не_Lookup_типі_кидає_ECR_TMPL_0422()
    {
        var field = Field(CellDataType.String, "Contractor");

        var ex = Assert.Throws<DomainException>(() => field.SetLookup(3));
        Assert.Equal("ECR-TMPL-0422", ex.ErrorCode);
        Assert.Equal("err.ECR-TMPL-0422.headerFieldLookupRequiresLookupType", ex.Details!["messageKey"]);
        Assert.Null(field.LookupRegistryDefId);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Unit_поле_зберігає_посилання_на_одиницю()
    {
        var field = Field(CellDataType.Unit, "AmountUnit");

        Assert.Null(field.ValidateValue(new DocumentHeaderValueData { ValueUnitId = 8 }));
        Assert.Equal("ECR-HDR-0422", field.ValidateValue(new DocumentHeaderValueData { ValueString = "kg" }));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Ціле_число_з_дробовою_частиною_відхиляється()
    {
        var field = Field(CellDataType.Int, "Count");

        Assert.Null(field.ValidateValue(new DocumentHeaderValueData { ValueNumeric = 5m }));
        Assert.Equal("ECR-HDR-0422", field.ValidateValue(new DocumentHeaderValueData { ValueNumeric = 5.5m }));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Рядок_довший_за_ColumnDef_MaxStringLength_відхиляється()
    {
        // ⚠ Та сама межа, що й у ColumnDef (ColumnDef.MaxStringLength),
        // навмисно перевикористана, а не продубльована новою константою:
        // те саме фізичне обмеження стовпця nvarchar(1000) у
        // doc.DocumentHeaderValue.ValueString.
        var field = Field(CellDataType.String, "Note");

        var atLimit = new string('я', ColumnDef.MaxStringLength);
        Assert.Null(field.ValidateValue(new DocumentHeaderValueData { ValueString = atLimit }));

        var overLimit = new string('я', ColumnDef.MaxStringLength + 1);
        Assert.Equal("ECR-HDR-0422", field.ValidateValue(new DocumentHeaderValueData { ValueString = overLimit }));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait("Requirement", "ФВ-7.6")]
    public void SoftDelete_позначає_поле_видаленим_без_фізичного_прибирання()
    {
        var field = Field(CellDataType.String, "Note");
        var when = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);

        Assert.False(field.IsDeleted);

        field.SoftDelete(userId: 9, when);

        Assert.True(field.IsDeleted);
        Assert.Equal(when, field.DeletedAt);
        Assert.Equal(9, field.DeletedByUserId);

        // Значення все одно перевіряються за старим описом типу — м'яко
        // видалене поле не зникає з домену, лише зі складу нового запису.
        Assert.Null(field.ValidateValue(new DocumentHeaderValueData { ValueString = "still typed" }));
    }
}
