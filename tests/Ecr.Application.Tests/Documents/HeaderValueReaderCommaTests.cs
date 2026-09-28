using System.Globalization;
using System.Text.Json;
using Ecr.Application.Documents;
using Ecr.Application.Errors;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Documents;

/// <summary>
/// Аудит `C1`, шапка документа: той самий суворий розбір, що
/// <see cref="CellValueReader"/>, — <c>NumberStyles.Float</c> під Invariant.
/// </summary>
/// <remarks>
/// ⛔ Доти <c>HeaderValueReader</c> розбирав рядок з <c>NumberStyles.Number</c>,
/// і «12,5» у числовому полі шапки лягало як 125.
/// </remarks>
public sealed class HeaderValueReaderCommaTests
{
    public static TheoryData<string, string> Rejected() => Cross("12,5", "1,234", "1,2,3,4", "1,234.5");

    public static TheoryData<string, string> Accepted() => Cross("1234.5", "1E-05", "-7.25");

    /// <summary>⛔ Мутація: повернути <c>NumberStyles.Number</c> — «12,5» знову 125.</summary>
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Finding", "C1")]
    [MemberData(nameof(Rejected))]
    public void Кома_в_числі_шапки_відхиляється_з_назвою_поля(string cultureName, string text)
    {
        var error = WithCulture(cultureName, () => Assert.Throws<BusinessRuleException>(
            () => HeaderValueReader.Read(FromWire(text), Field())));

        Assert.Equal(ErrorCodes.HeaderValueInvalid, error.ErrorCode);
        Assert.Equal("err.ECR-HDR-0422.expectsNumber", error.Details?["messageKey"]);
        Assert.Equal("QTY", error.Details?["headerFieldCode"]);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Finding", "C1")]
    [MemberData(nameof(Accepted))]
    public void Число_без_коми_в_шапці_читається_однаково(string cultureName, string text)
    {
        var data = WithCulture(cultureName, () => HeaderValueReader.Read(FromWire(text), Field()));

        Assert.NotNull(data);
        Assert.Equal(
            decimal.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture),
            data.ValueNumeric);
    }

    private static TheoryData<string, string> Cross(params string[] texts)
    {
        var data = new TheoryData<string, string>();

        foreach (var culture in new[] { "uk-UA", "" })
        {
            foreach (var text in texts)
            {
                data.Add(culture, text);
            }
        }

        return data;
    }

    /// <summary>Рядок так, як він приходить через API: <see cref="JsonElement"/>.</summary>
    private static JsonElement FromWire(string value)
        => JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(value));

    private static HeaderFieldDef Field()
        => new(templateVersionId: 1, EcrCode.Create("QTY"), new LocalizedText(), 1, CellDataType.Decimal);

    private static T WithCulture<T>(string name, Func<T> action)
    {
        var culture = CultureInfo.CurrentCulture;
        var uiCulture = CultureInfo.CurrentUICulture;

        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(name);
            CultureInfo.CurrentUICulture = new CultureInfo(name);

            return action();
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = uiCulture;
        }
    }
}
