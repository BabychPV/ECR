using Ecr.Application.Documents;
using Ecr.Application.Documents.Dto;
using Ecr.Application.Errors;
using Ecr.Domain.Errors;
using Xunit;

namespace Ecr.Application.Tests.Documents;

/// <summary>
/// Форма запиту <c>PATCH …/cell-styles</c> (CELL-STYLE L3a): колір — лише <c>#rrggbb</c>, стеля комірок,
/// дублі й порожні зміни відхиляються ДО бази; до сховища (L3b) каркас обробника відмовляє чесно.
/// </summary>
public sealed class PatchCellStylesRequestTests
{
    private static PatchCellStylesRequest Req(bool reset = false, params CellStyleChange[] items)
        => new(202610, items, reset);

    private static string KeyOf(BusinessRuleException ex)
        => (string)ex.Details!["messageKey"]!;

    [Fact]
    public void Коректний_запит_проходить()
    {
        Req(false, new CellStyleChange("r1", "c1", "#FFFF00", "", true)).EnsureValid();
        Req(false, new CellStyleChange("r1", "c1", IsBold: false)).EnsureValid();
    }

    [Theory]
    [InlineData("FFFF00")]
    [InlineData("#FFF")]
    [InlineData("#GGGGGG")]
    [InlineData("#FFFF0080")]
    [InlineData("red")]
    public void Колір_не_у_формі_rrggbb_відхиляється(string color)
    {
        var ex = Assert.Throws<BusinessRuleException>(
            () => Req(false, new CellStyleChange("r1", "c1", color)).EnsureValid());

        Assert.Equal(ErrorCodes.CellStyleInvalid, ex.ErrorCode);
        Assert.Equal("err.ECR-CELL-4224.invalidColor", KeyOf(ex));
    }

    [Fact]
    public void Reset_ігнорує_поля_кольору()
    {
        Req(true, new CellStyleChange("r1", "c1", "not-a-color")).EnsureValid();
    }

    [Fact]
    public void Понад_стелю_відхиляється()
    {
        var items = Enumerable.Range(0, PatchCellStylesRequest.MaxItems + 1)
            .Select(i => new CellStyleChange($"r{i}", "c1", "#FFFF00"))
            .ToArray();

        var ex = Assert.Throws<BusinessRuleException>(() => Req(false, items).EnsureValid());

        Assert.Equal("err.ECR-CELL-4224.tooManyItems", KeyOf(ex));
    }

    [Fact]
    public void Стеля_включно_проходить()
    {
        var items = Enumerable.Range(0, PatchCellStylesRequest.MaxItems)
            .Select(i => new CellStyleChange($"r{i}", "c1", "#FFFF00"))
            .ToArray();

        Req(false, items).EnsureValid();
    }

    [Fact]
    public void Дубль_комірки_відхиляється()
    {
        var ex = Assert.Throws<BusinessRuleException>(() => Req(
            false,
            new CellStyleChange("r1", "c1", "#FFFF00"),
            new CellStyleChange("r1", "c1", "#FF0000")).EnsureValid());

        Assert.Equal("err.ECR-CELL-4224.duplicateCell", KeyOf(ex));
    }

    [Fact]
    public void Порожня_зміна_відхиляється()
    {
        var ex = Assert.Throws<BusinessRuleException>(
            () => Req(false, new CellStyleChange("r1", "c1")).EnsureValid());

        Assert.Equal("err.ECR-CELL-4224.emptyChange", KeyOf(ex));
    }
}
